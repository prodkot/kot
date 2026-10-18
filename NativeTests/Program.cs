using Kot.Windows;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

if (args.Length == 1 && args[0] == "--stdin-hash")
{
    using var source = Console.OpenStandardInput();
    Console.WriteLine(Convert.ToHexString(await SHA256.HashDataAsync(source))); return 0;
}
if (args.Length == 1 && args[0] == "--stdin-ignore")
{
    Console.WriteLine("ignoring-input"); await Task.Delay(30000); return 0;
}
if (args.Length == 1 && args[0] == "--stdin-reject")
{
    Console.Error.WriteLine("FATAL rejected-before-reading"); return 23;
}

if (args.Length == 2 && args[0] == "--service-assets")
{
    File.WriteAllText(args[1], Environment.GetEnvironmentVariable("XRAY_LOCATION_ASSET") ?? ""); return 0;
}
if (args.Length > 1 && args[0] == "--service-args")
{
    File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(args.Skip(2))); return 42;
}
if (args.Length > 1 && args[0] == "--service-hold")
{
    // Publish readiness only after closing the writer, so readers cannot race its file lock.
    File.WriteAllText(args[1] + ".tmp", Environment.ProcessId.ToString()); File.Move(args[1] + ".tmp", args[1]);
    await Task.Delay(30000); return 0;
}
if (args.Length > 1 && args[0] == "--service-parent")
{
    var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    info.ArgumentList.Add("--service-hold"); info.ArgumentList.Add(args[1]);
    using var child = System.Diagnostics.Process.Start(info); await Task.Delay(30000); return 0;
}
if (args.Length > 0 && args[0] == "--service-flood")
{
    Console.Write(new string('x', 5 * 1024 * 1024)); return 0;
}

