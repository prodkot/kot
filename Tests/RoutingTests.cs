using Kot.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kot.Tests;

public static class RoutingTests
{
    public static async Task Run(Action<bool, string> check, Node node, string fixtureFolder)
    {
        void Reject(Action action, string name)
        {
            bool rejected = false; try { action(); } catch (UserError) { rejected = true; } check(rejected, name);
        }
        var rules = new RoutingRules
        {
            Name = "Работа", DefaultAction = "direct", DirectDomains = [".bank.example"], ProxyDomains = ["YouTube.com"], BlockDomains = ["ads.example"],
            DirectIps = ["203.0.113.19/24"], ProxyIps = ["2001:db8::1/32"], BlockIps = ["198.51.100.7"],
            DirectApps = ["Game.EXE"], ProxyApps = ["chrome.exe"], BlockApps = ["tracker.exe"], DirectDns = "adguard", RemoteDns = "quad9"
        };
        rules.Validate();
        check(Configuration.Domains(".ru\nRU\n.su\n.xn--p1ai").SequenceEqual(new[] { "ru", "su", "xn--p1ai" }), "routing accepts top-level domain suffixes and normalizes leading dots and case");
        foreach (var invalid in new[] { ".", "r", "https://example.ru", "example.ru/path", "*.ru", "bad..ru", "-ru", "ru-", "123", "example.-ru" })
            Reject(() => Configuration.Domains(invalid), "routing rejects malformed domain suffix: " + invalid);
        var zones = RoutingProfiles.Parse("""{"version":1,"name":"Зоны напрямую","directDomains":[".ru",".su",".xn--p1ai"]}""");
        var zoneProfile = new RoutingProfile { Rules = zones };
        var zoneConnection = new Profile { Nodes = [node], Selected = node.Id, RoutingProfiles = [zoneProfile], ActiveRoutingProfile = zoneProfile.Id };
        foreach (string mode in new[] { "tun", "proxy" })
        foreach (string selected in new[] { node.Id, "auto" })
        {
            zoneConnection.ConnectionMode = mode; zoneConnection.Selected = selected;
            var zoneConfig = Configuration.BuildConnection(zoneConnection, 18780, 18782);
            var suffixRoute = zoneConfig["route"]!["rules"]!.AsArray().Single(r => r!["domain_suffix"] != null)!;
            var suffixDns = zoneConfig["dns"]!["rules"]!.AsArray().Single(r => r!["domain_suffix"] != null)!;
            check(suffixRoute["domain_suffix"]!.AsArray().Select(v => v!.ToString()).SequenceEqual(new[] { "ru", "su", "xn--p1ai" })
                && suffixRoute["outbound"]!.ToString() == "direct" && suffixDns["server"]!.ToString() == "bootstrap"
                && zoneConfig["route"]!["final"]!.ToString() == "proxy", "domain zones use direct connections and direct DNS without bypassing other traffic: " + mode + "/" + (selected == "auto" ? "auto" : "selected"));
            File.WriteAllText(Path.Combine(fixtureFolder, "routing-zones-" + mode + "-" + (selected == "auto" ? "auto" : "selected") + ".json"), zoneConfig.ToJsonString());
        }
        check(rules.DirectDomains.SequenceEqual(new[] { "bank.example" }) && rules.ProxyDomains.SequenceEqual(new[] { "youtube.com" }), "routing normalizes domains and subdomain suffixes");
        check(rules.DirectIps[0] == "203.0.113.0/24" && rules.ProxyIps[0] == "2001:db8::/32" && rules.BlockIps[0] == "198.51.100.7/32", "routing canonicalizes IPv4/IPv6 prefixes and individual addresses");
        check(RoutingRules.IpRanges("0.0.0.0/0\n::/0").SequenceEqual(new[] { "0.0.0.0/0", "::/0" }), "routing accepts zero-length IPv4 and IPv6 masks");
        foreach (var invalid in new[] { "1", "127.1", "0x7f000001", "010.0.0.1", "1.2.3.4/33", "2001:db8::/129", "fe80::1%2", "example.com", "1.2.3.4/-1", "1.2.3.4/24/2" })
            Reject(() => RoutingRules.IpRanges(invalid), "routing rejects invalid or ambiguous IP: " + invalid);
        foreach (var invalid in new[] { @"C:\Apps\chrome.exe", "*.exe", "chrome", "../test.exe", "bad?.exe", "bad\u0000.exe" })
            Reject(() => RoutingRules.Applications(invalid), "routing rejects paths, masks and invalid process names");
        var encoded = RoutingProfiles.Serialize(rules);
        check(RoutingProfiles.Serialize(RoutingProfiles.Parse(encoded)) == encoded, "routing JSON export/import roundtrip preserves all rules");
        foreach (var invalid in new[] { "[]", "{}", "{\"version\":2}", "{\"version\":1,\"Version\":1}", "{\"version\":1,\"outbounds\":[]}", "{\"version\":1,\"blockDomains\":null}", "{\"version\":1,\"proxyApps\":[null]}", "{\"version\":1,\"remoteDns\":\"system\"}", "{\"version\":1,\"name\":\"\"}", "{\"version\":1,\"directDomains\":[\"bank.example\\nother.example\"]}" })
            Reject(() => RoutingProfiles.Parse(invalid), "routing rejects malformed, ambiguous or unsupported JSON");
        Reject(() => RoutingProfiles.Parse(new string(' ', RoutingRules.MaxBytes + 1)), "routing rejects oversized clipboard JSON before parsing");
        Reject(() => new RoutingRules { ProxyApps = Enumerable.Range(0, 101).Select(i => i + ".exe").ToArray() }.Validate(), "routing bounds process list size");
        foreach (var invalid in new[] { "http://example.com/profile.json", "https://user:pass@example.com/profile.json", "https://example.com/profile.json#fragment", "file:///tmp/profile.json" })
            Reject(() => RoutingProfiles.Source(invalid), "routing source requires credential-free HTTPS without fragment");
        var routing = new RoutingProfile { Rules = rules, SourceUrl = "https://rules.example/profile.json", Updated = DateTimeOffset.Parse("2026-10-10T00:00:00Z") };
        routing.Validate();
        check(!routing.Due(routing.Updated.Value.AddHours(23)) && routing.Due(routing.Updated.Value.AddHours(24)), "autorouting uses the selected interval and last successful update");
        routing.RefreshHours = 0; check(!routing.Due(routing.Updated.Value.AddDays(30)), "zero interval disables autorouting"); routing.RefreshHours = 24;
        check(!new RoutingProfile().Due(DateTimeOffset.UtcNow), "local routing profile never triggers a download");
        var profile = new Profile { Nodes = [node], Selected = node.Id, Mode = "smart", Bypass = ["legacy.example"], RoutingProfiles = [routing], ActiveRoutingProfile = routing.Id };
        var config = Configuration.BuildConnection(profile, 18780, apiPort: 18781, apiSecret: new string('A', 64));
        var routes = config["route"]!["rules"]!.AsArray();
        check(config["route"]!["final"]!.ToString() == "direct" && config["dns"]!["final"]!.ToString() == "bootstrap", "default direct route also uses direct DNS");
        check(config["dns"]!["servers"]![0]!["server"]!.ToString() == "94.140.14.14" && config["dns"]!["servers"]![1]!["detour"]!.ToString() == "proxy", "direct and remote encrypted DNS use independent presets and detours");
        check(routes[2]!["inbound"]![0]!.ToString() == "health-in" && routes[2]!["outbound"]!.ToString() == "proxy" && config["dns"]!["rules"]![0]!["inbound"]![0]!.ToString() == "health-in", "health connection and DNS ignore custom bypass/block rules");
        check(routes[3]!["ip_is_private"]!.GetValue<bool>() && routes[3]!["outbound"]!.ToString() == "direct", "routing retains direct LAN access before user rules");
        var userRules = routes.Skip(4).Where(r => r!["action"]!.ToString() != "resolve" && r["ip_is_private"] == null).ToArray();
        check(routes[4]!["action"]!.ToString() == "resolve" && routes[4]!["server"] == null && routes[5]!["ip_is_private"]!.GetValue<bool>(), "IP rules resolve proxy hostnames using profile DNS rules and then preserve private destinations");
        check(userRules[0]!["action"]!.ToString() == "reject" && userRules[2]!["action"]!.ToString() == "reject" && userRules[3]!["outbound"]!.ToString() == "proxy" && userRules[4]!["outbound"]!.ToString() == "direct", "blocks precede per-app rules, followed by domain and IP rules");
        var expression = userRules[4]!["process_path_regex"]![0]!.ToString();
        check(Regex.IsMatch(@"C:\Games\GAME.exe", expression) && !Regex.IsMatch(@"C:\Games\other-game.exe", expression), "process rules match exact executable filenames regardless of Windows case");
        check(!routes.Any(r => r!["domain_suffix"]?.ToJsonString().Contains("legacy.example") == true), "selected routing profile replaces legacy bypass rules");
        check(config["dns"]!["rules"]!.AsArray().Any(r => r!["domain_suffix"]?.ToJsonString().Contains("ads.example") == true && r["action"]!.ToString() == "reject"), "blocked domains are also rejected at DNS resolution");
        File.WriteAllText(Path.Combine(fixtureFolder, "routing-tun.json"), config.ToJsonString());
        profile.ConnectionMode = "proxy"; profile.Selected = "auto";
        var automatic = Configuration.BuildConnection(profile, 18780, 18782);
        check(automatic["outbounds"]!.AsArray().Any(o => o!["type"]!.ToString() == "urltest") && automatic["route"]!["final"]!.ToString() == "direct", "routing works with automatic server selection and system proxy mode");
        File.WriteAllText(Path.Combine(fixtureFolder, "routing-auto-proxy.json"), automatic.ToJsonString());
        profile.ActiveRoutingProfile = "";
        check(Configuration.BuildConnection(profile, 18780, 18782)["route"]!["rules"]!.AsArray().Any(r => r!["domain_suffix"]?.ToJsonString().Contains("legacy.example") == true), "default profile restores previously saved bypass domains");
        profile.ActiveRoutingProfile = "missing"; Reject(() => Configuration.BuildConnection(profile, 18780, 18782), "deleted active profile fails closed instead of silently rerouting traffic");
        profile.Address = "https://subscription.example/list"; profile.CaptureActive(); profile.ActiveRoutingProfile = routing.Id; profile.Validate();
        var restored = Backup.Import(Backup.Export(profile, "routing-test-password"), "routing-test-password");
        check(restored.ActiveRoutingProfile == routing.Id && RoutingProfiles.Serialize(restored.RoutingProfiles[0].Rules) == encoded && restored.RoutingProfiles[0].SourceUrl == routing.SourceUrl, "encrypted backup preserves profiles, rules and update source");
        check(!encoded.Contains(routing.SourceUrl) && !encoded.Contains(routing.Id), "sharing a routing JSON omits private update URL and local ID");
        restored.ActiveRoutingProfile = "missing"; Reject(restored.Validate, "backup rejects unknown active routing profile");
        restored.ActiveRoutingProfile = routing.Id; restored.RoutingProfiles.Add(routing); Reject(restored.Validate, "backup rejects duplicate routing profile IDs");
        var requests = new List<string>();
        var result = await RoutingProfiles.Download(routing.SourceUrl, CancellationToken.None, new Handler((request, index) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri); check(!request.Headers.Contains("x-hwid"), "routing fetch sends no device identifier");
            return index == 0 ? new(HttpStatusCode.Found) { Headers = { Location = new Uri("/new.json", UriKind.Relative) } } : new(HttpStatusCode.OK) { Content = new StringContent(encoded) };
        }));
        check(result.Name == rules.Name && requests.SequenceEqual(new[] { routing.SourceUrl, "https://rules.example/new.json" }), "autorouting follows relative HTTPS redirects and parses validated rules");
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://rules.example/plain") } },
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', RoutingRules.MaxBytes + 1)) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Body(new byte[RoutingRules.MaxBytes + 1])) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xFF }) }
        })
        {
            bool rejected = false;
            try { await RoutingProfiles.Download(routing.SourceUrl, CancellationToken.None, new Handler((_, _) => response)); } catch (UserError) { rejected = true; }
            check(rejected, "autorouting rejects downgrade, HTTP errors, invalid formats and oversized/invalid UTF8 bodies");
        }
        int redirects = 0; bool loop = false;
        try { await RoutingProfiles.Download(routing.SourceUrl, CancellationToken.None, new Handler((_, _) => { redirects++; return new(HttpStatusCode.Found) { Headers = { Location = new Uri("/again", UriKind.Relative) } }; })); } catch (UserError) { loop = true; }
        check(loop && redirects == 6, "autorouting bounds redirect loops");
        using var cancellation = new CancellationTokenSource(100); bool canceled = false;
        try { await RoutingProfiles.Download(routing.SourceUrl, cancellation.Token, new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StreamContent(new Body(null)) })); } catch (OperationCanceledException) { canceled = true; }
        check(canceled, "autorouting cancels a stalled response body");
    }
    sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        int index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request, index++));
    }
    sealed class Body(byte[]? bytes) : Stream
    {
        int offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (bytes == null) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
            int count = Math.Min(buffer.Length, bytes.Length - offset); bytes.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
