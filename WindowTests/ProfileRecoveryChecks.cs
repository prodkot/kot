using Kot.Core;
using Kot.Windows;
using System.Security.Cryptography;
using System.Text;
namespace Kot.WindowTests;

static class ProfileRecoveryChecks
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "kot-profile-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Store.Persistence(folder);
            store.Save(new Profile { Theme = "light", SendHwid = false }); store.Save(new Profile { Theme = "dark" });
            check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.BackupPath)).Contains("theme"), "previous profile remains DPAPI encrypted");
            File.WriteAllBytes(store.PrimaryPath, [1, 2, 3, 4]);
            bool damaged = false; try { store.Load(); } catch (CryptographicException) { damaged = true; }
            check(damaged && store.RestoreBackup().Theme == "light" && !store.Load().SendHwid, "DPAPI corruption restores the last good profile and privacy settings");
            using (var locked = new FileStream(store.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { store.Change(store.Load(), p => p.Theme = "dark"); } catch (IOException) { failed = true; }
                check(failed && store.Load().Theme == "light" && store.LoadBackup().Theme == "light", "Windows file lock leaves both valid encrypted copies untouched");
            }
            check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "failed DPAPI profile replacement removes its temporary file");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
