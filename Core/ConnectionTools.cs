using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Kot.Core;

public sealed record QualitySummary(int Attempts, int Successes, int? MedianMs, int? MinMs, int? MaxMs, double? JitterMs);
public sealed record DownloadSample(long Bytes, double Seconds, double MegabitsPerSecond);
public static class ConnectionTools
{
    public const int SpeedBytes = 1024 * 1024;
    public const string SpeedUrl = "https://speed.cloudflare.com/__down?bytes=1048576";
    public static Uri Website(string text)
    {
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0 || url.Fragment.Length > 0) throw new UserError("Нужен HTTPS-адрес сайта без логина, пароля и фрагмента.");
        return url;
    }
    public static HttpClient Client(int proxyPort)
    {
        if (proxyPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(proxyPort));
        return new(new HttpClientHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static QualitySummary Summarize(IReadOnlyList<int?> samples)
    {
        if (samples.Count is < 1 or > 20 || samples.Any(n => n <= 0)) throw new ArgumentOutOfRangeException(nameof(samples));
        var good = samples.Where(n => n.HasValue).Select(n => n!.Value).ToArray(); var ordered = good.Order().ToArray();
        int? median = ordered.Length == 0 ? null : ordered.Length % 2 == 1 ? ordered[ordered.Length / 2] : (int)(((long)ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2);
        return new(samples.Count, good.Length, median, ordered.Length == 0 ? null : ordered[0], ordered.Length == 0 ? null : ordered[^1], good.Length < 2 ? null : good.Zip(good.Skip(1), (a, b) => Math.Abs((double)a - b)).Average());
    }
    static async Task<string> JsonBody(HttpClient client, string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(8000);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new UserError($"Сервис проверки вернул HTTP {(int)response.StatusCode}.");
        return await BoundedBody.ReadUtf8(response.Content, 16384, deadline.Token);
    }
    public static async Task<string> PublicIp(HttpClient client, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await JsonBody(client, "https://api64.ipify.org?format=json", ct));
        if (!doc.RootElement.TryGetProperty("ip", out var item) || item.ValueKind != JsonValueKind.String || !IPAddress.TryParse(item.GetString(), out var ip)) throw new UserError("Сервис IP вернул неверный ответ.");
        return ip.ToString();
    }
    public static async Task<string> DnsThroughVpn(HttpClient client, string host, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await JsonBody(client, "https://dns.google/resolve?name=" + Uri.EscapeDataString(host) + "&type=A", ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("Status", out var status) || !status.TryGetInt32(out int code) || code != 0 || !root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array) throw new UserError("DNS через VPN не вернул адрес сайта.");
        var ips = answers.EnumerateArray().Where(a => a.TryGetProperty("type", out var t) && t.TryGetInt32(out int n) && n == 1 && a.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String).Select(a => a.GetProperty("data").GetString()).Where(s => IPAddress.TryParse(s, out _)).Take(4).ToArray();
        return ips.Length > 0 ? string.Join(", ", ips) : throw new UserError("DNS через VPN не вернул IPv4-адрес сайта.");
    }
    public static async Task<DownloadSample> Download(HttpClient client, CancellationToken ct, Action<long>? progress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(15000);
        using var request = new HttpRequestMessage(HttpMethod.Get, SpeedUrl); request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        var watch = Stopwatch.StartNew(); using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new UserError($"Тест загрузки вернул HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is long length && length != SpeedBytes) throw new UserError("Сервис скорости вернул неверный размер данных.");
        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token); byte[] block = new byte[16384]; long bytes = 0;
        while (true)
        {
            int read = await body.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, SpeedBytes + 1L - bytes)), deadline.Token); if (read == 0) break;
            bytes += read; if (bytes > SpeedBytes) throw new UserError("Тест загрузки превысил лимит 1 МиБ."); progress?.Invoke(bytes);
        }
        if (bytes != SpeedBytes) throw new UserError("Тест загрузки оборвался до получения 1 МиБ.");
        double seconds = Math.Max(.001, watch.Elapsed.TotalSeconds); return new(bytes, seconds, bytes * 8d / seconds / 1_000_000);
    }
    public static string Explain(Exception ex) => ex switch
    {
        UserError => ex.Message, OperationCanceledException => "Время ожидания истекло. Проверьте доступность сервера или сайта.",
        SocketException => "Адрес или порт недоступен. Проверьте сеть и адрес сервера.",
        HttpRequestException => "Запрос через сервер не прошёл. Возможны проблемы DNS, TLS или блокировка сайта.",
        JsonException => "Сервис проверки вернул повреждённый ответ.", _ => "Проверка не выполнена. Подробности есть в журнале."
    };
}
