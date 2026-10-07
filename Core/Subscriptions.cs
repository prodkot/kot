using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.Core;
public sealed record Node(string Id, string Name, string Protocol, JsonObject Outbound);
public sealed record SubscriptionResult(List<Node> Nodes, List<string> Warnings);
public sealed class UserError(string message) : Exception(message);

public static class Subscriptions
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public static string Decode64(string input)
    {
        string s = string.Concat(input.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        s = s.TrimEnd('='); s += new string('=', (4 - s.Length % 4) % 4);
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s));
    }
    public static SubscriptionResult Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new UserError("Подписка слишком большая.");
        text = text.Trim().TrimStart('\uFEFF');
        if (!text.Contains("://"))
            try { text = Decode64(text).Trim().TrimStart('\uFEFF'); } catch { throw new UserError("Не удалось прочитать подписку. Нужен список ссылок на серверы, обычный или Base64."); }
        if (text.StartsWith('<') || text.StartsWith('{') || text.StartsWith("proxies:") || text.StartsWith("proxy-providers:"))
            throw new UserError("Сервер вернул HTML, YAML или JSON. Эта версия читает подписки со ссылками на серверы, в том числе Base64.");
        List<Node> nodes = []; List<string> warnings = []; HashSet<string> ids = [];
        int lineNo = 0;
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            lineNo++; if (lineNo > 10000) throw new UserError("В подписке слишком много строк."); string raw = line.Trim(); if (raw.Length == 0 || raw.StartsWith('#')) continue;
            try
            {
                Node node = ParseNode(raw);
                if (ids.Add(node.Id)) { if (nodes.Count < 2000) nodes.Add(node); else warnings.Add("Лимит: первые 2000 серверов."); }
            }
            catch (UserError ex) { warnings.Add($"Строка {lineNo}: {ex.Message}"); }
            catch { warnings.Add($"Строка {lineNo}: неверный формат сервера."); }
        }
        if (nodes.Count == 0) throw new UserError(warnings.FirstOrDefault() ?? "В подписке нет серверов.");
        return new(nodes, warnings);
    }
    public static Node ParseNode(string raw)
    {
        if (raw.Length > 32768) throw new UserError("Ссылка на сервер слишком длинная.");
        if (raw.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return Vmess(raw);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) throw new UserError("Неверная ссылка на сервер.");
        string scheme = u.Scheme.ToLowerInvariant();
        if (scheme == "ss") return Shadowsocks(raw);
        if (scheme is not ("vless" or "trojan" or "hysteria2" or "hy2")) throw new UserError($"Протокол {scheme[..Math.Min(scheme.Length, 40)]} пока не поддерживается.");
        if (string.IsNullOrWhiteSpace(u.Host)) throw new UserError("Не указан адрес сервера.");
        var q = Query(u.Query); string Get(string key, string fallback = "") => q.GetValueOrDefault(key, fallback);
        NodeSecurity.RejectInsecureFlag(Get("allowInsecure"), Get("insecure"));
        int port = u.Port > 0 ? u.Port : 443; string credential = Uri.UnescapeDataString(u.UserInfo);
        JsonObject o = new() { ["type"] = scheme == "hy2" ? "hysteria2" : scheme, ["tag"] = "proxy", ["server"] = u.Host.Trim('[', ']'), ["server_port"] = port };
        string kind = Get("type", "tcp").ToLowerInvariant();
        if (scheme == "vless")
        {
            if (!Guid.TryParse(credential, out _)) throw new UserError("Неверный VLESS UUID.");
            if (Get("encryption", "none") != "none") throw new UserError("Это VLESS-шифрование пока не поддерживается.");
            o["uuid"] = credential;
            string flow = Get("flow");
            if (flow.Length > 0) { if (flow != "xtls-rprx-vision" || kind != "tcp") throw new UserError("Неподдерживаемый VLESS flow."); o["flow"] = flow; }
        }
        else { if (credential.Length == 0) throw new UserError("Не указан пароль сервера."); o["password"] = credential; }
        string security = Get("security", scheme == "vless" ? "none" : "tls").ToLowerInvariant();
        if (security is not ("none" or "tls" or "reality")) throw new UserError("Неподдерживаемая защита транспорта.");
        if ((scheme == "trojan" || scheme is "hy2" or "hysteria2") && security != "tls") throw new UserError("Для этого протокола требуется TLS.");
        if (security != "none")
        {
            string sni = Get("sni", Get("peer", u.Host.Trim('[', ']')));
            JsonObject tls = new() { ["enabled"] = true, ["server_name"] = sni };
            string alpn = Get("alpn"); if (alpn.Length > 0) tls["alpn"] = JsonSerializer.SerializeToNode(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries));
            string fp = Get("fp", security == "reality" ? "chrome" : "");
            if (fp.Length > 0)
            {
                if (!new[] { "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized" }.Contains(fp)) throw new UserError("Неподдерживаемый TLS fingerprint.");
                tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
            }
            if (security == "reality")
            {
                if (Get("pbk").Length == 0) throw new UserError("В Reality не указан публичный ключ.");
                tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = Get("pbk"), ["short_id"] = Get("sid") };
                if (Get("spx").Length > 0) { /* Xray spiderX is not part of the wire handshake. */ }
            }
            o["tls"] = tls;
        }
        if (scheme is "hy2" or "hysteria2")
        {
            string obfs = Get("obfs");
            if (obfs.Length > 0) { if (obfs != "salamander" || Get("obfs-password").Length == 0) throw new UserError("Неподдерживаемый Hysteria2 obfs."); o["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = Get("obfs-password") }; }
            if (Get("mport").Length > 0) throw new UserError("Hysteria2 port hopping пока не поддерживается.");
        }
        else
        {
            var transport = Transport(kind, Get("host"), Get("path"), Get("serviceName"));
            if (transport != null) o["transport"] = transport;
            if (kind == "tcp" && Get("headerType", "none") != "none") throw new UserError("TCP HTTP header пока не поддерживается.");
            if (Get("packetEncoding").Length > 0) o["packet_encoding"] = Get("packetEncoding");
        }
        return Make(raw, Uri.UnescapeDataString(u.Fragment.TrimStart('#')), o);
    }
    static JsonObject? Transport(string type, string host, string path, string service)
    {
        JsonObject t = new() { ["type"] = type };
        switch (type)
        {
            case "tcp": case "none": case "raw": return null;
            case "ws":
                t["path"] = path.Length > 0 ? path : "/";
                if (host.Length > 0) t["headers"] = new JsonObject { ["Host"] = host };
                var pathQuery = path.Contains('?') ? Query(path[(path.IndexOf('?') + 1)..]) : [];
                if (pathQuery.TryGetValue("ed", out string? ed))
                {
                    if (!int.TryParse(ed, out int max) || max < 0 || max > 65536) throw new UserError("Неверный WS early data.");
                    t["max_early_data"] = max; t["early_data_header_name"] = "Sec-WebSocket-Protocol";
                    t["path"] = path[..path.IndexOf('?')] + (pathQuery.Count > 1 ? "?" + string.Join('&', pathQuery.Where(kv => kv.Key != "ed").Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))) : "");
                }
                break;
            case "grpc": t["service_name"] = service; break;
            case "h2": case "http":
                t["type"] = "http"; t["path"] = path.Length > 0 ? path : "/";
                if (host.Length > 0) t["host"] = JsonSerializer.SerializeToNode(host.Split(',')); break;
            case "httpupgrade": t["path"] = path.Length > 0 ? path : "/"; if (host.Length > 0) t["host"] = host; break;
            default: throw new UserError($"Транспорт {type} пока не поддерживается.");
        }
        return t;
    }
    static Node Vmess(string raw)
    {
        using var d = JsonDocument.Parse(Decode64(raw[8..])); var root = d.RootElement;
        string G(string name, string fallback = "") => root.TryGetProperty(name, out var x) ? x.ToString() : fallback;
        NodeSecurity.RejectInsecureFlag(G("allowInsecure"), G("insecure"));
        if (!Guid.TryParse(G("id"), out _) || !int.TryParse(G("port"), out int port) || port < 1 || port > 65535 || G("add").Length == 0) throw new UserError("Неверный VMess сервер.");
        if (G("aid", "0") != "0") throw new UserError("Устаревший VMess alterId не поддерживается.");
        JsonObject o = new() { ["type"] = "vmess", ["tag"] = "proxy", ["server"] = G("add"), ["server_port"] = port, ["uuid"] = G("id"), ["security"] = G("scy", "auto") };
        string net = G("net", "tcp"); if (net == "tcp" && G("type", "none") != "none") throw new UserError("VMess TCP header пока не поддерживается.");
        var t = Transport(net, G("host"), G("path"), G("path")); if (t != null) o["transport"] = t;
        string tls = G("tls"); if (tls.Length > 0 && tls != "none")
        {
            if (tls != "tls") throw new UserError("Неподдерживаемая защита VMess.");
            JsonObject opt = new() { ["enabled"] = true, ["server_name"] = G("sni", G("host", G("add"))) };
            if (G("alpn").Length > 0) opt["alpn"] = JsonSerializer.SerializeToNode(G("alpn").Split(','));
            if (G("fp").Length > 0) opt["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = G("fp") };
            o["tls"] = opt;
        }
        return Make(raw, G("ps"), o);
    }
    static Node Shadowsocks(string raw)
    {
        string rest = raw[5..]; string name = "";
        int hash = rest.IndexOf('#'); if (hash >= 0) { name = Uri.UnescapeDataString(rest[(hash + 1)..]); rest = rest[..hash]; }
        int query = rest.IndexOf('?'); if (query >= 0) { if (Query(rest[(query + 1)..]).GetValueOrDefault("plugin", "").Length > 0) throw new UserError("Shadowsocks plugin пока не поддерживается."); rest = rest[..query]; }
        rest = rest.TrimEnd('/');
        if (!rest.Contains('@')) rest = Decode64(rest);
        int at = rest.LastIndexOf('@'); if (at <= 0) throw new UserError("Неверная Shadowsocks ссылка.");
        string cred = Uri.UnescapeDataString(rest[..at]); if (!cred.Contains(':')) cred = Decode64(cred);
        int colon = cred.IndexOf(':'); if (colon <= 0) throw new UserError("Неверные Shadowsocks данные.");
        if (!Uri.TryCreate("ss://" + rest[(at + 1)..], UriKind.Absolute, out var u) || u.Port < 1) throw new UserError("Неверный Shadowsocks адрес.");
        JsonObject o = new() { ["type"] = "shadowsocks", ["tag"] = "proxy", ["server"] = u.Host.Trim('[', ']'), ["server_port"] = u.Port, ["method"] = cred[..colon], ["password"] = cred[(colon + 1)..] };
        return Make(raw, name, o);
    }
    static Node Make(string raw, string name, JsonObject o)
    {
        name = string.Concat(name.Where(c => !char.IsControl(c))); if (name.Length > 140) name = name[..140];
        string protocol = o["type"]!.GetValue<string>();
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), name.Length > 0 ? name : protocol.ToUpperInvariant() + " · " + o["server"], protocol, o);
    }
    static Dictionary<string, string> Query(string query)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        { var kv = pair.Split('=', 2); result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : ""; }
        return result;
    }
    public static async Task<SubscriptionResult> Download(string address, CancellationToken ct, HttpMessageHandler? testTransport = null, SubscriptionDevice? device = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new UserError("Нужна HTTPS-ссылка на подписку.");
        device?.Validate();
        Uri origin = uri;
        bool deviceOrigin = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(35)); ct = deadline.Token;
        using var handler = testTransport ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseProxy = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientIdentity.SubscriptionUserAgent);
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (device != null && deviceOrigin)
            {
                request.Headers.Add("x-hwid", device.Hwid);
                request.Headers.Add("x-device-os", device.Os);
                request.Headers.Add("x-ver-os", device.OsVersion);
                request.Headers.Add("x-device-model", device.Model);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            bool Flag(string name) => response.Headers.TryGetValues(name, out var values) && values.Any(v => v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            if (Flag("x-hwid-max-devices-reached") || Flag("x-hwid-limit"))
                throw new UserError("Достигнут лимит устройств подписки. Освободите место в кабинете VPN-сервиса или обратитесь в его поддержку.");
            if (Flag("x-hwid-not-supported"))
                throw new UserError(device == null ? "Для подписки нужен HWID. Включите «Передавать HWID» в Настройки → Дополнительно и повторите загрузку."
                    : !deviceOrigin ? "Подписка перенаправлена на другой сервер, которому HWID не передаётся. Запросите у VPN-сервиса прямую ссылку на подписку."
                    : "Сервер подписки не принял HWID клиента. Обратитесь в поддержку VPN-сервиса.");
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? next = response.Headers.Location; if (next == null) throw new UserError("Подписка перенаправляет без адреса.");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                if (uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new UserError("Подписка перенаправляет на адрес без HTTPS.");
                // A redirect does not authorize exposing device identity to a different origin.
                deviceOrigin = deviceOrigin && uri.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == origin.Port;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new UserError($"Подписка вернула HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxBytes) throw new UserError("Подписка слишком большая.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using MemoryStream buffer = new(); byte[] block = new byte[8192];
            while (true) { int count = await stream.ReadAsync(block, ct); if (count == 0) break; if (buffer.Length + count > MaxBytes) throw new UserError("Подписка слишком большая."); buffer.Write(block, 0, count); }
            return Parse(new UTF8Encoding(false, true).GetString(buffer.ToArray()));
        }
        throw new UserError("Слишком много перенаправлений подписки.");
    }
}
