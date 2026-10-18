using Kot.Core;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
namespace Kot.Tests;

public static class AutomaticSelectionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var one = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@127.0.0.1:19001?security=none");
        var two = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@127.0.0.1:19002?security=none");
        var full = one with { Id = "provider-one", FullConfig = new JsonObject() };
        var identical = two with { Id = "provider-two", FullConfig = new JsonObject() };
        var profile = new Profile { Selected = "auto", Nodes = [full, one, identical, two] };
        check(Xhttp.ConnectionNodes(profile).Select(n => n.Id).SequenceEqual(new[] { one.Id, two.Id }), "Auto excludes every provider config, including identical rules");
        profile.Favorites = [full.Id, two.Id];
        check(Xhttp.ConnectionNodes(profile).Single().Id == two.Id, "Auto filters full configs before honoring favorites");
        profile.Favorites = [full.Id, identical.Id];
        check(Xhttp.ConnectionNodes(profile).Count == 0, "provider-only favorites never fall back to all nodes");
        bool rejected = false;
        try { await AutomaticSelection.Select(profile, one.Id, (_, _) => throw new Exception("must not probe"), default); }
        catch (UserError e) { rejected = e.Message == AutomaticSelection.NoCandidates; }
        check(rejected, "empty Auto selection explains manual provider selection");
        profile.Selected = full.Id;
        check(Xhttp.ConnectionNodes(profile).Single().Id == full.Id, "manual selection keeps provider configs and ignores favorites");
        profile.Selected = "auto"; profile.Favorites.Clear();
        var measured = new ConcurrentBag<string>();
        string chosen = await AutomaticSelection.Select(profile, "", (n, _) => { measured.Add(n.Id); return Task.FromResult(n.Id == one.Id ? 80 : 20); }, default);
        check(chosen == two.Id && measured.Count == 2 && !measured.Contains(full.Id), "Auto chooses only a successfully probed ordinary node");
        profile.Automation.AutoToleranceMs = 100;
        chosen = await AutomaticSelection.Select(profile, one.Id, (n, _) => Task.FromResult(n.Id == one.Id ? 80 : 20), default);
        check(chosen == one.Id, "Auto retains a working current node within tolerance");
        chosen = await AutomaticSelection.Select(profile, one.Id, (n, _) => n.Id == one.Id ? Task.FromException<int>(new IOException()) : Task.FromResult(20), default);
        check(chosen == two.Id, "Auto never retains a failed current node");
        foreach (string current in new[] { "", one.Id })
        {
            rejected = false;
            try { await AutomaticSelection.Select(profile, current, (_, _) => Task.FromException<int>(new IOException()), default); }
            catch (UserError) { rejected = true; }
            check(rejected, "all failed Auto probes report failure without first/current-node fallback");
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { await AutomaticSelection.Select(profile, "", (_, _) => Task.FromResult(1), cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "cancelled Auto never selects or starts another node");
    }
}
