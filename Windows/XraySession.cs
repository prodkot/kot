using Kot.Core;
namespace Kot.Windows;

// Configuration validation uses the same native core as actual connections.
public static class XraySession
{
    public static async Task Validate(IReadOnlyList<Node> nodes, CancellationToken ct)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
        if (!File.Exists(executable)) throw new UserError("Ядро Xray не найдено. Переустановите kot.");
        Directory.CreateDirectory(Store.Folder);
        using var port = LoopbackPort.Reserve(ct);
        var clean = CoreDiagnostics.CreateRedactor(nodes);
        foreach (var node in nodes)
        {
            string path = Path.Combine(Store.Folder, "xray-check-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await File.WriteAllTextAsync(path, XrayRuntime.Build(node, port.Port).ToJsonString(), ct);
                await Check(executable, path, ProviderAssets.Cache.DirectoryFor(node), clean, ct);
            }
            finally { File.Delete(path); }
        }
    }
    internal static async Task Check(string executable, string path, string? assets, Func<string, string> clean, CancellationToken ct)
    {
        using var check = new NativeCore(executable, ["run", "-test", "-c", path], Path.GetDirectoryName(executable)!, "kot. Xray configuration check", text => AppLog.Write("xray-check", clean(text)), assets);
        try { await check.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { await check.Stop(); throw; }
        if (check.ExitCode != 0) throw new UserError("Xray не принял конфигурацию: " + CoreDiagnostics.Summary(clean(await check.ReadRecentOutput(true))));
    }
}
