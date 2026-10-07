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
    bool ticking, connecting, networkChanged;
    int failedHealth;
    string backgroundError = "", networkSignature = "", connectionNotice = "";
    void InitAutomation()
    {
        networkSignature = PhysicalNetwork();
        NetworkChange.NetworkAddressChanged += AddressChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        automationTimer.Tick += async (_, _) => await TickAutomation(); automationTimer.Start();
    }
    void AddressChanged(object? sender, EventArgs e) { if (!IsDisposed && !exiting && IsHandleCreated) BeginInvoke(() => networkChanged = true); }
    void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume && !IsDisposed && !exiting && IsHandleCreated) BeginInvoke(() => { networkChanged = true; networkSignature = ""; }); }
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
        if (exiting) return;
        connectionNotice = "";
        if (reconnect.Desired || tunnel.State != "idle")
        {
            reconnect.Stop(); importAttempt?.Cancel(); await tunnel.Stop(); KillSwitch.Release(); Snapshot();
        }
        else if (profile.Nodes.Count > 0) { reconnect.Start(DateTimeOffset.UtcNow); StartConnection(); Snapshot(); }
    }
    void ResumeDesired()
    {
        if (reconnect.Desired && !exiting && profile.Nodes.Count > 0) { reconnect.Start(DateTimeOffset.UtcNow); StartConnection(); }
    }
    async void StartConnection()
    {
        if (connecting || exiting || !reconnect.Desired || profile.Nodes.Count == 0) return;
        connecting = true;
        try
        {
            await tunnel.Connect(profile);
            if (tunnel.State == "connected") { reconnect.Healthy(); connectionNotice = ""; failedHealth = 0; lastHealth = DateTimeOffset.UtcNow; }
            else if (reconnect.Desired) { if (profile.Automation.Reconnect) reconnect.Retry(DateTimeOffset.UtcNow); else reconnect.Stop(); }
        }
        catch (Exception ex) { AppLog.Error("automatic connect", ex); if (reconnect.Desired) { if (profile.Automation.Reconnect) reconnect.Retry(DateTimeOffset.UtcNow); else reconnect.Stop(); } }
        finally { connecting = false; if (!exiting) Snapshot(); }
    }
    async Task TickAutomation()
    {
        if (ticking || exiting || !bridgeReady) return;
        ticking = true;
        try
        {
            var now = DateTimeOffset.UtcNow; string signature = PhysicalNetwork(); bool online = signature.Length > 0;
            if (networkChanged)
            {
                networkChanged = false;
                if (signature != networkSignature) { networkSignature = signature; if (profile.Automation.Reconnect && reconnect.Desired) { AppLog.Write("network", "physical network changed or resumed"); await tunnel.Stop(); reconnect.Start(now); } }
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
            if (online && !connecting && profile.Automation.RefreshHours > 0 && await operations.WaitAsync(0))
            {
                try
                {
                    profile.CaptureActive();
                    var due = profile.Subscriptions.FirstOrDefault(s => now - (s.Updated ?? DateTimeOffset.MinValue) >= TimeSpan.FromHours(profile.Automation.RefreshHours) && (!refreshAttempts.TryGetValue(s.Id, out var tried) || now - tried >= TimeSpan.FromMinutes(15)));
                    if (due != null)
                    {
                        refreshAttempts[due.Id] = now; AppLog.Secret(due.Address);
                        using var ct = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); importAttempt = ct;
                        try
                        {
                            var device = profile.SendHwid ? DeviceIdentity.Get() : null; if (device != null) AppLog.Secret(device.Hwid);
                            var result = await Subscriptions.Download(due.Address, ct.Token, device: device);
                            if (exiting) return;
                            if (due.Id == profile.ActiveSubscription) await ApplySubscription(due.Address, due.Name, result, false);
                            else { due.Nodes = result.Nodes; due.Updated = DateTimeOffset.UtcNow; if (due.Selected != "auto" && due.Nodes.All(n => n.Id != due.Selected)) due.Selected = due.Nodes[0].Id; Store.Save(profile); AppLog.Register(profile); }
                            backgroundError = ""; AppLog.Write("automation", "subscription updated: " + result.Nodes.Count + " nodes");
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { AppLog.Error("subscription background refresh", ex); backgroundError = "Не удалось обновить подписку. " + Store.Friendly(ex); }
                        finally { if (importAttempt == ct) importAttempt = null; }
                    }
                }
                finally { operations.Release(); }
            }
            Snapshot();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Error("automation", ex); }
        finally { ticking = false; }
    }
    async Task ApplySubscription(string address, string name, SubscriptionResult result, bool importing)
    {
        profile.CaptureActive();
        var saved = profile.Subscriptions.FirstOrDefault(s => s.Address == address);
        bool switchProfile = importing && saved?.Id != profile.ActiveSubscription;
        bool restart = switchProfile || (profile.Selected == "auto" ? !profile.Nodes.Select(n => n.Id).Order().SequenceEqual(result.Nodes.Select(n => n.Id).Order()) : result.Nodes.All(n => n.Id != profile.Selected));
        if (restart) await tunnel.Stop(); await ping.Stop(clear: true);
        if (switchProfile)
        {
            if (saved == null)
            {
                if (profile.Subscriptions.Count >= 50) throw new UserError("Максимум 50 подписок.");
                saved = new SavedSubscription { Address = address, Name = name.Length > 0 ? name : "Подписка" }; profile.Subscriptions.Add(saved);
            }
            saved.Nodes = result.Nodes; saved.Updated = DateTimeOffset.UtcNow; if (saved.Selected != "auto" && saved.Nodes.All(n => n.Id != saved.Selected)) saved.Selected = result.Nodes[0].Id;
            profile.Activate(saved.Id);
        }
        else
        {
            profile.Address = address; profile.Name = name.Length > 0 ? name : "Подписка"; profile.Nodes = result.Nodes; profile.Updated = DateTimeOffset.UtcNow;
            if (profile.Selected != "auto" && result.Nodes.All(n => n.Id != profile.Selected)) profile.Selected = result.Nodes[0].Id;
        }
        profile.Favorites.RemoveAll(id => profile.Nodes.All(n => n.Id != id)); warnings = result.Warnings;
        Store.Save(profile); AppLog.Register(profile); AppLog.Write("subscription", "saved: " + result.Nodes.Count + " nodes; skipped=" + warnings.Count);
        if (restart) ResumeDesired();
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
        AppLog.Register(restored); reconnect.Stop(); await ping.Stop(clear: true); await tunnel.Stop();
        if (restored.Startup != profile.Startup) await Startup.Set(restored.Startup);
        // Keep a recoverable encrypted copy before replacing a working profile.
        string old = Path.Combine(Store.Folder, "profile.bin"); if (File.Exists(old)) File.Copy(old, Path.Combine(Store.Folder, "profile-before-restore.bin"), true);
        KillSwitch.Release(); Store.Save(restored); profile = restored; warnings = []; backgroundError = ""; refreshAttempts.Clear(); lastPing = DateTimeOffset.UtcNow; AppLog.Write("backup", "profile restored; connection remains stopped");
    }
    void DisposeAutomation()
    {
        reconnect.Stop(); lifetime.Cancel(); telemetryTimer.Stop(); telemetryTimer.Dispose(); automationTimer.Stop(); automationTimer.Dispose();
        NetworkChange.NetworkAddressChanged -= AddressChanged; SystemEvents.PowerModeChanged -= PowerChanged;
    }
}
