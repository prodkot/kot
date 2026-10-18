using Kot.Core;
namespace Kot.Windows;

// Configuration validation uses the same native core as actual connections.
public static class XraySession
{
    public static Task<SubscriptionResult> Validate(SubscriptionResult result, CancellationToken ct)
        => Validate(result, ct, Path.Combine(AppContext.BaseDirectory, "core", "xray.exe"));

    internal static async Task<SubscriptionResult> Validate(SubscriptionResult result, CancellationToken ct, string executable)
    {
        if (!File.Exists(executable)) throw new UserError("Ядро Xray не найдено. Переустановите kot.");
        using var port = LoopbackPort.Reserve(ct);
        var clean = CoreDiagnostics.CreateRedactor(result.Nodes);
        return await SubscriptionValidation.Filter(result, (node, token) => Rejection(executable,
            XrayRuntime.Build(node, port.Port).ToJsonString(), ProviderAssets.Cache.DirectoryFor(node), clean, token), ct);
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
        string? rejection = await Rejection(executable, config, assets, clean, ct);
        if (rejection != null) throw new UserError("Xray не принял конфигурацию: " + rejection);
    }
    static async Task<string?> Rejection(string executable, string config, string? assets, Func<string, string> clean, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var check = Start(executable, config, assets, "kot. Xray configuration check", text => AppLog.Write("xray-check", clean(text)), test: true);
        try { await check.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { await check.Stop(); throw; }
        if (check.ExitCode == 0) return null;
        string reason = CoreDiagnostics.Summary(clean(await check.ReadRecentOutput(true)));
        if (check.ExitCode == 23) return reason;
        throw new UserError("Проверка Xray прервалась (код " + check.ExitCode + "): " + reason);
    }
}
