using Kot.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Kot.Tests;

static class SubscriptionNodesTests
{
    const string Link = "vless://11111111-1111-4111-8111-111111111111@example.com:443?security=tls&type=ws&path=%2Fws#old";
    public static void Run(Action<bool, string> check)
    {
        var old = Subscriptions.ParseNode(Link);
        Node Match(string link) => SubscriptionNodes.Match([old], [Subscriptions.ParseNode(link)]).Single();
        check(Match(Link.Replace("#old", "#new")).Id == old.Id, "cosmetic rename maps to the persisted legacy ID");
        check(Match(Link.Replace("security=tls&type=ws&path=%2Fws", "path=%2Fws&type=ws&security=tls")).Id == old.Id, "query parameter order does not change matched identity");
        foreach (string changed in new[] { Link.Replace("111111111111@", "222222222222@"), Link.Replace("example.com", "other.example.com"),
            Link.Replace(":443", ":8443"), Link.Replace("security=tls", "security=none"), Link.Replace("path=%2Fws", "path=%2Fnew"),
            Link.Replace("type=ws&path=%2Fws", "type=tcp") })
            check(Match(changed).Id != old.Id, "real authentication/server/TLS/transport changes keep a distinct identity");
        var duplicate = Subscriptions.ParseNode(Link.Replace("#old", "#duplicate"));
        var incoming = new[] { Subscriptions.ParseNode(Link.Replace("#old", "#renamed-a")), Subscriptions.ParseNode(Link.Replace("#old", "#renamed-b")) };
        check(SubscriptionNodes.Match([old, duplicate], incoming).Select(n => n.Id).SequenceEqual(incoming.Select(n => n.Id)),
            "ambiguous duplicate renames retain distinct incoming IDs without arbitrary merging");
        check(SubscriptionNodes.Match([old, duplicate], [old, incoming[0]])[1].Id == duplicate.Id, "exact duplicate match leaves one safe remaining migration");
        var active = new SavedSubscription { Local = true, Nodes = [old], Selected = old.Id, Favorites = [old.Id] };
        var inactive = new SavedSubscription { Local = true, Nodes = [old], Selected = old.Id, Favorites = [old.Id] };
        var profile = new Profile { Subscriptions = [active, inactive], ActiveSubscription = active.Id };
        profile.Normalize();
        var restored = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!; restored.Validate();
        foreach (var saved in restored.Subscriptions)
        {
            saved.Nodes = SubscriptionNodes.Match(saved.Nodes, [incoming[0]]);
            check(saved.Selected == old.Id && saved.Favorites.Single() == saved.Nodes.Single().Id, "legacy selected/favorite IDs migrate in both active and inactive saved subscriptions");
        }
        string Full(string name, string resolver, int port) => new JsonObject
        {
            ["remarks"] = name, ["inbounds"] = new JsonArray(), ["dns"] = new JsonObject { ["servers"] = new JsonArray(resolver) },
            ["outbounds"] = new JsonArray(new JsonObject { ["protocol"] = "socks", ["tag"] = "proxy",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = "example.com", ["port"] = port }) } })
        }.ToJsonString();
        var full = Subscriptions.Parse(Full("old", "1.1.1.1", 1080)).Nodes.Single();
        var renamed = Subscriptions.Parse(Full("new", "8.8.8.8", 1080)).Nodes.Single();
        check(SubscriptionNodes.Match([full], [renamed]).Single().Id == full.Id, "Xray title and DNS policy can change while the proxy remains the same");
        var different = Subscriptions.Parse(Full("old", "1.1.1.1", 1081)).Nodes.Single();
        check(SubscriptionNodes.Match([full], [different]).Single().Id == different.Id, "Xray endpoint change is distinct even with identical title");
        var france = Subscriptions.ParseNode(Link.Replace("example.com", "fr.example").Replace("#old", "#Same"));
        var germany = Subscriptions.ParseNode(Link.Replace("#old", "#Same"));
        var extra = Subscriptions.ParseNode(Link.Replace("example.com", "extra.example"));
        var feed = new SavedSubscription { Nodes = [france, germany], Selected = germany.Id, Favorites = [germany.Id] };
        string? Refresh(params Node[] nodes) => feed.Refresh(new SubscriptionResult(nodes.ToList(), []), DateTimeOffset.UtcNow);
        check(Refresh(france, germany) == null && feed.Selected == germany.Id, "unchanged subscription with duplicate labels preserves selection");
        check(Refresh(germany, france) == null && feed.Selected == germany.Id, "reorder keeps Germany");
        check(Refresh(extra, france, germany) == null && feed.Selected == germany.Id, "new first server does not replace Germany");
        check(Refresh(france, incoming[0]) == null && feed.Selected == germany.Id && feed.Favorites.Single() == germany.Id, "removing another server and renaming Germany keep selection and favorites");
        check(Refresh(extra, france)?.Contains(france.Name) == true && feed.Selected == france.Id && feed.Favorites.Count == 0, "removed selection falls back to first previous survivor and notifies once");
        check(Refresh(extra, france) == null, "second refresh has no replacement notice");
        feed.Selected = "auto";
        check(Refresh(germany) == null && feed.Selected == "auto", "refresh preserves automatic selection across a complete catalog replacement");
        const string yaml = "proxies:\n  - name: Old\n    type: vless\n    server: example.com\n    port: 443\n    uuid: 11111111-1111-4111-8111-111111111111\n    tls: true\n";
        var clash = Subscriptions.Parse(yaml).Nodes.Single();
        check(SubscriptionNodes.Match([clash], Subscriptions.Parse(yaml.Replace("name: Old", "name: New")).Nodes).Single().Id == clash.Id, "Clash rename preserves its previously saved ID");
    }
}
