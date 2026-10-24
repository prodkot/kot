using System.Net;
using System.Text.Json.Nodes;
using Kot.Core;
namespace Kot.Tests;

static class DownloadDeadlineTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var result = Subscriptions.Parse("vless://11111111-1111-4111-8111-111111111111@example.com:443?security=none");
        using var stalled = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) }));
        using var client = new HttpClient(stalled) { Timeout = TimeSpan.FromMilliseconds(100) };
        // The outer guard makes the regression fail promptly even if production
        // accidentally stops passing a deadline to the response-body stream.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        bool cancelled = false;
        try { await ProviderRouting.Resolve(result, "https://rules.example/routes", null, client, guard.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && !guard.IsCancellationRequested, "provider routing deadline includes a stalled response body");

        var node = result.Nodes[0] with { FullConfig = new JsonObject { ["routing"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["domain"] = new JsonArray("geosite:ru") }) } } };
        string root = Path.Combine(Path.GetTempPath(), "kot-geo-deadline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new XrayAssets(root);
            using var geoGuard = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            cancelled = false;
            try { await cache.Prepare([node], client, geoGuard.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !geoGuard.IsCancellationRequested && !cache.Available(node)
                && !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(), "geo download deadline cancels a stalled body and removes partial files");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new Handler(async ct =>
            {
                entered.TrySetResult(); await release.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([10, 2, 1, 0]) };
            });
            using var good = new HttpClient(handler);
            using var concurrentGuard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = cache.Prepare([node], good, concurrentGuard.Token);
            await entered.Task.WaitAsync(concurrentGuard.Token);
            var second = cache.Prepare([node], good, concurrentGuard.Token);
            using var waitingCancellation = new CancellationTokenSource();
            var waiting = cache.Prepare([node], good, waitingCancellation.Token);
            waitingCancellation.Cancel(); cancelled = false;
            try { await waiting; } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "a queued geo cache operation can be cancelled independently");
            release.TrySetResult();
            await Task.WhenAll(first, second);
            check(handler.Count == 1 && cache.Available(node), "simultaneous geo cache requests share one completed download without a rename race");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Count); return response(ct); }
    }
    sealed class StalledBody : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
