using Kot.Core;
using Kot.Windows;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
namespace Kot.WindowTests;

// A local TLS origin and an offline VPN make the selected direct route observable.
// The real builder supplies DNS, sniffing, outbound options and the Windows TUN.
public static class RoutingChecks
{
    static async Task<string> Request(string host, int originPort, X509Certificate2 certificate, CancellationToken ct, int proxyPort = 0)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = new TcpClient();
        await client.ConnectAsync(proxyPort == 0 ? host : "127.0.0.1", proxyPort == 0 ? originPort : proxyPort, deadline.Token);
        using var stream = client.GetStream();
        if (proxyPort != 0)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{originPort} HTTP/1.1\r\nHost: {host}:{originPort}\r\n\r\n"), deadline.Token);
            using var headers = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? status = await headers.ReadLineAsync(deadline.Token);
            if (status?.Contains(" 200 ") != true) throw new Exception("CONNECT rejected: " + status);
            while (await headers.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
        }
        // Trust exactly this ephemeral fixture certificate, never arbitrary TLS.
        using var tls = new SslStream(stream, false, (_, peer, _, _) => peer != null && peer.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, deadline.Token);
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"), deadline.Token);
        using var response = new StreamReader(tls, Encoding.ASCII);
        return await response.ReadToEndAsync(deadline.Token);
    }

    public static async Task Run(string xray, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65)); var ct = deadline.Token;
        using var key = RSA.Create(2048);
        string id = Guid.NewGuid().ToString("N");
        string[] proxyHosts = [$"proxy-{id}.ru", $"proxy-{id}.xn--p1ai"];
        string[] tunHosts = [$"tun-{id}.ru", $"tun-{id}.xn--p1ai"];
        var request = new CertificateRequest("CN=kot routing fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); foreach (string host in proxyHosts.Concat(tunHosts)) san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel needs an imported key container. Without PersistKeySet the
        // temporary container is removed when this certificate is disposed.
        using var certificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        using var web = new TcpListener(IPAddress.Loopback, 0); web.Start();
        int webPort = ((IPEndPoint)web.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await web.AcceptTcpClientAsync(ct);
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct); connection.CancelAfter(TimeSpan.FromSeconds(8));
                    using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, connection.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, true);
                    while (await reader.ReadLineAsync(connection.Token) is { Length: > 0 }) { }
                    await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 12\r\nConnection: close\r\n\r\ndirect-route"u8.ToArray(), connection.Token);
                }
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }, ct);
        using var health = LoopbackPort.Reserve(); using var proxy = LoopbackPort.Reserve(); using var offline = LoopbackPort.Reserve();
        var node = Subscriptions.ParseNode($"vless://11111111-1111-4111-8111-111111111111@127.0.0.1:{offline.Port}?security=none");
        var vpn = XrayConfig.Outbound(node); vpn["tag"] = "vpn";
        var hosts = new JsonObject();
        foreach (string host in proxyHosts) hosts[host] = new JsonArray("127.0.0.2", "::1", "127.0.0.1");
        // These addresses enter Wintun. The fixture direct outbound redirects to
        // the local TLS origin only after the SNI domain rule has matched.
        foreach (string host in tunHosts) hosts[host] = "192.0.2.123";
        var provider = new JsonObject {
            ["inbounds"] = new JsonArray(),
            ["outbounds"] = new JsonArray(vpn, new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "tun-direct", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = "127.0.0.1:" + webPort } }),
            ["dns"] = new JsonObject { ["hosts"] = hosts, ["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = offline.Port }) },
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = new JsonArray(
                new JsonObject { ["type"] = "field", ["domain"] = new JsonArray(tunHosts.Select(h => (JsonNode?)JsonValue.Create("full:" + h)).ToArray()), ["outboundTag"] = "tun-direct" },
                new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("domain:ru", "domain:xn--p1ai"), ["outboundTag"] = "direct" }) }
        };
        NativeCore? core = null;
        try
        {
            var config = XrayRuntime.Build(node with { FullConfig = provider }, health.Port, tun: true, proxyPort: proxy.Port);
            string json = config.ToJsonString();
            await XraySession.Check(xray, json, Path.GetDirectoryName(xray)!, s => s, ct);
            KillSwitch.ArmForTests(Environment.ProcessPath!, xray);
            health.Dispose(); proxy.Dispose();
            core = XraySession.Start(xray, json, Path.GetDirectoryName(xray)!, "Routing fixture", Console.WriteLine);
            bool ready = false;
            for (int i = 0; i < 100 && !core.HasExited; i++)
            {
                ready = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == "kot-tun" && n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().DnsAddresses.Contains(IPAddress.Parse("172.28.231.2")));
                if (ready)
                {
                    using var socket = new TcpClient();
                    try { await socket.ConnectAsync(IPAddress.Loopback, proxy.Port, ct); break; }
                    catch (SocketException) { ready = false; }
                }
                await Task.Delay(100, ct);
            }
            check(ready && !core.HasExited, "routing fixture starts the production TUN with provider DNS and an offline VPN");
            KillSwitch.ArmForTests(Environment.ProcessPath!, xray, KillSwitch.TunnelInterface());
            foreach (string host in proxyHosts)
                check((await Request(host, webPort, certificate, ct, proxy.Port)).EndsWith("direct-route"), "HTTPS CONNECT follows split DNS and skips unavailable A/AAAA addresses with TUN/WFP active: " + host.Split('.').Last());
            foreach (string host in tunHosts)
                check((await Request(host, webPort, certificate, ct)).EndsWith("direct-route"), "Windows DNS, TUN and TLS SNI select the direct route while VPN is offline: " + host.Split('.').Last());
            check(KillSwitch.Detect(), "direct routing never disables the WFP guard");
        }
        finally
        {
            try { if (core != null) { try { await core.Stop(); } finally { core.Dispose(); } } }
            finally
            {
                KillSwitch.Release(); deadline.Cancel(); web.Stop();
                await serve.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        check(!KillSwitch.Detect(), "routing fixture releases only its own filters on teardown");
    }
}
