using Kot.Core;
namespace Kot.Windows;

static class ProviderAssets
{
    public static readonly XrayAssets Cache = new(Path.Combine(Store.Folder, "xray-assets"));
    public static async Task Prepare(IEnumerable<Node> nodes, CancellationToken ct, int proxyPort = 0)
    {
        var selected = nodes.Where(n => !Cache.Available(n)).ToArray();
        if (selected.Length == 0) return;
        if (KillSwitch.Active && proxyPort == 0) throw new UserError("Для первой загрузки геобаз подписки подключитесь к VPN или временно отключите Kill switch. Сохранённые базы работают без загрузки.");
        using var handler = NetworkRequests.Handler(proxyPort);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        await Cache.Prepare(selected, client, ct);
    }
}
