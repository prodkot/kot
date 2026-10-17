using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Kot.Core;

public static class NodeLinks
{
    public static string Export(Node node)
    {
        NodeSecurity.Validate(node);
        if (node.FullConfig != null) throw new UserError("Полный конфиг с маршрутизацией нельзя передать ссылкой или QR без потери правил. Используйте исходную подписку или резервную копию.");
        var o = node.Outbound;
        string G(string key) => o[key]?.ToString() ?? "";
        string host = G("server"); if (host.Contains(':')) host = "[" + host.Trim('[', ']') + "]";
        string endpoint = host + ":" + G("server_port");
        var tls = o["tls"] as JsonObject; var transport = o["transport"] as JsonObject;
        string T(string key) => tls?[key]?.ToString() ?? "";
        string P(string key) => transport?[key]?.ToString() ?? "";
        string list(JsonNode? n) => n is JsonArray a ? string.Join(',', a.Select(x => x?.ToString() ?? "")) : n?.ToString() ?? "";
        string type = P("type"); if (type.Length == 0) type = "tcp";
        string transportHost = type == "ws" ? transport?["headers"]?["Host"]?.ToString() ?? "" : list(transport?["host"]);
        string path = P("path");
        if (transport?["max_early_data"] is JsonValue early && early.TryGetValue<int>(out int count) && count > 0)
            path += (path.Contains('?') ? "&" : "?") + "ed=" + count;
        string fp = tls?["utls"]?["fingerprint"]?.ToString() ?? "";
        string security = tls?["reality"]?["enabled"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true ? "reality" : tls?["enabled"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true ? "tls" : "none";
        if (node.Protocol == "vmess")
        {
            if (security == "reality") throw new UserError("Экспорт VMess с Reality не поддерживается.");
            JsonObject value = new() { ["v"] = "2", ["ps"] = node.Name, ["add"] = G("server"), ["port"] = G("server_port"), ["id"] = G("uuid"), ["aid"] = "0", ["scy"] = G("security"), ["net"] = type, ["type"] = "none", ["host"] = transportHost, ["path"] = type == "grpc" ? P("service_name") : path, ["tls"] = security == "tls" ? "tls" : "", ["sni"] = T("server_name"), ["alpn"] = list(tls?["alpn"]), ["fp"] = fp };
            if (G("packet_encoding").Length > 0) value["packetEncoding"] = G("packet_encoding");
            if (Xhttp.Uses(node)) { value["mode"] = P("mode"); if (transport?["extra"] != null) value["extra"] = transport["extra"]!.DeepClone(); }
            return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToJsonString()));
        }
        if (node.Protocol == "shadowsocks")
            return "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(G("method") + ":" + G("password"))).TrimEnd('=') + "@" + endpoint + "#" + Uri.EscapeDataString(node.Name);
        if (node.Protocol is not ("vless" or "trojan" or "hysteria2")) throw new UserError("Этот протокол нельзя экспортировать в QR.");
        Dictionary<string, string> q = new() { ["security"] = security };
        void Put(string key, string value) { if (value.Length > 0) q[key] = value; }
        Put("sni", T("server_name")); Put("fp", fp); Put("alpn", list(tls?["alpn"]));
        Put("pbk", tls?["reality"]?["public_key"]?.ToString() ?? ""); Put("sid", tls?["reality"]?["short_id"]?.ToString() ?? "");
        if (node.Protocol != "hysteria2") { Put("type", type); Put("host", transportHost); Put("path", path); Put("serviceName", P("service_name")); Put("flow", G("flow")); Put("packetEncoding", G("packet_encoding")); }
        else { Put("obfs", o["obfs"]?["type"]?.ToString() ?? ""); Put("obfs-password", o["obfs"]?["password"]?.ToString() ?? ""); }
        if (Xhttp.Uses(node)) { Put("type", "xhttp"); Put("mode", P("mode")); Put("extra", transport?["extra"]?.ToJsonString() ?? ""); }
        return node.Protocol + "://" + Uri.EscapeDataString(G(node.Protocol == "vless" ? "uuid" : "password")) + "@" + endpoint + "?" + string.Join('&', q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))) + "#" + Uri.EscapeDataString(node.Name);
    }
}
