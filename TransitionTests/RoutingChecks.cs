using Kot.Core;
using Kot.Windows;
using System.Text.Json;

static class RoutingChecks
{
    public static async Task Run()
    {
        var route = new RoutingProfile { Rules = new() { BlockDomains = ["old.example"] } };
        var profile = new Profile { RoutingProfiles = [route], ActiveRoutingProfile = route.Id };
        var window = new MainWindow(profile);
        string revision = RoutingProfiles.Revision(route);
        route.Rules.BlockDomains = ["old.example", "new-block.example"];
        var draft = new RoutingRules { Name = "renamed only", BlockDomains = ["old.example"] };
        object Save(string token) => new { id = route.Id, baseRevision = token, rules = draft, sourceUrl = "", refreshHours = 24 };
        // The actual backend must reject both a stale and a missing revision.
        foreach (var data in new[] { JsonSerializer.SerializeToElement(Save(revision)),
            JsonSerializer.SerializeToElement(new { id = route.Id, rules = draft, sourceUrl = "", refreshHours = 24 }) })
        {
            bool conflict = false;
            try { await window.Route("routingSave", data); } catch (UserError e) { conflict = e.Message.Contains("Черновик"); }
            GuardChecks.Check(conflict && window.Saved.RoutingProfiles.Single().Rules.BlockDomains.Contains("new-block.example"),
                "real backend rejects stale/missing routing revision without dropping a new block rule");
        }
        revision = RoutingProfiles.Revision(route);
        route.Updated = DateTimeOffset.UtcNow;
        GuardChecks.Check(revision == RoutingProfiles.Revision(route), "identical snapshots and timestamp-only refresh do not conflict");
        Store.FailSave = true;
        try { await window.Route("routingSave", JsonSerializer.SerializeToElement(Save(revision))); throw new Exception("save must fail"); }
        catch (IOException) { }
        finally { Store.FailSave = false; }
        GuardChecks.Check(revision == RoutingProfiles.Revision(window.Saved.RoutingProfiles.Single()), "failed routing persistence restores both content and revision");
        await window.Route("routingSave", JsonSerializer.SerializeToElement(Save(revision)));
        GuardChecks.Check(window.Saved.RoutingProfiles.Single().Rules.Name == "renamed only", "explicitly based routing save succeeds");
    }
}
