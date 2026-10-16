using Kot.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Kot.Tests;

public static class NetworkRequestTests
{
    public static async Task Run(Action<bool, string> check, string folder)
    {
        async Task ThroughProxy(string name, string host, Func<int, CancellationToken, Task> fetch)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            async Task<string> Accept()
            {
                using var peer = await listener.AcceptTcpClientAsync(limit.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string first = await reader.ReadLineAsync(limit.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(limit.Token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), limit.Token);
                return first;
            }
            var incoming = Accept();
            bool failed = false;
            try { await fetch(port, limit.Token); }
            catch (HttpRequestException) { failed = true; }
            string request = await incoming;
            check(failed && request.StartsWith("CONNECT " + host + ":443 ", StringComparison.Ordinal),
                name + " reaches the active loopback proxy and reports its failure without a direct retry");
        }
        await ThroughProxy("subscription", "subscription.invalid", async (port, ct) =>
            await Subscriptions.Download("https://subscription.invalid/sub", ct, proxyPort: port));
        await ThroughProxy("routing profile", "rules.invalid", async (port, ct) =>
            await RoutingProfiles.Download("https://rules.invalid/rules", ct, proxyPort: port));
        await ThroughProxy("release check", "api.github.com", async (port, ct) =>
            await RemoteUpdates.Check(new Version("0.7.0"), ct, proxyPort: port));
        await ThroughProxy("release download", "release.invalid", async (port, ct) =>
        {
            using var output = new MemoryStream();
            await RemoteUpdates.Download(new("0.7.1", "https://release.invalid/setup.exe", 1), output, ct, proxyPort: port);
        });
        await ThroughProxy("managed service check", "api.github.com", async (port, ct) =>
            await ServicePackages.Latest("telegram", ct, proxyPort: port));
        string path = Path.Combine(folder, "proxy-service-download.exe");
        await ThroughProxy("managed service download", "github.com", (port, ct) =>
            ServicePackages.Download(new("telegram", "v1.0.0", new("https://github.com/Flowseal/tg-ws-proxy/releases/download/v1.0.0/TgWsProxy_windows.exe"), 1, new string('0', 64), new("https://github.com/Flowseal/tg-ws-proxy")), path, null, ct, proxyPort: port));
        check(!File.Exists(path), "failed proxied service download leaves no partial executable");
    }
}
