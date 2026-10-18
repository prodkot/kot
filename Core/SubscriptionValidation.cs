namespace Kot.Core;

public static class SubscriptionValidation
{
    // Only a definite per-node rejection is skippable. Cancellation, missing
    // executables, timeouts and other infrastructure errors abort the update.
    public static async Task<SubscriptionResult> Filter(SubscriptionResult result,
        Func<Node, CancellationToken, Task<string?>> rejection, CancellationToken ct)
    {
        List<Node> accepted = [];
        List<string> warnings = [.. result.Warnings];
        for (int i = 0; i < result.Nodes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? reason = await rejection(result.Nodes[i], ct);
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
