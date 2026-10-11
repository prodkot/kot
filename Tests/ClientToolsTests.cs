using Kot.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Kot.Tests;

public static class ClientToolsTests
{
    const string Uuid = "11111111-1111-4111-8111-111111111111";
    const string Link = "vless://" + Uuid + "@example.com:443?security=tls&sni=example.com#Тест";
    static bool Reject(Action action) { try { action(); return false; } catch (UserError) { return true; } }
    static async Task<bool> RejectAsync(Func<Task> action) { try { await action(); return false; } catch (UserError) { return true; } }
    public static async Task Run(Action<bool, string> check, string configurations, string folder)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        long expire = now.AddDays(2).ToUnixTimeSeconds();
        var usage = SubscriptionUsage.Parse($"Upload = 10; download=20; total=100; expire={expire}; provider=x", now)!;
        check(usage.Used == 30 && usage.Remaining == 70 && usage.DaysLeft(now) == 2, "provider quota and expiry are parsed independently of local statistics");
        check(usage.DaysLeft(now.AddDays(3)) == -1 && usage.DaysLeft(now.AddHours(1)) == 2, "subscription expiry handles past dates and partial days");
        check(SubscriptionUsage.Parse("total=0;expire=0", now) is { Used: null, Remaining: null, Expires: null }, "unknown and zero provider quota are never guessed");
        check(SubscriptionUsage.Parse("download=10;total=100", now)?.Used == null, "incomplete provider counters do not invent usage");
        check(SubscriptionUsage.Parse($"upload={long.MaxValue};download=1;total=10", now)?.Used == long.MaxValue && SubscriptionUsage.Parse("upload=20;download=30;total=10", now)?.Remaining == 0, "provider quota clamps overflow and exhausted allowance");
        foreach (string header in new[] { "upload=-1", "download=wrong", "upload=1;UPLOAD=2", $"expire={long.MaxValue}", new string('x', 4097), "nothing=3" })
            check(SubscriptionUsage.Parse(header, now) == null, "malformed provider metadata is ignored without breaking servers");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Link) };
        response.Headers.TryAddWithoutValidation("Subscription-Userinfo", $"upload=10;download=20;total=100;expire={expire}");
        var fetched = await Subscriptions.Download("https://provider.example/sub", CancellationToken.None, new Transport(_ => response));
        check(fetched.Nodes.Count == 1 && fetched.Usage?.Used == 30, "HTTPS subscription carries provider metadata with its nodes");

        string yaml = """
        defaults: &common {server: example.com, port: 443}
        proxies:
          - {<<: *common, name: VLESS, type: vless, uuid: 'UUID_VALUE', tls: true, servername: example.com}
          - {<<: *common, name: VMess, type: vmess, uuid: 'UUID_VALUE', alterId: 0, cipher: auto, tls: true, network: ws, ws-opts: {path: /ws, headers: {Host: example.com}, max-early-data: 2048}}
          - {<<: *common, name: Trojan, type: trojan, password: 'p@ss:word', sni: example.com}
          - {<<: *common, name: SS, type: ss, cipher: aes-128-gcm, password: secret}
          - {<<: *common, name: Hysteria, type: hysteria2, password: secret, obfs: salamander, obfs-password: secret2, sni: example.com}
          - {<<: *common, name: HTTP, type: vless, uuid: 'UUID_VALUE', network: http, http-opts: {path: [/tunnel], headers: {Host: [one.example, two.example]}}}
          - {<<: *common, name: gRPC, type: vless, uuid: 'UUID_VALUE', tls: true, network: grpc, grpc-opts: {grpc-service-name: proxy}}
          - {<<: *common, name: VLESS, type: vless, uuid: 'UUID_VALUE', tls: true, servername: example.com}
          - {<<: *common, name: Unsupported, type: socks5}
          - {<<: *common, name: Unsafe, type: trojan, password: secret, skip-cert-verify: true}
          - {<<: *common, name: Plugin, type: ss, cipher: aes-128-gcm, password: secret, plugin: obfs}
          - {<<: *common, name: Pinned, type: trojan, password: secret, fingerprint: abc}
        rules: [MATCH,DIRECT]
        proxy-providers: {remote: {url: 'https://foreign.example/providers.yaml'}}
        """.Replace("UUID_VALUE", Uuid);
        var imported = Subscriptions.Parse(yaml);
        check(imported.Nodes.Count == 7 && imported.Warnings.Count == 4, "Clash imports supported protocols and reports unsupported or insecure nodes");
        check(imported.Nodes[1].Outbound["transport"]?["max_early_data"]?.ToString() == "2048", "Clash WebSocket early data survives conversion");
        check(imported.Nodes[5].Outbound["transport"]?["host"]?.AsArray().Count == 2, "Clash HTTP Host arrays survive conversion");
        check(imported.Nodes.All(n => n.Outbound["rules"] == null && n.Outbound["proxy-providers"] == null), "Clash imports no foreign rules, listeners or remote providers");
        foreach (var node in imported.Nodes)
        {
            var roundtrip = Subscriptions.ParseNode(NodeLinks.Export(node));
            check(JsonNode.DeepEquals(roundtrip.Outbound, node.Outbound) && roundtrip.Name == node.Name, "share link preserves credentials and transport: " + node.Name);
            File.WriteAllText(Path.Combine(configurations, "clash-" + node.Id[..8] + ".json"), Configuration.Build(node, 19223, false, [], tun: false).ToJsonString());
        }
        check(Subscriptions.Parse($"{{proxies: [{{type: vless, server: example.com, port: 443, uuid: '{Uuid}'}}]}}").Nodes.Count == 1, "flow-style Clash YAML is recognized");
        check(Subscriptions.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml))).Nodes.Count == 7, "base64 Clash subscriptions are recognized after decoding");
        foreach (string invalid in new[] { "proxies: [", "proxies: []", "proxy-providers: {}", "proxies: &self [*self]", "proxies: [{type: ss, server: example.com, port: 443, cipher: aes-128-gcm, password: secret, plugin: bad}]", "---\nproxies: []\n---\nproxies: []", "proxies: [" + new string('[', 33) + new string(']', 33) + "]" })
            check(Reject(() => Subscriptions.Parse(invalid)), "invalid or recursive YAML is rejected with a readable error");
        string ipv6 = $"trojan://p%40ss%3Aword@[2001:db8::1]:443?sni=example.com#IPv6";
        check(JsonNode.DeepEquals(Subscriptions.ParseNode(ipv6).Outbound, Subscriptions.ParseNode(NodeLinks.Export(Subscriptions.ParseNode(ipv6))).Outbound), "QR export preserves IPv6 and escaped credentials");
        var profile = new Profile { Subscriptions = [new() { Local = true, Nodes = imported.Nodes, Name = "Файл" }, new() { Address = "https://provider.example/sub", Name = "Провайдер", Nodes = fetched.Nodes, Usage = usage }] };
        profile.Normalize(); profile.Activate(profile.Subscriptions[1].Id); profile.CaptureActive();
        var restored = Backup.Import(Backup.Export(profile, "tools-password"), "tools-password");
        check(restored.Subscriptions[0].Local && restored.Subscriptions[0].Address == "" && restored.SubscriptionUsage == usage && restored.Nodes.Count == 1, "encrypted backup restores local imports and provider metadata");

        var matrix = QrCodes.Encode(Link, 320); var rgb = Pixels(matrix);
        check(QrCodes.Decode(rgb, matrix.Width, matrix.Height) == Link, "real QR codec roundtrip preserves Unicode server names");
        byte[] rotated = new byte[rgb.Length]; int width = matrix.Width;
        for (int y = 0; y < width; y++) for (int x = 0; x < width; x++) Array.Copy(rgb, (y * width + x) * 3, rotated, (x * width + width - 1 - y) * 3, 3);
        check(QrCodes.Decode(rotated, width, width) == Link, "QR import handles a rotated screenshot");
        var other = QrCodes.Encode("https://provider.example/sub", 320); byte[] two = new byte[width * width * 2 * 3];
        var right = Pixels(other); for (int y = 0; y < width; y++) { Array.Copy(rgb, y * width * 3, two, y * width * 6, width * 3); Array.Copy(right, y * width * 3, two, y * width * 6 + width * 3, width * 3); }
        check(Reject(() => QrCodes.Decode(two, width * 2, width)), "two different QR codes require cropping instead of silently choosing one");
        check(Reject(() => QrCodes.Decode(new byte[300], 10, 10)) && Reject(() => QrCodes.Decode([], 5000, 5000)), "QR import rejects missing codes and oversized images");
        foreach (string bad in new[] { "file:///secret", "http://provider.example/sub", "https://user:pass@example.com/sub", "javascript:alert(1)", Link + "\n" }) check(Reject(() => QrCodes.ValidatePayload(bad)), "QR accepts only supported links and HTTPS subscriptions");

        var summary = ConnectionTools.Summarize([100, 140, null, 120, 80]);
        check(summary is { Attempts: 5, Successes: 4, MedianMs: 110, MinMs: 80, MaxMs: 140 } && summary.JitterMs == 100d / 3, "quality summary separates failures, median and variation");
        check(ConnectionTools.Summarize([null, null]).MedianMs == null && ConnectionTools.Summarize([int.MaxValue, int.MaxValue]).MedianMs == int.MaxValue, "failed quality tests and large delays never fabricate a ping");
        foreach (string bad in new[] { "http://example.com", "https://a:b@example.com", "https://example.com/#fragment", "file:///tmp" }) check(Reject(() => ConnectionTools.Website(bad)), "diagnostics website accepts an uncredentialed HTTPS URL only");
        using (var client = Client("{\"ip\":\"2001:db8::1\"}")) check(await ConnectionTools.PublicIp(client, CancellationToken.None) == "2001:db8::1", "diagnostics accepts a validated IPv6 external address");
        using (var client = Client("{\"ip\":\"<script>\"}")) check(await RejectAsync(async () => { await ConnectionTools.PublicIp(client, CancellationToken.None); }), "diagnostics rejects a forged external IP");
        using (var client = Client("{\"Status\":0,\"Answer\":[{\"type\":5,\"data\":\"alias.example\"},{\"type\":1,\"data\":\"203.0.113.1\"}]}")) check(await ConnectionTools.DnsThroughVpn(client, "example.com", CancellationToken.None) == "203.0.113.1", "VPN DNS ignores CNAME records and validates returned addresses");
        using (var client = Client("{\"Status\":3}")) check(await RejectAsync(async () => { await ConnectionTools.DnsThroughVpn(client, "example.com", CancellationToken.None); }), "VPN DNS reports NXDOMAIN");
        using (var client = new HttpClient(new Transport(_ => new(HttpStatusCode.Found) { Content = new StringContent(""), Headers = { Location = new Uri("https://other.example") } }))) check(await RejectAsync(async () => { await ConnectionTools.PublicIp(client, CancellationToken.None); }), "diagnostics refuses a redirected check service");
        foreach (int length in new[] { ConnectionTools.SpeedBytes - 1, ConnectionTools.SpeedBytes, ConnectionTools.SpeedBytes + 1 })
        {
            using var client = new HttpClient(new Transport(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new Nonseekable(new byte[length])) }));
            if (length == ConnectionTools.SpeedBytes) { long progress = 0; var sample = await ConnectionTools.Download(client, CancellationToken.None, n => progress = n); check(sample.Bytes == length && sample.MegabitsPerSecond > 0 && progress == length, "speed test downloads exactly one MiB with progress"); }
            else check(await RejectAsync(async () => { await ConnectionTools.Download(client, CancellationToken.None); }), "speed test rejects short and oversized streamed responses");
        }
        using (var cancel = new CancellationTokenSource())
        using (var client = new HttpClient(new Transport(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[ConnectionTools.SpeedBytes]) })))
        { cancel.Cancel(); bool stopped = false; try { await ConnectionTools.Download(client, cancel.Token); } catch (OperationCanceledException) { stopped = true; } check(stopped, "speed test honors cancellation"); }
    }
    static HttpClient Client(string json) => new(new Transport(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
    static byte[] Pixels(ZXing.Common.BitMatrix matrix)
    {
        byte[] rgb = new byte[matrix.Width * matrix.Height * 3];
        for (int y = 0; y < matrix.Height; y++) for (int x = 0; x < matrix.Width; x++) { int offset = (y * matrix.Width + x) * 3; rgb[offset] = rgb[offset + 1] = rgb[offset + 2] = matrix[x, y] ? (byte)0 : (byte)255; }
        return rgb;
    }
    sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); } }
    sealed class Nonseekable(byte[] bytes) : Stream
    {
        readonly MemoryStream stream = new(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => stream.ReadAsync(buffer, ct);
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
}
