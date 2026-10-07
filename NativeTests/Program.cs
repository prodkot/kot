using Kot.Windows;
using System.Collections.Concurrent;

// Windows-only integration checks of the real launcher, using this executable
// as a fixture child. No provider, VPN driver, network or administrator is needed.
if (args.Length == 3 && args[0] == "run" && args[1] == "-c")
{
    string mode = File.ReadAllText(args[2]);
    if (mode == "hold")
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.WriteLine("fixture-ready");
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop.Token); } catch (OperationCanceledException) { }
        return 0;
    }
    Console.Error.WriteLine("FATAL fixture-original-error");
    await Task.Delay(150);
    return int.Parse(mode);
}
if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP native launcher tests: Windows required"); return 77; }

string fixtureExe = Environment.ProcessPath!;
if (!fixtureExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
    || Path.GetFileNameWithoutExtension(fixtureExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Run the published Kot.NativeTests.exe apphost.");
string folder = Path.Combine(Path.GetTempPath(), "Kot-NativeTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
async Task NaturalExit(int code)
{
    string config = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".txt");
    File.WriteAllText(config, code.ToString());
    var journal = new ConcurrentQueue<string>();
    using var core = new NativeCore(fixtureExe, config, journal.Enqueue);
    await core.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(core.ExitCode == code, "CreateProcess child exit code " + unchecked((uint)code).ToString("X8"));
    Check((await core.ReadRecentOutput(true)).Contains("FATAL fixture-original-error"), "original core error captured before cleanup");
    await core.Stop();
    Check(journal.Any(line => line == "core stopped; exit=" + code), "cleanup logs native exit code without masking the failure");
    core.Dispose(); // Stop + caller Dispose must be harmless.
}
try
{
    await NaturalExit(17);
    await NaturalExit(259); // STILL_ACTIVE must not be confused with an exited child.
    await NaturalExit(unchecked((int)0xC0000005));
    await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Task.Run(() => NaturalExit(20 + i))));
    string config = Path.Combine(folder, "hold.txt"); File.WriteAllText(config, "hold");
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var core = new NativeCore(fixtureExe, config, line => { if (line == "fixture-ready") ready.TrySetResult(); }))
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool runningRejected = false;
        try { _ = core.ExitCode; } catch (InvalidOperationException ex) { runningRejected = ex.Message.Contains("still running"); }
        Check(runningRejected, "a running process has no termination code");
        var exited = core.Process.WaitForExitAsync();
        await core.Stop();
        await exited.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "stop waits for a live child and always disposes its handles");
    }
    bool missingRejected = false;
    try { using var core = new NativeCore(Path.Combine(folder, "missing.exe"), config); }
    catch (System.ComponentModel.Win32Exception) { missingRejected = true; }
    Check(missingRejected, "failed CreateProcess preserves its original Win32 error");
    return 0;
}
finally { Directory.Delete(folder, recursive: true); }
