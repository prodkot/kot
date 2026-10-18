using Kot.Core;
using Kot.Windows;
namespace Kot.WindowTests;

public static class XrayInputChecks
{
    public static async Task Run(string executable, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string folder = Path.Combine(Path.GetTempPath(), "kot-input-" + Guid.NewGuid().ToString("N"), "профиль с пробелами 出口");
        Directory.CreateDirectory(folder);
        string core = Path.Combine(folder, "xray.exe"); File.Copy(executable, core);
        const string json = """{"log":{"loglevel":"warning"},"outbounds":[{"protocol":"freedom","tag":"прямой 出口"}]}""";
        string large = new string(' ', 512 * 1024) + json;
        try
        {
            // Reproduce the reported failure class with the same executable.
            string missing = Path.Combine(folder, "missing-profile", "runtime.json");
            using (var old = new NativeCore(core, ["run", "-test", "-c", missing], folder, "missing file regression"))
            {
                await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
                check(old.ExitCode == 23 && (await old.ReadRecentOutput(true)).Contains("failed to read config"),
                    "real Xray reproduces the missing runtime.json startup failure");
            }
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => XraySession.Check(core, large, folder, text => text, deadline.Token)));
            check(!Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).Any(),
                "real Xray validates large Unicode stdin from a Unicode installation without a runtime file");
            bool rejected = false;
            try { await XraySession.Check(core, "{\"outbounds\":[{\"protocol\":\"fixture-invalid\"}]}", folder, text => text, deadline.Token); }
            catch (UserError ex) { rejected = ex.Message.Contains("Xray не принял конфигурацию") && !ex.Message.Contains("failed to read config:"); }
            check(rejected, "stdin validation still rejects an invalid outbound instead of masking configuration errors");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { await XraySession.Check(core, large, folder, text => text, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped, "cancelled validation never launches another stdin worker");
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, true); }
    }
}
