using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kot.Core;

// Subscription routing is part of the provider's client configuration, not the remote server's rules.
public static class ProviderRouting
{
    public static bool Metadata(string line, ref string? routing, ref string? autorouting)
    {
        string value = line.TrimStart('#').Trim();
        var header = Regex.Match(value, @"^(routing|autorouting)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
        if (header.Success)
        {
            if (header.Groups[1].Value.Equals("autorouting", StringComparison.OrdinalIgnoreCase)) autorouting ??= header.Groups[2].Value;
            else routing ??= header.Groups[2].Value;
            return true;
        }
        if (value.Contains("://autorouting/", StringComparison.OrdinalIgnoreCase)) { autorouting ??= value; return true; }
        if (value.Contains("://routing/", StringComparison.OrdinalIgnoreCase)) { routing ??= value; return true; }
        return false;
    }
    public static SubscriptionResult Inline(SubscriptionResult result)
    {
        string? source = result.Autorouting ?? result.Routing;
        if (source == null) return result;
        string payload = Payload(source);
        return payload.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? result : Apply(result, payload);
    }
    static string Payload(string value)
    {
        value = value.Trim();
        if (value.Length > RoutingRules.MaxBytes * 2) throw new UserError("Маршрутизация подписки слишком большая.");
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.EndsWith("://routing/off", StringComparison.OrdinalIgnoreCase)) return "off";
        var link = Regex.Match(value, @"^(?:[a-zA-Z][a-zA-Z0-9+.-]*)?://(?:auto)?routing/(?:onadd|add)/(.+)$", RegexOptions.IgnoreCase);
        if (link.Success) value = link.Groups[1].Value;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) throw new UserError("Маршрутизация подписки требует HTTPS.");
        if (value.Contains("://") && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new UserError("Неверная ссылка маршрутизации подписки.");
        return value;
    }
    public static async Task<SubscriptionResult> Resolve(SubscriptionResult result, string? routing, string? autorouting, HttpClient client, CancellationToken ct)
    {
        // Provider priority: autorouting header, autorouting body, routing header, routing body.
        string? source = autorouting ?? result.Autorouting ?? routing ?? result.Routing;
        if (source == null) return result;
        string value = Payload(source);
        for (int depth = 0; value.StartsWith("https://", StringComparison.OrdinalIgnoreCase); depth++)
        {
            if (depth >= 3) throw new UserError("Слишком много вложенных источников маршрутизации.");
            value = Payload(await Download(value, client, ct));
        }
        return Apply(result, value);
    }
    public static async Task<SubscriptionResult> ResolveLocal(SubscriptionResult result, CancellationToken ct, int proxyPort = 0)
    {
        using var handler = NetworkRequests.Handler(proxyPort);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        return await Resolve(result, null, null, client, ct);
    }
    static async Task<string> Download(string address, HttpClient client, CancellationToken ct)
    {
        var uri = RoutingProfiles.Source(address);
        // GitHub's file page is an HTML document. Fetch the same file from its raw endpoint.
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && parts[2] == "blob") uri = new Uri("https://raw.githubusercontent.com/" + string.Join('/', parts.Take(2).Concat(parts.Skip(3))));
        }
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            // Device headers live on the subscription request only. Never copy them to this request.
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new UserError("Источник маршрутизации перенаправляет без адреса.");
                uri = RoutingProfiles.Source((location.IsAbsoluteUri ? location : new Uri(uri, location)).AbsoluteUri); continue;
            }
            if (!response.IsSuccessStatusCode) throw new UserError($"Источник маршрутизации вернул HTTP {(int)response.StatusCode}.");
            return await BoundedBody.ReadUtf8(response.Content, RoutingRules.MaxBytes, ct);
        }
        throw new UserError("Слишком много перенаправлений маршрутизации.");
    }
    static SubscriptionResult Apply(SubscriptionResult result, string value)
    {
        if (value == "off") return result with { Nodes = result.Nodes.Select(n => n.Protocol == "xray" ? n : n with { FullConfig = null, GeoIpUrl = "", GeoSiteUrl = "" }).ToList() };
        try
        {
            if (!value.TrimStart().StartsWith('{')) value = Subscriptions.Decode64(value);
            if (Encoding.UTF8.GetByteCount(value) > RoutingRules.MaxBytes) throw new UserError("Маршрутизация подписки слишком большая.");
            var source = JsonNode.Parse(value, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject ?? throw new JsonException();
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var doc = JsonDocument.Parse(value))
                foreach (var property in doc.RootElement.EnumerateObject()) if (!fields.Add(property.Name)) throw new UserError("Повторяющиеся поля маршрутизации подписки.");
            var supported = new HashSet<string>(new[] { "Name", "GlobalProxy", "FakeDNS", "DomainStrategy", "DnsHosts", "Geoipurl", "Geositeurl",
                "DirectSites", "DirectIp", "ProxySites", "ProxyIp", "BlockSites", "BlockIp",
                "RemoteDNSType", "RemoteDNSIP", "RemoteDns", "RemoteDNSDomain", "DomesticDNSType", "DomesticDNSIP", "DomesticDns", "DomesticDNSDomain" }, StringComparer.OrdinalIgnoreCase);
            if (fields.Any(field => !supported.Contains(field))) throw new UserError("Профиль маршрутизации подписки содержит неподдерживаемые параметры. Правила не применены; прежняя подписка сохранена.");
            string G(string key, string fallback = "") => source.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? fallback;
            if (!bool.TryParse(G("FakeDNS", "false"), out _)) throw new UserError("Неверный FakeDNS в профиле подписки.");
            if (G("FakeDNS", "false").Equals("true", StringComparison.OrdinalIgnoreCase)) throw new UserError("FakeDNS в профиле подписки пока не поддерживается. Провайдер должен выдать профиль с FakeDNS=false; прежняя подписка сохранена.");
            JsonArray List(string key)
            {
                var item = source.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
                if (item == null) return [];
                if (item is not JsonArray list || list.Count > 10000 || list.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _))) throw new UserError("Неверный список " + key + " в маршрутизации подписки.");
                return (JsonArray)list.DeepClone();
            }
            JsonArray rules = [];
            foreach (var (prefix, tag) in new[] { ("Block", "block"), ("Proxy", "proxy"), ("Direct", "direct") })
            {
                var domains = List(prefix + "Sites");
                for (int i = 0; i < domains.Count; i++) domains[i] = Domain(domains[i]!.GetValue<string>());
                if (domains.Count > 0) rules.Add(new JsonObject { ["type"] = "field", ["domain"] = domains, ["outboundTag"] = tag });
                var ips = List(prefix + "Ip");
                if (ips.Count > 0) rules.Add(new JsonObject { ["type"] = "field", ["ip"] = ips, ["outboundTag"] = tag });
            }
            string global = G("GlobalProxy", "true").ToLowerInvariant();
            if (global is not ("true" or "false")) throw new UserError("Неверный GlobalProxy в маршрутизации подписки.");
            string strategy = G("DomainStrategy", "IPIfNonMatch");
            if (strategy is not ("AsIs" or "IPIfNonMatch" or "IPOnDemand")) throw new UserError("Неподдерживаемый DomainStrategy подписки.");
            JsonObject Dns(string prefix, bool remote)
            {
                string type = G(prefix + "DNSType", remote ? "DoH" : "DoU");
                string ip = G(prefix + "DNSIP", G(prefix + "Dns", remote ? "1.1.1.1" : "8.8.8.8"));
                if (!IPAddress.TryParse(ip, out _)) throw new UserError("В DNS подписки нужен IP-адрес.");
                string address = type switch
                {
                    "DoH" => G(prefix + "DNSDomain", remote ? "https://cloudflare-dns.com/dns-query" : "https://dns.google/dns-query"),
                    "DoU" => ip, _ => throw new UserError("Неподдерживаемый тип DNS подписки: " + type)
                };
                if (type == "DoH") RoutingProfiles.Source(address);
                return new JsonObject { ["address"] = address, ["skipFallback"] = !remote, ["tag"] = remote ? "provider-remote-dns" : "provider-direct-dns" };
            }
            var remoteDns = Dns("Remote", true); var directDns = Dns("Domestic", false);
            var directDomains = List("DirectSites");
            for (int i = 0; i < directDomains.Count; i++) directDomains[i] = Domain(directDomains[i]!.GetValue<string>());
            var proxyDomains = List("ProxySites");
            for (int i = 0; i < proxyDomains.Count; i++) proxyDomains[i] = Domain(proxyDomains[i]!.GetValue<string>());
            if (directDomains.Count > 0) directDns["domains"] = directDomains;
            if (proxyDomains.Count > 0) remoteDns["domains"] = proxyDomains;
            if (global == "false") { directDns["skipFallback"] = false; remoteDns["skipFallback"] = true; }
            rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("provider-direct-dns"), ["outboundTag"] = "direct" });
            rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("provider-remote-dns"), ["outboundTag"] = "proxy" });
            var hosts = source.FirstOrDefault(p => p.Key.Equals("DnsHosts", StringComparison.OrdinalIgnoreCase)).Value?.DeepClone() ?? new JsonObject();
            if (hosts is not JsonObject hostMap) throw new UserError("Неверные DnsHosts в маршрутизации.");
            // Bootstrap DoH hostnames without depending on that same DoH connection.
            foreach (var (prefix, dns, fallback) in new[] { ("Remote", remoteDns, "1.1.1.1"), ("Domestic", directDns, "8.8.8.8") })
                if (Uri.TryCreate(dns["address"]?.ToString(), UriKind.Absolute, out var doh) && doh.Scheme == "https" && !hostMap.ContainsKey(doh.IdnHost)
                    && IPAddress.TryParse(G(prefix + "DNSIP", G(prefix + "Dns", fallback)), out var address)) hostMap[doh.IdnHost] = address.ToString();
            string geoIp = G("Geoipurl"), geoSite = G("Geositeurl");
            if (geoIp.Length > 0) RoutingProfiles.Source(geoIp);
            if (geoSite.Length > 0) RoutingProfiles.Source(geoSite);
            var nodes = result.Nodes.Select(node =>
            {
                if (node.Protocol == "xray") return node with { GeoIpUrl = geoIp, GeoSiteUrl = geoSite }; // Keep the full config's routing and DNS.
                var proxy = XrayConfig.Outbound(node); proxy["tag"] = "proxy";
                var direct = new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" };
                JsonArray outbounds = global == "true" ? new(proxy, direct) : new(direct, proxy);
                outbounds.Add(new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" });
                JsonArray servers = global == "true" ? (directDomains.Count > 0 ? new JsonArray(directDns.DeepClone(), remoteDns.DeepClone()) : new JsonArray(remoteDns.DeepClone()))
                    : (proxyDomains.Count > 0 ? new JsonArray(remoteDns.DeepClone(), directDns.DeepClone()) : new JsonArray(directDns.DeepClone()));
                var config = new JsonObject { ["inbounds"] = new JsonArray(), ["outbounds"] = outbounds, ["routing"] = new JsonObject { ["domainStrategy"] = strategy, ["rules"] = rules.DeepClone() }, ["dns"] = new JsonObject { ["servers"] = servers, ["hosts"] = hosts.DeepClone(), ["queryStrategy"] = "UseIP", ["disableFallbackIfMatch"] = true } };
                XrayConfig.Validate(config);
                return node with { FullConfig = config, GeoIpUrl = geoIp, GeoSiteUrl = geoSite };
            }).ToList();
            return result with { Nodes = nodes };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        { throw new UserError("Не удалось прочитать маршрутизацию подписки. " + ex.Message); }
    }
    static string Domain(string value)
    {
        if (value.StartsWith("geosite:") || value.StartsWith("regexp:") || value.StartsWith("keyword:") || value.StartsWith("ext:")) return value;
        string prefix = value.StartsWith("full:") ? "full:" : "domain:";
        if (value.StartsWith("full:") || value.StartsWith("domain:")) value = value[(value.IndexOf(':') + 1)..];
        return prefix + Configuration.Domains(value).Single();
    }
}
