using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kot.Core;
namespace Kot.Tests;

static class ProviderRoutingTests
{
    const string Link = "vless://11111111-1111-4111-8111-111111111111@127.0.0.1:19451?security=none#Provider";
    static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    static string ProfileJson => """{"Name":"Provider","DirectSites":[".ru",".рф"],"BlockSites":["blocked.example"],"RemoteDNSType":"DoU","RemoteDNSIP":"127.0.0.3","DomesticDNSType":"DoU","DomesticDNSIP":"127.0.0.2","DomainStrategy":"AsIs"}""";
    public static async Task Run(Action<bool, string> check)
    {
        await ProviderRoutingCompatibilityTests.Run(check);
        var result = Subscriptions.Parse(Link + "\nincy://routing/onadd/" + Encode(ProfileJson));
        var node = result.Nodes.Single(); var config = node.FullConfig!;
        check(Xhttp.Uses(node) && !Xhttp.UsesTransport(node) && config != null, "provider rules automatically select official Xray without a manual profile");
        check(config!.ToJsonString().Contains("domain:xn--p1ai") && config.ToJsonString().Contains("domain:ru"), "provider .ru and .рф are suffix rules with IDN normalization");
        var dns = (JsonArray)config["dns"]!["servers"]!;
        check(dns[0]!["domains"]!.ToJsonString().Contains("xn--p1ai") && dns[0]!["tag"]!.ToString() == "provider-direct-dns", "provider direct domains use domestic DNS with an explicit direct route");
        check(config["dns"]!["disableFallbackIfMatch"]!.GetValue<bool>(), "empty IPv6 answers cannot send matched direct domains to the remote resolver");
        var again = Subscriptions.Parse(Encode(Link + "\n#routing: " + Encode(ProfileJson)));
        check(JsonNode.DeepEquals(again.Nodes[0].FullConfig, config), "metadata inside base64 subscriptions is recognized");
        using var overrideHandler = new Handler((request, i) =>
        {
            var response = Ok(Link + "\nincy://routing/onadd/not-valid-base64"); response.Headers.Add("routing", "off"); return response;
        });
        var off = await Subscriptions.Download("https://subscription.example/test", CancellationToken.None, overrideHandler);
        check(off.Nodes.Single().FullConfig == null, "routing header off overrides even malformed lower-priority body metadata");
        using var remote = new Handler((request, i) =>
        {
            if (i == 0)
            {
                var response = Ok(Link); response.Headers.Add("autorouting", "incy://autorouting/onadd/https://rules.example/routes.json"); response.Headers.Add("routing", "off"); return response;
            }
            check(request.RequestUri!.AbsoluteUri == "https://rules.example/routes.json" && !request.Headers.Contains("x-hwid"), "autorouting fetch never forwards subscription device identity");
            return Ok(Encode(ProfileJson));
        });
        var device = new SubscriptionDevice(SubscriptionDevice.HashMachineId("fixture"), "Windows", "10", ClientIdentity.DeviceName);
        var downloaded = await Subscriptions.Download("https://subscription.example/test", CancellationToken.None, remote, device);
        check(downloaded.Nodes[0].FullConfig != null && remote.Count == 2, "autorouting HTTPS has priority and accepts a base64 remote profile");
        using var downgrade = new Handler((_, i) => i == 0 ? WithHeader(Link, "autorouting", "https://rules.example/start") : new(HttpStatusCode.Found) { Headers = { Location = new Uri("http://rules.example/unsafe") } });
        bool rejected = false; try { await Subscriptions.Download("https://subscription.example/test", CancellationToken.None, downgrade); } catch (UserError) { rejected = true; }
        check(rejected, "autorouting cannot downgrade HTTPS on redirect");
        using var bad = new Handler((_, _) => WithHeader(Link, "routing", Encode("""{"DirectSites":"ru"}""")));
        rejected = false; try { await Subscriptions.Download("https://subscription.example/test", CancellationToken.None, bad); } catch (UserError) { rejected = true; }
        check(rejected, "invalid provider rules fail the entire refresh instead of silently replacing working rules");
        foreach (string text in new[] { """{"FakeDNS":true}""", """{"FakeDNS":"invalid"}""", """{"DirectApps":["browser.exe"]}""", """{"RemoteDNSType":"system"}""", """{"DirectSites":["https://example.com"]}""", """{"Geoipurl":"http://unsafe.example/geoip.dat"}""" })
        {
            rejected = false; try { Subscriptions.Parse(Link + "\n#routing: " + Encode(text)); } catch (UserError) { rejected = true; }
            check(rejected, "unsupported routing configuration is reported explicitly");
        }
        var full = (JsonObject)config.DeepClone(); full["remarks"] = "Full JSON";
        var raw = Subscriptions.Parse(full.ToJsonString()).Nodes[0];
        check(raw.Protocol == "xray" && raw.Name == "Full JSON" && JsonNode.DeepEquals(raw.FullConfig!["routing"], config["routing"]), "full Xray client JSON preserves routing and DNS");
        check(Subscriptions.Parse(Encode(new JsonArray(full.DeepClone(), full.DeepClone()).ToJsonString())).Nodes.Count == 1, "full JSON arrays and base64 wrappers deduplicate identical configs");
        var bridge = new XhttpBridge(19452, "fixture-user", new string('a', 64), 19454);
        var egress = new XhttpBridge(19453, "physical-user", new string('b', 64));
        var runtime = XrayConfig.Runtime(raw, bridge, egress);
        check(JsonNode.DeepEquals(full["dns"]!["servers"], runtime["dns"]!["servers"]) && JsonNode.DeepEquals(full["routing"]!["rules"], config["routing"]!["rules"]), "runtime patches do not mutate saved provider rules or DNS servers");
        check(((JsonArray)runtime["inbounds"]!).All(i => i!["listen"]!.ToString() == "127.0.0.1" && i["settings"]!["auth"]!.ToString() == "password"), "full JSON cannot open unauthenticated or public proxy listeners");
        check(runtime["routing"]!["rules"]![0]!["port"]!.ToString() == "53" && runtime["outbounds"]!.ToJsonString().Contains("kot-transport"), "provider DNS is intercepted inside Xray and physical sockets use the authenticated dialer");
        check(runtime["outbounds"]![4]!["streamSettings"] == null, "physical dialer cannot recursively proxy itself");
        var balanced = (JsonObject)full.DeepClone();
        balanced["routing"]!["balancers"] = new JsonArray(new JsonObject { ["tag"] = "provider-balance", ["selector"] = new JsonArray("proxy"), ["strategy"] = new JsonObject { ["type"] = "leastPing" } });
        balanced["observatory"] = new JsonObject { ["subjectSelector"] = new JsonArray("proxy"), ["probeURL"] = "https://www.gstatic.com/generate_204", ["probeInterval"] = "1m" };
        var balancedRuntime = XrayConfig.Runtime(raw with { FullConfig = balanced }, bridge, egress);
        check(balancedRuntime["stats"] != null && balancedRuntime["routing"]!["rules"]![0]!["outboundTag"]!.ToString() == "direct" && balancedRuntime["routing"]!["rules"]!.ToJsonString().Contains("\"balancerTag\":\"provider-balance\""), "balancer health uses its selector and DNS bootstrap cannot depend on observatory results");
        var directProfile = Subscriptions.Parse(Link + "\n#routing: " + Encode("""{"GlobalProxy":false,"ProxySites":["example.com"]}""")).Nodes[0];
        check(directProfile.FullConfig!["outbounds"]![0]!["protocol"]!.ToString() == "freedom" && directProfile.FullConfig["dns"]!["servers"]![0]!["domains"]![0]!.ToString() == "domain:example.com", "direct-by-default provider profiles retain remote DNS for explicit proxy domains");
        var bridges = new Dictionary<string, XhttpBridge> { [node.Id] = bridge };
        var sb = Configuration.Build(node, 19455, false, [], tun: false, xhttp: bridges); Xhttp.AddEgress(sb, egress);
        check(sb["dns"]!["final"]!.ToString() == "provider" && sb["route"]!["rules"]![3]!["outbound"]!.ToString() == "health-proxy", "TUN and proxy configurations use provider DNS with a separate forced-proxy health bridge");
        var auto = Configuration.BuildAutomatic([node], 19455, false, [], new(), tun: false, xhttp: bridges);
        check(((JsonArray)auto["outbounds"]!).Single(o => o!["tag"]!.ToString() == "proxy")!["type"]!.ToString() == "selector" && auto["dns"]!["final"]!.ToString() == "provider", "Auto chooses by isolated health probes while DNS follows its traffic selector");
        var local = new RoutingProfile { Rules = new RoutingRules { ProxyDomains = ["ru", "xn--p1ai"] } };
        var localProfile = new Profile { Nodes = [node], Selected = node.Id, ConnectionMode = "proxy", RoutingProfiles = [local], ActiveRoutingProfile = local.Id };
        var manual = Configuration.BuildConnection(localProfile, 19455, 19456, xhttp: bridges, xhttpEgress: egress);
        check(manual["route"]!["final"]!.ToString() == "health-proxy" && ((JsonArray)manual["route"]!["rules"]!).Any(r => r?["domain_suffix"]?.ToJsonString().Contains("xn--p1ai") == true && r["outbound"]!.ToString() == "health-proxy") && manual["dns"]!["servers"]![1]!["detour"]!.ToString() == "health-proxy", "explicit local routing overrides provider direct rules for both traffic and DNS");
        rejected = false; try { NodeLinks.Export(node); } catch (UserError) { rejected = true; }
        check(rejected, "QR and share links cannot discard provider routing silently");
        var secret = (JsonObject)full.DeepClone(); secret["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]![0]!["id"] = "private-full-config-credential";
        string clean = CoreDiagnostics.Redact("private-full-config-credential", raw with { FullConfig = secret });
        check(!clean.Contains("private-full-config-credential"), "credentials inside full JSON are redacted from logs");
        foreach (string field in new[] { "api", "reverse", "metrics" })
        {
            var unsafeConfig = (JsonObject)full.DeepClone(); unsafeConfig[field] = new JsonObject();
            rejected = false; try { Subscriptions.Parse(unsafeConfig.ToJsonString()); } catch (UserError) { rejected = true; }
            check(rejected, "full JSON refuses remote control sections: " + field);
        }
        var unsafeFile = (JsonObject)full.DeepClone(); unsafeFile["outbounds"]![0]!["streamSettings"]!["tlsSettings"] = new JsonObject { ["certificateFile"] = "private.key" };
        rejected = false; try { Subscriptions.Parse(unsafeFile.ToJsonString()); } catch (UserError) { rejected = true; }
        check(rejected, "full JSON cannot read local certificate files");
        string folder = Path.Combine("Tests", "generated", "provider"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "xray.json"), XrayConfig.Runtime(node, bridge, egress).ToJsonString());
        File.WriteAllText(Path.Combine(folder, "sing-box.json"), sb.ToJsonString());
        File.WriteAllText(Path.Combine(folder, "auto.json"), auto.ToJsonString());
        File.WriteAllText(Path.Combine(folder, "manual.json"), manual.ToJsonString());
        File.WriteAllText(Path.Combine(folder, "balanced.json"), balancedRuntime.ToJsonString());
        foreach (string link in new[] { Link, Link.Replace("security=none", "security=tls&type=ws&path=%2Fws%3Fed%3D2048"), Link.Replace("vless://11111111-1111-4111-8111-111111111111", "trojan://secret").Replace("security=none", "security=tls"), "hysteria2://secret@127.0.0.1:19451?sni=example.com&obfs=salamander&obfs-password=secret" })
        {
            var converted = Subscriptions.Parse(link + "\n#routing: " + Encode(ProfileJson)).Nodes[0];
            File.WriteAllText(Path.Combine(folder, "convert-" + converted.Id[..8] + ".json"), XrayConfig.Runtime(converted, bridge, egress).ToJsonString());
        }
        var geo = node with { FullConfig = (JsonObject)config.DeepClone(), GeoIpUrl = "https://geo.example/one.dat", GeoSiteUrl = "https://geo.example/two.dat" };
        ((JsonArray)geo.FullConfig!["routing"]!["rules"]!).Add(new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("geosite:ru"), ["outboundTag"] = "direct" });
        string cacheRoot = Path.Combine(folder, "cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new XrayAssets(cacheRoot); using var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent([10, 2, 1, 0]) }); using var client = new HttpClient(handler);
            await cache.Prepare([geo], client, CancellationToken.None); await cache.Prepare([geo], client, CancellationToken.None);
            check(handler.Count == 1 && cache.Available(geo), "required provider geo files are cached for offline reconnects");
            check(cache.DirectoryFor(geo) != cache.DirectoryFor(geo with { GeoSiteUrl = "https://other.example/geosite.dat" }), "different provider geo sources cannot overwrite one another");
            var htmlGeo = geo with { GeoSiteUrl = "https://html.example/geosite.dat" };
            using var htmlHandler = new Handler((_, _) => Ok("<html>not a database</html>")); using var htmlClient = new HttpClient(htmlHandler);
            rejected = false; try { await cache.Prepare([htmlGeo], htmlClient, CancellationToken.None); } catch (UserError) { rejected = true; }
            check(rejected && cache.Available(geo) && !cache.Available(htmlGeo), "invalid geo download preserves the working cache and cleans temporary files");
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true); }
    }
    static HttpResponseMessage Ok(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    static HttpResponseMessage WithHeader(string value, string name, string header) { var response = Ok(value); response.Headers.Add(name, header); return response; }
    sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request, Count++));
    }
}
