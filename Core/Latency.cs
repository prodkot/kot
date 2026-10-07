using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Kot.Core;

public sealed class PingOptions
{
    public string Mode { get; set; } = "http";
    public string Url { get; set; } = "https://www.gstatic.com/generate_204";
    public int TimeoutMs { get; set; } = 5000;
    public int Attempts { get; set; } = 2;
    public int Parallelism { get; set; } = 3;
    public string Sort { get; set; } = "none";
    public void Validate()
    {
        if (Mode is not ("tcp" or "http")) throw new UserError("Выберите TCP или HTTP.");
        if (TimeoutMs is < 500 or > 30000) throw new UserError("Таймаут: от 500 до 30000 мс.");
        if (Attempts is < 1 or > 5) throw new UserError("Количество попыток: от 1 до 5.");
        if (Parallelism is < 1 or > 8) throw new UserError("Одновременных проверок: от 1 до 8.");
        if (Sort is not ("none" or "ping" or "name")) throw new UserError("Неизвестная сортировка.");
        if (Url.Length > 2048 || Mode == "http" && (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
            throw new UserError("Нужен HTTP(S)-адрес проверки без логина и пароля.");
    }
}
public sealed record PingResult(string Status, int? Ms = null, string Error = "", string Mode = "", int Successes = 0, int Attempts = 0);
public static class Latency
{
    // New connection for every sample: includes DNS, handshake and time to response headers.
    public static async Task<int> Tcp(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
        var watch = Stopwatch.StartNew();
        using var socket = new TcpClient();
        await socket.ConnectAsync(host, port, deadline.Token);
        return Math.Max(1, (int)watch.ElapsedMilliseconds);
    }
    public static async Task<int> Http(string url, int proxyPort, int timeoutMs, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.ConnectionClose = true;
        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        // A captive portal/redirect/403 is not a successful tunnel test.
        if (!response.IsSuccessStatusCode) throw new UserError($"HTTP {(int)response.StatusCode}");
        return Math.Max(1, (int)watch.ElapsedMilliseconds);
    }
    public static async Task<PingResult> Sample(PingOptions options, Func<CancellationToken, Task<int>> measure, CancellationToken ct, Action<Exception>? failed = null)
    {
        List<int> samples = []; string error = "";
        for (int i = 0; i < options.Attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { samples.Add(await measure(ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed?.Invoke(ex);
                error = ex switch { OperationCanceledException => "Таймаут", SocketException => "TCP недоступен", HttpRequestException => "Нет ответа через сервер", UserError => ex.Message, _ => ex.GetType().Name };
            }
        }
        // Median limits outliers; successful/total samples remain visible in the tooltip.
        samples.Sort();
        int? median = samples.Count == 0 ? null : samples.Count % 2 == 1 ? samples[samples.Count / 2] : (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2;
        return new(median.HasValue ? "ok" : "error", median, error, options.Mode, samples.Count, options.Attempts);
    }
    public static Task Batch<T>(IEnumerable<T> items, int parallelism, Func<T, CancellationToken, ValueTask> check, CancellationToken ct) =>
        Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, check);
}
