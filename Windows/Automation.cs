using Kot.Core;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Text.Json;
namespace Kot.Windows;
public sealed partial class MainWindow
{
    readonly ReconnectPolicy reconnect = new();
    readonly System.Windows.Forms.Timer automationTimer = new() { Interval = 2000 };
    readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<string, DateTimeOffset> refreshAttempts = [];
    DateTimeOffset lastPing = DateTimeOffset.MinValue, lastHealth = DateTimeOffset.MinValue;
    bool ticking, connecting, toggling, networkChanged;
    int failedHealth;
    string backgroundError = "", networkSignature = "", connectionNotice = "";
    void StartApplication()
    {
        if (applicationStarted || exiting) return;
        applicationStarted = true;
        bool autoConnect = (profile.AutoConnect || resumeConnection) && profile.Nodes.Count > 0;
        if (autoConnect) { reconnect.Start(DateTimeOffset.UtcNow); StartConnection(); }
        _ = StartServices(autoConnect);
    }
    async Task StartServices(bool autoConnect)
    {
        try { await services.AutoStart(startZapret: !autoConnect); }
        catch (Exception ex) { if (!exiting) { AppLog.Error("service startup", ex); backgroundError = Store.Friendly(ex); Snapshot(); } }
    }
    void InitAutomation()
    {
        networkSignature = PhysicalNetwork();
        NetworkChange.NetworkAddressChanged += AddressChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        automationTimer.Tick += async (_, _) => await TickAutomation(); automationTimer.Start();
    }
    void AddressChanged(object? sender, EventArgs e) { if (!IsDisposed && !exiting && IsHandleCreated) BeginInvoke(() => networkChanged = true); }
    void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !IsDisposed && !exiting && IsHandleCreated) BeginInvoke(() =>
        {
            networkChanged = true; networkSignature = "";
            // Sleeping Windows could not deliver a heartbeat. Give the existing
            // page time to answer after resume before treating it as unresponsive.
            if (browserLoaded) { browserDeadline = DateTimeOffset.UtcNow.AddSeconds(45); browserHeartbeat = DateTimeOffset.MinValue; }
        });
    }
    static string PhysicalNetwork()
    {
        try
        {
            return string.Join(';', NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) && !n.Name.Contains("kot-tun", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => !System.Net.IPAddress.IsLoopback(a.Address)).Select(a => n.Id + ":" + a.Address)).Order());
        }
        catch { return ""; }
    }
    async Task ToggleConnection()
    {
        if (exiting || toggling) return;
        toggling = true;
        try
        {
        if (services.Testing) { connectionNotice = "Завершите подбор zapret перед подключением VPN."; Snapshot(); return; }
        connectionNotice = "";
        if (reconnect.Desired || tunnel.State != "idle")
        {
            reconnect.Stop(); importAttempt?.Cancel(); await StopTools(); await tunnel.Stop(); KillSwitch.Release(); Snapshot();
        }
        else if (profile.Nodes.Count > 0) { reconnect.Start(DateTimeOffset.UtcNow); StartConnection(); Snapshot(); }
        }
        finally { toggling = false; }
    }
    void ResumeDesired()
    {
        if (reconnect.Desired && !exiting && profile.Nodes.Count > 0) { reconnect.ResetDelay(DateTimeOffset.UtcNow); StartConnection(); }
    }
    async void StartConnection()
    {
        if (connecting || exiting || services.Testing || !reconnect.Desired || profile.Nodes.Count == 0) return;
        connecting = true;
        try
        {
            try { await services.StopZapretForVpn(); }
            catch (Exception ex)
            {
                reconnect.Stop();
                connectionNotice = "Не удалось остановить zapret перед подключением VPN. " + Store.Friendly(ex);
                throw;
            }
            if (exiting || !reconnect.Desired) return;
            await tunnel.Connect(profile);
            if (tunnel.State == "connected") { reconnect.Healthy(); connectionNotice = ""; failedHealth = 0; lastHealth = DateTimeOffset.UtcNow; }
            else if (reconnect.Desired) { if (profile.Automation.Reconnect) reconnect.Retry(DateTimeOffset.UtcNow); else reconnect.Stop(); }
        }
        catch (Exception ex) { AppLog.Error("automatic connect", ex); if (reconnect.Desired) { if (profile.Automation.Reconnect) reconnect.Retry(DateTimeOffset.UtcNow); else reconnect.Stop(); } }
        finally { connecting = false; if (!exiting) Snapshot(); }
    }
    async Task TickAutomation()
    {
        if (ticking || exiting || !applicationStarted) return;
        ticking = true;
        try
        {
            var now = DateTimeOffset.UtcNow; NotifySubscriptionExpiry(now); string signature = PhysicalNetwork(); bool online = signature.Length > 0;
            if (networkChanged)
            {
                networkChanged = false;
                if (signature != networkSignature) { networkSignature = signature; if (profile.Automation.Reconnect && reconnect.Desired) { AppLog.Write("network", "physical network changed or resumed"); await tunnel.Stop(); reconnect.ResetDelay(now); } }
            }
            if (reconnect.Desired && tunnel.State == "idle" && !connecting && operations.CurrentCount > 0)
            {
                if (!profile.Automation.Reconnect && reconnect.Failures == 0) reconnect.Stop();
                else if (reconnect.Due(now, online)) StartConnection();
            }
            if (tunnel.State == "connected" && now - lastHealth >= TimeSpan.FromSeconds(30))
            {
                lastHealth = now; int session = tunnel.Session; bool healthy = online && await tunnel.Probe(lifetime.Token);
                if (session != tunnel.Session || exiting) return;
                failedHealth = healthy ? 0 : failedHealth + 1;
                if (healthy) reconnect.Healthy();
                if (profile.Selected == "auto") await tunnel.ReadAutomaticNode(lifetime.Token);
                if (failedHealth >= 3) { AppLog.Write("network", "three health checks failed"); failedHealth = 0; await tunnel.Stop(); connectionNotice = "Соединение потеряно: сервер не отвечает."; if (profile.Automation.Reconnect && reconnect.Desired) reconnect.Retry(DateTimeOffset.UtcNow); else reconnect.Stop(); }
            }
            if (online && operations.CurrentCount > 0 && !connecting && !ping.Busy && profile.Nodes.Count > 0 && profile.Automation.PingMinutes > 0 && now - lastPing >= TimeSpan.FromMinutes(profile.Automation.PingMinutes))
            { lastPing = now; ping.Start(profile.Nodes.ToList(), profile.Ping); }
            if (online && DownloadsAvailable && !connecting && profile.Automation.RefreshHours > 0 && await operations.WaitAsync(0))
            {
                try
                {
                    profile.CaptureActive();
                    var due = profile.Subscriptions.FirstOrDefault(s => !s.Local && now - (s.Updated ?? DateTimeOffset.MinValue) >= TimeSpan.FromHours(profile.Automation.RefreshHours) && (!refreshAttempts.TryGetValue(s.Id, out var tried) || now - tried >= TimeSpan.FromMinutes(15)));
                    if (due != null)
                    {
                        refreshAttempts[due.Id] = now;
                        using var ct = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); importAttempt = ct;
                        try
                        {
                            var device = profile.SendHwid ? DeviceIdentity.Get() : null;
                            var result = await Subscriptions.Download(due.Address, ct.Token, device: device, proxyPort: RequestProxyPort);
                            if (exiting) return;
                            if (due.Id == profile.ActiveSubscription) await ApplySubscription(due.Address, due.Name, result, false);
                            else
                            {
                                await ProviderAssets.Prepare(result.Nodes, ct.Token, RequestProxyPort);
                                result = await XraySession.Validate(result, ct.Token);
                                SaveProfile(p =>
                                {
                                    var updated = p.Subscriptions.Single(s => s.Id == due.Id);
                                    updated.Nodes = result.Nodes; updated.Usage = result.Usage; updated.Updated = DateTimeOffset.UtcNow;
                                    if (updated.Selected != "auto" && updated.Nodes.All(n => n.Id != updated.Selected)) updated.Selected = updated.Nodes[0].Id;
                                    updated.Favorites.RemoveAll(id => updated.Nodes.All(n => n.Id != id));
                                });
                                AppLog.Register(profile);
                            }
                            backgroundError = ""; AppLog.Write("automation", "subscription updated: " + result.Nodes.Count + " nodes");
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { AppLog.Error("subscription background refresh", ex); backgroundError = "Не удалось обновить подписку. " + Store.Friendly(ex); }
                        finally { if (importAttempt == ct) importAttempt = null; }
                    }
                }
                finally { operations.Release(); }
            }
            await TickRouting(now, online && DownloadsAvailable);
            await TickGeoIp(now, online && DownloadsAvailable);
            Snapshot();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("automation", ex); }
        finally { ticking = false; }
    }
    async Task ApplySubscription(string address, string name, SubscriptionResult result, bool importing)
    {
        await ProviderAssets.Prepare(result.Nodes, lifetime.Token, RequestProxyPort);
        result = await XraySession.Validate(result, lifetime.Token);
        var saved = profile.Subscriptions.FirstOrDefault(s => !s.Local && s.Address == address);
        bool switchProfile = importing && saved?.Id != profile.ActiveSubscription;
        if (switchProfile && saved == null && profile.Subscriptions.Count >= 50) throw new UserError("Максимум 50 подписок.");
        await StopTools();
        string Fingerprint(Node n) => n.Id + ":" + n.FullConfig?.ToJsonString() + ":" + n.GeoIpUrl + ":" + n.GeoSiteUrl;
        bool restart = switchProfile || (profile.Selected == "auto" ? !profile.Nodes.Select(Fingerprint).Order().SequenceEqual(result.Nodes.Select(Fingerprint).Order())
            : result.Nodes.FirstOrDefault(n => n.Id == profile.Selected) is not { } updated || profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected) is not { } current || Fingerprint(updated) != Fingerprint(current));
        if (restart) await tunnel.Stop(); await ping.Stop(clear: true);
        try
        {
            SaveProfile(p =>
            {
                if (switchProfile)
                {
                    var candidate = p.Subscriptions.FirstOrDefault(s => s.Id == saved?.Id);
                    if (candidate == null) { candidate = new SavedSubscription { Address = address, Name = name.Length > 0 ? name : "Подписка" }; p.Subscriptions.Add(candidate); }
                    candidate.Nodes = result.Nodes; candidate.Usage = result.Usage; candidate.Updated = DateTimeOffset.UtcNow;
                    if (candidate.Selected != "auto" && candidate.Nodes.All(n => n.Id != candidate.Selected)) candidate.Selected = result.Nodes[0].Id;
                    p.Activate(candidate.Id);
                }
                else
                {
                    p.Address = address; p.Local = false; p.SubscriptionUsage = result.Usage; p.Name = name.Length > 0 ? name : "Подписка"; p.Nodes = result.Nodes; p.Updated = DateTimeOffset.UtcNow;
                    if (p.Selected != "auto" && result.Nodes.All(n => n.Id != p.Selected)) p.Selected = result.Nodes[0].Id;
                }
                p.Favorites.RemoveAll(id => p.Nodes.All(n => n.Id != id));
            });
            warnings = result.Warnings; AppLog.Register(profile); AppLog.Write("subscription", "saved: " + result.Nodes.Count + " nodes; skipped=" + warnings.Count);
        }
        finally { if (restart) ResumeDesired(); }
    }
    async Task ExportBackup(string password)
    {
        profile.CaptureActive(); byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(profile, Json); Profile copy;
        try { copy = JsonSerializer.Deserialize<Profile>(snapshot, Json)!; } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(snapshot); }
        byte[] data = await Task.Run(() => Backup.Export(copy, password));
        using var dialog = new SaveFileDialog { Filter = "Резервная копия kot. (*.kotbackup)|*.kotbackup", FileName = "kot-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".kotbackup" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { await File.WriteAllBytesAsync(dialog.FileName, data); AppLog.Write("backup", "encrypted backup saved"); }
    }
    async Task ImportBackup(string password)
    {
        using var dialog = new OpenFileDialog { Filter = "Резервная копия kot. (*.kotbackup)|*.kotbackup" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(dialog.FileName).Length > 20 * 1024 * 1024 + 52) throw new UserError("Резервная копия слишком большая.");
        byte[] data = await File.ReadAllBytesAsync(dialog.FileName); var restored = await Task.Run(() => Backup.Import(data, password));
        // Keep a recoverable encrypted copy before replacing a working profile.
        string old = Path.Combine(Store.Folder, "profile.bin"); if (File.Exists(old)) File.Copy(old, Path.Combine(Store.Folder, "profile-before-restore.bin"), true);
        await StopTools(); await services.StopAll(); reconnect.Stop(); await ping.Stop(clear: true); await tunnel.Stop();
        bool startupChanged = restored.Startup != profile.Startup;
        if (startupChanged) await Startup.Set(restored.Startup);
        try { Store.Save(restored); }
        catch
        {
            if (startupChanged) await Startup.Set(profile.Startup);
            throw;
        }
        profile = restored; AppLog.Register(profile); KillSwitch.Release(); warnings = []; backgroundError = ""; refreshAttempts.Clear(); routingAttempts.Clear(); routingErrors.Clear(); geoIpAttempt = null; geoIpError = ""; lastPing = DateTimeOffset.UtcNow; AppLog.Write("backup", "profile restored; connection remains stopped");
    }
    void DisposeAutomation()
    {
        reconnect.Stop(); lifetime.Cancel(); telemetryTimer.Stop(); telemetryTimer.Dispose(); automationTimer.Stop(); automationTimer.Dispose();
        NetworkChange.NetworkAddressChanged -= AddressChanged; SystemEvents.PowerModeChanged -= PowerChanged;
    }
}
