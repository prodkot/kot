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
            if (item.Length > 253 || !Regex.IsMatch(item, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")) throw new UserError("Введите домены без https:// и путей, по одному в строке.");
        return items;
    }
    public static JsonObject BuildAutomatic(IReadOnlyList<Node> nodes, int port, bool smart, string[] bypass, AutomationOptions options, int apiPort = 0, string apiSecret = "", bool tun = true)
    {
        options.Validate();
        if (nodes.Count == 0) throw new UserError("Нет серверов для автоматического выбора.");
        var config = Build(nodes[0], port, smart, bypass, tun);
        var outputs = new JsonArray(); var tags = new JsonArray();
        foreach (var node in nodes)
        {
            NodeSecurity.Validate(node);
            var outbound = (JsonObject)node.Outbound.DeepClone(); string tag = "node-" + node.Id;
            outbound["tag"] = tag; outputs.Add(outbound); tags.Add(tag);
        }
        outputs.Add(new JsonObject { ["type"] = "urltest", ["tag"] = "proxy", ["outbounds"] = tags,
            ["url"] = "https://www.gstatic.com/generate_204", ["interval"] = options.AutoMinutes + "m", ["tolerance"] = options.AutoToleranceMs, ["interrupt_exist_connections"] = false });
        outputs.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }); config["outbounds"] = outputs;
        if (apiPort > 0) Controller(config, apiPort, apiSecret);
        return config;
    }
    public static JsonObject BuildConnection(Profile profile, int healthPort, int proxyPort = 0, int apiPort = 0, string apiSecret = "")
    {
        if (profile.ConnectionMode is not ("tun" or "proxy")) throw new UserError("Неизвестный режим подключения.");
        bool tun = profile.ConnectionMode == "tun";
        if (!tun && (proxyPort is < 1 or > 65535 || proxyPort == healthPort)) throw new UserError("Некорректный порт системного прокси.");
        bool smart = profile.Mode == "smart";
        var candidates = profile.Favorites.Count > 0 ? profile.Nodes.Where(n => profile.Favorites.Contains(n.Id)).ToList() : profile.Nodes;
        if (candidates.Count == 0) candidates = profile.Nodes;
        var node = profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected);
        var config = profile.Selected == "auto" ? BuildAutomatic(candidates, healthPort, smart, profile.Bypass, profile.Automation, tun: tun)
            : Build(node ?? throw new UserError("Выберите сервер."), healthPort, smart, profile.Bypass, tun);
        // A separate listener keeps the health probe on the selected server even when rules bypass its domain.
        if (!tun) ((JsonArray)config["inbounds"]!).Add(new JsonObject { ["type"] = "mixed", ["tag"] = "system-in", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort });
        if (apiPort > 0) Controller(config, apiPort, apiSecret);
        return config;
    }
    public static void Controller(JsonObject config, int port, string secret)
    {
        if (port is < 1 or > 65535 || secret.Length < 32) throw new UserError("Небезопасные настройки локального API.");
        config["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:" + port, ["secret"] = secret, ["access_control_allow_origin"] = new JsonArray("https://kot.local") } };
        ((JsonObject)config["route"]!)["find_process"] = true;
    }
    public static JsonObject Build(Node node, int port, bool smart, string[] bypass, bool tun = true)
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
            ["outbounds"] = new JsonArray(node.Outbound.DeepClone(), new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject { ["rules"] = routeRules, ["final"] = "proxy", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "bootstrap" }
        };
    }
}
