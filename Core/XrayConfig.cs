using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.Core;

public static class XrayConfig
{
    public const int MaxBytes = 512 * 1024;
    static readonly HashSet<string> Top = new(["inbounds", "outbounds", "routing", "dns", "policy", "stats", "observatory", "burstObservatory", "fakedns", "log", "remarks", "ps", "meta"], StringComparer.Ordinal);
    public static SubscriptionResult Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            Duplicates(doc.RootElement);
            var root = JsonNode.Parse(text)!;
            var configs = root is JsonArray array ? array.ToArray() : [root];
            if (configs.Length is < 1 or > 200) throw new UserError("Полная подписка Xray: от 1 до 200 конфигураций.");
            List<Node> nodes = [];
            foreach (var item in configs)
            {
                var config = item as JsonObject ?? throw new UserError("Ожидается клиентская конфигурация Xray.");
                Validate(config);
                var first = ((JsonArray)config["outbounds"]!).OfType<JsonObject>().First(o => IsProxy(o));
                string name = config["remarks"]?.ToString() ?? config["ps"]?.ToString() ?? config["meta"]?["serverDescription"]?.ToString() ?? first["tag"]?.ToString() ?? "Xray";
                name = string.Concat(name.Where(c => !char.IsControl(c))); if (name.Length > 140) name = name[..140];
                var target = first["settings"]?["vnext"]?[0] ?? first["settings"]?["servers"]?[0];
                string address = target?["address"]?.ToString() ?? first["settings"]?["address"]?.ToString() ?? "";
                int port = target?["port"]?.GetValue<int>() ?? first["settings"]?["port"]?.GetValue<int>() ?? 443;
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.ToJsonString()))).ToLowerInvariant();
                nodes.Add(new(id, name.Length == 0 ? "Xray" : name, "xray", new JsonObject { ["type"] = "xray", ["tag"] = "proxy", ["server"] = address, ["server_port"] = port }, (JsonObject)config.DeepClone()));
            }
            return new(nodes.DistinctBy(n => n.Id).ToList(), []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { throw new UserError("Неверная клиентская JSON-конфигурация Xray. " + ex.Message); }
    }
    static void Duplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject()) { if (!keys.Add(p.Name)) throw new UserError("Повторяющиеся поля конфигурации Xray."); Duplicates(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Duplicates(item);
    }
    public static bool IsProxy(JsonObject outbound) => outbound["protocol"]?.ToString() is "vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria" or "socks" or "http";
    public static void Validate(JsonObject config)
    {
        if (Encoding.UTF8.GetByteCount(config.ToJsonString()) > MaxBytes) throw new UserError("Клиентский конфиг Xray больше 512 КиБ.");
        foreach (var item in config) if (!Top.Contains(item.Key)) throw new UserError("Неподдерживаемый раздел клиентского Xray-конфига: " + item.Key);
        if (config["inbounds"] is not JsonArray inputs || inputs.Count > 32 || config["outbounds"] is not JsonArray outputs || outputs.Count is < 1 or > 128 || outputs.Any(o => o is not JsonObject) || !outputs.OfType<JsonObject>().Any(IsProxy))
            throw new UserError("Нужен полный клиентский Xray-конфиг с inbounds и outbounds, включая прокси-сервер.");
        foreach (var input in inputs)
            if (input is not JsonObject inbound || inbound["protocol"]?.ToString() is not ("socks" or "http" or "mixed" or "tun" or "dokodemo-door")) throw new UserError("В клиентском конфиге Xray указан серверный inbound.");
        var tags = outputs.Select(o => o!["tag"]?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
        if (tags.Distinct().Count() != tags.Length || tags.Any(s => s.StartsWith("kot-", StringComparison.Ordinal))) throw new UserError("Повторяющиеся или зарезервированные теги Xray.");
        foreach (var output in outputs.OfType<JsonObject>())
            if (output["protocol"]?.ToString() is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria" or "socks" or "http" or "freedom" or "blackhole" or "dns" or "loopback")) throw new UserError("Неподдерживаемый outbound клиентского Xray-конфига.");
        Safe(config);
        if (config["fakedns"] != null || config["dns"]?["servers"] is JsonArray dns && dns.Any(s => (s is JsonObject server ? server["address"]?.ToString() : s?.ToString()) == "fakedns")) throw new UserError("Полные конфиги с FakeDNS пока не поддерживаются. Нужен конфиг с обычными DNS-ответами.");
    }
    static void Safe(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if (key.Contains("file", StringComparison.OrdinalIgnoreCase) || key is "certificates" or "masterKeyLog" or "execute" or "plugin" or "systemRootCerts" or "browserForwarder" or "browserDialer")
                    throw new UserError("Локальные файлы, ключи и внешние команды из подписки Xray не разрешены: " + key);
                if (key is "allowInsecure" or "allowInsecureCiphers" && value?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true) throw new UserError("Подписка Xray отключает проверку TLS.");
                if (key is "access" or "error" && value?.ToString() is { Length: > 0 } path && path != "none") throw new UserError("Подписка Xray не может задавать пути лог-файлов.");
                Safe(value);
            }
        else if (node is JsonArray array) foreach (var item in array) Safe(item);
        else if (node is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith("ext:", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("ext:geoip.dat:", StringComparison.Ordinal) && !text.StartsWith("ext:geosite.dat:", StringComparison.Ordinal)) throw new UserError("Подписка ссылается на неизвестный локальный геофайл.");
    }
    public static string[] GeoFiles(JsonObject config)
    {
        HashSet<string> files = [];
        void Visit(JsonNode? value)
        {
            if (value is JsonObject obj) foreach (var p in obj) Visit(p.Value);
            else if (value is JsonArray arr) foreach (var item in arr) Visit(item);
            else if (value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                if (s.StartsWith("geoip:") || s.StartsWith("ext:geoip.dat:")) files.Add("geoip.dat");
                if (s.StartsWith("geosite:") || s.StartsWith("ext:geosite.dat:")) files.Add("geosite.dat");
            }
        }
        Visit(config); return files.Order().ToArray();
    }
    public static JsonObject Runtime(Node node, XhttpBridge bridge, XhttpBridge egress)
    {
        var saved = node.FullConfig ?? throw new UserError("Нет клиентского конфига Xray."); Validate(saved);
        var config = (JsonObject)saved.DeepClone();
        var outputs = (JsonArray)config["outbounds"]!;
        string firstTag = outputs[0]!["tag"]?.ToString() ?? "kot-default"; outputs[0]!["tag"] = firstTag;
        var proxy = outputs.OfType<JsonObject>().First(IsProxy);
        string proxyTag = proxy["tag"]?.ToString() ?? "kot-health-proxy"; proxy["tag"] = proxyTag;
        var originalInputs = ((JsonArray)config["inbounds"]!).Select(i => i?["tag"]?.ToString()).Where(t => t != null).ToHashSet();
        JsonObject Input(int port, string tag) => new() { ["tag"] = tag, ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "password", ["accounts"] = new JsonArray(new JsonObject { ["user"] = bridge.Username, ["pass"] = bridge.Password }), ["udp"] = true, ["ip"] = "127.0.0.1" }, ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = true } };
        config["inbounds"] = new JsonArray(Input(bridge.Port, "kot-in"));
        if (bridge.HealthPort > 0) ((JsonArray)config["inbounds"]!).Add(Input(bridge.HealthPort, "kot-health"));
        outputs.Add(new JsonObject { ["tag"] = "kot-dns-out", ["protocol"] = "dns" });
        if (bridge.DnsPort > 0)
        {
            ((JsonArray)config["inbounds"]!).Add(new JsonObject { ["tag"] = "kot-dns-in", ["listen"] = "127.0.0.1", ["port"] = bridge.DnsPort, ["protocol"] = "dokodemo-door", ["settings"] = new JsonObject { ["address"] = "1.1.1.1", ["port"] = 53, ["network"] = "tcp,udp" } });
        }
        var routing = config["routing"] as JsonObject ?? new JsonObject(); config["routing"] = routing;
        var rules = routing["rules"] as JsonArray ?? new JsonArray(); routing["rules"] = rules;
        foreach (var rule in rules.OfType<JsonObject>())
            if (rule["inboundTag"] is JsonArray list)
                for (int i = 0; i < list.Count; i++) if (originalInputs.Contains(list[i]?.ToString())) list[i] = "kot-in";
        // A health check must reach a proxy even when provider rules bypass its hostname.
        string? balancer = routing["balancers"] is JsonArray balances ? balances.OfType<JsonObject>().FirstOrDefault()?["tag"]?.ToString() : null;
        rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-health"), [balancer == null ? "outboundTag" : "balancerTag"] = balancer ?? proxyTag });
        if (bridge.DnsPort > 0) rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-dns-in"), ["outboundTag"] = "kot-dns-out" });
        rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-in"), ["port"] = "53", ["outboundTag"] = "kot-dns-out" });
        rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("kot-in"), ["outboundTag"] = firstTag });
        var dns = config["dns"] as JsonObject;
        if (dns == null || dns["servers"] is not JsonArray servers || servers.Count == 0)
        {
            dns ??= new JsonObject();
            dns["servers"] = new JsonArray("https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query");
            dns["queryStrategy"] = "UseIP"; config["dns"] = dns;
        }
        if (dns["tag"] == null) dns["tag"] = "kot-dns";
        if (config["observatory"] != null || config["burstObservatory"] != null)
        {
            config["stats"] ??= new JsonObject();
            if (balancer != null)
            {
                // A balancer needs probe results; DNS needed by those probes cannot depend on it.
                var direct = outputs.OfType<JsonObject>().FirstOrDefault(o => o["protocol"]?.ToString() == "freedom");
                if (direct == null) { direct = new JsonObject { ["protocol"] = "freedom", ["tag"] = "kot-bootstrap" }; outputs.Add(direct); }
                direct["tag"] ??= "kot-bootstrap";
                var tags = new HashSet<string> { dns["tag"]!.ToString() };
                foreach (var server in ((JsonArray)dns["servers"]!).OfType<JsonObject>()) if (server["tag"] != null) tags.Add(server["tag"]!.ToString());
                rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(tags.Select(t => JsonValue.Create(t)).ToArray()), ["outboundTag"] = direct["tag"]!.DeepClone() });
            }
        }
        outputs.Add(new JsonObject { ["tag"] = "kot-transport", ["protocol"] = "socks", ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = egress.Port, ["users"] = new JsonArray(new JsonObject { ["user"] = egress.Username, ["pass"] = egress.Password }) }) } });
        // Physical dialing stays inside sing-box, outside its TUN and through its WFP allowance.
        foreach (var output in outputs.OfType<JsonObject>().Where(o => o["tag"]?.ToString() != "kot-transport" && (IsProxy(o) || o["protocol"]?.ToString() == "freedom")))
        {
            var stream = output["streamSettings"] as JsonObject ?? new JsonObject(); output["streamSettings"] = stream;
            var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject(); stream["sockopt"] = sockopt;
            if (sockopt["dialerProxy"] == null && output["proxySettings"] == null)
            {
                sockopt["dialerProxy"] = "kot-transport";
                // The authenticated physical SOCKS dialer resolves transport endpoints.
                // Using the provider resolver here can depend on the unfinished tunnel.
                sockopt["domainStrategy"] = "AsIs";
            }
        }
        config["log"] = new JsonObject { ["loglevel"] = "warning" };
        config.Remove("remarks"); config.Remove("ps"); config.Remove("meta");
        return config;
    }
    public static JsonObject Outbound(Node node)
    {
        if (Xhttp.UsesTransport(node)) return Xhttp.Outbound(node);
        var o = node.Outbound; string G(string key) => o[key]?.ToString() ?? "";
        JsonObject target = new() { ["address"] = G("server"), ["port"] = o["server_port"]!.DeepClone() };
        JsonObject settings;
        if (node.Protocol is "vless" or "vmess")
        {
            var user = new JsonObject { ["id"] = G("uuid") };
            if (node.Protocol == "vless") { user["encryption"] = "none"; if (G("flow").Length > 0) user["flow"] = G("flow"); }
            else { user["alterId"] = 0; user["security"] = G("security") is { Length: > 0 } cipher ? cipher : "auto"; }
            target["users"] = new JsonArray(user); settings = new JsonObject { ["vnext"] = new JsonArray(target) };
        }
        else if (node.Protocol is "trojan" or "shadowsocks")
        {
            target["password"] = G("password"); if (node.Protocol == "shadowsocks") target["method"] = G("method");
            settings = new JsonObject { ["servers"] = new JsonArray(target) };
        }
        else if (node.Protocol == "hysteria2") settings = new JsonObject { ["version"] = 2, ["address"] = G("server"), ["port"] = o["server_port"]!.DeepClone() };
        else throw new UserError("Этот протокол не поддерживает маршрутизацию Xray.");
        JsonObject stream = new() { ["network"] = "raw", ["security"] = "none", ["sockopt"] = new JsonObject { ["domainStrategy"] = "ForceIP" } };
        if (o["transport"] is JsonObject t)
        {
            string type = t["type"]?.ToString() ?? "";
            switch (type)
            {
                case "ws":
                    stream["network"] = "ws"; stream["wsSettings"] = new JsonObject { ["path"] = t["path"]?.DeepClone() ?? JsonValue.Create("/"), ["headers"] = t["headers"]?.DeepClone() ?? new JsonObject() };
                    if (t["max_early_data"]?.GetValue<int>() is > 0)
                    {
                        string path = stream["wsSettings"]!["path"]!.GetValue<string>();
                        stream["wsSettings"]!["path"] = path + (path.Contains('?') ? "&" : "?") + "ed=" + t["max_early_data"];
                    }
                    break;
                case "grpc": stream["network"] = "grpc"; stream["grpcSettings"] = new JsonObject { ["serviceName"] = t["service_name"]?.DeepClone() }; break;
                case "httpupgrade": stream["network"] = "httpupgrade"; stream["httpupgradeSettings"] = new JsonObject { ["host"] = t["host"]?.DeepClone(), ["path"] = t["path"]?.DeepClone() }; break;
                default: throw new UserError("Транспорт подписки нельзя перенести в Xray: " + type);
            }
        }
        if (o["tls"] is JsonObject tls && tls["enabled"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            var opt = new JsonObject { ["serverName"] = tls["server_name"]?.DeepClone() ?? JsonValue.Create(G("server")), ["allowInsecure"] = false };
            if (tls["utls"]?["fingerprint"] != null) opt["fingerprint"] = tls["utls"]!["fingerprint"]!.DeepClone();
            if (tls["alpn"] != null) opt["alpn"] = tls["alpn"]!.DeepClone();
            if (tls["reality"] is JsonObject reality)
            {
                stream["security"] = "reality"; opt.Remove("allowInsecure"); opt["publicKey"] = reality["public_key"]?.DeepClone(); opt["shortId"] = reality["short_id"]?.DeepClone(); stream["realitySettings"] = opt;
            }
            else { stream["security"] = "tls"; stream["tlsSettings"] = opt; }
        }
        if (node.Protocol == "hysteria2")
        {
            stream["network"] = "hysteria"; stream["hysteriaSettings"] = new JsonObject { ["version"] = 2, ["auth"] = G("password") };
            if (o["obfs"] is JsonObject obfs) stream["finalmask"] = new JsonObject { ["udp"] = new JsonArray(new JsonObject { ["type"] = "salamander", ["settings"] = new JsonObject { ["password"] = obfs["password"]?.DeepClone() } }) };
        }
        var output = new JsonObject { ["protocol"] = node.Protocol == "hysteria2" ? "hysteria" : node.Protocol, ["tag"] = "proxy", ["settings"] = settings, ["streamSettings"] = stream };
        if (G("packet_encoding") == "xudp" && node.Protocol is "vless" or "vmess") output["mux"] = new JsonObject { ["enabled"] = true, ["concurrency"] = -1, ["xudpConcurrency"] = 8 };
        return output;
    }
}
