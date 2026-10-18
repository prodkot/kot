using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kot.Core;
namespace Kot.Tests;

static class ProviderRoutingCompatibilityTests
{
    const string Link = "vless://c9a83ccf-d2e9-4ef2-a8b4-b1b8cb5f52a1@127.0.0.1:19441?security=none#Provider";
    static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    static Node Import(string value) => Subscriptions.Parse(Link + "\n#routing: " + Encode(value)).Nodes.Single();
    public static async Task Run(Action<bool, string> check)
    {
        // Fields emitted by https://routing.happ.su/en, including its defaults.
        foreach (string metadata in new[] { "\"RouteOrder\":\"block-proxy-direct\"", "\"LastUpdated\":\"\"", "\"UseChunkFiles\":true" })
            check(Import("{" + metadata + ",\"DirectSites\":[\".ru\"]}").FullConfig != null, "provider builder metadata imports: " + metadata);
        var legacy = Import("""{"RemoteDns":"","DomesticDns":"","DirectSites":[".ru"]}""");
        check(legacy.FullConfig!["dns"]!["servers"]!.AsArray().Count == 2, "empty legacy DNS fields use defaults");
        using var handler = new Handler((_, i) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(i == 0 ? Link : """{"DirectSites":[".ru"],"RemoteDNSDomain":"https://cloudflare-dns.com/dns-query"}""") };
            if (i == 0) response.Headers.Add("autorouting", "happ://autorouting/onadd/https://rules.example/profile.json");
            return response;
        });
        var remote = await Subscriptions.Download("https://subscription.example/fixture", CancellationToken.None, handler);
        check(remote.Nodes.Single().FullConfig!["routing"]!["rules"]!.ToJsonString().Contains("domain:ru"), "remote raw JSON can contain HTTPS DNS URLs");
        foreach (string order in new[] { "block-proxy-direct", "block-direct-proxy", "proxy-block-direct", "proxy-direct-block", "direct-proxy-block", "direct-block-proxy" })
        {
            var policy = JsonNode.Parse("""{"DirectSites":["route-order.ru"],"ProxySites":["route-order.ru"],"BlockSites":["route-order.ru"],"DirectIp":["127.0.0.1"],"ProxyIp":["127.0.0.1"],"BlockIp":["127.0.0.1"],"DnsHosts":{"route-order.ru":"127.0.0.1"}}""")!.AsObject();
            policy["RouteOrder"] = order; policy["LastUpdated"] = 1760000000; policy["UseChunkFiles"] = true;
            var node = Import(policy.ToJsonString());
            var runtime = XrayRuntime.Build(node, 19442, proxyPort: 19443);
            var actions = runtime["routing"]!["rules"]!.AsArray().Where(r => r?["domain"] != null || r?["ip"] != null).Select(r => r!["outboundTag"]!.ToString());
            check(actions.SequenceEqual(order.Split('-').SelectMany(tag => new[] { tag, tag })), "native runtime preserves domain/IP precedence: " + order);
            Directory.CreateDirectory("Tests/generated/native-xray");
            File.WriteAllText("Tests/generated/native-xray/provider-order-" + order + ".json", runtime.ToJsonString());
        }
        foreach (string invalid in new[] { """{"RouteOrder":"direct-direct-proxy"}""", """{"RouteOrder":"proxy-direct"}""", """{"RouteOrder":"anything-proxy-direct"}""", """{"LastUpdated":"yesterday"}""", """{"LastUpdated":-1}""", """{"UseChunkFiles":"invalid"}""", """{"LastUpdated":1,"LastUpdatedDate":2}""" })
        {
            bool rejected = false; try { Import(invalid); } catch (UserError) { rejected = true; }
            check(rejected, "malformed provider metadata cannot silently change routing: " + invalid);
        }
        // Precedence is resolved once, before parsing lower-priority metadata.
        for (int winner = 0; winner < 4; winner++)
        {
            string Metadata(int position) => position == winner ? Encode("""{"DirectSites":[".ru"]}""") : "invalid";
            using var precedence = new Handler((_, _) =>
            {
                string body = Link + (winner <= 1 ? "\n#autorouting: " + Metadata(1) : "") + "\n#routing: " + Metadata(3);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
                if (winner == 0) response.Headers.Add("autorouting", Metadata(0));
                if (winner <= 2) response.Headers.Add("routing", Metadata(2));
                return response;
            });
            var selected = await Subscriptions.Download("https://subscription.example/priority", CancellationToken.None, precedence);
            check(selected.Nodes[0].FullConfig!["routing"]!["rules"]!.ToJsonString().Contains("domain:ru"), "provider source priority level " + winner);
        }
        var versioned = Import("""{"DirectSites":["geosite:ru"],"DirectIp":["geoip:ru"],"LastUpdated":"1760000000"}""");
        check(versioned.GeoRevision == 1760000000 && Import("""{"LastUpdatedDate":1760000000}""").GeoRevision == versioned.GeoRevision, "both documented Unix revision fields are accepted");
        var full = XrayConfig.Parse(versioned.FullConfig!.ToJsonString());
        using (var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException())))
        {
            var result = await ProviderRouting.Resolve(full, Encode("""{"DirectSites":["changed.example"],"RouteOrder":"direct-proxy-block","LastUpdated":1760000001}"""), null, client, CancellationToken.None);
            check(JsonNode.DeepEquals(result.Nodes[0].FullConfig, full.Nodes[0].FullConfig) && result.Nodes[0].GeoRevision == 1760000001, "full Xray JSON keeps its own rules while accepting provider geo revision");
        }
        var profile = new Profile { Local = true, Nodes = [versioned], Selected = versioned.Id }; profile.CaptureActive();
        var restored = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!; restored.Validate();
        check(restored.Nodes.Single().GeoRevision == versioned.GeoRevision && JsonNode.DeepEquals(restored.Nodes[0].FullConfig, versioned.FullConfig), "provider rules and geo revision survive saving and subscription activation");
        string folder = Path.Combine("Tests/generated", "provider-revision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new XrayAssets(folder); int requests = 0;
            using var client = new HttpClient(new Handler((_, _) => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent([10, 2, 1, 0]) }; }));
            await cache.Prepare([versioned], client, CancellationToken.None);
            await cache.Prepare([versioned], client, CancellationToken.None);
            check(requests == 2 && cache.Available(versioned), "unchanged provider version reuses both geo files offline");
            var next = versioned with { GeoRevision = versioned.GeoRevision + 1 };
            using var broken = new HttpClient(new Handler((_, i) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(i == 0 ? [10, 2, 2, 0] : [60, 104, 116, 109, 108]) }));
            bool rejected = false; try { await cache.Prepare([next], broken, CancellationToken.None); } catch (UserError) { rejected = true; }
            check(rejected && cache.Available(versioned) && !cache.Available(next)
                && File.ReadAllBytes(Path.Combine(cache.DirectoryFor(versioned), "geoip.dat")).SequenceEqual(new byte[] { 10, 2, 1, 0 }), "partial version update cannot overwrite the active provider databases");
            await cache.Prepare([next], client, CancellationToken.None);
            check(cache.Available(next) && requests == 3 && cache.DirectoryFor(next) != cache.DirectoryFor(versioned), "retry completes the new provider version at the same source URLs");
            check(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories).Length == 0, "provider revision downloads clean temporary files");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        int count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request, count++));
    }
}
