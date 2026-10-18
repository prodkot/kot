using Kot.Core;
namespace Kot.Windows;

// Configuration validation uses the same native core as actual connections.
public static class XraySession
{
    public static async Task Validate(IReadOnlyList<Node> nodes, CancellationToken ct)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
        if (!File.Exists(executable)) throw new UserError("Ядро Xray не найдено. Переустановите kot.");
        using var port = LoopbackPort.Reserve(ct);
        var clean = CoreDiagnostics.CreateRedactor(nodes);
        foreach (var node in nodes)
        {
            await Check(executable, XrayRuntime.Build(node, port.Port).ToJsonString(), ProviderAssets.Cache.DirectoryFor(node), clean, ct);
        }
    }
    internal static NativeCore Start(string executable, string config, string? assets, string title, Action<string> report, bool test = false)
    {
        // Use Xray's native stdin loader for both validation and traffic. The
        // exact same JSON reaches each child without reopening a profile path
        // or leaving credentials on disk. Asset resolution is still explicit.
        string[] arguments = test ? ["run", "-test", "-format", "json", "-c", "stdin:"] : ["run", "-format", "json", "-c", "stdin:"];
        return new NativeCore(executable, arguments, Path.GetDirectoryName(executable)!, title, report, assets, config);
    }
    internal static async Task Check(string executable, string config, string? assets, Func<string, string> clean, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var check = Start(executable, config, assets, "kot. Xray configuration check", text => AppLog.Write("xray-check", clean(text)), test: true);
        try { await check.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { await check.Stop(); throw; }
        if (check.ExitCode != 0) throw new UserError("Xray не принял конфигурацию: " + CoreDiagnostics.Summary(clean(await check.ReadRecentOutput(true))));
    }
}
