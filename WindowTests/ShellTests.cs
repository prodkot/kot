using Kot.Windows;
using System.Runtime.InteropServices;
namespace Kot.WindowTests;

static class ShellTests
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, IntPtr value);

    static void ThemeChecks(Action<bool, string> check)
    {
        using var icons = new ThemeIcons();
        using var window = new WindowChrome { Text = "kot. icon fixture", FormBorderStyle = FormBorderStyle.None, Size = new Size(240, 120) };
        using var tray = new NotifyIcon();
        icons.Apply(window, tray, "light");
        check(ReferenceEquals(window.Icon, icons.Window("light")), "saved light theme selects its icon before the window handle exists");
        window.Show(); tray.Visible = true;
        try
        {
            foreach (string theme in new[] { "light", "dark", "light", "dark" })
            {
                window.Hide(); icons.Apply(window, tray, theme); window.RestoreWindow(); Application.DoEvents();
                Color background = ColorTranslator.FromHtml(theme == "light" ? "#efefeb" : "#111515");
                Color point = ColorTranslator.FromHtml(theme == "light" ? "#55752a" : "#d5f88c");
                foreach (int kind in new[] { 0, 1 })
                {
                    IntPtr handle = SendMessage(window.Handle, 0x007F, (IntPtr)kind, IntPtr.Zero); // WM_GETICON: SMALL, BIG
                    check(handle != IntPtr.Zero, "Windows exposes the live shell icon handle " + theme + " " + kind);
                    using var icon = Icon.FromHandle(handle);
                    using var bitmap = icon.ToBitmap();
                    check(bitmap.GetPixel(bitmap.Width / 10, bitmap.Height / 2).ToArgb() == background.ToArgb()
                        && bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).ToArgb() == point.ToArgb(),
                        "actual window/taskbar icon changes after hide, theme switch and restore " + theme + " " + kind);
                }
                using var trayBitmap = tray.Icon!.ToBitmap();
                check(trayBitmap.GetPixel(trayBitmap.Width / 10, trayBitmap.Height / 2).ToArgb() == background.ToArgb()
                    && trayBitmap.GetPixel(trayBitmap.Width / 2, trayBitmap.Height / 2).ToArgb() == point.ToArgb(),
                    "notification-area icon changes in the same theme switch " + theme);
                IntPtr previous = tray.Icon.Handle;
                icons.Apply(window, tray, theme);
                check(tray.Icon.Handle == previous, "repeated snapshots reuse native icon handles " + theme);
            }
            check(ShellIdentity.CurrentId() == ShellIdentity.AppId, "theme switching preserves the stable taskbar grouping ID");
        }
        finally { tray.Visible = false; tray.Icon = null; window.Icon = null; window.Close(); }
    }

    public static void Run(Action<bool, string> check)
    {
        ShellIdentity.Initialize();
        check(ShellIdentity.CurrentId() == ShellIdentity.AppId, "Windows recognizes the stable kot application ID before its windows open");
        ThemeChecks(check);
        string folder = Path.Combine(Path.GetTempPath(), "kot-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            string path = Path.Combine(folder, "kot..lnk"), target = Environment.ProcessPath!;
            dynamic wsh = shell;
            shortcut = wsh.CreateShortcut(path);
            dynamic link = shortcut;
            link.TargetPath = target; link.Arguments = "--startup"; link.WorkingDirectory = folder; link.Description = "kot. test"; link.Save();
            Marshal.ReleaseComObject(shortcut); shortcut = null;
            ShellIdentity.SetShortcutId(path); ShellIdentity.SetShortcutId(path);
            check(ShellIdentity.ShortcutId(path) == ShellIdentity.AppId, "installed shortcuts persist the same application ID; repeat registration is safe");
            shortcut = wsh.CreateShortcut(path); link = shortcut;
            check((string)link.TargetPath == target && (string)link.Arguments == "--startup" && (string)link.WorkingDirectory == folder,
                "application ID registration preserves the shortcut target, arguments and working directory");
            Marshal.ReleaseComObject(shortcut); shortcut = null;
            foreach (string theme in new[] { "light", "dark" })
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, theme == "light" ? "kot-light.ico" : "kot.ico");
                check(ShellIdentity.SetShortcutIcon(path, target, iconPath), "pinned shortcut accepts the selected icon theme " + theme);
                shortcut = wsh.CreateShortcut(path); link = shortcut;
                check((string)link.IconLocation == iconPath + ",0" && (string)link.TargetPath == target
                    && (string)link.Arguments == "--startup" && (string)link.WorkingDirectory == folder
                    && ShellIdentity.ShortcutId(path) == ShellIdentity.AppId,
                    "pinned icon refresh preserves the launch command and grouping " + theme);
                Marshal.ReleaseComObject(shortcut); shortcut = null;
            }
            check(!ShellIdentity.SetShortcutIcon(path, Path.Combine(folder, "another-app.exe"), Path.Combine(AppContext.BaseDirectory, "kot-light.ico")),
                "theme refresh does not change a shortcut for another executable");
            string unicodeFolder = Path.Combine(folder, "Проверка kot"); Directory.CreateDirectory(unicodeFolder);
            string unicodeTarget = Path.Combine(unicodeFolder, "Kot.exe"), unicodeIcon = Path.Combine(unicodeFolder, "kot.ico");
            File.Copy(target, unicodeTarget); File.Copy(Path.Combine(AppContext.BaseDirectory, "kot.ico"), unicodeIcon);
            ShellIdentity.RegisterInstalledShortcut(path, unicodeTarget, unicodeIcon);
            var saved = ShellIdentity.ReadShortcut(path);
            check(saved.Target == unicodeTarget && saved.IconPath == unicodeIcon && saved.IconIndex == 0
                && saved.Arguments == "--startup" && saved.WorkingDirectory == unicodeFolder && ShellIdentity.ShortcutId(path) == ShellIdentity.AppId,
                "installed shortcut round-trips Unicode paths, abstract icon, arguments and application ID");
            check(ShellIdentity.SetShortcutIcon(path, unicodeTarget, Path.Combine(AppContext.BaseDirectory, "kot-light.ico")),
                "pinned icon refresh recognizes an executable in a Unicode folder");
            saved = ShellIdentity.ReadShortcut(path);
            check(saved.Target == unicodeTarget && saved.Arguments == "--startup" && saved.WorkingDirectory == unicodeFolder
                && saved.IconPath == Path.Combine(AppContext.BaseDirectory, "kot-light.ico") && ShellIdentity.ShortcutId(path) == ShellIdentity.AppId,
                "Unicode icon refresh preserves the launch command and taskbar grouping");
            foreach (string theme in new[] { "dark", "light" })
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, theme == "light" ? "kot-light.ico" : "kot.ico");
                foreach (int size in new[] { 16, 24, 32, 48, 64, 128, 256 })
                {
                    // The Windows extractor understands the 256 frame's directory byte (0), just like Explorer.
                    using var icon = size == 256 ? Icon.ExtractIcon(iconPath, 0, size)! : new Icon(iconPath, size, size);
                    check(icon.Width == size && icon.Height == size, "abstract icon has a native Windows frame at " + size + " pixels " + theme);
                }
                using var original = new Icon(iconPath, 64, 64);
                using var bitmap = original.ToBitmap();
                check(bitmap.GetPixel(16, 24).ToArgb() == ColorTranslator.FromHtml(theme == "light" ? "#252a23" : "#f3f4ed").ToArgb()
                    && bitmap.GetPixel(32, 32).ToArgb() == ColorTranslator.FromHtml(theme == "light" ? "#55752a" : "#d5f88c").ToArgb()
                    && bitmap.GetPixel(6, 32).ToArgb() == ColorTranslator.FromHtml(theme == "light" ? "#efefeb" : "#111515").ToArgb()
                    && bitmap.GetPixel(0, 0).A == 0, "native abstract icon preserves the open contour, central point, theme and transparent corners " + theme);
            }
        }
        finally
        {
            if (shortcut != null) Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell); Directory.Delete(folder, true);
        }
    }
}
