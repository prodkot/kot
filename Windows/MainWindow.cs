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
    public MainWindow(bool startup, bool resumeConnection = false, Profile? initialProfile = null)
    {
        this.startup = startup; this.resumeConnection = resumeConnection; profile = initialProfile ?? Store.Load();
        if (!profile.KillSwitch) KillSwitch.Release(); else KillSwitch.Detect();
        AppLog.Register(profile); AppLog.Write("app", "kot. " + ClientIdentity.DisplayVersion + " starting; OS=" + Environment.OSVersion + "; core=" + XrayRuntime.Description);
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
                await Startup.RepairEnabled();
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
    async Task ExitApp()
    {
        if (exiting) return;
        exiting = true; browserWatch.Stop(); browserWatch.Dispose(); DisposeAutomation(); importAttempt?.Cancel(); tunnel.Cancel(); Enabled = false;
        try
        {
            try { await Task.WhenAll(services.StopAll(exit: true), connectionAttempt); }
            finally { await StopTools(); await ping.Stop(); await tunnel.Stop(); if (!preserveKillSwitch) KillSwitch.Release(); }
        }
        finally { allowClose = true; Close(); }
    }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
