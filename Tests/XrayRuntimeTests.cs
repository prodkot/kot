using Kot.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Kot.Tests;
public static class XrayRuntimeTests
{
    public static void Run(Action<bool, string> check)
    {
        const string uuid = "c9a83ccf-d2e9-4ef2-a8b4-b1b8cb5f52a1";
        string dir = "Tests/generated/native-xray"; Directory.CreateDirectory(dir);
        var links = new[] {
            $"vless://{uuid}@127.0.0.1:19441?security=none",
            $"vless://{uuid}@example.com:443?security=tls&type=ws&host=cdn.example.com&path=%2Fws%3Fed%3D2048",
            $"vless://{uuid}@example.com:443?security=tls&type=grpc&serviceName=example",
            $"vless://{uuid}@example.com:443?security=tls&type=httpupgrade&host=example.com&path=%2Fup",
            $"vless://{uuid}@example.com:443?security=tls&type=xhttp&path=%2Fx&mode=auto",
            "trojan://password@example.com:443?sni=example.com",
            "ss://YWVzLTEyOC1nY206cGFzc3dvcmQ=@example.com:8388",
            "hysteria2://password@example.com:443?sni=example.com",
            "vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"v\":\"2\",\"add\":\"example.com\",\"port\":\"443\",\"id\":\"" + uuid + "\",\"aid\":\"0\",\"net\":\"tcp\",\"tls\":\"tls\"}"))
        };
        int i = 0;
        foreach (string link in links)
        {
            var n = Subscriptions.ParseNode(link); string before = n.Outbound.ToJsonString();
            var c = XrayRuntime.Build(n, 19442, proxyPort: 19443, metricsPort: 19444, secret: new string('s', 64));
            check(n.Outbound.ToJsonString() == before, "native generator preserves stored node " + i);
            check(c["outbounds"]!.AsArray().All(o => o?["protocol"] != null) && !c.ToJsonString().Contains("dialerProxy"), "native node needs no transport bridge " + i);
            File.WriteAllText(Path.Combine(dir, "transport-" + i++ + ".json"), c.ToJsonString());
        }
        var node = Subscriptions.ParseNode(links[0]);
        var tun = XrayRuntime.Build(node, 19442, true);
        check(tun["inbounds"]!.AsArray().Single(o => o?["protocol"]?.ToString() == "tun")!["settings"]!["autoOutboundsInterface"]!.ToString() == "auto", "native TUN installs both routes with physical socket binding");
        File.WriteAllText(Path.Combine(dir, "tun.json"), tun.ToJsonString());
        var saved = JsonNode.Parse("""
        {"inbounds":[{"tag":"provider-in","protocol":"socks","listen":"127.0.0.1","port":1080}],"outbounds":[{"tag":"vpn","protocol":"vless","settings":{"vnext":[{"address":"127.0.0.1","port":19441,"users":[{"id":"UUID","encryption":"none"}]}]}},{"tag":"direct","protocol":"freedom"}],"dns":{"tag":"provider-dns","servers":[{"address":"127.0.0.1","port":19445,"domains":["domain:ru","domain:xn--p1ai"],"skipFallback":true},"1.1.1.1"],"disableFallbackIfMatch":true},"routing":{"domainStrategy":"IPIfNonMatch","rules":[{"type":"field","inboundTag":["provider-dns"],"outboundTag":"direct"},{"type":"field","inboundTag":["provider-in"],"domain":["domain:ru","domain:xn--p1ai"],"outboundTag":"direct"}]}}
        """.Replace("UUID", uuid))!.AsObject();
        var full = XrayConfig.Parse(saved.ToJsonString()).Nodes[0];
        string original = full.FullConfig!.ToJsonString();
        var runtime = XrayRuntime.Build(full, 19442, proxyPort: 19443, metricsPort: 19444, secret: new string('s', 64));
        check(original == full.FullConfig.ToJsonString(), "provider JSON is immutable");
        var inherited = runtime["routing"]!["rules"]!.AsArray().Single(r => r?["domain"] != null);
        check(inherited!["inboundTag"]!.AsArray().Select(t => t!.ToString()).SequenceEqual(new[] { "kot-tun", "kot-in" }) && inherited["outboundTag"]!.ToString() == "direct", "provider RU and IDN rules retain their action on native inputs");
        check(JsonNode.DeepEquals(runtime["dns"], saved["dns"]), "provider DNS and empty-AAAA fallback policy survive natively");
        check(runtime["routing"]!["rules"]![1]!["outboundTag"]!.ToString() == "kot-dns-out" && runtime["routing"]!["rules"]![2]!["outboundTag"]!.ToString() == "vpn", "DNS interception and proxy-only health precede provider bypass");
        File.WriteAllText(Path.Combine(dir, "provider.json"), runtime.ToJsonString());
        var manual = new RoutingRules { DirectDomains = ["ru", "xn--p1ai"], BlockDomains = ["blocked.test"], ProxyApps = ["chrome.exe"], DirectIps = ["203.0.113.0/24"] };
        runtime = XrayRuntime.Build(full, 19442, manual: manual);
        check(!runtime.ToJsonString().Contains("provider-in") && runtime["routing"]!["rules"]!.AsArray().Any(r => r?["process"]?[0]?.ToString() == "chrome.exe"), "explicit local rules replace provider rules and translate process matching");
        File.WriteAllText(Path.Combine(dir, "manual.json"), runtime.ToJsonString());
        var stats = ConnectionSnapshot.ParseXray("""{"stats":{"inbound":{"kot-tun":{"downlink":500,"uplink":200},"kot-in":{"downlink":20,"uplink":10},"kot-health":{"downlink":9999,"uplink":9999}}}}""");
        check(stats.DownloadTotal == 520 && stats.UploadTotal == 210 && stats.Count == -1, "native counters exclude health traffic and never invent connection detail");
        bool invalid = false; try { ConnectionSnapshot.ParseXray("""{"stats":{"inbound":{"kot-in":{"downlink":-1,"uplink":10}}}}"""); } catch (JsonException) { invalid = true; }
        check(invalid, "negative native counters rejected");
        check(GeoIp.Prefixes("1.1.1.0/24\n2001:db8::/32\n").Length == 2, "native GeoIP validates both address families");
        invalid = false; try { GeoIp.Prefixes("<html>not a database</html>"); } catch (UserError) { invalid = true; } check(invalid, "native GeoIP rejects error pages");
        File.WriteAllText(Path.Combine(dir, "server.json"), """{"inbounds":[{"protocol":"vless","listen":"127.0.0.1","port":19441,"settings":{"clients":[{"id":"UUID"}],"decryption":"none"}}],"outbounds":[{"protocol":"freedom","settings":{"finalRules":[{"action":"allow","ip":["127.0.0.0/8","::1/128"]}]}}]}""".Replace("UUID", uuid));
    }
}