// Windows-only integration checks of the real launcher, using this executable
// as a fixture child. No provider, tunnel driver, network or administrator is needed.
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
    await core.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(core.HasExited, "native handle signals natural process termination");
    Check(core.ExitCode == code, "CreateProcess child exit code " + unchecked((uint)code).ToString("X8"));
    Check((await core.ReadRecentOutput(true)).Contains("FATAL fixture-original-error"), "original core error captured before cleanup");
    await core.Stop();
    Check(journal.Any(line => line == "core stopped; exit=" + code), "cleanup logs native exit code without masking the failure");
    core.Dispose(); // Stop + caller Dispose must be harmless.
}
try
{
    string payload = string.Concat(Enumerable.Repeat("конфигурация 出口 ", 32768));
    await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
    {
        using var child = new NativeCore(fixtureExe, ["--stdin-hash"], folder, "stdin fixture", standardInput: payload + i);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload + i)));
        Check(child.ExitCode == 0 && (await child.ReadRecentOutput(true)).Trim() == expected,
            "concurrent native stdin transmits complete large UTF-8 payloads and EOF without mixing workers");
    }));
    using (var child = new NativeCore(fixtureExe, ["--stdin-reject"], folder, "stdin reject", standardInput: payload))
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.ExitCode == 23 && (await child.ReadRecentOutput(true)).Contains("rejected-before-reading"),
            "an early child exit releases a blocked stdin writer and preserves the original error");
    }
    var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var child = new NativeCore(fixtureExe, ["--stdin-ignore"], folder, "stdin cancellation",
        line => { if (line == "ignoring-input") inputReady.TrySetResult(); }, standardInput: payload))
    {
        await inputReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var observer = System.Diagnostics.Process.GetProcessById(child.Process.Id);
        var ended = observer.WaitForExitAsync();
        await child.Stop().WaitAsync(TimeSpan.FromSeconds(15));
        await ended.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "stopping a child that never reads stdin releases the process and pipe writer");
    }
    string? originalAssets = Environment.GetEnvironmentVariable("XRAY_LOCATION_ASSET");
    await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
    {
        string assets = Path.Combine(folder, "геобазы " + i), captured = Path.Combine(folder, "asset-" + i + ".txt");
        Directory.CreateDirectory(assets);
        using var child = new NativeCore(fixtureExe, ["--service-assets", captured], folder, "kot. assets fixture", assetDirectory: assets);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.ExitCode == 0 && File.ReadAllText(captured) == assets, "native workers receive isolated Unicode asset directories");
    }));
    Check(Environment.GetEnvironmentVariable("XRAY_LOCATION_ASSET") == originalAssets, "provider workers never change the parent's asset directory");
    await NaturalExit(17);
    await NaturalExit(259); // STILL_ACTIVE must not be confused with an exited child.
    await NaturalExit(unchecked((int)0xC0000005));
    await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Task.Run(() => NaturalExit(20 + i))));
    string config = Path.Combine(folder, "hold.txt"); File.WriteAllText(config, "hold");
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stopping = new ConcurrentQueue<string>();
    using (var core = new NativeCore(fixtureExe, config, line => { stopping.Enqueue(line); if (line == "fixture-ready") ready.TrySetResult(); }))
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(Kot.WindowTests.ProcessParent.Id(core.Process.Handle) == Environment.ProcessId, "VPN core stays a direct child of its kot host for process grouping");
        bool runningRejected = false;
        try { _ = core.ExitCode; } catch (InvalidOperationException ex) { runningRejected = ex.Message.Contains("still running"); }
        Check(runningRejected, "a running process has no termination code");
        Check(!core.HasExited, "native handle confirms the child remains alive before stopping");
        // Stop disposes its own Process wrapper. Observe termination through a
        // separate wrapper whose event subscription survives that disposal.
        using var observer = System.Diagnostics.Process.GetProcessById(core.Process.Id);
        var exited = observer.WaitForExitAsync();
        await core.Stop();
        await exited.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "stop waits for a live child and always disposes its handles");
        Check(stopping.Any(line => line.StartsWith("core stopped; exit=")), "live child cleanup records an exit only after native termination");
    }
    string argumentFile = Path.Combine(folder, "argument capture.json");
    string[] expectedArguments = ["space value", "literal\"quote", "trailing path\\", "тест Unicode", ""];
    using (var service = new NativeCore(fixtureExe, new[] { "--service-args", argumentFile }.Concat(expectedArguments), folder, "kot. fixture service"))
    {
        await service.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(service.ExitCode == 42 && System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(argumentFile))!.SequenceEqual(expectedArguments), "managed service launcher preserves the complete Windows argument vector");
    }
    string descendantFile = Path.Combine(folder, "descendant.txt");
    using (var parent = new NativeCore(fixtureExe, ["--service-parent", descendantFile], folder, "kot. fixture service"))
    {
        for (int i = 0; i < 100 && !File.Exists(descendantFile); i++) await Task.Delay(50);
        using var descendant = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(descendantFile)));
        Check(Kot.WindowTests.ProcessParent.Id(parent.Process.Handle) == Environment.ProcessId && Kot.WindowTests.ProcessParent.Id(descendant.Handle) == parent.Process.Id, "managed workers and their children remain in the kot process tree");
        var ended = descendant.WaitForExitAsync(); parent.Dispose(); await ended.WaitAsync(TimeSpan.FromSeconds(10));
        Check(true, "closing the service job kills its descendant even after an abrupt host cleanup");
    }
    int longest = 0;
    string orphanFile = Path.Combine(folder, "orphan.txt");
    using (var parent = new NativeCore(fixtureExe, ["--service-parent", orphanFile], folder, "kot. fixture orphan cleanup"))
    {
        for (int i = 0; i < 100 && !File.Exists(orphanFile); i++) await Task.Delay(50);
        using var descendant = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(orphanFile)));
        using (var observer = System.Diagnostics.Process.GetProcessById(parent.Process.Id))
        { observer.Kill(); await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        await parent.Stop();
        Check(descendant.HasExited, "stop waits for every child after the launcher is killed unexpectedly");
    }
    using (var flood = new NativeCore(fixtureExe, ["--service-flood"], folder, "kot. fixture flood", line => { longest = Math.Max(longest, line.Length); }))
    {
        await flood.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        string tail = await flood.ReadRecentOutput(true);
        Check(longest <= 1500 && tail.Length < 65536, "a five-megabyte unterminated service log remains bounded in memory");
    }
    bool missingRejected = false;
    try { using var core = new NativeCore(Path.Combine(folder, "missing.exe"), config); }
    catch (System.ComponentModel.Win32Exception) { missingRejected = true; }
    Check(missingRejected, "failed CreateProcess preserves its original Win32 error");
    return 0;
}
finally { Directory.Delete(folder, recursive: true); }
