using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Kot.Core;
using Microsoft.Win32.SafeHandles;
namespace Kot.Windows;
// A dedicated console lets sing-box receive a normal interrupt and release TUN/WFP.
// The suspended child is assigned to a kill-on-close job before any core code runs.
public sealed class NativeCore : IDisposable
{
    IntPtr job;
    readonly SafeProcessHandle processHandle;
    readonly AnonymousPipeServerStream output;
    static readonly object launchLock = new();
    readonly CoreLogBuffer log = new();
    readonly Action<string>? report;
    static readonly SemaphoreSlim consoleGate = new(1);
    Task readOutput = Task.CompletedTask;
    public Process Process { get; }
    public bool HasExited => WaitForExit(0);
    bool WaitForExit(uint milliseconds)
    {
        uint result = WaitForSingleObject(processHandle, milliseconds);
        if (result == 0) return true;
        if (result == 258) return false;
        if (result == uint.MaxValue) throw new Win32Exception();
        throw new InvalidOperationException("Unexpected process wait result: " + result);
    }
    public Task WaitForExitAsync() => Task.Run(() => WaitForExit(uint.MaxValue));
    public int ExitCode
    {
        get
        {
            // This child was created by CreateProcess, not Process.Start.
            // Process.GetProcessById cannot supply its ExitCode; keep the original
            // native handle, which also remains valid after the process exits.
            if (!HasExited) throw new InvalidOperationException("The core process is still running.");
            if (!GetExitCodeProcess(processHandle, out uint code)) throw new Win32Exception();
            return unchecked((int)code);
        }
    }
    public NativeCore(string exe, string config, Action<string>? report = null)
    {
        this.report = report;
        // Keep temporary inheritable client pipe handles out of other concurrent core launches.
        lock (launchLock)
        {
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            report?.Invoke("create job");
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { var error = new Win32Exception(); output.Dispose(); throw error; }
            try
            {
                var limit = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
                if (!SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception();
                report?.Invoke("create inherited pipes");
                using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                var si = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfo>(), dwFlags = 0x101, wShowWindow = 0,
                    hStdInput = input.ClientSafePipeHandle.DangerousGetHandle(),
                    hStdOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
                    hStdError = output.ClientSafePipeHandle.DangerousGetHandle()
                };
                // Paths are quoted and contain no user-supplied command text.
                report?.Invoke("CreateProcess suspended");
                if (!CreateProcess(exe, new StringBuilder('"' + exe + "\" run -c \"" + config + '"'), IntPtr.Zero, IntPtr.Zero, true, 0x10 | 0x4, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi)) throw new Win32Exception();
                processHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
                try
                {
                    output.DisposeLocalCopyOfClientHandle(); input.DisposeLocalCopyOfClientHandle();
                    if (!AssignProcessToJobObject(job, pi.hProcess)) { var error = new Win32Exception(); TerminateProcess(pi.hProcess, 1); throw error; }
                    report?.Invoke("job assigned; associate process");
                    Process = Process.GetProcessById((int)pi.dwProcessId);
                    readOutput = Task.Run(ReadOutput);
                    report?.Invoke("resume core");
                    if (ResumeThread(pi.hThread) == uint.MaxValue) { var error = new Win32Exception(); TerminateProcess(pi.hProcess, 1); throw error; }
                }
                finally { CloseHandle(pi.hThread); }
            }
            catch (Exception ex) { report?.Invoke("native launch exception: " + ex); CloseHandle(job); job = IntPtr.Zero; processHandle?.Dispose(); Process?.Dispose(); output.Dispose(); log.Clear(); throw; }
        }
    }
    async Task ReadOutput()
    {
        try
        {
            using var reader = new StreamReader(output, Encoding.UTF8, false, 4096, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null) { log.Append((line + "\n").AsSpan()); report?.Invoke(line); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { report?.Invoke("output pipe closed: " + ex.GetType().Name); }
        catch (Exception ex) { report?.Invoke("output capture exception: " + ex); }
    }
    public async Task<string> ReadRecentOutput(bool exited)
    {
        if (exited) { try { await readOutput.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { } }
        return log.Snapshot();
    }
    public async Task Stop()
    {
        try
        {
            if (!HasExited)
            {
                // AttachConsole is process-wide; simultaneous ping workers must serialize it.
                await consoleGate.WaitAsync();
                try
                {
                    if (AttachConsole((uint)Process.Id))
                    {
                        SetConsoleCtrlHandler(IntPtr.Zero, true);
                        try { GenerateConsoleCtrlEvent(0, 0); await Task.Delay(200); }
                        finally { FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); }
                    }
                }
                finally { consoleGate.Release(); }
                // Wait on the original CreateProcess handle throughout. A separate
                // Process wrapper can report an exit before this handle is signaled.
                if (!await Task.Run(() => WaitForExit(4000)))
                {
                    report?.Invoke("interrupt timeout; terminate core job");
                    if (!TerminateJobObject(job, 130))
                    {
                        var error = new Win32Exception();
                        if (!HasExited) throw error;
                    }
                    if (!await Task.Run(() => WaitForExit(10000))) throw new TimeoutException("The core job did not stop.");
                }
            }
            await ReadRecentOutput(true);
            report?.Invoke("core stopped; exit=" + ExitCode);
        }
        finally { Dispose(); }
    }
    public void Dispose() { if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; } output.Dispose(); log.Clear(); Process?.Dispose(); processHandle.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo { public int cb; public string? lpReserved, lpDesktop, lpTitle; public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ctrl, uint group);
}
