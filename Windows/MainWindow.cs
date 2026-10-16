using Kot.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Kot.Windows;
public sealed partial class MainWindow : WindowChrome
{
    WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(21, 23, 24) };
    readonly NotifyIcon tray;
    readonly ThemeIcons icons = new();
    readonly TrayMenu trayMenu;
    readonly StartupScreen loading;
    readonly bool startupSpin = Random.Shared.Next(100) == 0;
    string? pendingTrayPage;
    readonly Tunnel tunnel = new();
    readonly PingService ping = new();
    readonly SemaphoreSlim operations = new(1);
    Profile profile;
    readonly bool startup, resumeConnection;
    bool preserveKillSwitch;
    bool exiting, allowClose, bridgeReady, initialized;
    List<string> warnings = [];
    CancellationTokenSource? importAttempt;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public MainWindow(bool startup, bool resumeConnection = false, Profile? initialProfile = null)
    {
        this.startup = startup; this.resumeConnection = resumeConnection; profile = initialProfile ?? Store.Load();
        if (!profile.KillSwitch) KillSwitch.Release(); else KillSwitch.Detect();
        AppLog.Register(profile); AppLog.Write("app", "kot. " + ClientIdentity.Version + " starting; OS=" + Environment.OSVersion + "; core=sing-box 1.14.2");
        try { profile.Ping.Validate(); } catch { profile.Ping = new(); }
        try { profile.Automation.Validate(); } catch { profile.Automation = new(); }
        InitAutomation();
        services = new ManagedServices(() => profile.Services, () => tunnel.State != "idle" || reconnect.Desired || KillSwitch.Active, () => { if (!IsDisposed && !exiting && IsHandleCreated) BeginInvoke(() => { if (!exiting) Snapshot(); }); }, lifetime.Token, proxyPort: () => RequestProxyPort, downloadsAvailable: () => DownloadsAvailable);
        InitTelemetry();
        foreach (string orphan in Directory.GetFiles(Store.Folder, "ping-*.json")) { try { File.Delete(orphan); } catch (Exception ex) { AppLog.Error("orphan ping cleanup", ex); } }
        Text = AppRuntime.DisplayName; FormBorderStyle = FormBorderStyle.None; MinimumSize = new Size(800, 600); ClientSize = new Size(960, 640); StartPosition = FormStartPosition.CenterScreen;
        BackColor = ColorTranslator.FromHtml(profile.Theme == "light" ? "#f6f6f2" : "#151718");
        web.DefaultBackgroundColor = BackColor;
        Icon = icons.Window(profile.Theme);
        Controls.Add(web);
        loading = new StartupScreen(profile.Theme, Icon, MinimizeWindow, ToggleMaximize, Close, () => { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }, startupSpin);
        Controls.Add(loading); loading.BringToFront();
        InitBrowserWatch();
        trayMenu = new TrayMenu(Restore, OpenTrayPage, ToggleConnection, ExitApp);
        trayMenu.Opening += (_, _) => UpdateTray();
        tray = new NotifyIcon { Text = AppRuntime.DisplayName + " · Не подключено", ContextMenuStrip = trayMenu };
        icons.Apply(this, tray, profile.Theme); tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };
        FormClosed += (_, _) => { tray.Dispose(); trayMenu.Dispose(); Icon = null; icons.Dispose(); };
        tunnel.Changed += () => { if (!IsDisposed && !exiting && IsHandleCreated) BeginInvoke(() => { tray.Text = AppRuntime.DisplayName + (tunnel.State == "connected" ? " · Подключено" : " · Не подключено"); Snapshot(); }); };
        ping.Changed += () => { if (!IsDisposed && !exiting && IsHandleCreated) BeginInvoke(Snapshot); };
        Shown += async (_, _) => { StartApplication(); await Initialize(); };
        FormClosing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (!initialized) { await ExitApp(); return; }
            if (profile.Tray && !exiting) Hide(); else await ExitApp();
        };
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Program.ShutdownMessage) { _ = ExitApp(); m.Result = IntPtr.Zero; return; }
        if (m.Msg == Program.ActivateMessage) { Restore(); m.Result = IntPtr.Zero; return; }
        const int WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16, WM_NCHITTEST = 0x84;
        if (m.Msg == WM_QUERYENDSESSION) { tunnel.Cancel(); m.Result = (IntPtr)1; return; }
        if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero) { allowClose = true; Close(); return; }
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            int lp = unchecked((int)m.LParam.ToInt64()); Point p = PointToClient(new Point((short)(lp & 0xffff), (short)(lp >> 16)));
            int g = Math.Max(6, (int)(6 * DeviceDpi / 96f));
            bool left = p.X < g, right = p.X >= ClientSize.Width - g, top = p.Y < g, bottom = p.Y >= ClientSize.Height - g;
            int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
            if (hit != 0) m.Result = (IntPtr)hit;
        }
    }
    void Restore() => RestoreWindow();
    void OpenTrayPage(string page)
    {
        if (page is not ("home" or "servers" or "statistics" or "diagnostics" or "logs" or "services" or "settings")) return;
        Restore();
        if (bridgeReady) Send(new { kind = "navigate", page }); else pendingTrayPage = page;
    }
    void UpdateTray()
    {
        icons.Apply(this, tray, profile.Theme);
        string state = reconnect.Desired && tunnel.State == "idle" ? "waiting" : tunnel.State;
        string name = profile.Selected == "auto" ? "Авто" : profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Name ?? "";
        trayMenu.Update(profile.Theme, state, name, profile.Nodes.Count > 0);
        tray.Text = AppRuntime.DisplayName + (state switch { "connected" => " · Подключено", "connecting" => " · Подключение", "waiting" => " · Ожидание сети", _ => " · Не подключено" });
    }
    async Task Initialize()
    {
        try
        {
            try
            {
                bool enabled = await Startup.IsEnabled();
                if (exiting || IsDisposed) return;
                if (profile.Startup != enabled) SaveProfile(p => p.Startup = enabled);
            }
            catch (Exception ex) { AppLog.Error("startup state", ex); }
            CoreWebView2Environment env;
            try { env = await WebViewHost.CreateEnvironment(Path.Combine(Store.Folder, "WebView")).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token); }
            catch (WebView2RuntimeNotFoundException)
            {
                string installer = Path.Combine(AppContext.BaseDirectory, "Install-WebView2.exe");
                if (!File.Exists(installer)) throw new UserError("Не найден WebView2 Runtime и установщик Install-WebView2.exe.");
                MessageBox.Show("Для интерфейса нужен Microsoft WebView2. Сейчас откроется официальный установщик.", "kot.");
                var installInfo = new ProcessStartInfo(installer) { UseShellExecute = true }; installInfo.ArgumentList.Add("/install");
                using var install = Process.Start(installInfo); if (install != null) await install.WaitForExitAsync(lifetime.Token).WaitAsync(TimeSpan.FromMinutes(3), lifetime.Token);
                if (exiting || IsDisposed) return;
                env = await WebViewHost.CreateEnvironment(Path.Combine(Store.Folder, "WebView")).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            }
            if (exiting || IsDisposed) return;
            await web.EnsureCoreWebView2Async(env).WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            if (exiting || IsDisposed) return;
            var c = web.CoreWebView2;
            WatchBrowser(env, c);
            c.Settings.AreDefaultContextMenusEnabled = false; c.Settings.AreDevToolsEnabled = false; c.Settings.IsStatusBarEnabled = false; c.Settings.IsZoomControlEnabled = false;
            c.Settings.IsGeneralAutofillEnabled = false; c.Settings.IsPasswordAutosaveEnabled = false;
            c.Settings.AreHostObjectsAllowed = false; c.Settings.AreDefaultScriptDialogsEnabled = false;
            c.SetVirtualHostNameToFolderMapping("kot.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            c.NavigationStarting += (_, e) => { if (e.Uri != "https://kot.local/index.html") e.Cancel = true; };
            c.FrameNavigationStarting += (_, e) => e.Cancel = true;
            c.NewWindowRequested += (_, e) => e.Handled = true;
            c.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            c.DownloadStarting += (_, e) => e.Cancel = true;
            c.WebMessageReceived += async (_, e) => { if (!exiting && !web.IsDisposed && web.CoreWebView2 == c) await Message(e); };
            // Bootstrap contains only appearance, available before the first HTML paint.
            await c.AddScriptToExecuteOnDocumentCreatedAsync("window.kotStartup=" + JsonSerializer.Serialize(new { theme = profile.Theme, spin = startupSpin }) + ";");
            if (exiting || IsDisposed) return;
            c.Navigate("https://kot.local/index.html"); initialized = true;
            if (!interfaceStarted && startup && profile.Tray) Hide();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { BrowserFailure(ex); }
    }
    void Send(object value)
    {
        if (exiting || !bridgeReady || web.IsDisposed || web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, Json)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { RecoverBrowser("Связь с интерфейсом потеряна."); }
    }
    object Model() => new
    {
        name = profile.Name, hasSubscription = profile.Subscriptions.Count > 0, localSubscription = profile.Local, subscriptionUsage = SubscriptionUsageModel(), tools = ToolsModel(),
        nodes = profile.Nodes.Select(n => new { n.Id, n.Name, n.Protocol, ping = ping.Get(n.Id), code = n.Protocol == "shadowsocks" ? "SS" : n.Protocol == "hysteria2" ? "HY" : n.Protocol == "trojan" ? "TR" : n.Protocol == "vmess" ? "VM" : "VL" }),
        subscriptions = profile.Subscriptions.Select(s => new { s.Id, s.Name, count = s.Nodes.Count }), activeSubscription = profile.ActiveSubscription,
        services = services.Model(visiblePage == "services"), telemetry = TelemetryModel(), statistics = StatisticsModel(), journal = visiblePage == "logs" ? AppLog.Tail() : null, updates = UpdateModel(),
        favorites = profile.Favorites, automaticNode = tunnel.AutomaticNode, automation = profile.Automation,
        routing = RoutingModel(), geoIp = GeoIpModel(), backgroundError = string.Join('\n', new[] { backgroundError, routingErrors.Values.FirstOrDefault(), geoIpError }.Where(s => !string.IsNullOrEmpty(s))),
        selected = profile.Selected, state = tunnel.State == "idle" && reconnect.Desired ? "waiting" : tunnel.State, error = tunnel.Error.Length > 0 ? tunnel.Error : connectionNotice,
        settings = new { profile.Theme, mode = profile.ConnectionMode, profile.Startup, profile.AutoConnect, profile.Tray, profile.SendHwid, profile.KillSwitch, killSwitchActive = KillSwitch.Active, bypass = string.Join('\n', profile.Bypass) },
        ping = new { busy = ping.Busy, done = ping.Done, total = ping.Total, error = ping.Error, settings = profile.Ping },
        updated = profile.Updated?.ToLocalTime().ToString("dd.MM HH:mm"), warnings, version = ClientIdentity.Version
    };
    void Snapshot() { UpdateTray(); Send(new { kind = "snapshot", data = Model() }); }
    async Task Message(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (exiting || e.Source != "https://kot.local/index.html") return;
        int id = 0;
        int generation = browserGeneration;
        void Reply(object value) { if (generation == browserGeneration) Send(value); }
        try
        {
            string raw = e.WebMessageAsJson;
            if (raw.Length > 300_000) throw new UserError("Слишком большой запрос интерфейса.");
            using var d = JsonDocument.Parse(raw); var root = d.RootElement;
            id = root.GetProperty("id").GetInt32(); string action = root.GetProperty("action").GetString() ?? "";
            JsonElement data = root.GetProperty("data");
            if (action == "ready")
            {
                interfaceStarted = true; bridgeReady = true; Snapshot(); Reply(new { kind = "reply", id, ok = true });
                if (pendingTrayPage is { } page) { Send(new { kind = "navigate", page }); pendingTrayPage = null; }
                return;
            }
            if (!bridgeReady) return;
            switch (action)
            {
                case "heartbeat": browserDeadline = DateTimeOffset.UtcNow.AddSeconds(45); Reply(new { kind = "reply", id, ok = true }); return;
                case "uiReady": BrowserReady(); break;
                case "drag": if (data.GetProperty("double").GetBoolean()) ToggleMaximize(); else { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } break;
                case "resize":
                    var hit = data.GetProperty("edge").GetString() switch { "w" => 10, "e" => 11, "n" => 12, "nw" => 13, "ne" => 14, "s" => 15, "sw" => 16, "se" => 17, _ => 0 };
                    if (hit != 0 && WindowState == FormWindowState.Normal) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)hit, IntPtr.Zero); } break;
                case "minimize": MinimizeWindow(); break;
                case "maximize": ToggleMaximize(); break;
                case "close": Close(); break;
                case "toggle": await ToggleConnection(); break;
                case "view":
                    string page = data.GetProperty("page").GetString() ?? ""; if (page is not ("home" or "servers" or "statistics" or "diagnostics" or "logs" or "services" or "settings")) throw new UserError("Неизвестная страница."); visiblePage = page; break;
                case "serviceAction": case "serviceCancel": case "serviceSettings": case "serviceUpdateSettings": case "serviceRotateSecret":
                case "serviceSource": case "serviceCopyLink": case "serviceOpenTelegram": case "serviceCopyResults": case "serviceExportResults": case "serviceClearLog": case "serviceSelectTest":
                    await ServiceMessage(action, data); break;
                case "runDiagnostics": StartTools("diagnostics", data); break;
                case "testQuality": StartTools("quality", data); break;
                case "cancelTools": toolsAttempt?.Cancel(); break;
                case "readQrFile": case "readQrClipboard":
                    string? qrText = await ReadQr(action == "readQrClipboard");
                    Reply(new { kind = "reply", id, ok = true, data = new { text = qrText } }); return;
                case "readNodeQr":
                    Reply(new { kind = "reply", id, ok = true, data = ReadShareQr(data) }); return;
                case "copyNodeLink": case "copyNodeQr": case "saveNodeQr": ExportShareQr(data, action); break;
                case "checkUpdates": await CheckUpdates(); break;
                case "cancelUpdate": updateAttempt?.Cancel(); break;
                case "cancelImport": importAttempt?.Cancel(); break;
                case "pingAll": lastPing = DateTimeOffset.UtcNow; ping.Start(profile.Nodes.ToList(), profile.Ping); break;
                case "pingNode":
                    var node = profile.Nodes.FirstOrDefault(n => n.Id == data.GetProperty("id").GetString()) ?? throw new UserError("Сервер уже удалён.");
                    ping.Start([node], profile.Ping); break;
                case "cancelPing": lastPing = DateTimeOffset.UtcNow; ping.Cancel(); break;
                case "readHwid":
                    var device = DeviceIdentity.Get();
                    Reply(new { kind = "reply", id, ok = true, data = new { hwid = device.Hwid, name = device.Model } });
                    return;
                case "copyHwid":
                    Clipboard.SetText(DeviceIdentity.Get().Hwid); break;
                case "copyLog":
                    Clipboard.SetText(FullLog()); break;
                case "copyDiagnostics": Clipboard.SetText(JsonSerializer.Serialize(ToolsModel(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true })); break;
                case "diagnostics":
                    Clipboard.SetText(JsonSerializer.Serialize(new { app = "kot. " + ClientIdentity.Version, core = "sing-box 1.14.2", os = Environment.OSVersion.VersionString, hwidEnabled = profile.SendHwid, state = tunnel.State, protocol = profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Protocol, nodeCount = profile.Nodes.Count, updated = profile.Updated, lastError = tunnel.Error, coreExitCode = tunnel.CoreExitCode, coreLog = tunnel.CoreDetails, skipped = warnings.Count }, new JsonSerializerOptions { WriteIndented = true })); break;
                default:
                    await operations.WaitAsync(lifetime.Token);
                    try { if (!exiting) await Mutate(action, data); } finally { operations.Release(); }
                    break;
            }
            Snapshot(); Reply(new { kind = "reply", id, ok = true });
        }
        catch (Exception ex) { AppLog.Error("UI request", ex); Reply(new { kind = "reply", id, ok = false, error = Store.Friendly(ex) }); Snapshot(); }
    }
    void SaveProfile(Action<Profile> change) => profile = Store.Change(profile, change);
    async Task Mutate(string action, JsonElement data)
    {
        switch (action)
        {
            case "routingSelect": case "routingSave": case "routingImport": case "routingRefresh": case "routingRemove": case "routingExport":
                await MutateRouting(action, data); break;
            case "geoIpRefresh": case "geoIpSettings":
                await MutateGeoIp(action, data); break;
            case "import": case "refresh":
                if (action == "refresh" && profile.Local) throw new UserError("Локальный профиль обновляется повторным импортом файла.");
                string address = action == "import" ? data.GetProperty("url").GetString()?.Trim() ?? "" : profile.Address;
                string name = action == "import" ? data.GetProperty("name").GetString()?.Trim() ?? "Подписка" : profile.Name;
                AppLog.Write("subscription", action + ": starting, hwid=" + profile.SendHwid);
                if (name.Length > 60) throw new UserError("Название слишком длинное.");
                SubscriptionResult result;
                using (var fetchCancellation = new CancellationTokenSource())
                {
                    importAttempt = fetchCancellation;
                    try { var device = profile.SendHwid ? DeviceIdentity.Get() : null; result = await Subscriptions.Download(address, fetchCancellation.Token, device: device, proxyPort: RequestProxyPort); }
                    finally { if (importAttempt == fetchCancellation) importAttempt = null; }
                }
                if (exiting) return;
                await ApplySubscription(address, name, result, action == "import"); break;
            case "importText": await ApplyLocalImport(data.GetProperty("text").GetString() ?? "", data.GetProperty("name").GetString()?.Trim() ?? ""); break;
            case "importServersFile": await ImportServersFile(data.GetProperty("name").GetString()?.Trim() ?? ""); break;
            case "pingSettings":
                var options = JsonSerializer.Deserialize<PingOptions>(data.GetRawText(), Json) ?? throw new UserError("Не переданы настройки пинга.");
                options.Validate(); SaveProfile(p => p.Ping = options); await ping.Stop(clear: true); AppLog.Write("ping", "settings saved"); break;
            case "select":
                string id = data.GetProperty("id").GetString() ?? "";
                if (id != "auto" && !profile.Nodes.Any(n => n.Id == id)) throw new UserError("Сервер уже удалён. Обновите подписку.");
                if (id != profile.Selected) { await StopTools(); await tunnel.Stop(); try { SaveProfile(p => p.Selected = id); } finally { ResumeDesired(); } }
                break;
            case "settings": await ChangeSetting(data); break;
            case "switchSubscription":
                string subscriptionId = data.GetProperty("id").GetString() ?? "";
                if (profile.Subscriptions.All(s => s.Id != subscriptionId)) throw new UserError("Подписка уже удалена.");
                if (subscriptionId != profile.ActiveSubscription)
                {
                    await StopTools(); await ping.Stop(clear: true); await tunnel.Stop();
                    try { SaveProfile(p => p.Activate(subscriptionId)); warnings = []; AppLog.Register(profile); }
                    finally { ResumeDesired(); }
                }
                break;
            case "favorite":
                string favorite = data.GetProperty("id").GetString() ?? "";
                if (profile.Nodes.All(n => n.Id != favorite)) throw new UserError("Сервер уже удалён.");
                SaveProfile(p => { if (!p.Favorites.Remove(favorite)) p.Favorites.Add(favorite); }); if (profile.Selected == "auto" && reconnect.Desired) { await tunnel.Stop(); ResumeDesired(); } break;
            case "automationSettings":
                var automation = JsonSerializer.Deserialize<AutomationOptions>(data.GetRawText(), Json) ?? throw new UserError("Не переданы настройки.");
                automation.Validate(); bool autoRestart = profile.Selected == "auto" && (automation.AutoMinutes != profile.Automation.AutoMinutes || automation.AutoToleranceMs != profile.Automation.AutoToleranceMs);
                SaveProfile(p => p.Automation = automation); if (autoRestart) { await tunnel.Stop(); ResumeDesired(); } break;
            case "backupExport": await ExportBackup(data.GetProperty("password").GetString() ?? ""); break;
            case "backupImport": await ImportBackup(data.GetProperty("password").GetString() ?? ""); break;
            case "downloadUpdate": await DownloadUpdate(data.GetProperty("version").GetString() ?? ""); break;
            case "deferUpdate": DeferUpdate(data.GetProperty("version").GetString() ?? ""); break;
            case "remove":
                string removedSubscription = data.GetProperty("id").GetString() ?? "";
                if (removedSubscription.Length == 0 || removedSubscription != profile.ActiveSubscription || profile.Subscriptions.All(s => s.Id != removedSubscription))
                    throw new UserError("Подписка уже удалена или выбрана другая подписка.");
                await StopTools(); await ping.Stop(clear: true); await tunnel.Stop();
                try
                {
                    SaveProfile(p =>
                    {
                        p.Subscriptions.RemoveAll(s => s.Id == removedSubscription);
                        p.ActiveSubscription = ""; p.Address = ""; p.Local = false; p.SubscriptionUsage = null; p.Nodes = []; p.Selected = ""; p.Name = "Подписка"; p.Updated = null; p.Favorites = [];
                        if (p.Subscriptions.Count > 0) p.Activate(p.Subscriptions[0].Id);
                    });
                    warnings = []; if (profile.Subscriptions.Count == 0) reconnect.Stop();
                    refreshAttempts.Remove(removedSubscription); AppLog.Register(profile);
                }
                finally { ResumeDesired(); }
                break;
            default: throw new UserError("Неизвестная команда.");
        }
    }
    string FullLog()
    {
        var header = new
        {
            app = "kot. " + ClientIdentity.Version, core = "sing-box 1.14.2", os = Environment.OSVersion.VersionString,
            hwidEnabled = profile.SendHwid, state = tunnel.State,
            protocol = profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Protocol,
            nodeCount = profile.Nodes.Count, updated = profile.Updated, lastError = tunnel.Error,
            coreExitCode = tunnel.CoreExitCode, coreLog = tunnel.CoreDetails, skipped = warnings.Count,
            traffic, telemetryError, subscriptions = profile.Subscriptions.Count, automatic = profile.Selected == "auto", automation = profile.Automation, backgroundError, reconnectDesired = reconnect.Desired, retryFailures = reconnect.Failures,
            ping = new { busy = ping.Busy, done = ping.Done, total = ping.Total, mode = profile.Ping.Mode, timeoutMs = profile.Ping.TimeoutMs, attempts = profile.Ping.Attempts, parallelism = profile.Ping.Parallelism }
        };
        AppLog.Write("app", "log copied");
        return AppLog.Clean("kot. - полный журнал текущего запуска\n" +
            JsonSerializer.Serialize(header, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) +
            "\n\n" + AppLog.Snapshot());
    }
    async Task ExitApp()
    {
        if (exiting) return;
        exiting = true; browserWatch.Stop(); browserWatch.Dispose(); DisposeAutomation(); importAttempt?.Cancel(); tunnel.Cancel(); Enabled = false;
        try
        {
            try { await services.StopAll(exit: true); }
            finally { await StopTools(); await ping.Stop(); await tunnel.Stop(); if (!preserveKillSwitch) KillSwitch.Release(); }
        }
        finally { allowClose = true; Close(); }
    }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
