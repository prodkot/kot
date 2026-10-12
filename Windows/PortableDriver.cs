using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Kot.Core;
namespace Kot.Windows;

// Keep the SCM handle before WinDivert marks its service for deletion. An independent
// helper can then unload our driver after winws stops, including when the UI crashes.
internal sealed class PortableDriver
{
    readonly Process watcher;
    readonly string marker;
    PortableDriver(Process watcher, string marker) { this.watcher = watcher; this.marker = marker; }
    public static async Task<PortableDriver> Start(string executable, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(executable)!, driver = Path.Combine(directory, "WinDivert64.sys");
        string library = Encoding.Unicode.GetString(File.ReadAllBytes(Path.Combine(directory, "WinDivert.dll")));
        string name = Regex.Match(library, @"\\\\\.\\(WinDivert(?:\d\.\d)?)\0").Groups[1].Value;
        if (name.Length == 0 || !File.Exists(driver)) throw new UserError("Не найден поддерживаемый WinDivert внутри пакета portable.");
        string marker = Path.Combine(Store.Folder, "temp", "driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!); File.WriteAllText(marker, "waiting");
        using var owner = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (string value in new[] { "--portable-driver-watch", owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString(), name, driver, marker }) start.ArgumentList.Add(value);
        var guard = new PortableDriver(Process.Start(start) ?? throw new UserError("Не удалось запустить очистку portable-драйвера."), marker);
        try
        {
            var clock = Stopwatch.StartNew();
            while (!File.Exists(marker + ".ready"))
            {
                ct.ThrowIfCancellationRequested();
                if (guard.watcher.HasExited || clock.Elapsed.TotalSeconds > 10) throw new UserError(File.Exists(marker + ".error") ? File.ReadAllText(marker + ".error") : "Подготовка portable-драйвера не завершилась.");
                await Task.Delay(50, ct);
            }
            return guard;
        }
        catch { await guard.Stop(); throw; }
    }
    public async Task Stop()
    {
        try
        {
            File.Delete(marker);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await watcher.WaitForExitAsync(limit.Token);
            if (File.Exists(marker + ".error")) throw new UserError(File.ReadAllText(marker + ".error"));
            if (watcher.ExitCode != 0) throw new UserError("Portable-драйвер не завершил очистку.");
        }
        finally
        {
            watcher.Dispose(); File.Delete(marker + ".ready"); File.Delete(marker + ".error");
        }
    }
    public static void Watch(int pid, long ticks, string name, string driver, string marker)
    {
        if (!AppRuntime.Portable) throw new UserError("Очистка portable-драйвера доступна только в portable.");
        string root = Path.GetFullPath(Store.Folder) + Path.DirectorySeparatorChar;
        driver = Path.GetFullPath(driver); marker = Path.GetFullPath(marker);
        if (!driver.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !marker.StartsWith(Path.Combine(root, "temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(driver) != "WinDivert64.sys" || !Regex.IsMatch(name, @"^WinDivert(?:\d\.\d)?$"))
            throw new UserError("Некорректный путь portable-драйвера.");
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
        bool created = false, ready = false;
        try
        {
            using var owner = Process.GetProcessById(pid);
            if (owner.StartTime.ToUniversalTime().Ticks != ticks || owner.HasExited) return;
            manager = OpenSCManager(null, null, 3); Require(manager != IntPtr.Zero);
            service = OpenService(manager, name, 0x10025); // delete, stop, query config/status
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1060) throw new Win32Exception(error);
                service = CreateService(manager, name, "kot. portable WinDivert", 0x10025, 1, 3, 1, driver, null, IntPtr.Zero, null, null, null); Require(service != IntPtr.Zero);
                created = true;
            }
            else if (!BinaryPath(service).Equals(driver, StringComparison.OrdinalIgnoreCase))
                throw new UserError("WinDivert принадлежит другой программе. Portable не будет менять её драйвер; сначала завершите другую копию zapret.");
            File.WriteAllText(marker + ".ready", "ready");
            ready = true;
            while (File.Exists(marker) && !owner.WaitForExit(100)) { }
            // NativeCore's job closes descendants on abrupt UI exit. Give those handles time to close.
            var clock = Stopwatch.StartNew();
            while (true)
            {
                Require(QueryServiceStatus(service, out var status));
                if (status.State == 1) break;
                if (!ControlService(service, 1, out status) && Marshal.GetLastWin32Error() != 1062 && clock.Elapsed.TotalSeconds > 8)
                    throw new UserError("WinDivert занят другой программой. Закройте её перед удалением portable-папки.");
                if (clock.Elapsed.TotalSeconds > 10) throw new UserError("Windows не освободила portable-драйвер. Завершите остальные программы с WinDivert.");
                Thread.Sleep(100);
            }
            if (!DeleteService(service) && Marshal.GetLastWin32Error() != 1072) Require(false);
        }
        catch (Exception ex)
        {
            // If preparation failed before winws could start, do not leave an unused
            // service registration. Never delete a service that was already present.
            if (created && !ready) DeleteService(service);
            Environment.ExitCode = 1;
            File.WriteAllText(marker + ".error", Store.Friendly(ex));
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
            File.Delete(marker); File.Delete(marker + ".ready");
        }
    }
    static string BinaryPath(IntPtr service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out uint bytes);
        if (bytes is 0 or > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            Require(QueryServiceConfig(service, memory, bytes, out _));
            string path = Marshal.PtrToStringUni(Marshal.PtrToStructure<Config>(memory).BinaryPath)!.Trim('"');
            if (path.StartsWith(@"\??\")) path = path[4..];
            return Path.GetFullPath(path);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    static void Require(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] struct Config { public uint Type, Start, Error; public IntPtr BinaryPath, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    [StructLayout(LayoutKind.Sequential)] struct Status { public uint Type, State, Accepted, ExitCode, SpecificExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr service, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr service, uint control, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr service);
}
