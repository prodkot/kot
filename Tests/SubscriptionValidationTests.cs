using Kot.Core;
namespace Kot.Tests;

public static class SubscriptionValidationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var good = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@localhost:443?security=none");
        var bad = good with { Id = "bad" };
        var usage = new SubscriptionUsage(1, 2, 3, null, DateTimeOffset.UtcNow);
        var input = new SubscriptionResult([bad, good, bad], ["parse warning"], usage, "provider routing", "autorouting");
        var result = await SubscriptionValidation.Filter(input, (n, ct) => Task.FromResult<string?>(n == bad ? "rejected by native core" : null), CancellationToken.None);
        check(result.Nodes.SequenceEqual([good]) && result.Warnings.Count == 3, "native rejection skips only incompatible nodes and retains parse warnings");
        check(result.Usage == usage && result.Routing == input.Routing && result.Autorouting == input.Autorouting, "native filtering preserves provider metadata and routing");
        check(input.Nodes.Count == 3 && input.Warnings.Count == 1 && ReferenceEquals(result.Nodes[0], good), "validation never mutates the downloaded subscription or accepted config");
        bool failed = false;
        try { await SubscriptionValidation.Filter(input, (n, ct) => Task.FromResult<string?>("all invalid"), CancellationToken.None); }
        catch (UserError ex) { failed = ex.Message.Contains("all invalid"); }
        check(failed, "all-invalid update fails before any stored profile can be replaced");
        int called = 0; failed = false;
        try { await SubscriptionValidation.Filter(input, (n, ct) => { called++; throw new IOException("worker unavailable"); }, CancellationToken.None); }
        catch (IOException) { failed = true; }
        check(failed && called == 1, "infrastructure failure aborts instead of silently deleting nodes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); called = 0; failed = false;
        try { await SubscriptionValidation.Filter(input, (n, ct) => { called++; return Task.FromResult<string?>(null); }, cancelled.Token); }
        catch (OperationCanceledException) { failed = true; }
        check(failed && called == 0, "cancelled subscription validation starts no worker");
        using var midway = new CancellationTokenSource(); failed = false;
        try { await SubscriptionValidation.Filter(input with { Nodes = [good] }, (n, ct) => { midway.Cancel(); return Task.FromResult<string?>(null); }, midway.Token); }
        catch (OperationCanceledException) { failed = true; }
        check(failed, "cancellation during the last worker cannot commit a partial update");
        await Parallel(check, good);
        foreach (bool duringAssets in new[] { true, false })
        {
            using var cancelStage = new CancellationTokenSource();
            int validated = 0; bool returned = false; failed = false;
            try
            {
                await SubscriptionValidation.Prepare(input, (nodes, token) =>
                {
                    check(token == cancelStage.Token, "import assets receive the attempt cancellation token");
                    if (duringAssets) cancelStage.Cancel();
                    return Task.CompletedTask;
                }, (prepared, token) =>
                {
                    validated++;
                    check(token == cancelStage.Token, "native import validation receives the attempt cancellation token");
                    cancelStage.Cancel(); return Task.FromResult(prepared);
                }, cancelStage.Token);
                returned = true;
            }
            catch (OperationCanceledException) { failed = true; }
            check(failed && !returned && validated == (duringAssets ? 0 : 1),
                "cancellation during " + (duringAssets ? "asset preparation" : "last native validation") + " cannot return an import for saving");
        }
    }
    static async Task Parallel(Action<bool, string> check, Node node)
    {
        var nodes = Enumerable.Range(0, 40).Select(i => node with { Id = i.ToString() }).ToList();
        var input = new SubscriptionResult(nodes, ["original"]);
        int active = 0, peak = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await SubscriptionValidation.Filter(input, async (n, ct) =>
        {
            int count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); if (previous >= count) break; }
            while (Interlocked.CompareExchange(ref peak, count, previous) != previous);
            if (count == 4) started.TrySetResult();
            try
            {
                await started.Task.WaitAsync(ct);
                await Task.Delay(2 * (40 - int.Parse(n.Id)), ct);
                return int.Parse(n.Id) % 3 == 0 ? "invalid " + n.Id : null;
            }
            finally { Interlocked.Decrement(ref active); }
        }, guard.Token, concurrency: 4);
        check(peak == 4 && active == 0, "subscription validation runs at most four workers and joins them before returning");
        check(result.Nodes.Select(n => n.Id).SequenceEqual(nodes.Where(n => int.Parse(n.Id) % 3 != 0).Select(n => n.Id))
            && result.Warnings.Skip(1).Select(w => w.Split(' ').Last()).SequenceEqual(nodes.Where(n => int.Parse(n.Id) % 3 == 0).Select(n => n.Id)),
            "out of order validation completion preserves node and warning order");

        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0; active = 0; bool failed = false;
        try
        {
            await SubscriptionValidation.Filter(input, async (n, ct) =>
            {
                Interlocked.Increment(ref active);
                if (Interlocked.Increment(ref entered) == 4) allStarted.TrySetResult();
                try
                {
                    await allStarted.Task.WaitAsync(ct);
                    if (n.Id == "0") throw new IOException("native launch failed");
                    await Task.Delay(Timeout.Infinite, ct); return null;
                }
                finally { Interlocked.Decrement(ref active); }
            }, guard.Token, concurrency: 4);
        }
        catch (IOException) { failed = true; }
        check(failed && active == 0 && entered == 4, "infrastructure failure cancels and joins sibling validators without losing the original error");
        using var cancel = new CancellationTokenSource(); entered = 0; active = 0; failed = false;
        try
        {
            await SubscriptionValidation.Filter(input, async (n, ct) =>
            {
                Interlocked.Increment(ref active);
                try
                {
                    if (Interlocked.Increment(ref entered) == 4) cancel.Cancel();
                    await Task.Delay(Timeout.Infinite, ct); return null;
                }
                finally { Interlocked.Decrement(ref active); }
            }, cancel.Token, concurrency: 4);
        }
        catch (OperationCanceledException) { failed = true; }
        check(failed && active == 0 && entered == 4, "cancelling parallel validation joins every active worker before aborting the import");
    }
}
