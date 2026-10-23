using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
namespace Kot.Core;

// The only production runtime builder. Stored node outbounds retain their old
// schema so existing installations and backups need no destructive conversion.
public static class XrayRuntime
{
    public const string Revision = "0f3d884786f9";
    public static JsonObject Build(Node node, int healthPort, bool tun = false, int proxyPort = 0,
        int metricsPort = 0, string secret = "", bool smart = false, string[]? bypass = null,
        RoutingRules? manual = null, IReadOnlyDictionary<string, string>? geoIpPaths = null, string physicalInterface = "",
        XrayPlatform platform = XrayPlatform.Windows)
    {
        NodeSecurity.Validate(node);
        if (platform == XrayPlatform.Linux && manual != null && manual.DirectApps.Concat(manual.ProxyApps).Concat(manual.BlockApps).Any())
            throw new UserError("Правила приложений Windows .exe не поддерживаются в Linux. Удалите их из активного профиля; правила сайтов и IP доступны.");
        Port(healthPort);
        if (proxyPort != 0) Port(proxyPort);
        if (metricsPort != 0) { Port(metricsPort); if (secret.Length < 32) throw new UserError("Небезопасные настройки статистики."); }
        if (new[] { healthPort, proxyPort, metricsPort }.Where(p => p > 0).Distinct().Count() != 1 + (proxyPort > 0 ? 1 : 0) + (metricsPort > 0 ? 1 : 0)) throw new UserError("Локальные порты должны отличаться.");
        JsonObject c;
        if (node.FullConfig != null) { XrayConfig.Validate(node.FullConfig); c = (JsonObject)node.FullConfig.DeepClone(); }
        else c = new() { ["outbounds"] = new JsonArray(XrayConfig.Outbound(node)) };
        var outputs = (JsonArray)c["outbounds"]!;
        for (int i = 0; i < outputs.Count; i++) outputs[i]!["tag"] ??= "kot-provider-" + i;
        string defaultTag = outputs[0]!["tag"]!.ToString();
        string proxyTag = outputs.OfType<JsonObject>().First(XrayConfig.IsProxy)["tag"]!.ToString();
        var oldTags = (c["inbounds"] as JsonArray ?? []).Select(i => i?["tag"]?.ToString()).Where(t => t != null).ToHashSet();
        var routing = c["routing"] as JsonObject ?? new(); c["routing"] = routing;
        var rules = routing["rules"] as JsonArray ?? []; routing["rules"] = rules;
        if (platform == XrayPlatform.Linux && rules.OfType<JsonObject>().Any(rule => rule["process"] is JsonArray apps && apps.Count > 0))
            throw new UserError("Маршрутизация по процессам в полном конфиге не поддерживается изолированным Linux-ядром. Нужна отдельная Linux-политика приложений.");
        foreach (var rule in rules.OfType<JsonObject>())
            if (rule["inboundTag"] is JsonArray tags)
            {
                bool replace = tags.Any(t => oldTags.Contains(t?.ToString()));
                var retained = tags.Where(t => !oldTags.Contains(t?.ToString())).Select(t => t?.DeepClone()).ToArray();
                if (replace) rule["inboundTag"] = new JsonArray(retained.Concat(new JsonNode?[] { JsonValue.Create("kot-tun"), JsonValue.Create("kot-in") }).ToArray());
            }
        outputs.Add(new JsonObject { ["tag"] = "kot-direct", ["protocol"] = "freedom" });
        foreach (var output in outputs.OfType<JsonObject>().Where(o => o["protocol"]?.ToString() == "freedom"))
        {
            // An unconfigured freedom uses system DNS, bypassing the provider's
            // split DNS (and potentially recursing into the Windows TUN). Resolve
            // inside Xray, with no implicit system fallback. Preserve explicit
            // provider strategies, including legacy fields and chained outbounds.
            if (output["targetStrategy"] != null || output["settings"]?["domainStrategy"] != null
                || output["settings"]?["targetStrategy"] != null || output["proxySettings"] != null
                || output["streamSettings"]?["sockopt"]?["domainStrategy"] != null
                || output["streamSettings"]?["sockopt"]?["dialerProxy"] != null) continue;
            var stream = output["streamSettings"] as JsonObject ?? new(); output["streamSettings"] = stream;
            var sockopt = stream["sockopt"] as JsonObject ?? new(); stream["sockopt"] = sockopt;
            sockopt["domainStrategy"] = "ForceIP";
            // Without racing Xray picks a random address on each of five tries,
            // so a reachable address can be missed when other A/AAAA records fail.
            sockopt["happyEyeballs"] ??= new JsonObject { ["tryDelayMs"] = 250, ["interleave"] = 1, ["maxConcurrentTry"] = 4, ["prioritizeIPv6"] = false };
        }
        outputs.Add(new JsonObject { ["tag"] = "kot-block", ["protocol"] = "blackhole" });
        outputs.Add(new JsonObject { ["tag"] = "kot-dns-out", ["protocol"] = "dns" });
        JsonObject Input(int port, string tag, string? password = null) => new()
        {
            ["tag"] = tag, ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "http",
            ["settings"] = password == null ? new JsonObject() : new JsonObject { ["accounts"] = new JsonArray(new JsonObject { ["user"] = "kot", ["pass"] = password }) },
            ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = true }
        };
        var inputs = new JsonArray(Input(healthPort, "kot-health")); c["inbounds"] = inputs;
        if (proxyPort > 0) inputs.Add(Input(proxyPort, "kot-in"));
        if (tun) inputs.Add(new JsonObject
        {
            ["tag"] = "kot-tun", ["protocol"] = "tun", ["settings"] = new JsonObject
            {
                ["name"] = "kot-tun", ["mtu"] = 1500
            },
            // Keep the sniffed destination for egress as well as routing. Otherwise
            // a browser-selected unreachable IPv6 survives into freedom/VLESS,
            // and neither split DNS nor address racing can recover via IPv4.
            ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = false }
        });
        if (tun) XrayTunSettings.Apply(inputs.OfType<JsonObject>().Single(i => i["tag"]?.ToString() == "kot-tun")["settings"]!.AsObject(), platform);
        if (manual != null || node.FullConfig == null)
        {
            manual?.Validate(); rules.Clear(); routing.Remove("balancers"); c.Remove("observatory"); c.Remove("burstObservatory");
            routing["domainStrategy"] = "IPIfNonMatch";
            defaultTag = manual == null || manual.DefaultAction == "proxy" ? proxyTag : "kot-direct";
            rules.Add(Rule("ip", ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "::1/128", "fc00::/7", "fe80::/10"], "kot-direct"));
            if (manual != null)
            {
                void Add(string field, IEnumerable<string> values, string target) { var a = values.ToArray(); if (a.Length > 0) rules.Add(Rule(field, a, target)); }
                string[] Countries(string[] countries) => countries.SelectMany(code =>
                {
                    if (geoIpPaths == null || !geoIpPaths.TryGetValue(code, out string? path) || !Path.IsPathFullyQualified(path)) throw new UserError("Обновите GeoIP " + code.ToUpperInvariant() + ".");
                    return GeoIp.Prefixes(File.ReadAllText(path));
                }).Distinct().ToArray();
                Add("process", manual.BlockApps, "kot-block"); Add("domain", manual.BlockDomains.Select(d => "domain:" + d), "kot-block"); Add("ip", manual.BlockIps.Concat(Countries(manual.BlockCountries)), "kot-block");
                Add("process", manual.ProxyApps, proxyTag); Add("process", manual.DirectApps, "kot-direct");
                Add("domain", manual.ProxyDomains.Select(d => "domain:" + d), proxyTag); Add("domain", manual.DirectDomains.Select(d => "domain:" + d), "kot-direct");
                Add("ip", manual.ProxyIps, proxyTag); Add("ip", manual.DirectIps, "kot-direct");
                Add("ip", Countries(manual.ProxyCountries), proxyTag); Add("ip", Countries(manual.DirectCountries), "kot-direct");
            }
            else if (smart && bypass is { Length: > 0 }) rules.Add(Rule("domain", bypass.Select(d => "domain:" + d), "kot-direct"));
            string DnsUrl(string preset, bool direct) => (direct ? "https+local://" : "https://") + RoutingRules.DnsPreset(preset).Ip + "/dns-query";
            string directDns = DnsUrl(manual?.DirectDns ?? "cloudflare", true), remoteDns = DnsUrl(manual?.RemoteDns ?? "cloudflare", false);
            JsonArray servers = [];
            var directDomains = manual?.DirectDomains ?? (smart ? bypass ?? [] : []);
            if (directDomains.Length > 0) servers.Add(new JsonObject { ["address"] = directDns, ["domains"] = Array(directDomains.Select(d => "domain:" + d)), ["skipFallback"] = true });
            if (manual?.ProxyDomains.Length > 0) servers.Add(new JsonObject { ["address"] = remoteDns, ["domains"] = Array(manual.ProxyDomains.Select(d => "domain:" + d)), ["skipFallback"] = true });
            servers.Add(manual?.DefaultAction == "direct" ? directDns : remoteDns);
            c["dns"] = new JsonObject { ["servers"] = servers, ["queryStrategy"] = "UseIP", ["disableFallbackIfMatch"] = true, ["tag"] = "kot-dns" };
        }
        var dns = c["dns"] as JsonObject ?? new(); c["dns"] = dns;
        if (dns["servers"] is not JsonArray dnsServers || dnsServers.Count == 0) dns["servers"] = new JsonArray("https://1.1.1.1/dns-query");
        if (tun && platform == XrayPlatform.Linux)
            foreach (var server in dns["servers"]!.AsArray())
            {
                string address = server is JsonObject entry ? entry["address"]?.ToString() ?? "" : server?.ToString() ?? "";
                string host = Uri.TryCreate(address, UriKind.Absolute, out var uri) && address.Contains("://") ? uri.Host.Trim('[', ']') : address.Trim('[', ']');
                if (!IPAddress.TryParse(host, out var ip) || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                    throw new UserError("Linux TUN требует DNS с явным IP-адресом, без системного/localhost bootstrap. Используйте IP-based DNS или режим системного прокси для этого полного конфига.");
            }
        // Resolve transport endpoints independently of the tunnel they establish.
        HashSet<string> endpoints = [];
        void Find(JsonNode? value)
        {
            if (value is JsonObject o) foreach (var pair in o) { if (pair.Key == "address" && pair.Value is JsonValue v && v.TryGetValue<string>(out var host) && Uri.CheckHostName(host) == UriHostNameType.Dns) endpoints.Add("full:" + host); else Find(pair.Value); }
            else if (value is JsonArray a) foreach (var item in a) Find(item);
        }
        Find(outputs);
        if (endpoints.Count > 0)
        {
            var bootstrap = new JsonObject { ["address"] = tun || physicalInterface.Length == 0 ? "https+local://1.1.1.1/dns-query" : "https://1.1.1.1/dns-query", ["domains"] = Array(endpoints), ["skipFallback"] = true };
            if (!tun && physicalInterface.Length > 0) bootstrap["tag"] = "kot-bootstrap-dns";
            ((JsonArray)dns["servers"]!).Insert(0, bootstrap);
            if (!tun && physicalInterface.Length > 0) rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-bootstrap-dns"), ["outboundTag"] = "kot-direct" });
        }
        // A provider balancer can include freedom or a direct fallback. It must
        // never make the proxy-only health check succeed without a VPN server.
        // Preserve the provider's DNS fallback policy, including omitted values.
        rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-health"), ["outboundTag"] = proxyTag });
        rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-tun", "kot-in"), ["port"] = "53", ["outboundTag"] = "kot-dns-out" });
        rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-tun", "kot-in"), ["outboundTag"] = defaultTag });
        if (metricsPort > 0)
        {
            inputs.Add(Input(metricsPort, "kot-metrics-in", secret));
            c["metrics"] = new JsonObject { ["tag"] = "kot-metrics" };
            rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-metrics-in"), ["outboundTag"] = "kot-metrics" });
        }
        c["stats"] = new JsonObject();
        var policy = c["policy"] as JsonObject ?? new(); c["policy"] = policy;
        var system = policy["system"] as JsonObject ?? new(); policy["system"] = system;
        system["statsInboundUplink"] = true; system["statsInboundDownlink"] = true;
        // Probes running alongside a TUN bind to the physical interface captured
        // before the tunnel started. The connected core uses Xray's own auto binding.
        if (!tun && physicalInterface.Length > 0)
            foreach (var output in outputs.OfType<JsonObject>().Where(o => XrayConfig.IsProxy(o) || o["protocol"]?.ToString() == "freedom"))
            {
                // A local proxy belongs to the loopback interface. IP_UNICAST_IF
                // on the physical adapter makes Windows reject that connection.
                var addresses = Addresses(output).ToArray();
                if (XrayConfig.IsProxy(output) && addresses.Length > 0 && addresses.All(IsLoopback)) continue;
                var stream = output["streamSettings"] as JsonObject ?? new(); output["streamSettings"] = stream;
                var sockopt = stream["sockopt"] as JsonObject ?? new(); stream["sockopt"] = sockopt;
                if (sockopt["dialerProxy"] == null && output["proxySettings"] == null) sockopt["interface"] = physicalInterface;
            }
        c["log"] = new JsonObject { ["loglevel"] = "warning" };
        c.Remove("remarks"); c.Remove("ps"); c.Remove("meta");
        return c;
    }
    static bool IsLoopback(string host) => host.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    static IEnumerable<string> Addresses(JsonNode? node)
    {
        if (node is JsonObject o)
            foreach (var pair in o)
            {
                if (pair.Key == "address" && pair.Value is JsonValue v && v.TryGetValue<string>(out var host)) yield return host;
                else foreach (string nested in Addresses(pair.Value)) yield return nested;
            }
        else if (node is JsonArray a) foreach (var child in a) foreach (string host in Addresses(child)) yield return host;
    }
    static void Port(int port) { if (port is < 1 or > 65535) throw new UserError("Неверный локальный порт."); }
    static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    static JsonObject Rule(string field, IEnumerable<string> values, string target) => new() { ["type"] = "field", [field] = Array(values), ["outboundTag"] = target };
}
