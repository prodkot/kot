using Kot.Core;
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
        await using var probe = await ProbeCore.Start(node, ct);
        return await Latency.Sample(options, token => Latency.Http(options.Url, probe.Port, options.TimeoutMs, token), ct, ex => AppLog.Error("ping http " + node.Id, ex));
    }
}
