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
        var full = old with { FullConfig = JsonNode.Parse("""{"remarks":"old","dns":{"servers":["1.1.1.1"]},"routing":{"rules":[]}}""")!.AsObject() };
        var renamed = full with { Id = new string('b', 64), Name = "new", FullConfig = (JsonObject)full.FullConfig!.DeepClone() };
        renamed.FullConfig!["remarks"] = "new";
        check(SubscriptionNodes.Match([full], [renamed]).Single().Id == old.Id, "full-config display metadata can be renamed");
        renamed.FullConfig["dns"]!["servers"]![0] = "8.8.8.8";
        check(SubscriptionNodes.Match([full], [renamed]).Single().Id == renamed.Id, "full-config DNS changes cannot masquerade as a cosmetic rename");
        renamed.FullConfig["dns"]!["servers"]![0] = "1.1.1.1";
        renamed.FullConfig["routing"]!["rules"]!.AsArray().Add(JsonNode.Parse("""{"outboundTag":"direct","domain":["changed.example"]}"""));
        check(SubscriptionNodes.Match([full], [renamed]).Single().Id == renamed.Id, "full-config routing changes remain significant");
        const string yaml = "proxies:\n  - name: Old\n    type: vless\n    server: example.com\n    port: 443\n    uuid: 11111111-1111-4111-8111-111111111111\n    tls: true\n";
        var clash = Subscriptions.Parse(yaml).Nodes.Single();
        check(SubscriptionNodes.Match([clash], Subscriptions.Parse(yaml.Replace("name: Old", "name: New")).Nodes).Single().Id == clash.Id, "Clash rename preserves its previously saved ID");
    }
}
