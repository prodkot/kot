using Kot.Core;
using Kot.Windows;

static class IdentityChecks
{
    public static async Task Run()
    {
        Fixture.Reset();
        const string link = "vless://11111111-1111-4111-8111-111111111111@example.com:443?security=none#old";
        var old = Subscriptions.ParseNode(link);
        var profile = new Profile { Address = "https://example.com/sub", Nodes = [old], Selected = old.Id, Favorites = [old.Id] };
        profile.CaptureActive();
        var inactive = new SavedSubscription { Address = "https://other.example.com/sub", Nodes = [old], Selected = old.Id, Favorites = [old.Id] };
        profile.Subscriptions.Add(inactive);
        var window = new MainWindow(profile);
        await window.Begin().WaitAsync(TimeSpan.FromSeconds(5));
        int session = window.Connection.Session;
        await window.Refresh(profile.Address, Subscriptions.Parse(link.Replace("#old", "#renamed")), CancellationToken.None);
        GuardChecks.Check(window.Saved.Selected == old.Id && window.Saved.Favorites.SingleOrDefault() == old.Id && window.Saved.Nodes.Single().Name == "renamed",
            "real active subscription refresh migrates legacy selection and favorites across rename");
        GuardChecks.Check(window.Connection.State == "connected" && window.Connection.Session == session, "cosmetic active rename does not restart a working connection");
        window.RefreshInactive(inactive.Id, Subscriptions.Parse(link.Replace("#old", "#inactive-renamed")));
        var refreshed = window.Saved.Subscriptions.Single(s => s.Id == inactive.Id);
        GuardChecks.Check(refreshed.Nodes.Single().Name == "inactive-renamed" && refreshed.Selected == old.Id && refreshed.Favorites.SingleOrDefault() == old.Id,
            "real inactive refresh preserves legacy selection and favorites");
        await window.Refresh(profile.Address, Subscriptions.Parse(link.Replace("example.com:443", "other.example.com:443")), CancellationToken.None);
        GuardChecks.Check(window.Connection.State == "idle" && window.Saved.Selected != old.Id && !window.Saved.Favorites.Contains(old.Id),
            "real endpoint change restarts the session and does not migrate unrelated preferences");
    }
}
