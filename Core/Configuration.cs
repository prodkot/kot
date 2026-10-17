using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Kot.Core;
public static class Configuration
{
    public static string[] Domains(string input)
    {
        var items = input.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToArray();
        if (items.Length > 100) throw new UserError("Максимум 100 доменов.");
        foreach (string item in items)
            if (item.Length > 253 || !Regex.IsMatch(item, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z][a-z0-9-]{0,61}[a-z0-9]$")) throw new UserError("Введите домены или зоны, например example.com или .ru, без https://, путей и масок, по одному в строке.");
        return items;
    }
    public static JsonObject BuildAutomatic(IReadOnlyList<Node> nodes, int port, bool smart, string[] bypass, AutomationOptions options, int apiPort = 0, string apiSecret = "", bool tun = true, IReadOnlyDictionary<string, XhttpBridge>? xhttp = null)
    {
        options.Validate();
        if (nodes.Count == 0) throw new UserError("Нет серверов для автоматического выбора.");
        var config = Build(nodes[0], port, smart, bypass, tun, xhttp);
        var outputs = new JsonArray(); var tags = new JsonArray();
        foreach (var node in nodes)
        {
            NodeSecurity.Validate(node);
            var outbound = Xhttp.SingBoxOutbound(node, xhttp); string tag = "node-" + node.Id;
            outbound["tag"] = tag; outputs.Add(outbound); tags.Add(tag);
        }
        outputs.Add(new JsonObject { ["type"] = "urltest", ["tag"] = "proxy", ["outbounds"] = tags,
            ["url"] = "https://www.gstatic.com/generate_204", ["interval"] = options.AutoMinutes + "m", ["tolerance"] = options.AutoToleranceMs, ["interrupt_exist_connections"] = false });
        outputs.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }); config["outbounds"] = outputs;
        if (apiPort > 0) Controller(config, apiPort, apiSecret);
        return config;
    }
    public static JsonObject BuildConnection(Profile profile, int healthPort, int proxyPort = 0, int apiPort = 0, string apiSecret = "", IReadOnlyDictionary<string, string>? geoIpPaths = null, IReadOnlyDictionary<string, XhttpBridge>? xhttp = null, XhttpBridge? xhttpEgress = null)
    {
        if (profile.ConnectionMode is not ("tun" or "proxy")) throw new UserError("Неизвестный режим подключения.");
        bool tun = profile.ConnectionMode == "tun";
        if (!tun && (proxyPort is < 1 or > 65535 || proxyPort == healthPort)) throw new UserError("Некорректный порт системного прокси.");
        bool smart = profile.Mode == "smart";
        var candidates = profile.Favorites.Count > 0 ? profile.Nodes.Where(n => profile.Favorites.Contains(n.Id)).ToList() : profile.Nodes;
        if (candidates.Count == 0) candidates = profile.Nodes;
        var node = profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected);
        var config = profile.Selected == "auto" ? BuildAutomatic(candidates, healthPort, smart, profile.Bypass, profile.Automation, tun: tun, xhttp: xhttp)
            : Build(node ?? throw new UserError("Выберите сервер."), healthPort, smart, profile.Bypass, tun, xhttp);
        // A separate listener keeps the health probe on the selected server even when rules bypass its domain.
        if (!tun) ((JsonArray)config["inbounds"]!).Add(new JsonObject { ["type"] = "mixed", ["tag"] = "system-in", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort });
        if (profile.ActiveRoutingProfile.Length > 0)
        {
            var routing = profile.RoutingProfiles.FirstOrDefault(p => p.Id == profile.ActiveRoutingProfile) ?? throw new UserError("Профиль маршрутизации уже удалён.");
            routing.Validate(); ApplyRouting(config, routing.Rules, geoIpPaths);
        }
        if (Xhttp.ConnectionNodes(profile).Any(Xhttp.Uses))
        {
            if (xhttpEgress == null) throw new UserError("Для XHTTP нужен локальный выход ядра Xray.");
            Xhttp.AddEgress(config, xhttpEgress);
        }
        if (apiPort > 0) Controller(config, apiPort, apiSecret);
        return config;
    }
    static void ApplyRouting(JsonObject config, RoutingRules rules, IReadOnlyDictionary<string, string>? geoIpPaths)
    {
        JsonObject Dns(string tag, string preset, string? detour = null)
        {
            var (ip, host) = RoutingRules.DnsPreset(preset);
            var server = new JsonObject { ["type"] = "https", ["tag"] = tag, ["server"] = ip, ["path"] = "/dns-query", ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = host } };
            if (detour != null) server["detour"] = detour;
            return server;
        }
        var route = (JsonObject)config["route"]!; var routes = (JsonArray)route["rules"]!;
        var countries = rules.Countries();
        if (countries.Length > 0)
        {
            JsonArray sets = [];
            foreach (string code in countries)
            {
                if (geoIpPaths == null || !geoIpPaths.TryGetValue(code, out string? path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                    throw new UserError($"База GeoIP {code.ToUpperInvariant()} ещё не загружена. Обновите GeoIP перед подключением.");
                sets.Add(new JsonObject { ["type"] = "local", ["tag"] = "geoip-" + code, ["format"] = "binary", ["path"] = path });
            }
            route["rule_set"] = sets;
        }
        // Retain DNS interception, the isolated health listener and LAN access, then replace legacy domain rules.
        while (routes.Count > 4) routes.RemoveAt(4);
        if (countries.Length + rules.DirectIps.Length + rules.ProxyIps.Length + rules.BlockIps.Length > 0)
        {
            // HTTP/SOCKS requests can contain a hostname. IP and country matchers need resolved addresses;
            // omitting server lets the configured DNS rules select the direct or VPN resolver.
            routes.Add(new JsonObject { ["action"] = "resolve" });
            routes.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });
        }
        JsonArray dns = [new JsonObject { ["inbound"] = new JsonArray("health-in"), ["action"] = "route", ["server"] = "remote" }];
        void Rule(string field, string[] values, string action, bool dnsRule = false)
        {
            if (values.Length == 0) return;
            var rule = new JsonObject { [field] = JsonSerializer.SerializeToNode(values), ["action"] = action == "block" ? "reject" : "route" };
            if (action != "block") rule[dnsRule ? "server" : "outbound"] = dnsRule ? action == "direct" ? "bootstrap" : "remote" : action;
            (dnsRule ? dns : routes).Add(rule);
        }
        void Apps(string[] values, string action, bool dnsRule = false) => Rule("process_path_regex", values.Select(name => @"(?i)(?:^|[\\/])" + Regex.Escape(name) + "$" ).ToArray(), action, dnsRule);
        // First match wins: blocks, applications, domains, explicit IPs, GeoIP, then the default route.
        Apps(rules.BlockApps, "block"); Rule("domain_suffix", rules.BlockDomains, "block"); Rule("ip_cidr", rules.BlockIps, "block");
        Rule("rule_set", rules.BlockCountries.Select(code => "geoip-" + code).ToArray(), "block");
        Apps(rules.ProxyApps, "proxy"); Apps(rules.DirectApps, "direct");
        Rule("domain_suffix", rules.ProxyDomains, "proxy"); Rule("domain_suffix", rules.DirectDomains, "direct");
        Rule("ip_cidr", rules.ProxyIps, "proxy"); Rule("ip_cidr", rules.DirectIps, "direct");
        Rule("rule_set", rules.ProxyCountries.Select(code => "geoip-" + code).ToArray(), "proxy");
        Rule("rule_set", rules.DirectCountries.Select(code => "geoip-" + code).ToArray(), "direct");
        Apps(rules.BlockApps, "block", true); Rule("domain_suffix", rules.BlockDomains, "block", true);
        Apps(rules.ProxyApps, "proxy", true); Apps(rules.DirectApps, "direct", true);
        Rule("domain_suffix", rules.ProxyDomains, "proxy", true); Rule("domain_suffix", rules.DirectDomains, "direct", true);
        route["final"] = rules.DefaultAction;
        route["find_process"] = rules.DirectApps.Length + rules.ProxyApps.Length + rules.BlockApps.Length > 0;
        config["dns"] = new JsonObject { ["servers"] = new JsonArray(Dns("bootstrap", rules.DirectDns), Dns("remote", rules.RemoteDns, "proxy")), ["rules"] = dns, ["final"] = rules.DefaultAction == "direct" ? "bootstrap" : "remote", ["strategy"] = "prefer_ipv4" };
    }
    public static void Controller(JsonObject config, int port, string secret)
    {
        if (port is < 1 or > 65535 || secret.Length < 32) throw new UserError("Небезопасные настройки локального API.");
        config["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:" + port, ["secret"] = secret, ["access_control_allow_origin"] = new JsonArray("https://kot.local") } };
        ((JsonObject)config["route"]!)["find_process"] = true;
    }
    public static JsonObject Build(Node node, int port, bool smart, string[] bypass, bool tun = true, IReadOnlyDictionary<string, XhttpBridge>? xhttp = null)
    {
        NodeSecurity.Validate(node);
        JsonObject Dns(string tag, string? detour = null)
        {
            var dns = new JsonObject { ["type"] = "https", ["tag"] = tag, ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" } };
            // New DNS transports use their own direct dialer when detour is absent.
            // A detour to a plain direct outbound fails at service startup in 1.14.2.
            if (detour != null) dns["detour"] = detour;
            return dns;
        }
        JsonArray inbound = [new JsonObject { ["type"] = "mixed", ["tag"] = "health-in", ["listen"] = "127.0.0.1", ["listen_port"] = port }];
        if (tun) inbound.Add(new JsonObject { ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "kot-tun", ["address"] = new JsonArray("172.28.231.1/30", "fdfe:dcba:231::1/126"), ["mtu"] = 1500, ["auto_route"] = true, ["strict_route"] = true, ["dns_mode"] = "hijack" });
        JsonArray routeRules = [new JsonObject { ["action"] = "sniff" }, new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }, new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" }];
        routeRules.Insert(2, new JsonObject { ["inbound"] = new JsonArray("health-in"), ["action"] = "route", ["outbound"] = "proxy" });
        JsonArray dnsRules = [];
        if (smart && bypass.Length > 0)
        {
            routeRules.Add(new JsonObject { ["domain_suffix"] = JsonSerializer.SerializeToNode(bypass), ["action"] = "route", ["outbound"] = "direct" });
            dnsRules.Add(new JsonObject { ["domain_suffix"] = JsonSerializer.SerializeToNode(bypass), ["action"] = "route", ["server"] = "bootstrap" });
        }
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = false },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(Dns("bootstrap"), Dns("remote", "proxy")), ["rules"] = dnsRules, ["final"] = "remote", ["strategy"] = "prefer_ipv4" },
            ["inbounds"] = inbound,
            ["outbounds"] = new JsonArray(Xhttp.SingBoxOutbound(node, xhttp), new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject { ["rules"] = routeRules, ["final"] = "proxy", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "bootstrap" }
        };
    }
}
