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
    }
}
