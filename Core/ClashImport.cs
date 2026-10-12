using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
namespace Kot.Core;

// Only proxy definitions are translated. Foreign rules, scripts, files and listeners are never executed.
public static partial class ClashImport
{
    [GeneratedRegex(@"(?m)^proxies\s*:|^\s*\{\s*[""']?proxies[""']?\s*:", RegexOptions.CultureInvariant)] private static partial Regex ProxyKey();
    public static bool LooksLikeYaml(string text) => ProxyKey().IsMatch(text) || text.StartsWith("---") || text.StartsWith("proxy-providers:");
    public static SubscriptionResult Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > Subscriptions.MaxBytes) throw new UserError("YAML слишком большой.");
        try
        {
            var parser = new Parser(new StringReader(text)); int events = 0, depth = 0;
            List<(int Depth, string Name)> activeAnchors = [];
            while (parser.MoveNext())
            {
                if (++events > 100_000) throw new UserError("В YAML слишком много элементов.");
                if (parser.Current is AnchorAlias alias && activeAnchors.Any(a => a.Name == alias.Value.ToString())) throw new UserError("Циклические ссылки YAML не поддерживаются.");
                if (parser.Current is MappingStart or SequenceStart)
                {
                    if (++depth > 32) throw new UserError("YAML вложен слишком глубоко.");
                    if (parser.Current is NodeEvent nodeEvent && !nodeEvent.Anchor.IsEmpty) activeAnchors.Add((depth, nodeEvent.Anchor.ToString()));
                }
                if (parser.Current is MappingEnd or SequenceEnd) { depth--; activeAnchors.RemoveAll(a => a.Depth > depth); }
            }
            var yaml = new YamlStream(); yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root) throw new UserError("Нужен один документ Clash YAML с разделом proxies.");
            if (Get(root, "proxies") is not YamlSequenceNode proxies) throw new UserError("В YAML нет списка proxies. Ссылки proxy-providers автоматически не загружаются.");
            if (proxies.Children.Count > 2000) throw new UserError("В YAML больше 2000 серверов.");
            List<Node> nodes = []; List<string> warnings = []; HashSet<string> ids = [];
            for (int i = 0; i < proxies.Children.Count; i++)
            {
                try
                {
                    int budget = 10_000;
                    var converted = ConvertNode(proxies.Children[i], new(ReferenceEqualityComparer.Instance), ref budget, 0) as JsonObject ?? throw new UserError("Неверное описание сервера.");
                    Node node = ConvertProxy(converted); if (ids.Add(node.Id)) nodes.Add(node);
                }
                catch (UserError ex) { warnings.Add($"Сервер {i + 1}: {ex.Message}"); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
                { warnings.Add($"Сервер {i + 1}: неверное описание сервера."); }
            }
            if (nodes.Count == 0) throw new UserError(warnings.FirstOrDefault() ?? "В YAML нет поддерживаемых серверов.");
            return new(nodes, warnings);
        }
        catch (YamlException) { throw new UserError("Повреждён Clash YAML: проверьте отступы и повторяющиеся ключи."); }
    }
    static YamlNode? Get(YamlMappingNode mapping, string key) => mapping.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode scalar && scalar.Value == key).Value;
    static JsonNode? ConvertNode(YamlNode node, HashSet<YamlNode> path, ref int budget, int depth)
    {
        if (--budget < 0 || depth > 32 || !path.Add(node)) throw new UserError("Циклические или слишком большие ссылки YAML.");
        try
        {
            if (node is YamlScalarNode scalar) return JsonValue.Create(scalar.Value ?? "");
            if (node is YamlSequenceNode sequence)
            {
                JsonArray array = []; foreach (var child in sequence.Children) array.Add(ConvertNode(child, path, ref budget, depth + 1)); return array;
            }
            if (node is not YamlMappingNode mapping) throw new UserError("Неизвестный элемент YAML.");
            JsonObject obj = new();
            if (Get(mapping, "<<") is { } merge)
            {
                var inherited = ConvertNode(merge, path, ref budget, depth + 1);
                IEnumerable<JsonNode?> sources = inherited is JsonArray a ? a : [inherited];
                foreach (var source in sources) { if (source is not JsonObject parent) throw new UserError("Неверное объединение YAML."); foreach (var kv in parent) if (!obj.ContainsKey(kv.Key)) obj[kv.Key] = kv.Value?.DeepClone(); }
            }
            foreach (var kv in mapping.Children)
            {
                if (kv.Key is not YamlScalarNode key || key.Value == null) throw new UserError("Неверный ключ YAML.");
                if (key.Value != "<<") obj[key.Value] = ConvertNode(kv.Value, path, ref budget, depth + 1);
            }
            return obj;
        }
        finally { path.Remove(node); }
    }
    static Node ConvertProxy(JsonObject p)
    {
        string G(string key, string fallback = "") => p[key]?.ToString() ?? fallback;
        bool B(string key) => G(key).Equals("true", StringComparison.OrdinalIgnoreCase) || G(key) == "1";
        string kind = G("type").ToLowerInvariant(); kind = kind == "ss" ? "shadowsocks" : kind;
        if (kind is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria2")) throw new UserError("Протокол пока не поддерживается.");
        if (G("server").Length == 0 || G("server").Length > 253 || !int.TryParse(G("port"), out int port) || port is < 1 or > 65535) throw new UserError("Неверный адрес или порт.");
        NodeSecurity.RejectInsecureFlag(G("skip-cert-verify"));
        if (new[] { "plugin", "plugin-opts", "dialer-proxy", "ports", "certificate", "private-key", "fingerprint", "encryption", "ech-opts", "shadow-tls-opts", "reality-opts-extra" }.Any(k => p[k] != null && G(k) is not ("" or "none"))) throw new UserError("Плагины, цепочки, port hopping или нестандартные параметры TLS/шифрования пока не поддерживаются.");
        JsonObject o = new() { ["type"] = kind, ["tag"] = "proxy", ["server"] = G("server"), ["server_port"] = port };
        if (kind is "vless" or "vmess") { if (!Guid.TryParse(G("uuid"), out _)) throw new UserError("Неверный UUID."); o["uuid"] = G("uuid"); }
        else { if (G("password").Length == 0) throw new UserError("Нет пароля."); o["password"] = G("password"); }
        if (kind == "vmess") { if (G("alterId", "0") != "0") throw new UserError("Устаревший VMess alterId."); o["security"] = G("cipher", "auto"); }
        if (kind == "shadowsocks") o["method"] = G("cipher");
        if (kind == "vless" && G("flow").Length > 0) o["flow"] = G("flow");
        if (B("tls") || kind is "trojan" or "hysteria2" || p["reality-opts"] != null)
        {
            JsonObject tls = new() { ["enabled"] = true, ["server_name"] = G("servername", G("sni", G("server"))) };
            if (p["alpn"] is JsonArray alpn) tls["alpn"] = alpn.DeepClone();
            if (G("client-fingerprint").Length > 0) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = G("client-fingerprint") };
            if (p["reality-opts"] is JsonObject reality) { tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = reality["public-key"]?.ToString(), ["short_id"] = reality["short-id"]?.ToString() ?? "" }; tls["utls"] ??= new JsonObject { ["enabled"] = true, ["fingerprint"] = "chrome" }; }
            o["tls"] = tls;
        }
        string network = G("network", "tcp").ToLowerInvariant();
        if (network != "tcp" && network != "none")
        {
            JsonObject t = new() { ["type"] = network == "h2" ? "http" : network };
            var opts = p[network + "-opts"] as JsonObject;
            if (network == "grpc") t["service_name"] = opts?["grpc-service-name"]?.ToString() ?? "";
            else if (network is "xhttp" or "splithttp")
            {
                if (opts != null && opts.Any(kv => kv.Key is not ("host" or "path" or "mode" or "extra"))) throw new UserError("Неизвестный параметр Clash XHTTP.");
                var extra = opts?["extra"] is JsonObject extraObject ? XhttpYamlExtra(extraObject) : opts?["extra"];
                t = Xhttp.Transport(opts?["host"]?.ToString() ?? "", opts?["path"]?.ToString() ?? "", opts?["mode"]?.ToString() ?? "", extra is JsonObject ? extra.ToJsonString() : extra?.ToString() ?? "");
            }
            else if (network is "ws" or "http" or "h2" or "httpupgrade")
            {
                t["path"] = opts?["path"] is JsonArray paths ? paths.FirstOrDefault()?.ToString() ?? "/" : opts?["path"]?.ToString() ?? "/";
                var hostValue = (opts?["headers"] as JsonObject)?.FirstOrDefault(kv => kv.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)).Value ?? opts?["host"];
                string host = hostValue is JsonArray hostList ? hostList.FirstOrDefault()?.ToString() ?? "" : hostValue?.ToString() ?? "";
                if (opts?["headers"] is JsonObject headers && headers.Any(kv => !kv.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))) throw new UserError("Дополнительные заголовки транспорта пока не поддерживаются.");
                if (opts?["path"] is JsonArray manyPaths && manyPaths.Count > 1 || network is "ws" or "httpupgrade" && hostValue is JsonArray manyHosts && manyHosts.Count > 1) throw new UserError("Несколько путей или заголовков Host пока не поддерживаются.");
                if (network == "ws" && host.Length > 0) t["headers"] = new JsonObject { ["Host"] = host };
                if (network is "http" or "h2" && host.Length > 0) t["host"] = hostValue is JsonArray hosts ? hosts.DeepClone() : new JsonArray(host);
                if (network == "httpupgrade" && host.Length > 0) t["host"] = host;
                if (opts?["method"] is { } method && method.ToString() != "GET") throw new UserError("Нестандартный HTTP-метод пока не поддерживается.");
                if (network == "ws" && opts?["max-early-data"] is { } early)
                {
                    if (!int.TryParse(early.ToString(), out int count) || count < 0 || count > 65535) throw new UserError("Неверный размер WebSocket early data.");
                    if (opts?["early-data-header-name"] is { } header && header.ToString() != "Sec-WebSocket-Protocol") throw new UserError("Нестандартный заголовок early data пока не поддерживается.");
                    if (count > 0) { t["max_early_data"] = count; t["early_data_header_name"] = "Sec-WebSocket-Protocol"; }
                }
            }
            else throw new UserError("Транспорт пока не поддерживается.");
            o["transport"] = t;
        }
        if (kind == "hysteria2" && G("obfs").Length > 0) o["obfs"] = new JsonObject { ["type"] = G("obfs"), ["password"] = G("obfs-password") };
        if (G("packet-encoding").Length > 0) o["packet_encoding"] = G("packet-encoding");
        string name = string.Concat(G("name", kind.ToUpperInvariant()).Where(c => !char.IsControl(c))); if (name.Length > 140) name = name[..140];
        // Reuse the same validation and wire format as ordinary share links.
        var candidate = new Node("", name, kind, o); string link = NodeLinks.Export(candidate); var parsed = Subscriptions.ParseNode(link);
        return parsed with { Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link))).ToLowerInvariant() };
    }
    static JsonObject XhttpYamlExtra(JsonObject source)
    {
        var result = (JsonObject)source.DeepClone();
        foreach (var (key, value) in source)
        {
            if (key == "headers") continue; // A numeric or boolean-looking header remains text.
            if (value is JsonObject nested) result[key] = XhttpYamlExtra(nested);
            else if (key is "xPaddingObfsMode" or "noGRPCHeader" or "noSSEHeader" or "allowInsecure" && bool.TryParse(value?.ToString(), out bool flag)) result[key] = flag;
            else if (key is "port" or "xPaddingBytes" or "uplinkChunkSize" or "scMaxEachPostBytes" or "scMinPostsIntervalMs" or "scStreamUpServerSecs" or "scMaxBufferedPosts" or "serverMaxHeaderBytes" or "maxConcurrency" or "maxConnections" or "cMaxReuseTimes" or "hMaxRequestTimes" or "hMaxReusableSecs" or "hKeepAlivePeriod" && int.TryParse(value?.ToString(), out int number)) result[key] = number;
        }
        return result;
    }
}
