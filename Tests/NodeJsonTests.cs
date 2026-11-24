using System.Text.Json;
using System.Text.Json.Nodes;
using Kot.Core;

namespace Kot.Tests;

public static class NodeJsonTests
{
    public static void Run(Action<bool, string> check)
    {
        const string uuid = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";
        var link = Subscriptions.ParseNode($"vless://{uuid}@fixture.example:443?security=tls&type=ws&path=%2Fprivate-token#Fixture");
        string before = JsonSerializer.Serialize(link);
        var parsed = NodeJsonView.Read(link, "parsed", true);
        var runtime = NodeJsonView.Read(link, "xray", true);
        check(JsonNode.DeepEquals(JsonNode.Parse(parsed.Text!), link.Outbound), "parsed link uses stored schema");
        var actual = XrayRuntime.Build(link, 8192, tun:true, proxyPort:8193, metricsPort:8194, secret:new string('Z', 32));
        check(JsonNode.DeepEquals(JsonNode.Parse(runtime.Text!), actual["outbounds"]![0]), "inspector matches proxy built for actual TUN connection");
        check(before == JsonSerializer.Serialize(link), "preview leaves stored link and identity intact");
        foreach (var tab in new[] {"parsed", "xray"})
        {
            var masked = NodeJsonView.Read(link, tab, false);
            check(masked.Text != null && JsonNode.Parse(masked.Text) is JsonObject && masked.Text.Contains("[скрыто]") &&
                !new[] {uuid, "fixture.example", "private-token"}.Any(s => masked.Text.Contains(s, StringComparison.OrdinalIgnoreCase)), "masked link omits credentials and addresses in " + tab);
        }
        var clash = Subscriptions.Parse("""
            proxies:
              - name: Local fixture
                type: trojan
                server: fixture.example
                port: 443
                password: private-password
            """).Nodes.Single();
        check(JsonNode.DeepEquals(JsonNode.Parse(NodeJsonView.Read(clash,"parsed",true).Text!), clash.Outbound), "Clash uses the stored parsed outbound");
        foreach (var tab in new[] {"parsed", "xray"})
            check(NodeJsonView.Read(clash,tab,true).Text!.Contains("private-password") &&
                !NodeJsonView.Read(clash,tab,false).Text!.Contains("private-password"), "Clash password masked in " + tab);
        var fullConfig = JsonNode.Parse("""
            {"inbounds":[],"outbounds":[{"protocol":"freedom"},{"protocol":"socks","settings":{"servers":[{"address":"fixture.example","port":443,"users":[{"user":"private-name","pass":"private-pass"}]}]},"streamSettings":{"wsSettings":{"headers":{"private-header":"private-content"}}},"private-key":{"foo":"private-value"}},{"protocol":"http","tag":"private-tag"}]}
            """)!.AsObject();
        var native = XrayConfig.Parse(fullConfig.ToJsonString()).Nodes.Single();
        before = JsonSerializer.Serialize(native);
        parsed = NodeJsonView.Read(native, "parsed", true);
        runtime = NodeJsonView.Read(native, "xray", true);
        var original = JsonNode.Parse(parsed.Text!)!.AsArray();
        check(original.Count == 2 && JsonNode.DeepEquals(original[0], fullConfig["outbounds"]![1]) && original[0]!["tag"] == null, "native source preserves proxy order, original fields and missing tag");
        actual = XrayRuntime.Build(native, 19000);
        check(JsonNode.DeepEquals(JsonNode.Parse(runtime.Text!), new JsonArray(actual["outbounds"]![1]!.DeepClone(),actual["outbounds"]![2]!.DeepClone())), "native Xray view includes generated tag and second proxy only");
        var safe = NodeJsonView.Read(native, "parsed", false);
        check(!safe.Text!.Contains("private") && !safe.Text.Contains("fixture.example") && JsonNode.Parse(safe.Text)![0]!["settings"]!["servers"]![0]!["port"]!.GetValue<int>() == 443, "redaction hides nested values and provider-defined property names");
        check(before == JsonSerializer.Serialize(native), "native JSON and credentials unchanged by inspection");
        var derived = link with { FullConfig = fullConfig };
        check(JsonNode.DeepEquals(JsonNode.Parse(NodeJsonView.Read(derived, "parsed", true).Text!), link.Outbound), "generated provider policy is never presented as original imported Xray JSON");
        string? clipboard = null;
        check(safe.TryCopy(safe.Revision, s => clipboard = s) && clipboard == safe.Text, "masked copy writes exact displayed JSON");
        check(!runtime.TryCopy(safe.Revision, s => clipboard = s) && clipboard == safe.Text, "stale or wrong disclosure revision cannot write clipboard");
        check(runtime.TryCopy(runtime.Revision, s => clipboard = s) && clipboard == runtime.Text, "confirmed copy writes full formatted JSON");
        foreach (var tab in new[] {"parsed", "xray"})
        {
            var missing = NodeJsonView.Read(native with {FullConfig=null}, tab, true);
            check(missing.Text == null && missing.Note.Contains("отсутствует") && !missing.TryCopy(null,s => clipboard=s), "missing native original never falls back to display stub in " + tab);
        }
        var invalid = native with {FullConfig = new JsonObject {["private-exception-key"] = true}};
        var failure = NodeJsonView.Read(invalid, "xray", true);
        check(failure.Text == null && !failure.Note.Contains("private") && !runtime.ToString().Contains("private"), "failure and diagnostic string reveal no config");
    }
}
