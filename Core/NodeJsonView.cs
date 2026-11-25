using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.Core;

public sealed record NodeJsonView(string? Text, string Note, string? Revision = null)
{
    static readonly JsonSerializerOptions Pretty = new(JsonSerializerOptions.Default) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public bool TryCopy(string? displayedRevision, Action<string> write)
    {
        if (Text == null || Revision != displayedRevision) return false;
        write(Text);
        return true;
    }

    public override string ToString() => nameof(NodeJsonView);

    public static NodeJsonView Read(Node node, string view, bool full)
    {
        JsonNode? value;
        string note;
        if (view == "parsed" && node.Protocol != "xray")
        {
            if (node.Outbound?["type"] == null) return new(null, "В профиле отсутствует outbound после разбора.");
            value = node.Outbound;
            note = "Сохранённый outbound в формате kot после разбора ссылки или YAML.";
        }
        else
        {
            if (view is not ("parsed" or "xray")) return new(null, "Неизвестное представление.");
            if (node.Protocol == "xray" && node.FullConfig == null)
                return new(null, "В профиле отсутствует исходная конфигурация Xray.");
            // Tunnel.Connect uses Build with no physical-interface override. Local
            // ports and routing rules affect the rest of the config, not its proxy outbounds.
            JsonObject config;
            try { config = view == "parsed" ? node.FullConfig! : XrayRuntime.Build(node, healthPort: 10808); }
            catch (Exception ex) when (ex is UserError or InvalidOperationException or FormatException or JsonException or ArgumentException)
            {
                return new(null, "Не удалось сформировать outbound для Xray. Проверьте сохранённые параметры сервера.");
            }
            var proxies = (config["outbounds"] as JsonArray ?? []).OfType<JsonObject>().Where(XrayConfig.IsProxy).ToArray();
            if (proxies.Length == 0) return new(null, "В конфигурации нет прокси-outbound.");
            value = proxies.Length == 1 ? proxies[0] : new JsonArray(proxies.Select(p => p.DeepClone()).ToArray());
            note = view == "parsed" ? "Прокси-outbound из импортированной конфигурации Xray."
                : "Прокси-outbound для подключения по текущей записи сервера.";
            if (proxies.Length > 1) note += " Показаны все прокси в порядке провайдера.";
        }
        string text = (full ? value : Mask(value))!.ToJsonString(Pretty);
        return new(text, note, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    // Allow only schema names and strictly enumerated values. Provider-defined
    // object keys, HTTP headers, transport paths and arbitrary strings can carry credentials.
    const string Hidden = "[скрыто]";
    static readonly HashSet<string> Fields = new((
        "type protocol network security mode flow encryption method tag server server_port port address " +
        "uuid id user username password pass token publicKey privateKey shortId public_key short_id " +
        "settings streamSettings vnext servers users accounts transport tls reality enabled " +
        "tlsSettings realitySettings wsSettings grpcSettings xhttpSettings httpSettings tcpSettings " +
        "sockopt fingerprint serverName server_name host path serviceName headers extra alpn utls " +
        "obfs auth version alterId alter_id packet_encoding packetEncoding multiplex mux allowInsecure").Split(' '));

    static JsonNode? Mask(JsonNode? item, string key = "")
    {
        if (item is JsonObject obj)
        {
            var masked = new JsonObject();
            int unknown = 0;
            foreach (var pair in obj)
            {
                if (!Fields.Contains(pair.Key)) masked[$"скрытое поле {++unknown}"] = Hidden;
                else if (pair.Key is "headers" or "extra" or "password" or "pass" or "token" or "uuid" or "id" or "user" or "username" or "privateKey" or "publicKey" or "public_key" or "shortId" or "short_id") masked[pair.Key] = Hidden;
                else masked[pair.Key] = Mask(pair.Value, pair.Key);
            }
            return masked;
        }
        if (item is JsonArray array) return new JsonArray(array.Select(v => Mask(v, key)).ToArray());
        if (item == null) return null;
        if (item is JsonValue v)
        {
            if (key is "port" or "server_port" && v.TryGetValue<int>(out int port) && port is > 0 and <= 65535) return port;
            if (key is "enabled" or "allowInsecure" && v.TryGetValue<bool>(out bool flag)) return flag;
            if (v.TryGetValue<string>(out var s) && key switch
            {
                "protocol" or "type" => s is "vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria2" or "hysteria" or "http" or "socks" or "ws" or "grpc" or "xhttp",
                "network" => s is "tcp" or "raw" or "ws" or "grpc" or "xhttp" or "httpupgrade" or "kcp" or "quic",
                "security" => s is "none" or "tls" or "reality" or "auto",
                "encryption" => s == "none",
                "flow" => s == "xtls-rprx-vision",
                "method" => s is "aes-128-gcm" or "aes-256-gcm" or "chacha20-ietf-poly1305" or "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305",
                "fingerprint" => s is "chrome" or "firefox" or "safari" or "ios" or "android" or "edge" or "random" or "randomized",
                "alpn" => s is "http/1.1" or "h2" or "h3",
                "mode" => s is "auto" or "packet-up" or "stream-up" or "stream-one",
                _ => false
            }) return s;
        }
        return Hidden;
    }
}
