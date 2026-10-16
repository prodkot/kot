using Kot.Core;
using System.Diagnostics;
using System.IO.Compression;
namespace Kot.Tests;

public static class UpdateCrashTests
{
    public static void Child(string[] args)
    {
        string root = args[1], work = Path.Combine(root, ".kot-update-" + Guid.NewGuid().ToString("N"));
        using var zip = ZipFile.OpenRead(args[2]);
        new UpdateTransaction(root, work).Apply(zip, new Version("0.4.0"), publicKey: File.ReadAllText(args[3]),
            afterWrite: count => { if (count == int.Parse(args[4])) Environment.Exit(77); });
        throw new Exception("Crash point was not reached");
    }
    public static async Task Interrupt(string root, string package, string key, int count)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(UpdateCrashTests).Assembly.Location);
        foreach (string arg in new[] { "--crash-update", root, package, key, count.ToString() }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(limit.Token);
            if (process.ExitCode != 77) throw new Exception("Updater interruption failed: " + await output + await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(output, error);
        }
    }
}
