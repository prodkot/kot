using Kot.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
namespace Kot.Tests;

public static class NetworkRequestTests
{
    public static async Task Authentication(Action<bool, string> check)
    {
        bool invalid = false;
        try { await Latency.Http("http://target.invalid/", 0, 100, CancellationToken.None); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        check(invalid, "HTTP latency with no active proxy fails before attempting a direct request");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string secret = new('s', 64);
        using var registration = NetworkRequests.RegisterProxy(port, secret);
        using var handler = NetworkRequests.Handler(port);
        var credentials = handler.Proxy!.Credentials!.GetCredential(new Uri("http://127.0.0.1:" + port), "Basic") ?? throw new Exception("Missing proxy credentials");
        check(credentials.UserName == "kot" && credentials.Password == secret, "private core requests obtain the current listener credentials");
        var server = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var peer = await listener.AcceptTcpClientAsync(limit.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string headers = "", line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(limit.Token) ?? "")) headers += line + "\n";
                if (attempt == 1) check(headers.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("kot:" + secret))), "HTTP proxy challenge is answered with the session key");
                string response = attempt == 0 ? "407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"kot\"" : "204 No Content";
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + response + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), limit.Token);
            }
        });
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("http://target.invalid/check", limit.Token);
        await server;
        check(response.StatusCode == HttpStatusCode.NoContent, "private health proxy authentication works on a real loopback connection");
        registration.Dispose();
        using var later = NetworkRequests.Handler(port);
        check(later.Proxy!.Credentials == null && handler.Proxy!.Credentials!.GetCredential(new Uri("http://127.0.0.1:" + port), "Basic")?.Password == secret,
            "stopping a core removes its registry entry while existing requests retain their own credentials");
        using var replacement = NetworkRequests.RegisterProxy(port, new string('t', 64));
        registration.Dispose();
        using var nextSession = NetworkRequests.Handler(port);
        check(nextSession.Proxy!.Credentials!.GetCredential(new Uri("http://127.0.0.1:" + port), "Basic")?.Password == new string('t', 64),
            "disposing an old listener twice cannot remove a replacement session's credentials");
    }
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
