using Kot.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Kot.Windows;
public sealed partial class MainWindow : WindowChrome
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(21, 23, 24) };
    readonly NotifyIcon tray;
    readonly TrayMenu trayMenu;
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
    public MainWindow(bool startup, bool resumeConnection = false)
    {
        this.startup = startup; this.resumeConnection = resumeConnection; profile = Store.Load();
        if (!profile.KillSwitch) KillSwitch.Release(); else KillSwitch.Detect();
        AppLog.Register(profile); AppLog.Write("app", "kot. " + ClientIdentity.Version + " starting; OS=" + Environment.OSVersion + "; core=sing-box 1.14.2");
        try { profile.Ping.Validate(); } catch { profile.Ping = new(); }
        try { profile.Automation.Validate(); } catch { profile.Automation = new(); }
        InitAutomation(); InitTelemetry();
        foreach (string orphan in Directory.GetFiles(Store.Folder, "ping-*.json")) { try { File.Delete(orphan); } catch (Exception ex) { AppLog.Error("orphan ping cleanup", ex); } }
        Text = "kot."; FormBorderStyle = FormBorderStyle.None; MinimumSize = new Size(800, 600); ClientSize = new Size(960, 640); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(21, 23, 24); Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "kot.ico"));
        Controls.Add(web);
        trayMenu = new TrayMenu(Restore, OpenTrayPage, ToggleConnection, ExitApp);
        trayMenu.Opening += (_, _) => UpdateTray();
        tray = new NotifyIcon { Icon = Icon, Text = "kot. · Не подключено", Visible = true, ContextMenuStrip = trayMenu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Restore(); };
        tunnel.Changed += () => { if (!IsDisposed && !exiting && IsHandleCreated) BeginInvoke(() => { tray.Text = tunnel.State == "connected" ? "kot. · Подключено" : "kot. · Не подключено"; Snapshot(); }); };
        ping.Changed += () => { if (!IsDisposed && !exiting && IsHandleCreated) BeginInvoke(Snapshot); };
        Shown += async (_, _) => await Initialize();
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
        if (page is not ("home" or "servers" or "logs" or "settings")) return;
        Restore();
        if (bridgeReady) Send(new { kind = "navigate", page }); else pendingTrayPage = page;
    }
    void UpdateTray()
    {
        string state = reconnect.Desired && tunnel.State == "idle" ? "waiting" : tunnel.State;
        string name = profile.Selected == "auto" ? "Авто" : profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Name ?? "";
        trayMenu.Update(profile.Theme, profile.Accent, state, name, profile.Nodes.Count > 0);
        tray.Text = state switch { "connected" => "kot. · Подключено", "connecting" => "kot. · Подключение", "waiting" => "kot. · Ожидание сети", _ => "kot. · Не подключено" };
    }
    async Task Initialize()
    {
        try
        {
            CoreWebView2Environment env;
            try { env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Folder, "WebView")); }
            catch (WebView2RuntimeNotFoundException)
            {
                string installer = Path.Combine(AppContext.BaseDirectory, "Install-WebView2.exe");
                if (!File.Exists(installer)) throw new UserError("Не найден WebView2 Runtime и установщик Install-WebView2.exe.");
                MessageBox.Show("Для интерфейса нужен Microsoft WebView2. Сейчас откроется официальный установщик.", "kot.");
                var installInfo = new ProcessStartInfo(installer) { UseShellExecute = true }; installInfo.ArgumentList.Add("/install");
                using var install = Process.Start(installInfo); if (install != null) await install.WaitForExitAsync();
                env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Folder, "WebView"));
            }
            await web.EnsureCoreWebView2Async(env);
            var c = web.CoreWebView2;
            c.Settings.AreDefaultContextMenusEnabled = false; c.Settings.AreDevToolsEnabled = false; c.Settings.IsStatusBarEnabled = false; c.Settings.IsZoomControlEnabled = false;
            c.Settings.IsGeneralAutofillEnabled = false; c.Settings.IsPasswordAutosaveEnabled = false;
            c.Settings.AreHostObjectsAllowed = false; c.Settings.AreDefaultScriptDialogsEnabled = false;
            c.SetVirtualHostNameToFolderMapping("kot.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            c.NavigationStarting += (_, e) => { if (e.Uri != "https://kot.local/index.html") e.Cancel = true; };
            c.FrameNavigationStarting += (_, e) => e.Cancel = true;
            c.NewWindowRequested += (_, e) => e.Handled = true;
            c.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            c.DownloadStarting += (_, e) => e.Cancel = true;
            c.WebMessageReceived += async (_, e) => await Message(e);
            c.Navigate("https://kot.local/index.html"); initialized = true;
            if (startup && profile.Tray) Hide();
        }
        catch (Exception ex) { AppLog.Error("initialize", ex); MessageBox.Show(Store.Friendly(ex), "kot.", MessageBoxButtons.OK, MessageBoxIcon.Error); await ExitApp(); }
    }
    void Send(object value) { if (!exiting && bridgeReady && !web.IsDisposed && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, Json)); }
    object Model() => new
    {
        name = profile.Name, hasSubscription = profile.Address.Length > 0,
        nodes = profile.Nodes.Select(n => new { n.Id, n.Name, n.Protocol, ping = ping.Get(n.Id), code = n.Protocol == "shadowsocks" ? "SS" : n.Protocol == "hysteria2" ? "HY" : n.Protocol == "trojan" ? "TR" : n.Protocol == "vmess" ? "VM" : "VL" }),
        subscriptions = profile.Subscriptions.Select(s => new { s.Id, s.Name, count = s.Nodes.Count }), activeSubscription = profile.ActiveSubscription,
        telemetry = TelemetryModel(), journal = visiblePage == "logs" ? AppLog.Tail() : null, updates = UpdateModel(),
        favorites = profile.Favorites, automaticNode = tunnel.AutomaticNode, automation = profile.Automation, backgroundError,
        selected = profile.Selected, state = tunnel.State == "idle" && reconnect.Desired ? "waiting" : tunnel.State, error = tunnel.Error.Length > 0 ? tunnel.Error : connectionNotice,
        settings = new { profile.Theme, profile.Accent, mode = profile.ConnectionMode, profile.Startup, profile.AutoConnect, profile.Tray, profile.SendHwid, profile.KillSwitch, killSwitchActive = KillSwitch.Active, bypass = string.Join('\n', profile.Bypass) },
        ping = new { busy = ping.Busy, done = ping.Done, total = ping.Total, error = ping.Error, settings = profile.Ping },
        updated = profile.Updated?.ToLocalTime().ToString("dd.MM HH:mm"), warnings, version = ClientIdentity.Version
    };
    void Snapshot() { UpdateTray(); Send(new { kind = "snapshot", data = Model() }); }
    async Task Message(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (exiting || e.Source != "https://kot.local/index.html") return;
        int id = 0;
        try
        {
            string raw = e.WebMessageAsJson;
            if (raw.Length > 100_000) return;
            using var d = JsonDocument.Parse(raw); var root = d.RootElement;
            id = root.GetProperty("id").GetInt32(); string action = root.GetProperty("action").GetString() ?? "";
            JsonElement data = root.GetProperty("data");
            if (action == "ready")
            {
                bool first = !bridgeReady; bridgeReady = true; Snapshot(); Send(new { kind = "reply", id, ok = true });
                if (pendingTrayPage is { } page) { Send(new { kind = "navigate", page }); pendingTrayPage = null; }
                if (first) UpdateInstaller.Ready();
                if (first && (profile.AutoConnect || resumeConnection) && profile.Nodes.Count > 0) { reconnect.Start(DateTimeOffset.UtcNow); StartConnection(); }
                return;
            }
            if (!bridgeReady) return;
            switch (action)
            {
                case "drag": if (data.GetProperty("double").GetBoolean()) ToggleMaximize(); else { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } break;
                case "resize":
                    var hit = data.GetProperty("edge").GetString() switch { "w" => 10, "e" => 11, "n" => 12, "nw" => 13, "ne" => 14, "s" => 15, "sw" => 16, "se" => 17, _ => 0 };
                    if (hit != 0 && WindowState == FormWindowState.Normal) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)hit, IntPtr.Zero); } break;
                case "minimize": MinimizeWindow(); break;
                case "maximize": ToggleMaximize(); break;
                case "close": Close(); break;
                case "toggle": _ = ToggleConnection(); break;
                case "view":
                    string page = data.GetProperty("page").GetString() ?? ""; if (page is not ("home" or "servers" or "logs" or "settings")) throw new UserError("Неизвестная страница."); visiblePage = page; break;
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
                    Send(new { kind = "reply", id, ok = true, data = new { hwid = device.Hwid, name = device.Model } });
                    return;
                case "copyHwid":
                    Clipboard.SetText(DeviceIdentity.Get().Hwid); break;
                case "copyLog":
                    Clipboard.SetText(FullLog()); break;
                case "diagnostics":
                    Clipboard.SetText(JsonSerializer.Serialize(new { app = "kot. " + ClientIdentity.Version, core = "sing-box 1.14.2", os = Environment.OSVersion.VersionString, hwidEnabled = profile.SendHwid, state = tunnel.State, protocol = profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Protocol, nodeCount = profile.Nodes.Count, updated = profile.Updated, lastError = tunnel.Error, coreExitCode = tunnel.CoreExitCode, coreLog = tunnel.CoreDetails, skipped = warnings.Count }, new JsonSerializerOptions { WriteIndented = true })); break;
                default:
                    await operations.WaitAsync(lifetime.Token);
                    try { if (!exiting) await Mutate(action, data); } finally { operations.Release(); }
                    break;
            }
            Snapshot(); Send(new { kind = "reply", id, ok = true });
        }
        catch (Exception ex) { AppLog.Error("UI request", ex); Send(new { kind = "reply", id, ok = false, error = Store.Friendly(ex) }); Snapshot(); }
    }
    async Task Mutate(string action, JsonElement data)
    {
        switch (action)
        {
            case "import": case "refresh":
                string address = action == "import" ? data.GetProperty("url").GetString()?.Trim() ?? "" : profile.Address;
                string name = action == "import" ? data.GetProperty("name").GetString()?.Trim() ?? "Подписка" : profile.Name;
                AppLog.Secret(address); AppLog.Write("subscription", action + ": starting, hwid=" + profile.SendHwid);
                if (name.Length > 60) throw new UserError("Название слишком длинное.");
                SubscriptionResult result;
                using (var fetchCancellation = new CancellationTokenSource())
                {
                    importAttempt = fetchCancellation;
                    try { var device = profile.SendHwid ? DeviceIdentity.Get() : null; if (device != null) AppLog.Secret(device.Hwid); result = await Subscriptions.Download(address, fetchCancellation.Token, device: device); }
                    finally { if (importAttempt == fetchCancellation) importAttempt = null; }
                }
                if (exiting) return;
                await ApplySubscription(address, name, result, action == "import"); break;
            case "pingSettings":
                var options = JsonSerializer.Deserialize<PingOptions>(data.GetRawText(), Json) ?? throw new UserError("Не переданы настройки пинга.");
                options.Validate(); await ping.Stop(clear: true); profile.Ping = options; Store.Save(profile); AppLog.Write("ping", "settings saved"); break;
            case "select":
                string id = data.GetProperty("id").GetString() ?? "";
                if (id != "auto" && !profile.Nodes.Any(n => n.Id == id)) throw new UserError("Сервер уже удалён. Обновите подписку.");
                if (id != profile.Selected) { await tunnel.Stop(); profile.Selected = id; Store.Save(profile); ResumeDesired(); }
                break;
            case "settings":
                string key = data.GetProperty("key").GetString() ?? ""; var value = data.GetProperty("value");
                bool restart = false;
                switch (key)
                {
                    case "theme": string theme = value.GetString()!; if (theme is not ("dark" or "light")) throw new UserError("Неизвестная тема."); profile.Theme = theme; break;
                    case "accent": string accent = value.GetString()!; if (accent is not ("lime" or "gray" or "green" or "purple")) throw new UserError("Неизвестный акцент."); profile.Accent = accent; break;
                    case "mode": string mode = value.GetString()!; if (mode is not ("tun" or "proxy")) throw new UserError("Неизвестный режим."); restart = mode != profile.ConnectionMode; if (restart) await tunnel.Stop(); profile.ConnectionMode = mode; break;
                    case "bypass": var domains = Configuration.Domains(value.GetString() ?? ""); restart = !domains.SequenceEqual(profile.Bypass) || profile.Mode != "smart"; if (restart) await tunnel.Stop(); profile.Bypass = domains; profile.Mode = "smart"; break;
                    case "startup": bool enabled = value.GetBoolean(); if (enabled != profile.Startup) await Startup.Set(enabled); profile.Startup = enabled; break;
                    case "autoConnect": profile.AutoConnect = value.GetBoolean(); break;
                    case "tray": profile.Tray = value.GetBoolean(); break;
                    case "killSwitch":
                        bool protect = value.GetBoolean();
                        if (protect && tunnel.State != "idle") KillSwitch.Arm(tunnel.State == "connected" && profile.ConnectionMode == "tun" ? KillSwitch.TunnelInterface() : 0);
                        if (!protect) KillSwitch.Release();
                        profile.KillSwitch = protect; break;
                    case "sendHwid": profile.SendHwid = value.GetBoolean(); break;
                    default: throw new UserError("Неизвестная настройка.");
                }
                Store.Save(profile); if (restart) ResumeDesired(); break;
            case "switchSubscription":
                string subscriptionId = data.GetProperty("id").GetString() ?? "";
                if (profile.Subscriptions.All(s => s.Id != subscriptionId)) throw new UserError("Подписка уже удалена.");
                if (subscriptionId != profile.ActiveSubscription) { await ping.Stop(clear: true); await tunnel.Stop(); profile.CaptureActive(); profile.Activate(subscriptionId); warnings = []; Store.Save(profile); AppLog.Register(profile); ResumeDesired(); }
                break;
            case "favorite":
                string favorite = data.GetProperty("id").GetString() ?? "";
                if (profile.Nodes.All(n => n.Id != favorite)) throw new UserError("Сервер уже удалён.");
                if (!profile.Favorites.Remove(favorite)) profile.Favorites.Add(favorite);
                Store.Save(profile); if (profile.Selected == "auto" && reconnect.Desired) { await tunnel.Stop(); ResumeDesired(); } break;
            case "automationSettings":
                var automation = JsonSerializer.Deserialize<AutomationOptions>(data.GetRawText(), Json) ?? throw new UserError("Не переданы настройки.");
                automation.Validate(); bool autoRestart = profile.Selected == "auto" && (automation.AutoMinutes != profile.Automation.AutoMinutes || automation.AutoToleranceMs != profile.Automation.AutoToleranceMs);
                profile.Automation = automation; Store.Save(profile); if (autoRestart) { await tunnel.Stop(); ResumeDesired(); } break;
            case "backupExport": await ExportBackup(data.GetProperty("password").GetString() ?? ""); break;
            case "backupImport": await ImportBackup(data.GetProperty("password").GetString() ?? ""); break;
            case "downloadUpdate": await DownloadUpdate(data.GetProperty("version").GetString() ?? ""); break;
            case "deferUpdate": DeferUpdate(data.GetProperty("version").GetString() ?? ""); break;
            case "remove":
                await ping.Stop(clear: true); await tunnel.Stop();
                profile.Subscriptions.RemoveAll(s => s.Id == profile.ActiveSubscription);
                profile.ActiveSubscription = ""; profile.Address = ""; profile.Nodes = []; profile.Selected = ""; profile.Name = "Подписка"; profile.Updated = null; profile.Favorites = []; warnings = [];
                if (profile.Subscriptions.Count > 0) profile.Activate(profile.Subscriptions[0].Id); else reconnect.Stop();
                Store.Save(profile); ResumeDesired(); break;
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
        exiting = true; DisposeAutomation(); importAttempt?.Cancel(); tunnel.Cancel(); Enabled = false;
        try { await ping.Stop(); await tunnel.Stop(); if (!preserveKillSwitch) KillSwitch.Release(); }
        finally { tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); allowClose = true; Close(); }
    }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
