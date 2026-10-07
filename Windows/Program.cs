using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace Kot.Windows;
static class Program
{
    public static readonly int ShutdownMessage = (int)RegisterWindowMessage("KotVPN.Shutdown.v1");
    public static readonly int ActivateMessage = (int)RegisterWindowMessage("KotVPN.ShowWindow.v1");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--shutdown"))
            {
                SendNotifyMessage((IntPtr)0xffff, (uint)ShutdownMessage, IntPtr.Zero, IntPtr.Zero);
                using var running = new Mutex(false, "Local\\KotVPN-" + WindowsIdentity.GetCurrent().User!.Value);
                bool closed; try { closed = running.WaitOne(30000); } catch (AbandonedMutexException) { closed = true; }
                if (!closed) { Environment.ExitCode = 1; return; } running.ReleaseMutex(); return;
            }
            if (args.Contains("--apply-update")) { UpdateInstaller.Apply(); return; }
            if (args.Contains("--disable-startup")) { Startup.Set(false).GetAwaiter().GetResult(); var saved = Store.Load(); saved.Startup = false; Store.Save(saved); return; }
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
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            using Mutex mutex = new(true, "Local\\KotVPN-" + sid, out bool first);
            if (!first) { SendNotifyMessage((IntPtr)0xffff, (uint)ActivateMessage, IntPtr.Zero, IntPtr.Zero); return; }
            if (args.Contains("--update-startup")) Startup.Set(true).GetAwaiter().GetResult();
            foreach (string work in Directory.GetDirectories(AppContext.BaseDirectory, ".kot-update-*"))
            {
                if (work == UpdateInstaller.Receipt || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(work), "^\\.kot-update-[a-f0-9]{32}$")) continue;
                try { Kot.Core.UpdateTransaction.SafePath(work); if (File.Exists(Path.Combine(work, "committed")) || File.Exists(Path.Combine(work, "rolled-back"))) Directory.Delete(work, true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Application.Run(new MainWindow(args.Contains("--startup"), args.Contains("--resume-connection")));
        }
        catch (Exception ex) { Environment.ExitCode = 1; MessageBox.Show(Store.Friendly(ex), "kot.", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
