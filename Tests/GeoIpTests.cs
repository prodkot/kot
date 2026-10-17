using Kot.Core;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Kot.Tests;

public static class GeoIpTests
{
    public static async Task Run(Action<bool, string> check, Node node, string fixtureFolder)
    {
        void Reject(Action action, string name)
        {
            bool rejected = false; try { action(); } catch (UserError) { rejected = true; } check(rejected, name);
        }
        check(GeoIp.Countries("RU, cn;US\nru\nGB\nTZ").SequenceEqual(new[] { "ru", "cn", "us", "gb", "tz" }), "GeoIP normalizes and deduplicates supported country codes");
        foreach (string invalid in new[] { "russia", "ZZ", "UK", "../ru", "ru.srs", "https://example.org/file" })
            Reject(() => GeoIp.Countries(invalid), "GeoIP rejects unknown codes, paths and URLs");
        foreach (int hours in new[] { -1, 169 }) Reject(() => new GeoIpOptions { RefreshHours = hours }.Validate(), "GeoIP bounds the automatic update interval");
        foreach (string json in new[] { "{\"version\":1,\"directCountries\":null}", "{\"version\":1,\"blockCountries\":[null]}", "{\"version\":1,\"proxyCountries\":[\"RU,US\"]}" })
            Reject(() => RoutingProfiles.Parse(json), "GeoIP validates routing JSON country arrays");
        var rules = RoutingProfiles.Parse("{\"version\":1,\"directCountries\":[\"RU\"],\"proxyCountries\":[\"us\"],\"blockCountries\":[\"CN\"]}");
        check(RoutingProfiles.Parse(RoutingProfiles.Serialize(rules)).Countries().SequenceEqual(new[] { "cn", "ru", "us" }), "GeoIP country rules survive JSON export and import");
        var legacy = new Profile(); legacy.Normalize(); check(legacy.GeoIp.RefreshHours == 24 && legacy.RoutingProfiles.Count == 0, "legacy profiles gain daily GeoIP updates without changing routing");
        byte[] original = Encoding.ASCII.GetBytes("203.0.113.0/24\n2001:db8::/32\n"), newer = Encoding.ASCII.GetBytes("198.51.100.0/24\n2001:db8:1::/48\n");
        Task Validate(string path, CancellationToken ct) { ct.ThrowIfCancellationRequested(); check(File.ReadAllBytes(path).Length > 4, "GeoIP validates the staged file before committing it"); return Task.CompletedTask; }
        var cache = new GeoIpCache(Path.Combine(fixtureFolder, "geoip-cache"));
        Directory.CreateDirectory(Path.Combine(fixtureFolder, "geoip-cache"));
        foreach (string path in Directory.EnumerateFiles(Path.Combine(fixtureFolder, "geoip-cache"))) File.Delete(path);
        cache = new GeoIpCache(Path.Combine(fixtureFolder, "geoip-cache"));
        var changed = await cache.Update(["ru", "us"], false, Validate, CancellationToken.None, (_, _) => Task.FromResult(original));
        var before = cache.Paths(["ru", "us"]); var oldDates = cache.Status(["ru", "us"]);
        check(changed.SequenceEqual(new[] { "ru", "us" }) && oldDates.All(item => item.Ready && item.Updated != null), "GeoIP downloads only requested country files and records successful checks");
        check(!cache.Due(["ru"], 0, DateTimeOffset.UtcNow.AddDays(30)) && !cache.Due(["ru"], 24, oldDates[0].Updated!.Value.AddHours(23)) && cache.Due(["ru"], 24, oldDates[0].Updated!.Value.AddHours(25)), "GeoIP update scheduling uses the last success and respects disabled auto updates");
        check(cache.Due(["cn"], 24, DateTimeOffset.UtcNow), "GeoIP missing country databases are due when automatic updates are enabled");
        int requests = 0;
        await cache.Update(["ru", "us"], true, Validate, CancellationToken.None, (_, _) => { requests++; return Task.FromResult(newer); });
        check(requests == 0, "GeoIP connects offline using verified cache files without redownloading them");
        bool failed = false;
        try { await cache.Update(["ru", "us"], false, Validate, CancellationToken.None, (code, _) => code == "us" ? throw new UserError("HTTP 503") : Task.FromResult(newer)); }
        catch (UserError) { failed = true; }
        check(failed && cache.Paths(["ru", "us"]).OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)) && cache.Status(["ru", "us"]).SequenceEqual(oldDates), "GeoIP batch download failure preserves every old country file and success date");
        check(Directory.GetFiles(Path.Combine(fixtureFolder, "geoip-cache"), "download-*").Length == 0, "GeoIP cleans staged downloads after failure");
        failed = false;
        try { await cache.Update(["ru"], false, (_, _) => throw new UserError("invalid CIDR"), CancellationToken.None, (_, _) => Task.FromResult(newer)); }
        catch (UserError) { failed = true; }
        check(failed && File.ReadAllBytes(cache.Paths(["ru"])["ru"]).SequenceEqual(original), "GeoIP validation failure leaves the active database usable");
        using (var cancellation = new CancellationTokenSource())
        {
            bool canceled = false;
            try { await cache.Update(["ru"], false, (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token, (_, _) => Task.FromResult(newer)); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && cache.Paths(["ru"])["ru"] == before["ru"], "GeoIP cancellation before commit keeps the previous database");
        }
        changed = await cache.Update(["ru"], false, Validate, CancellationToken.None, (_, _) => Task.FromResult(newer));
        var updated = cache.Paths(["ru"])["ru"];
        check(changed.SequenceEqual(new[] { "ru" }) && updated != before["ru"] && File.Exists(before["ru"]), "GeoIP update uses immutable filenames and retains the running tunnel's old file");
        var reloaded = new GeoIpCache(Path.Combine(fixtureFolder, "geoip-cache"));
        check(reloaded.Paths(["ru", "us"])["ru"] == updated, "GeoIP index persists across app restarts with verified hashes");
        changed = await reloaded.Update(["ru"], false, Validate, CancellationToken.None, (_, _) => Task.FromResult(newer));
        check(changed.Length == 0, "GeoIP identical data refreshes the success date without requesting a tunnel restart");
        await reloaded.Update(["ru"], false, Validate, CancellationToken.None, (_, _) => Task.FromResult(Encoding.ASCII.GetBytes("192.0.2.0/24\n")));
        reloaded.Prune();
        check(!File.Exists(before["ru"]) && File.Exists(updated) && File.Exists(before["us"]), "GeoIP cache cleanup retains the current and previous generation of each country");
        File.WriteAllText(reloaded.Paths(["us"])["us"], "corrupted");
        var damaged = new GeoIpCache(Path.Combine(fixtureFolder, "geoip-cache"));
        Reject(() => damaged.Paths(["us"]), "GeoIP detects cached file tampering on restart");
        await damaged.Update(["us"], true, Validate, CancellationToken.None, (_, _) => Task.FromResult(original));
        check(damaged.Status(["us"])[0].Ready && File.ReadAllBytes(damaged.Paths(["us"])["us"]).SequenceEqual(original), "GeoIP can repair a corrupted cache even when the source hash is unchanged");
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example/geoip.srs") } },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GeoIp.MaxBytes + 1]) }
        })
        {
            failed = false; try { await GeoIp.Download("ru", CancellationToken.None, new Handler(_ => Task.FromResult(response))); } catch (UserError) { failed = true; }
            check(failed, "GeoIP rejects HTTP failures, redirects, HTML and oversized downloads");
        }
        var downloaded = await GeoIp.Download("ru", CancellationToken.None, new Handler(request =>
        {
            check(request.RequestUri!.AbsoluteUri == GeoIp.Source("ru").AbsoluteUri && !request.Headers.Contains("x-hwid") && request.Headers.Authorization == null,
                "GeoIP fetch uses the fixed official country URL without HWID or authorization headers");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(original) });
        }));
        check(downloaded.SequenceEqual(original), "GeoIP accepts bounded IPv4/IPv6 CIDR downloads for core validation");
        using (var cancellation = new CancellationTokenSource(50))
        {
            bool canceled = false;
            try { await GeoIp.Download("ru", cancellation.Token, new Handler(async _ => { await Task.Delay(10000, cancellation.Token); return new(HttpStatusCode.OK); })); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "GeoIP aborts stalled requests when the connection or app is canceled");
        }
        var routing = new RoutingProfile { Rules = rules };
        var profile = new Profile { Nodes = [node], Selected = node.Id, RoutingProfiles = [routing], ActiveRoutingProfile = routing.Id };
        Reject(() => Configuration.BuildConnection(profile, 18780), "GeoIP routing fails closed when a required database is missing");
        string file = damaged.Paths(["ru"])["ru"];
        var paths = rules.Countries().ToDictionary(code => code, _ => file);
        var config = Configuration.BuildConnection(profile, 18780, geoIpPaths: paths);
        var routes = config["route"]!["rules"]!.AsArray();
        check(routes[4]!["action"]!.ToString() == "resolve" && routes[4]!["server"] == null, "GeoIP resolves HTTP/SOCKS hostnames through the configured DNS rules before country matching");
        var userRules = routes.Skip(4).Where(r => r!["action"]!.ToString() != "resolve" && r["ip_is_private"] == null).ToArray();
        check(config["route"]!["rule_set"]!.AsArray().Count == 3 && userRules[0]!["action"]!.ToString() == "reject"
            && userRules[1]!["outbound"]!.ToString() == "proxy" && userRules[2]!["outbound"]!.ToString() == "direct", "GeoIP routing defines local binary rule sets with block/proxy/direct actions");
        check(!config.ToJsonString().Contains("geoip.db") && !config.ToJsonString().Contains("\"geoip\":"), "GeoIP routing does not emit removed sing-box geoip settings");
        profile.Address = "https://subscription.example/list"; profile.CaptureActive(); profile.GeoIp.RefreshHours = 0;
        var restored = Backup.Import(Backup.Export(profile, "geoip-password"), "geoip-password");
        check(restored.GeoIp.RefreshHours == 0 && restored.RoutingProfiles[0].Rules.Countries().Length == 3, "encrypted backup preserves GeoIP countries and update interval without copying databases");
        rules.ProxyIps = ["203.0.113.0/24"]; rules.ProxyDomains = ["example.org"]; rules.DirectApps = ["game.exe"];
        routes = Configuration.BuildConnection(profile, 18780, geoIpPaths: paths)["route"]!["rules"]!.AsArray();
        userRules = routes.Skip(4).Where(r => r!["action"]!.ToString() != "resolve" && r["ip_is_private"] == null).ToArray();
        check(userRules[0]!["action"]!.ToString() == "reject" && userRules[1]!["process_path_regex"] != null && userRules[2]!["domain_suffix"] != null
            && userRules[3]!["ip_cidr"] != null && userRules[4]!["rule_set"] != null, "GeoIP blocks precede application rules and explicit domains/IPs precede nonblocking country rules");
        var unsafeFolder = Path.Combine(fixtureFolder, "geoip-bad-index"); Directory.CreateDirectory(unsafeFolder);
        File.WriteAllText(Path.Combine(unsafeFolder, "index.json"), "{\"ru\":{\"Hash\":\"../../escape\",\"Updated\":\"2026-01-01T00:00:00Z\"}}");
        check(!new GeoIpCache(unsafeFolder).Status(["ru"])[0].Ready, "GeoIP ignores malformed cache indexes rather than accepting external paths");
        string[] many = "ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bl bm bn bo bq br bs bt bw by bz ca cd cf cg ch ci ck cl cm cn co cr cu cv cw cy cz de dj dk dm do dz ec ee eg er es et fi fj fk fm fo fr ga gb gd ge gf gg gh gi gl gm gn gp gq gr gt gu gw gy hk hn hr ht hu id ie il im in io iq ir".Split(' ').Take(101).ToArray();
        Reject(() => new RoutingRules { DirectCountries = many }.Validate(), "GeoIP limits each individual country rule list to 100 entries");
        var wideRules = new RoutingRules { DirectCountries = many.Take(100).ToArray(), ProxyCountries = many.Skip(100).ToArray() }; wideRules.Validate();
        var wideCache = new GeoIpCache(Path.Combine(fixtureFolder, "geoip-many"));
        await wideCache.Update(wideRules.Countries(), false, (_, _) => Task.CompletedTask, CancellationToken.None, (_, _) => Task.FromResult(original));
        check(wideCache.Paths(many).Count == 101, "GeoIP updates the union of valid rule lists even when profiles reference more than 100 countries");
    }
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request);
    }
}
