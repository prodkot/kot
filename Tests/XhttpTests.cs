using Kot.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Kot.Tests;

public static class XhttpTests
{
    const string Uuid = "11111111-1111-4111-8111-111111111111";
    public static void Run(Action<bool, string> check)
    {
        const string extra = """{"xPaddingBytes":"100-200","noGRPCHeader":true,"xmux":{"maxConcurrency":"4-8","hKeepAlivePeriod":30},"headers":{"X-Test":"fixture"},"downloadSettings":{"address":"download.example","port":443,"network":"xhttp","security":"tls","tlsSettings":{"serverName":"download.example","allowInsecure":false},"xhttpSettings":{"path":"/download","mode":"stream-one"}}}""";
        var node = Subscriptions.ParseNode($"vless://{Uuid}@example.com:443?security=tls&type=xhttp&host=cdn.example&path=%2Fa%3Fb%3D1&mode=packet-up&extra=" + Uri.EscapeDataString(extra) + "#XHTTP");
        check(Xhttp.Uses(node) && node.Outbound["transport"]?["mode"]?.ToString() == "packet-up", "XHTTP URL mode and escaped path import");
        var again = Subscriptions.ParseNode(NodeLinks.Export(node));
        check(JsonNode.DeepEquals(node.Outbound, again.Outbound), "XHTTP URL export preserves host, path, mode and nested extra");
        var alias = Subscriptions.ParseNode($"vless://{Uuid}@example.com:80?type=splithttp");
        check(alias.Outbound["transport"]?["type"]?.ToString() == "xhttp" && alias.Outbound["transport"]?["path"]?.ToString() == "/" && alias.Outbound["transport"]?["mode"]?.ToString() == "auto", "SplitHTTP alias and defaults are normalized");
        var vm = Subscriptions.ParseNode("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { add = "example.com", port = 443, id = Uuid, net = "xhttp", mode = "stream-up", tls = "tls", packetEncoding = "xudp", extra = JsonNode.Parse(extra) }))));
        check(JsonNode.DeepEquals(vm.Outbound, Subscriptions.ParseNode(NodeLinks.Export(vm)).Outbound), "VMess XHTTP JSON and object extra round-trip");
        var clash = Subscriptions.Parse($$"""
        proxies:
          - name: XHTTP
            type: vless
            server: example.com
            port: 443
            uuid: {{Uuid}}
            tls: true
            network: xhttp
            xhttp-opts:
              host: cdn.example
              path: /xhttp
              mode: stream-one
              extra:
                noSSEHeader: true
        """);
        check(clash.Nodes.Count == 1 && clash.Nodes[0].Outbound["transport"]?["extra"]?["noSSEHeader"]?.GetValue<bool>() == true, "Clash XHTTP options survive canonical import");
        var profile = new Profile { Local = true, Nodes = [node], Selected = node.Id }; profile.CaptureActive();
        check(JsonNode.DeepEquals(Backup.Import(Backup.Export(profile, "test-password"), "test-password").Nodes[0].Outbound, node.Outbound), "encrypted backup keeps XHTTP transport parameters");
        foreach (string query in new[] { "mode=unknown", "packetEncoding=packetaddr", "path=relative", "flow=xtls-rprx-vision", "extra=[]", "extra={", "extra=" + Uri.EscapeDataString("""{"downloadSettings":{"tlsSettings":{"allowInsecure":true}}}"""), "extra=" + Uri.EscapeDataString("""{"downloadSettings":{"sockopt":{"dialerProxy":"arbitrary"}}}"""), "extra=" + Uri.EscapeDataString("""{"headers":{"Host":"bad"}}"""), "extra=" + Uri.EscapeDataString("""{"xmux":{"maxConcurrency":"8-4"}}""") })
        {
            bool rejected = false; try { Subscriptions.ParseNode($"vless://{Uuid}@example.com:443?type=xhttp&" + query); } catch (UserError) { rejected = true; }
            check(rejected, "invalid or unsafe XHTTP options rejected: " + query.Split('=')[0]);
        }
        bool noBridge = false; try { Configuration.Build(node, 18080, false, []); } catch (UserError) { noBridge = true; }
        check(noBridge, "XHTTP never silently reaches sing-box without Xray");
        Dictionary<string, XhttpBridge> bridges = new() { [node.Id] = new(19081, "fixture", new string('p', 64)), [alias.Id] = new(19082, "fixture", new string('q', 64)) };
        var xray = Xhttp.Build([node, alias], bridges);
        check(xray["inbounds"]!.AsArray().Count == 2 && xray["inbounds"]![0]!["settings"]!["auth"]?.ToString() == "password" && xray["outbounds"]![0]!["protocol"]?.ToString() == "blackhole", "isolated authenticated SOCKS bridges route separately and fail closed");
        profile.Nodes.Add(alias); profile.Selected = "auto"; profile.Favorites = [alias.Id];
        check(Xhttp.ConnectionNodes(profile).Count == 1 && Xhttp.ConnectionNodes(profile)[0].Id == alias.Id, "Xray candidate list matches favorite-only automatic selection");
        profile.Favorites.Clear();
        var routing = new RoutingProfile { Rules = new RoutingRules { Name = "Fixture", DefaultAction = "proxy", ProxyDomains = ["example.com"] } }; profile.RoutingProfiles = [routing]; profile.ActiveRoutingProfile = routing.Id;
        var config = Configuration.BuildConnection(profile, 18080, xhttp: bridges, xrayExecutable: Path.GetFullPath("fixture/xray.exe"));
        check(config["route"]!["rules"]![0]!["process_path"] != null && config["dns"]!["rules"]![0]!["server"]?.ToString() == "bootstrap" && config["route"]!["find_process"]?.GetValue<bool>() == true, "Xray bypass survives custom routing and precedes DNS interception");
        check(config["outbounds"]![0]!["type"]?.ToString() == "socks" && config["outbounds"]![1]!["tag"]?.ToString() == "node-" + alias.Id, "automatic URL test retains node identities through bridges");
        var tls = Xhttp.Outbound(node)["streamSettings"]!["tlsSettings"]!;
        check(tls["allowInsecure"]?.GetValue<bool>() == false && Xhttp.Outbound(vm)["protocol"]?.ToString() == "vmess", "Xray conversion keeps TLS verification and actual protocol");
        var reality = Subscriptions.ParseNode($"vless://{Uuid}@example.com:443?type=xhttp&security=reality&sni=example.com&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=0123456789abcdef");
        check(Xhttp.Outbound(reality)["streamSettings"]!["realitySettings"]!["fingerprint"]?.ToString() == "chrome", "XHTTP REALITY key, short ID and fingerprint conversion");
    }
    public static void Fixtures(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string protocol in new[] { "vless", "vmess", "trojan" })
        foreach (string mode in new[] { "auto", "packet-up", "stream-up", "stream-one" })
        {
            JsonObject o = new() { ["type"] = protocol, ["tag"] = "proxy", ["server"] = "127.0.0.1", ["server_port"] = 19471, ["transport"] = Xhttp.Transport("fixture.test", "/fixture", mode) };
            if (protocol == "trojan") o["password"] = "fixture-password"; else o["uuid"] = Uuid;
            if (protocol == "vmess") o["security"] = "auto";
            o["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "fixture.test", ["alpn"] = new JsonArray("h2") };
            var node = new Node("fixture", "Fixture", protocol, o);
            Dictionary<string, XhttpBridge> bridges = new() { [node.Id] = new(19472, "fixture", new string('p', 64)) };
            string name = protocol + "-" + mode;
            File.WriteAllText(Path.Combine(directory, name + "-xray.json"), Xhttp.Build([node], bridges).ToJsonString());
            File.WriteAllText(Path.Combine(directory, name + "-client.json"), Configuration.Build(node, 19473, false, [], tun: false, xhttp: bridges).ToJsonString());
        }
    }
}
