using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kot.Core;

public sealed class RoutingRules
{
    public const int MaxBytes = 128 * 1024;
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Мой профиль";
    public string DefaultAction { get; set; } = "proxy";
    public string[] DirectDomains { get; set; } = [];
    public string[] ProxyDomains { get; set; } = [];
    public string[] BlockDomains { get; set; } = [];
    public string[] DirectIps { get; set; } = [];
    public string[] ProxyIps { get; set; } = [];
    public string[] BlockIps { get; set; } = [];
    public string[] DirectApps { get; set; } = [];
    public string[] ProxyApps { get; set; } = [];
    public string[] BlockApps { get; set; } = [];
    public string[] DirectCountries { get; set; } = [];
    public string[] ProxyCountries { get; set; } = [];
    public string[] BlockCountries { get; set; } = [];
    public string DirectDns { get; set; } = "cloudflare";
    public string RemoteDns { get; set; } = "cloudflare";

    public void Validate()
    {
        if (Version != 1) throw new UserError("Неподдерживаемая версия профиля маршрутизации.");
        if (Name == null || Name.Trim().Length is < 1 or > 60 || Name.Any(char.IsControl)) throw new UserError("Название профиля: от 1 до 60 символов.");
        Name = Name.Trim();
        if (DefaultAction is not ("proxy" or "direct")) throw new UserError("Выберите маршрут по умолчанию: VPN или напрямую.");
        DirectDomains = Configuration.Domains(Join(DirectDomains)); ProxyDomains = Configuration.Domains(Join(ProxyDomains)); BlockDomains = Configuration.Domains(Join(BlockDomains));
        DirectIps = IpRanges(Join(DirectIps)); ProxyIps = IpRanges(Join(ProxyIps)); BlockIps = IpRanges(Join(BlockIps));
        DirectApps = Applications(Join(DirectApps)); ProxyApps = Applications(Join(ProxyApps)); BlockApps = Applications(Join(BlockApps));
        DirectCountries = GeoIp.Countries(Join(DirectCountries)); ProxyCountries = GeoIp.Countries(Join(ProxyCountries)); BlockCountries = GeoIp.Countries(Join(BlockCountries));
        DnsPreset(DirectDns); DnsPreset(RemoteDns);
        if (Encoding.UTF8.GetByteCount(RoutingProfiles.Serialize(this)) > MaxBytes) throw new UserError("Профиль маршрутизации слишком большой.");
    }
    public string[] Countries() => DirectCountries.Concat(ProxyCountries).Concat(BlockCountries).Distinct().Order().ToArray();
    static string Join(string[]? items)
    {
        if (items == null || items.Any(i => i == null) || items.Length > 100) throw new UserError("В каждом списке должно быть не больше 100 записей.");
        // An array element is exactly one entry, not another delimited list.
        if (items.Any(i => i.IndexOfAny(['\r', '\n', ',', ';']) >= 0)) throw new UserError("Каждый элемент списка должен содержать одну запись.");
        return string.Join('\n', items);
    }
    public static string[] Entries(string input) => input.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string[] Applications(string input)
    {
        var items = Entries(input);
        if (items.Length > 100) throw new UserError("Максимум 100 приложений в одном списке.");
        foreach (var item in items)
            if (item.Length > 260 || item.Length <= 4 || !item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || item.Any(char.IsControl) || item.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new UserError("Введите имена приложений, например chrome.exe, без пути и масок.");
        return items;
    }
    public static string[] IpRanges(string input)
    {
        var items = Entries(input); List<string> result = [];
        if (items.Length > 100) throw new UserError("Максимум 100 IP-адресов или подсетей в одном списке.");
        foreach (var item in items)
        {
            var parts = item.Split('/');
            if (parts.Length > 2 || item.Length > 128 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var ip)) throw new UserError("Введите IPv4, IPv6 или подсеть, например 203.0.113.0/24.");
            if (ip.AddressFamily == AddressFamily.InterNetwork && (parts[0].Split('.').Length != 4 || parts[0].Split('.').Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9') || p.Length > 1 && p[0] == '0')))
                throw new UserError("IPv4 должен состоять из четырёх десятичных чисел без ведущих нулей.");
            int width = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128, bits = width;
            if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out bits) || bits < 0 || bits > width)) throw new UserError("Неверная длина маски IP-подсети.");
            var bytes = ip.GetAddressBytes();
            for (int i = 0; i < bytes.Length; i++) bytes[i] &= (byte)(bits >= (i + 1) * 8 ? 255 : bits <= i * 8 ? 0 : 255 << (8 - (bits - i * 8)));
            result.Add(new IPAddress(bytes) + "/" + bits);
        }
        return result.Distinct().ToArray();
    }
    public static (string Ip, string Host) DnsPreset(string preset) => preset switch
    {
        "cloudflare" => ("1.1.1.1", "cloudflare-dns.com"),
        "google" => ("8.8.8.8", "dns.google"),
        "quad9" => ("9.9.9.9", "dns.quad9.net"),
        "adguard" => ("94.140.14.14", "dns.adguard-dns.com"),
        _ => throw new UserError("Неизвестный DNS-сервис.")
    };
}

