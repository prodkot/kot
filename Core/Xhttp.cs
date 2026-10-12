using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Kot.Core;

// XHTTP belongs to Xray. Never pass this transport to sing-box or silently replace it with TCP.
public sealed record XhttpBridge(int Port, string Username, string Password);
public static class Xhttp
{
    public const string Version = "26.3.27";
    public static bool Uses(Node node) => node.Outbound["transport"]?["type"]?.ToString() is "xhttp" or "splithttp";
    public static IReadOnlyList<Node> ConnectionNodes(Profile profile)
    {
        if (profile.Selected != "auto") return profile.Nodes.Where(n => n.Id == profile.Selected).ToArray();
        var selected = profile.Nodes.Where(n => profile.Favorites.Count == 0 || profile.Favorites.Contains(n.Id)).ToArray();
        return selected.Length > 0 ? selected : profile.Nodes;
    }
    public static JsonObject Transport(string host, string path, string mode = "", string extra = "")
    {
        JsonObject value = new() { ["type"] = "xhttp", ["host"] = host, ["path"] = path.Length > 0 ? path : "/", ["mode"] = mode.Length > 0 ? mode : "auto" };
        if (!string.IsNullOrWhiteSpace(extra))
        {
            if (extra.Length > 16384) throw new UserError("Параметры XHTTP extra слишком большие.");
            try { value["extra"] = JsonNode.Parse(extra, documentOptions: new JsonDocumentOptions { MaxDepth = 12 }) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new UserError("В XHTTP extra нужен JSON-объект."); }
        }
        ValidateTransport(value, 0); return value;
    }
    public static void Validate(Node node)
    {
        if (!Uses(node)) return;
        if (node.Protocol is not ("vless" or "vmess" or "trojan")) throw new UserError("XHTTP поддерживается для VLESS, VMess и Trojan.");
        if (node.Outbound["flow"]?.ToString() is { Length: > 0 }) throw new UserError("Для XHTTP VLESS flow должен быть пустым.");
        if (node.Outbound["packet_encoding"]?.ToString() is { Length: > 0 } packet && (packet != "xudp" || node.Protocol == "trojan")) throw new UserError("Для XHTTP packetEncoding поддерживается только xudp у VLESS и VMess.");
        ValidateTransport((JsonObject)node.Outbound["transport"]!, 0);
    }
    static readonly HashSet<string> Ranges = new(["xPaddingBytes", "uplinkChunkSize", "scMaxEachPostBytes", "scMinPostsIntervalMs", "scStreamUpServerSecs"], StringComparer.Ordinal);
    static readonly HashSet<string> Strings = new(["xPaddingKey", "xPaddingHeader", "xPaddingPlacement", "xPaddingMethod", "uplinkHTTPMethod", "sessionPlacement", "sessionKey", "seqPlacement", "seqKey", "uplinkDataPlacement", "uplinkDataKey"], StringComparer.Ordinal);
    static void ValidateTransport(JsonObject t, int depth)
    {
        if (depth > 2 || t.ToJsonString().Length > 20000) throw new UserError("Слишком сложные параметры XHTTP.");
        foreach (var (key, value) in t)
        {
            if (key == "type") continue;
            if (key is "host" or "path" or "mode") Text(value, key);
            else if (key == "extra" && value is JsonObject extra) ValidateExtra(extra, depth);
            else if (depth > 0) ValidateExtra(new JsonObject { [key] = value?.DeepClone() }, depth);
            else throw new UserError("Неподдерживаемый параметр XHTTP: " + key);
        }
        string mode = t["mode"]?.ToString() ?? "auto";
        if (mode is not ("" or "auto" or "packet-up" or "stream-up" or "stream-one")) throw new UserError("Неизвестный режим XHTTP.");
        if (t["path"]?.ToString() is { Length: > 0 } path && !path.StartsWith('/')) throw new UserError("Путь XHTTP должен начинаться с /.");
    }
    static void ValidateExtra(JsonObject extra, int depth)
    {
        foreach (var (key, value) in extra)
        {
            if (Strings.Contains(key)) Text(value, key);
            else if (Ranges.Contains(key)) Range(value, key);
            else if (key is "xPaddingObfsMode" or "noGRPCHeader" or "noSSEHeader") Bool(value, key);
            else if (key is "scMaxBufferedPosts" or "serverMaxHeaderBytes") Number(value, key);
            else if (key == "headers" && value is JsonObject headers)
            {
                if (headers.Count > 64) throw new UserError("Слишком много заголовков XHTTP.");
                foreach (var (name, header) in headers)
                {
                    if (name.Equals("host", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(name, @"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")) throw new UserError("В XHTTP headers нельзя задавать Host; используйте host.");
                    Text(header, name);
                }
            }
            else if (key == "xmux" && value is JsonObject xmux)
            {
                foreach (var (name, option) in xmux)
                    if (name == "hKeepAlivePeriod") Number(option, name);
                    else if (name is "maxConcurrency" or "maxConnections" or "cMaxReuseTimes" or "hMaxRequestTimes" or "hMaxReusableSecs") Range(option, name);
                    else throw new UserError("Неизвестный параметр XHTTP xmux: " + name);
            }
            else if (key == "downloadSettings" && value is JsonObject download) ValidateDownload(download, depth + 1);
            else throw new UserError("Неподдерживаемый параметр XHTTP extra: " + key);
        }
    }
    static void ValidateDownload(JsonObject stream, int depth)
    {
        foreach (var (key, value) in stream)
        {
            if (key is "address" or "network" or "security") Text(value, key);
            else if (key == "port") { Number(value, key); if (value!.GetValue<int>() is < 1 or > 65535) throw new UserError("Неверный порт XHTTP downloadSettings."); }
            else if (key is "xhttpSettings" or "splithttpSettings" && value is JsonObject settings)
            {
                // The nested Xray settings use extra for tuning, just like share links.
                ValidateTransport(settings, depth);
            }
            else if (key is "tlsSettings" or "realitySettings" && value is JsonObject tls)
            {
                foreach (var (name, option) in tls)
                {
                    if (name == "allowInsecure") { Bool(option, name); if (option!.GetValue<bool>()) throw new UserError("XHTTP downloadSettings отключает проверку TLS."); }
                    else if (name == "alpn" && option is JsonArray alpn) { foreach (var item in alpn) Text(item, name); }
                    else if (name is "serverName" or "fingerprint" or "publicKey" or "password" or "shortId" or "spiderX" or "minVersion" or "maxVersion") Text(option, name);
                    else throw new UserError("Неподдерживаемый параметр TLS XHTTP downloadSettings: " + name);
                }
            }
            else throw new UserError("Неподдерживаемый параметр XHTTP downloadSettings: " + key);
        }
        if (stream["network"]?.ToString() is { Length: > 0 } network && network is not ("xhttp" or "splithttp")) throw new UserError("XHTTP downloadSettings требует сеть xhttp.");
        if (stream["security"]?.ToString() is { Length: > 0 } security && security is not ("none" or "tls" or "reality")) throw new UserError("Неизвестная защита XHTTP downloadSettings.");
    }
    static void Text(JsonNode? value, string key)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || text.Length > 8192 || text.Any(char.IsControl)) throw new UserError("Неверный строковый параметр XHTTP: " + key);
    }
    static void Bool(JsonNode? value, string key) { if (value is not JsonValue v || !v.TryGetValue<bool>(out _)) throw new UserError("Неверный флаг XHTTP: " + key); }
    static void Number(JsonNode? value, string key) { if (value is not JsonValue v || !v.TryGetValue<int>(out int n) || n < 0) throw new UserError("Неверное число XHTTP: " + key); }
    static void Range(JsonNode? value, string key)
    {
        if (value is JsonValue v && v.TryGetValue<int>(out int n) && n >= 0) return;
        if (value is JsonValue text && text.TryGetValue<string>(out string? range))
        {
            string[] parts = range.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out int from) && int.TryParse(parts[1], out int to) && from >= 0 && to >= from) return;
        }
        throw new UserError("Неверный диапазон XHTTP: " + key);
    }
    public static JsonObject SingBoxOutbound(Node node, IReadOnlyDictionary<string, XhttpBridge>? bridges)
    {
        NodeSecurity.Validate(node);
        if (!Uses(node)) return (JsonObject)node.Outbound.DeepClone();
        if (bridges == null || !bridges.TryGetValue(node.Id, out var bridge)) throw new UserError("Для подключения XHTTP требуется ядро Xray.");
        if (bridge.Port is < 1 or > 65535 || bridge.Username.Length == 0 || bridge.Password.Length < 32) throw new UserError("Небезопасное локальное соединение XHTTP.");
        return new JsonObject { ["type"] = "socks", ["tag"] = "proxy", ["server"] = "127.0.0.1", ["server_port"] = bridge.Port, ["version"] = "5", ["username"] = bridge.Username, ["password"] = bridge.Password };
    }
    public static JsonObject Build(IReadOnlyList<Node> nodes, IReadOnlyDictionary<string, XhttpBridge> bridges)
    {
        JsonArray inputs = [], outputs = [], rules = [];
        foreach (var node in nodes.Where(Uses).DistinctBy(n => n.Id))
        {
            var bridge = bridges[node.Id]; _ = SingBoxOutbound(node, bridges);
            string inbound = "in-" + node.Id, outbound = "node-" + node.Id;
            inputs.Add(new JsonObject { ["tag"] = inbound, ["listen"] = "127.0.0.1", ["port"] = bridge.Port, ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "password", ["accounts"] = new JsonArray(new JsonObject { ["user"] = bridge.Username, ["pass"] = bridge.Password }), ["udp"] = true, ["ip"] = "127.0.0.1" } });
            var output = Outbound(node); output["tag"] = outbound; outputs.Add(output);
            rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(inbound), ["outboundTag"] = outbound });
        }
        // A missing route must fail closed, including unauthenticated SOCKS requests.
        outputs.Insert(0, new JsonObject { ["tag"] = "blocked", ["protocol"] = "blackhole" });
        // Windows can resolve through the DNS Client service, whose process is not Xray.
        // With TUN or WFP active, system DNS would either loop through the unfinished
        // proxy or be blocked. Bootstrap only transport endpoints inside Xray instead.
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "info" },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray("https+local://1.1.1.1/dns-query", "https+local://8.8.8.8/dns-query"), ["queryStrategy"] = "UseIP" },
            ["inbounds"] = inputs, ["outbounds"] = outputs,
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules }
        };
    }
    public static JsonObject Outbound(Node node)
    {
        NodeSecurity.Validate(node);
        if (!Uses(node)) throw new UserError("Это не XHTTP-сервер.");
        var o = node.Outbound; string G(string key) => o[key]?.ToString() ?? "";
        JsonObject user = node.Protocol == "trojan" ? new() { ["password"] = G("password") } : new() { ["id"] = G("uuid") };
        if (node.Protocol == "vless") user["encryption"] = "none";
        if (node.Protocol == "vmess") { user["alterId"] = 0; user["security"] = G("security") is { Length: > 0 } cipher ? cipher : "auto"; }
        JsonObject target = new() { ["address"] = G("server"), ["port"] = o["server_port"]!.DeepClone() };
        target[node.Protocol == "trojan" ? "password" : "users"] = node.Protocol == "trojan" ? JsonValue.Create(G("password")) : new JsonArray(user);
        JsonObject settings = new() { [node.Protocol == "trojan" ? "servers" : "vnext"] = new JsonArray(target) };
        var t = (JsonObject)o["transport"]!.DeepClone(); t.Remove("type");
        JsonObject stream = new()
        {
            ["network"] = "xhttp", ["security"] = "none", ["xhttpSettings"] = t,
            // ForceIP forbids falling back to system DNS after a failed lookup.
            // Penetrate applies the same resolver to extra.downloadSettings.
            ["sockopt"] = new JsonObject { ["domainStrategy"] = "ForceIP", ["penetrate"] = true, ["happyEyeballs"] = new JsonObject { ["tryDelayMs"] = 250 } }
        };
        if (o["tls"] is JsonObject tls && tls["enabled"]?.GetValue<bool>() == true)
        {
            string fingerprint = tls["utls"]?["fingerprint"]?.ToString() ?? "";
            JsonObject security = new() { ["serverName"] = tls["server_name"]?.ToString() ?? G("server") };
            if (tls["reality"] is JsonObject reality && reality["enabled"]?.GetValue<bool>() == true)
            {
                stream["security"] = "reality"; security["fingerprint"] = fingerprint.Length > 0 ? fingerprint : "chrome";
                security["publicKey"] = reality["public_key"]?.ToString(); security["shortId"] = reality["short_id"]?.ToString() ?? ""; stream["realitySettings"] = security;
            }
            else
            {
                stream["security"] = "tls"; security["allowInsecure"] = false;
                if (fingerprint.Length > 0) security["fingerprint"] = fingerprint;
                if (tls["alpn"] != null) security["alpn"] = tls["alpn"]!.DeepClone(); stream["tlsSettings"] = security;
            }
        }
        JsonObject outbound = new() { ["protocol"] = node.Protocol, ["settings"] = settings, ["streamSettings"] = stream };
        if (G("packet_encoding") == "xudp") outbound["mux"] = new JsonObject { ["enabled"] = true, ["concurrency"] = -1, ["xudpConcurrency"] = 8, ["xudpProxyUDP443"] = "allow" };
        return outbound;
    }
    public static void BypassCore(JsonObject config, string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return;
        if (!Path.IsPathFullyQualified(executable)) throw new UserError("Нужен абсолютный путь к ядру Xray.");
        var route = (JsonObject)config["route"]!; route["find_process"] = true;
        ((JsonArray)route["rules"]!).Insert(0, new JsonObject { ["process_path"] = new JsonArray(executable), ["action"] = "route", ["outbound"] = "direct" });
        // Process matching precedes TUN DNS interception and user rules, preventing a VPN loop.
        ((JsonArray)config["dns"]!["rules"]!).Insert(0, new JsonObject { ["process_path"] = new JsonArray(executable), ["action"] = "route", ["server"] = "bootstrap" });
    }
}
