using Kot.Core;
namespace Kot.Windows;

static class ProviderAssets
{
    public static readonly XrayAssets Cache = new(Path.Combine(Store.Folder, "xray-assets"));
    public static async Task Prepare(IEnumerable<Node> nodes, CancellationToken ct, int proxyPort = 0)
    {
        var candidates = nodes.Where(n => n.FullConfig != null).DistinctBy(n => Cache.DirectoryFor(n) + string.Join(',', XrayConfig.GeoFiles(n.FullConfig!))).ToArray();
        if (candidates.Length == 0) { ct.ThrowIfCancellationRequested(); return; }
        var selected = await Task.Run(() => candidates.Where(n => !Cache.Available(n)).ToArray(), ct);
        if (KillSwitch.Active && proxyPort == 0 && selected.Length > 0) throw new UserError("Для первой загрузки геобаз подписки подключитесь к VPN или временно отключите Kill switch. Сохранённые базы работают без загрузки.");
        using var handler = NetworkRequests.Handler(proxyPort);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        await Task.Run(() => Cache.Prepare(candidates, client, ct), ct);
    }
}
