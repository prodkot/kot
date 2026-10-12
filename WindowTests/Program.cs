using Kot.Windows;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;

internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("iphlpapi.dll")] static extern uint GetBestInterface(uint address, out uint index);
    [DllImport("iphlpapi.dll")] static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
    static void Check(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static bool Connect()
        {
            using var socket = new System.Net.Sockets.TcpClient();
            try { socket.ConnectAsync("1.1.1.1", 443).WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult(); return true; }
            catch { return false; }
        }
    static void KillSwitchChecks()
    {
        Check(Connect(), "WFP test baseline: direct TCP is reachable");
        try
        {
            KillSwitch.ArmForTests(Environment.ProcessPath!, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
            Check(KillSwitch.Detect(), "persistent filters remain after the WFP engine handle closes");
            Check(!Connect(), "native WFP blocks direct TCP for the scoped fixture");
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            using var loopback = new System.Net.Sockets.TcpClient();
            loopback.Connect(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            Check(loopback.Connected, "kill switch preserves loopback access to the core API and proxy");
            Check(GetBestInterface(0x01010101, out var index) == 0 && ConvertInterfaceIndexToLuid(index, out var route) == 0, "resolve the real outgoing network interface");
            ConvertInterfaceIndexToLuid(index, out var allowedInterface);
            KillSwitch.ArmForTests(Environment.ProcessPath!, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), allowedInterface);
            Check(Connect(), "WFP permits traffic routed through the nominated interface");
            KillSwitch.ArmForTests(Environment.ProcessPath!, Environment.ProcessPath!);
            Check(Connect(), "higher priority core permission allows the nominated executable");
        }
        finally { KillSwitch.Release(); }
        Check(!KillSwitch.Detect() && Connect(), "removing only kot filters restores direct access");
    }
    static void TunnelChecks(string exe)
    {
        string config = Path.Combine(Path.GetTempPath(), "kot-wfp-tun-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(config, """
        {"log":{"level":"info"},"inbounds":[{"type":"tun","tag":"tun-in","interface_name":"kot-tun","address":["172.28.231.1/30","fdfe:dcba:231::1/126"],"mtu":1500,"auto_route":true,"strict_route":true,"dns_mode":"hijack"}],"outbounds":[{"type":"direct","tag":"direct"}],"route":{"auto_detect_interface":true,"final":"direct"}}
        """);
        try
        {
            KillSwitch.ArmForTests(Environment.ProcessPath!, exe);
            using var core = new NativeCore(exe, config, Console.WriteLine);
            ulong luid = 0; var timer = System.Diagnostics.Stopwatch.StartNew();
            while (luid == 0 && timer.Elapsed.TotalSeconds < 15 && !core.HasExited)
            {
                try { luid = KillSwitch.TunnelInterface(); } catch (Kot.Core.UserError) { Thread.Sleep(200); }
            }
            Check(luid != 0 && !core.HasExited, "bundled sing-box creates the real Windows TUN interface");
            KillSwitch.ArmForTests(Environment.ProcessPath!, exe, luid);
            // The adapter exists before Windows has finished installing its routes.
            // Keep the guard armed, and only try the protected connection on the TUN route.
            timer.Restart(); bool routed = false, connected = false;
            while (timer.Elapsed.TotalSeconds < 15 && !core.HasExited)
            {
                if (GetBestInterface(0x01010101, out var index) == 0 && ConvertInterfaceIndexToLuid(index, out var route) == 0 && route == luid)
                {
                    routed = true; if (Connect()) { connected = true; break; }
                }
                Thread.Sleep(200);
            }
            Check(routed, "Windows installs the outgoing route through the real TUN");
            Check(connected, "protected fixture connects through the real TUN and sing-box");
            core.Process.Kill(); core.WaitForExitAsync().Wait(TimeSpan.FromSeconds(10));
            Check(KillSwitch.Detect() && !Connect(), "abrupt sing-box termination leaves direct TCP blocked");
        }
        finally { KillSwitch.Release(); File.Delete(config); }
        Check(Connect(), "direct access is restored after intentional release following a core crash");
    }
    static void UpdateCleanupChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "kot-cleanup-" + Guid.NewGuid().ToString("N"));
        string work = Path.Combine(root, ".kot-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string old = Path.Combine(root, "README.md"), helper = Path.Combine(work, "Updater.exe");
        File.WriteAllText(old, "old guide"); File.WriteAllText(helper, "helper");
        using (var locked = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var cleanup = Task.Run(() => UpdateInstaller.CleanupAfterUpdate(work, root));
            Thread.Sleep(500);
            Check(File.Exists(old) && Directory.Exists(work), "new app preserves legacy files and recovery folder before updater commit");
            File.WriteAllText(Path.Combine(work, "committed"), "ok");
            Check(SpinWait.SpinUntil(() => !File.Exists(old), 5000), "new app removes old docs after an older updater commits");
            Check(File.Exists(Path.Combine(work, "committed")) && File.Exists(helper), "locked helper preserves the committed marker for a later cleanup");
            locked.Dispose();
            Check(cleanup.Wait(10000) && !Directory.Exists(work), "completed update folder is removed after the helper releases its executable");
        }
        Directory.CreateDirectory(work); File.WriteAllText(old, "old guide");
        File.WriteAllText(Path.Combine(work, "rolled-back"), "ok");
        UpdateInstaller.CleanupAfterUpdate(work, root);
        Check(File.ReadAllText(old) == "old guide" && Directory.Exists(work), "rolled-back update does not trigger legacy-file deletion");
        Directory.Delete(root, true);
    }
    [STAThread] static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 1 && args[0] == "--services-test") { Kot.WindowTests.ManagedServiceChecks.Run(Check).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--tunnel-test") { var core = Path.GetFullPath(args[1]); Kot.WindowTests.ProbeTests.Run(core, Check).GetAwaiter().GetResult(); TunnelChecks(core); return 0; }
        if (args.Length == 4 && args[0] == "--proxy-watch") { SystemProxy.Watch(int.Parse(args[1]), long.Parse(args[2]), args[3]); return 0; }
        if (args.Length == 3 && args[0] == "--proxy-owner") { SystemProxy.Enable(int.Parse(args[1])); File.WriteAllText(args[2], "ready"); Thread.Sleep(60000); return 0; }
        Kot.WindowTests.ProfileUpgradeChecks.Run(Check);
        Kot.WindowTests.ShellTests.Run(Check);
        Kot.WindowTests.WebViewTests.Run(Check);
        ProxyFixture.Run(Check);
        Kot.WindowTests.QrTests.Run(Check);
        UpdateCleanupChecks();
        StartupFixture.Run(Check);
        KillSwitchChecks();
        using var window = new WindowChrome { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = true, Size = new Size(600, 400) };
        window.Show(); Application.DoEvents();
        Check((GetWindowLong(window.Handle, -16) & 0xb0000) == 0xb0000, "shell system, minimize and maximize styles are present");
        SendMessage(window.Handle, 0x112, (IntPtr)0xf020, IntPtr.Zero); Application.DoEvents();
        Check(window.Visible && window.ShowInTaskbar && window.WindowState == FormWindowState.Minimized, "shell minimize keeps the window on the taskbar");
        window.RestoreWindow(); Check(window.WindowState == FormWindowState.Normal && window.Visible, "tray activation restores a minimized normal window");
        window.ToggleMaximize(); window.MinimizeWindow(); window.RestoreWindow();
        Check(window.WindowState == FormWindowState.Maximized, "restoring remembers a maximized window");
        window.Hide(); window.RestoreWindow(); Check(window.Visible, "tray activation shows a hidden window");
        window.ToggleMaximize();
        int opened = 0, toggled = 0; string page = "";
        using var menu = new TrayMenu(() => opened++, p => page = p, () => { toggled++; return Task.CompletedTask; }, () => Task.CompletedTask);
        menu.Update("dark", "idle", "", false);
        var power = menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "power");
        Check(!power.Enabled, "connect disabled without a subscription");
        menu.Update("dark", "connected", "Test & node\nwith control", true);
        Check(menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "status").Text.Contains("Test & node") && !menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "status").Text.Contains("\n"), "server label preserves ampersands and removes control characters");
        Check(power.Enabled && power.Text == "Отключить", "tray connection action follows state");
        power.PerformClick(); Check(toggled == 1, "tray action executes once");
        menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "open").PerformClick(); Check(opened == 1, "open action executes once");
        menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "logs").PerformClick(); Check(page == "logs", "tray opens the logs page");
        menu.Update("dark", "waiting", "Авто", true); Check(power.Text == "Отменить подключение", "reconnect can be cancelled from tray");
        string folder = args.Length > 0 ? args[0] : "artifacts/window-ui"; Directory.CreateDirectory(folder);
        foreach (string theme in new[] { "dark", "light" })
        {
            menu.Update(theme, "connected", "Германия · Frankfurt", true);
            menu.Show(window, new Point(20, 20)); Application.DoEvents();
            Check(menu.Region != null, "rounded native menu " + theme);
            Check(menu.Width == menu.Height && Math.Abs(menu.Width - 400 * menu.DeviceDpi / 96f) <= 1, "square tray menu scales with Windows DPI " + theme);
            var rows = menu.Items.Cast<ToolStripItem>().ToArray();
            var commands = rows.OfType<ToolStripMenuItem>().ToArray();
            Console.WriteLine("Tray geometry " + theme + ": " + string.Join("; ", rows.Select(i => $"{i.Tag ?? "separator"} {i.Bounds}")));
            Check(commands.Where(i => i.Enabled).All(i => i.Bounds.Width == menu.ClientSize.Width - menu.Padding.Horizontal && Math.Abs(i.Height - 40 * menu.DeviceDpi / 96f) <= 1), "tray commands have equal full-width hit areas " + theme);
            Check(commands.All(i => i.Bounds.Left >= menu.Padding.Left && i.Bounds.Right <= menu.ClientSize.Width - menu.Padding.Right && i.Bounds.Bottom <= menu.ClientSize.Height - menu.Padding.Bottom), "all tray commands fit within the common padding " + theme);
            using var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(folder, "tray-" + theme + ".png"), ImageFormat.Png);
            menu.Close();
            using var sourceIcon = new Icon(Path.Combine(AppContext.BaseDirectory, "kot.ico"));
            int minimized = 0, maximized = 0, closed = 0;
            using var loading = new StartupScreen(theme, sourceIcon, () => minimized++, () => maximized++, () => closed++, () => { });
            window.ClientSize = new Size(800, 600); window.Controls.Add(loading); Application.DoEvents();
            var buttons = new[] { "minimize", "maximize", "close" }.Select(name => (Button)loading.Controls.Find(name, true).Single()).ToArray();
            var title = buttons[0].Parent!;
            var version = title.Controls.OfType<Label>().Single();
            Check(version.Right <= buttons[0].Left && buttons.Zip(buttons.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left) && buttons.All(button => button.Visible && button.Width > 0 && button.Bottom <= title.ClientSize.Height && button.Right <= title.ClientSize.Width), "loading title and window buttons have separate visible bounds " + theme);
            foreach (var button in buttons) button.PerformClick();
            Check(minimized == 1 && maximized == 1 && closed == 1, "loading screen retains all window actions " + theme);
            using var startupBitmap = new Bitmap(loading.Width, loading.Height);
            loading.DrawToBitmap(startupBitmap, new Rectangle(Point.Empty, startupBitmap.Size));
            startupBitmap.Save(Path.Combine(folder, "loading-" + theme + ".png"), ImageFormat.Png);
            window.Controls.Remove(loading);
        }
        window.Hide(); Console.WriteLine("Windows menu and shell checks passed."); return 0;
    }
}
