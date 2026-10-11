using Kot.Windows;
using System.Runtime.InteropServices;
namespace Kot.WindowTests;

static class ShellTests
{
    public static void Run(Action<bool, string> check)
    {
        ShellIdentity.Initialize();
        check(ShellIdentity.CurrentId() == ShellIdentity.AppId, "Windows recognizes the stable kot application ID before its windows open");
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
            string iconPath = Path.Combine(AppContext.BaseDirectory, "kot.ico");
            foreach (int size in new[] { 16, 24, 32, 48, 64, 128, 256 })
            {
                using var icon = new Icon(iconPath, size, size);
                check(icon.Width == size && icon.Height == size, "website icon has a native Windows frame at " + size + " pixels");
            }
            using var original = new Icon(iconPath, 64, 64);
            using var bitmap = original.ToBitmap();
            check(bitmap.GetPixel(20, 20).ToArgb() == Color.FromArgb(243, 244, 237).ToArgb()
                && bitmap.GetPixel(53, 49).ToArgb() == Color.FromArgb(213, 248, 140).ToArgb()
                && bitmap.GetPixel(6, 32).ToArgb() == Color.FromArgb(17, 21, 21).ToArgb()
                && bitmap.GetPixel(0, 0).A == 0, "native icon keeps the website's white k, lime dot, dark background and transparent rounded corners");
        }
        finally
        {
            if (shortcut != null) Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell); Directory.Delete(folder, true);
        }
    }
}
