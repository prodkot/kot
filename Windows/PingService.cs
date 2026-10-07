using Kot.Core;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Kot.Windows;

public sealed class PingService
{
    readonly Dictionary<string, PingResult> results = [];
    CancellationTokenSource? attempt;
    Task running = Task.CompletedTask;
    public bool Busy { get; private set; }
    public int Done { get; private set; }
    public int Total { get; private set; }
    public string Error { get; private set; } = "";
    public event Action? Changed;
    // Updated on the UI synchronization context, including after worker awaits.
    public PingResult? Get(string id) => results.GetValueOrDefault(id);
    public void Cancel() => attempt?.Cancel();
    public async Task Stop(bool clear = false)
    {
        Cancel(); await running;
        if (clear) { results.Clear(); Done = Total = 0; Error = ""; Changed?.Invoke(); }
    }
    public void Start(List<Node> nodes, PingOptions options)
    {
        if (Busy) throw new UserError("Проверка уже идёт. Остановите её перед новым запуском.");
        options.Validate();
        if (nodes.Count == 0) throw new UserError("Сначала добавьте серверы.");
        var context = SynchronizationContext.Current ?? throw new InvalidOperationException("Ping requires UI context.");
        attempt = new CancellationTokenSource();
        Busy = true; Done = 0; Total = nodes.Count; Error = "";
        foreach (var n in nodes) results[n.Id] = new("queued", Mode: options.Mode);
        Changed?.Invoke();
        running = Run(nodes.ToArray(), options, attempt, context);
    }
    async Task Run(Node[] nodes, PingOptions options, CancellationTokenSource owner, SynchronizationContext context)
    {
        var ct = owner.Token;
        // Awaiting this callback ensures progress callbacks complete before final cleanup.
        Task Publish(Action update)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(_ => { try { update(); Changed?.Invoke(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } }, null);
            return completion.Task;
        }
        AppLog.Write("ping", $"start count={nodes.Length} mode={options.Mode} timeout={options.TimeoutMs} attempts={options.Attempts} parallel={options.Parallelism}");
        try
        {
            await Latency.Batch(nodes, options.Parallelism, async (node, token) =>
            {
                await Publish(() => results[node.Id] = new("checking", Mode: options.Mode));
                PingResult result;
                try { result = await Measure(node, options, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new("cancelled", Error: "Отменено", Mode: options.Mode); }
                catch (Exception ex) { AppLog.Error("ping " + node.Id, ex); result = new("error", Error: AppLog.Clean(Store.Friendly(ex)), Mode: options.Mode); }
                AppLog.Write("ping", $"node={node.Id} status={result.Status} ms={result.Ms} samples={result.Successes}/{result.Attempts}");
                await Publish(() => { results[node.Id] = result; Done++; });
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { AppLog.Write("ping", "cancelled"); }
        catch (Exception ex) { AppLog.Error("ping batch", ex); Error = AppLog.Clean(Store.Friendly(ex)); }
        finally
        {
            foreach (var node in nodes) if (results[node.Id].Status is "queued" or "checking") results[node.Id] = new("cancelled", Error: "Отменено", Mode: options.Mode);
            Busy = false; if (attempt == owner) attempt = null; owner.Dispose(); Changed?.Invoke();
        }
    }
    async Task<PingResult> Measure(Node node, PingOptions options, CancellationToken ct)
    {
        if (options.Mode == "tcp")
        {
            if (node.Protocol == "hysteria2") return new("unsupported", Error: "UDP-протокол: выберите HTTP", Mode: "tcp");
            string host = node.Outbound["server"]?.GetValue<string>() ?? throw new UserError("Нет адреса сервера.");
            int port = node.Outbound["server_port"]?.GetValue<int>() ?? throw new UserError("Нет порта сервера.");
            return await Latency.Sample(options, token => Latency.Tcp(host, port, options.TimeoutMs, token), ct, ex => AppLog.Error("ping tcp " + node.Id, ex));
        }
        NativeCore? core = null;
        string path = Path.Combine(Store.Folder, "ping-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var config = Configuration.Build(node, port, false, [], tun: false);
            config["log"]!["level"] = "info";
            await File.WriteAllTextAsync(path, config.ToJsonString(), ct);
            ct.ThrowIfCancellationRequested();
            core = new NativeCore(Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"), path, text => AppLog.Write("ping-core " + node.Id, text));
            // Listener readiness is outside the latency sample; never count a successful local TCP check as HTTP success.
            var deadline = DateTime.UtcNow.AddSeconds(10); bool ready = false;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (core.HasExited) throw new UserError("Ядро проверки завершилось: " + CoreDiagnostics.Summary(AppLog.Clean(await core.ReadRecentOutput(true))));
                try { await Latency.Tcp("127.0.0.1", port, 250, ct); ready = true; break; }
                catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
                await Task.Delay(100, ct);
            }
            if (!ready) throw new UserError("Ядро проверки не открыло локальный порт.");
            return await Latency.Sample(options, token => Latency.Http(options.Url, port, options.TimeoutMs, token), ct, ex => AppLog.Error("ping http " + node.Id, ex));
        }
        finally
        {
            if (core != null) { try { await core.Stop(); } catch (Exception ex) { AppLog.Error("ping cleanup", ex); core.Dispose(); } }
            try { File.Delete(path); } catch (Exception ex) { AppLog.Error("ping config cleanup", ex); }
        }
    }
}
