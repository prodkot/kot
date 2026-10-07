using Kot.Windows;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;

internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    static void Check(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void KillSwitchChecks()
    {
        bool Connect()
        {
            using var socket = new System.Net.Sockets.TcpClient();
            try { socket.ConnectAsync("1.1.1.1", 443).WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult(); return true; }
            catch { return false; }
        }
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
            KillSwitch.ArmForTests(Environment.ProcessPath!, Environment.ProcessPath!);
            Check(Connect(), "higher priority core permission allows the nominated executable");
        }
        finally { KillSwitch.Release(); }
        Check(!KillSwitch.Detect() && Connect(), "removing only kot filters restores direct access");
    }
    [STAThread] static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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
        menu.Update("dark", "lime", "idle", "", false);
        var power = menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "power");
        Check(!power.Enabled, "connect disabled without a subscription");
        menu.Update("dark", "lime", "connected", "Test & node\nwith control", true);
        Check(menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "status").Text.Contains("Test & node") && !menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "status").Text.Contains("\n"), "server label preserves ampersands and removes control characters");
        Check(power.Enabled && power.Text == "Отключить", "tray connection action follows state");
        power.PerformClick(); Check(toggled == 1, "tray action executes once");
        menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "open").PerformClick(); Check(opened == 1, "open action executes once");
        menu.Items.Cast<ToolStripItem>().Single(i => (string?)i.Tag == "logs").PerformClick(); Check(page == "logs", "tray opens the logs page");
        menu.Update("dark", "lime", "waiting", "Авто", true); Check(power.Text == "Отменить подключение", "reconnect can be cancelled from tray");
        string folder = args.Length > 0 ? args[0] : "artifacts/window-ui"; Directory.CreateDirectory(folder);
        foreach (string theme in new[] { "dark", "light" })
        {
            menu.Update(theme, "lime", "connected", "Германия · Frankfurt", true);
            menu.Show(window, new Point(20, 20)); Application.DoEvents();
            Check(menu.Region != null, "rounded native menu " + theme);
            using var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(folder, "tray-" + theme + ".png"), ImageFormat.Png);
            menu.Close();
        }
        window.Hide(); Console.WriteLine("Windows menu and shell checks passed."); return 0;
    }
}
