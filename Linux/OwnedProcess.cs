using System.Diagnostics;
using System.Runtime.InteropServices;
using Kot.Core;
namespace Kot.Linux;

// Only signals the session created by our own launch. Never searches process names.
public sealed class OwnedProcess : IAsyncDisposable
{
    readonly Process process;
    readonly Task output, error;
    public readonly CoreLogBuffer Log = new();
    public bool Exited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public StreamWriter Input => process.StandardInput;
    public OwnedProcess(string executable, IEnumerable<string> arguments, string? assets = null)
    {
        var info = new ProcessStartInfo("/usr/bin/setsid") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("/usr/bin/python3"); info.ArgumentList.Add("-I");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-launcher.py"));
        info.ArgumentList.Add(executable);
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        // Never inherit Xray's asset/TUN/plugin loader environment from a shell.
        foreach (string key in info.Environment.Keys.Where(k => k.StartsWith("XRAY_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("xray.", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        if (assets != null) info.Environment["XRAY_LOCATION_ASSET"] = assets;
        process = Process.Start(info) ?? throw new UserError("Не удалось запустить процесс.");
        output = Drain(process.StandardOutput); error = Drain(process.StandardError);
    }
    async Task Drain(StreamReader reader)
    {
        char[] block = new char[2048]; int count;
        while ((count = await reader.ReadAsync(block)) > 0) Log.Append(block.AsSpan(0, count));
    }
    public async Task Write(string text, CancellationToken ct)
    { await Input.WriteAsync(text.AsMemory(), ct); await Input.FlushAsync(ct); }
    public void CloseInput() => Input.Close();
    public async Task Wait(CancellationToken ct) { await process.WaitForExitAsync(ct); await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2), ct); }
    public async Task Stop()
    {
        if (!process.HasExited)
        {
            // setsid execs the target; session creation might still be in flight.
            if (kill(-process.Id, 15) != 0) process.Kill(entireProcessTree: true);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (TimeoutException) { kill(-process.Id, 9); if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
        }
        // A parent may have exited while its children still hold the pipes.
        kill(-process.Id, 9);
        try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
    }
    public async ValueTask DisposeAsync() { try { await Stop(); } finally { process.Dispose(); Log.Clear(); } }
    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int signal);
    public static async Task<string> Run(string exe, string[] args, string? input = null, CancellationToken ct = default)
    {
        await using var child = new OwnedProcess(exe, args);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        if (input != null) await child.Write(input, deadline.Token);
        child.CloseInput(); await child.Wait(deadline.Token);
        if (child.ExitCode != 0) throw new UserError("Операция " + Path.GetFileName(exe) + " завершилась ошибкой (" + child.ExitCode + ").");
        return child.Log.Snapshot().Trim();
    }
}
