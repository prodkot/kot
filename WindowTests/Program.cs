using Kot.Windows;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;

internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    static void Check(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS " + message); }
    [STAThread] static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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
