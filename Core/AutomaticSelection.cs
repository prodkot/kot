using System.Collections.Concurrent;
namespace Kot.Core;

public static class AutomaticSelection
{
    public const string NoCandidates = "Нет обычных серверов для Авто. Полные конфигурации выбираются вручную; проверьте также избранное.";
    public static IReadOnlyList<Node> Candidates(Profile profile) => profile.Nodes
        .Where(n => n.FullConfig == null && n.Protocol != "xray"
            && (profile.Favorites.Count == 0 || profile.Favorites.Contains(n.Id)))
        .DistinctBy(n => n.Id).ToArray();

    public static async Task<string> Select(Profile profile, string current,
        Func<Node, CancellationToken, Task<int>> probe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var nodes = Candidates(profile);
        if (nodes.Count == 0) throw new UserError(NoCandidates);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var results = new ConcurrentDictionary<string, int>();
        try
        {
            await Parallel.ForEachAsync(nodes.OrderBy(n => n.Id == current ? 0 : 1),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (node, token) =>
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(6));
                    try
                    {
                        int latency = await probe(node, deadline.Token);
                        if (latency >= 0 && !deadline.IsCancellationRequested) results[node.Id] = latency;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* Only successful HTTP probes qualify. */ }
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        if (results.IsEmpty) throw new UserError("Ни один сервер Авто не прошёл проверку через VPN. Выберите сервер вручную или повторите попытку.");
        var best = results.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First();
        return results.TryGetValue(current, out int value) && (long)value <= (long)best.Value + profile.Automation.AutoToleranceMs ? current : best.Key;
    }
}
