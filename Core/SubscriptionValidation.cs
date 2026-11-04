namespace Kot.Core;

public static class SubscriptionValidation
{
    public static async Task<SubscriptionResult> Prepare(SubscriptionResult result,
        Func<IEnumerable<Node>, CancellationToken, Task> assets,
        Func<SubscriptionResult, CancellationToken, Task<SubscriptionResult>> validate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await assets(result.Nodes, ct);
        ct.ThrowIfCancellationRequested();
        result = await validate(result, ct);
        ct.ThrowIfCancellationRequested();
        return result;
    }
    // Only a definite per-node rejection is skippable. Cancellation, missing
    // executables, timeouts and other infrastructure errors abort the update.
    public static async Task<SubscriptionResult> Filter(SubscriptionResult result,
        Func<Node, CancellationToken, Task<string?>> rejection, CancellationToken ct, int concurrency = 1)
    {
        if (concurrency is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(concurrency));
        ct.ThrowIfCancellationRequested();
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reasons = new string?[result.Nodes.Count];
        int next = -1;
        async Task Work()
        {
            try
            {
                while (true)
                {
                    workers.Token.ThrowIfCancellationRequested();
                    int index = Interlocked.Increment(ref next);
                    if (index >= result.Nodes.Count) return;
                    reasons[index] = await rejection(result.Nodes[index], workers.Token);
                }
            }
            catch { workers.Cancel(); throw; }
        }
        // Only a bounded number of native processes exist at once. Join every
        // worker on failure/cancellation before the caller may save or retry.
        await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, result.Nodes.Count)).Select(_ => Work()));
        ct.ThrowIfCancellationRequested();
        List<Node> accepted = [];
        List<string> warnings = [.. result.Warnings];
        for (int i = 0; i < result.Nodes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? reason = reasons[i];
            ct.ThrowIfCancellationRequested();
            if (reason == null) accepted.Add(result.Nodes[i]);
            else warnings.Add($"Сервер {i + 1} пропущен: {reason}");
        }
        ct.ThrowIfCancellationRequested();
        if (accepted.Count == 0)
            throw new UserError("Xray не принял ни одного сервера. Сохранённая подписка не изменена. " + warnings.LastOrDefault());
        return result with { Nodes = accepted, Warnings = warnings };
    }
}
