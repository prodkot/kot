using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Kot.Windows;
static class Program
{
    public static readonly int ShutdownMessage = (int)RegisterWindowMessage(AppRuntime.Scope + ".Shutdown.v1");
    public static readonly int ActivateMessage = (int)RegisterWindowMessage(AppRuntime.Scope + ".ShowWindow.v1");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            if (args.Contains("--register-shortcuts")) { ShellIdentity.RegisterInstalledShortcuts(); return; }
            if (args.Length == 4 && args[0] == "--proxy-watch") { SystemProxy.Watch(int.Parse(args[1]), long.Parse(args[2]), args[3]); return; }
            if (args.Contains("--restore-proxy")) { SystemProxy.Restore(); return; }
            if (args.Contains("--disable-kill-switch")) { KillSwitch.Release(); return; }
            if (args.Contains("--shutdown"))
            {
                SendNotifyMessage((IntPtr)0xffff, (uint)ShutdownMessage, IntPtr.Zero, IntPtr.Zero);
                using var running = new Mutex(false, AppRuntime.InstanceMutex);
                bool closed; try { closed = running.WaitOne(30000); } catch (AbandonedMutexException) { closed = true; }
                if (!closed) { Environment.ExitCode = 1; return; } running.ReleaseMutex(); SystemProxy.Restore(); return;
            }
            if (args.Contains("--apply-update")) { UpdateInstaller.Apply(); return; }
            if (args.Contains("--recover-update")) { UpdateInstaller.Recover(); return; }
            if (args.Contains("--repair-startup")) { Startup.RepairEnabled().GetAwaiter().GetResult(); return; }
            if (args.Contains("--remove-owned-startup")) { Startup.RemoveOwnedTasks().GetAwaiter().GetResult(); return; }
            if (args.Contains("--enable-startup")) { Startup.Set(true).GetAwaiter().GetResult(); return; }
            if (args.Contains("--disable-startup")) { Startup.Set(false).GetAwaiter().GetResult(); return; }
            ShellIdentity.Initialize();
            int receipt = Array.IndexOf(args, "--update-receipt");
            if (receipt >= 0 && receipt + 1 < args.Length)
            {
                string work = Path.GetFullPath(args[receipt + 1]);
                string root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (Path.GetDirectoryName(work) != root || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(work), "^\\.kot-update-[a-f0-9]{32}$")) throw new Kot.Core.UserError("Некорректный путь подтверждения обновления.");
                Kot.Core.UpdateTransaction.SafePath(work); UpdateInstaller.Receipt = work;
            }
            if (UpdateInstaller.Receipt == null)
            using (var barrier = new Mutex(false, UpdateInstaller.MutexName))
            {
                bool owned; try { owned = barrier.WaitOne(90000); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new Kot.Core.UserError("Установка обновления ещё выполняется."); barrier.ReleaseMutex();
            }
            int waiting = Array.IndexOf(args, "--wait-for");
            if (waiting >= 0 && waiting + 1 < args.Length && int.TryParse(args[waiting + 1], out int pid) && pid != Environment.ProcessId)
            {
                try { using var previous = Process.GetProcessById(pid); if (!previous.WaitForExit(60000)) throw new Kot.Core.UserError("Предыдущая версия не закрылась. Закройте её и снова запустите kot."); }
                catch (ArgumentException) { }
            }
            using Mutex mutex = new(true, AppRuntime.InstanceMutex, out bool first);
            if (!first) { if (!args.Contains("--startup")) SendNotifyMessage((IntPtr)0xffff, (uint)ActivateMessage, IntPtr.Zero, IntPtr.Zero); return; }
            if (args.Contains("--startup")) StartupEvidence.Begin();
            SystemProxy.Restore();
            if (UpdateInstaller.Receipt == null && UpdateInstaller.StartRecovery(args)) return;
            if (UpdateInstaller.Receipt == null) Kot.Core.InstallationCleanup.RemoveLegacyDocuments(AppContext.BaseDirectory);
            if (args.Contains("--update-startup")) Startup.Set(true).GetAwaiter().GetResult();
            int aborted = Array.IndexOf(args, "--aborted-update");
            string? abortedUpdate = aborted >= 0 && aborted + 1 < args.Length ? args[aborted + 1] : null;
            UpdateInstaller.CleanupFinishedUpdates(AppContext.BaseDirectory, UpdateInstaller.Receipt, abortedUpdate);
            var profile = ProfileRecovery.LoadProfile();
            if (profile != null) Application.Run(new MainWindow(args.Contains("--startup"), args.Contains("--resume-connection"), profile));
        }
        catch (Exception ex)
        {
            StartupEvidence.Write("failed");
            Environment.ExitCode = 1;
            if (args.Any(a => a is "--proxy-watch" or "--restore-proxy" or "--shutdown" or "--disable-startup" or "--disable-kill-switch" or "--register-shortcuts" or "--repair-startup" or "--remove-owned-startup" or "--enable-startup"))
            {
                AppLog.Error("maintenance", ex);
                try { Directory.CreateDirectory(Store.Folder); File.WriteAllText(Path.Combine(Store.Folder, "maintenance-error.txt"), AppLog.Snapshot()); } catch { }
                return;
            }
            AppLog.PersistStartupFailure("startup", ex);
            MessageBox.Show(Store.Friendly(ex), AppRuntime.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { StartupEvidence.Write(Environment.ExitCode == 0 ? "exited" : "failed"); }
    }
}