public sealed class RoutingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RoutingRules Rules { get; set; } = new();
    public string SourceUrl { get; set; } = "";
    public int RefreshHours { get; set; } = 24;
    public DateTimeOffset? Updated { get; set; }
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Rules == null || SourceUrl == null || RefreshHours is < 0 or > 168) throw new UserError("Некорректный профиль маршрутизации.");
        Rules.Validate();
        if (SourceUrl.Length > 0) RoutingProfiles.Source(SourceUrl);
    }
    public bool Due(DateTimeOffset now) => SourceUrl.Length > 0 && RefreshHours > 0 && now - (Updated ?? DateTimeOffset.MinValue) >= TimeSpan.FromHours(RefreshHours);
}

public static class RoutingProfiles
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8
    };
    public static string Serialize(RoutingRules rules) => JsonSerializer.Serialize(rules, Json);
    public static RoutingRules Parse(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > RoutingRules.MaxBytes) throw new UserError("Профиль маршрутизации слишком большой.");
        try
        {
            content = content.Trim().TrimStart('\uFEFF');
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new UserError("Нужен JSON-профиль маршрутизации kot.");
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in document.RootElement.EnumerateObject()) if (!fields.Add(field.Name)) throw new UserError("Повторяющиеся поля в профиле маршрутизации.");
            if (!fields.Contains("version")) throw new UserError("В JSON-профиле маршрутизации нужно поле version: 1.");
            var rules = JsonSerializer.Deserialize<RoutingRules>(content, Json) ?? throw new UserError("Пустой профиль маршрутизации.");
            rules.Validate(); return rules;
        }
        catch (JsonException) { throw new UserError("Неверный JSON-профиль маршрутизации kot. Проверьте формат и поля."); }
    }
    public static Uri Source(string address)
    {
        if (address.Length > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new UserError("Нужна HTTPS-ссылка на JSON-профиль без логина, пароля и фрагмента.");
        return uri;
    }
    public static async Task<RoutingRules> Download(string address, CancellationToken ct, HttpMessageHandler? testTransport = null, int proxyPort = 0)
    {
        var uri = Source(address);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(35)); ct = deadline.Token;
        using var handler = testTransport ?? NetworkRequests.Handler(proxyPort);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientIdentity.SubscriptionUserAgent);
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location ?? throw new UserError("Источник профиля перенаправляет без адреса.");
                uri = Source((next.IsAbsoluteUri ? next : new Uri(uri, next)).AbsoluteUri); continue;
            }
            if (!response.IsSuccessStatusCode) throw new UserError($"Источник профиля вернул HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > RoutingRules.MaxBytes) throw new UserError("Профиль маршрутизации слишком большой.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using MemoryStream buffer = new(); byte[] block = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(block, ct); if (count == 0) break;
                if (buffer.Length + count > RoutingRules.MaxBytes) throw new UserError("Профиль маршрутизации слишком большой.");
                buffer.Write(block, 0, count);
            }
            try { return Parse(new UTF8Encoding(false, true).GetString(buffer.ToArray())); }
            catch (DecoderFallbackException) { throw new UserError("JSON-профиль должен быть в UTF-8."); }
        }
        throw new UserError("Слишком много перенаправлений источника профиля.");
    }
}
