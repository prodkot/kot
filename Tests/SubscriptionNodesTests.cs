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
        foreach (string changed in new[] { Link.Replace("example.com", "other.example.com"),
            Link.Replace(":443", ":8443"), Link.Replace("security=tls", "security=none"), Link.Replace("path=%2Fws", "path=%2Fnew"),
            Link.Replace("type=ws&path=%2Fws", "type=tcp") })
            check(Match(changed).Id != old.Id, "real server/TLS/transport changes keep a distinct identity");
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
        check(Refresh(extra, france)?.Contains(SavedSubscription.ConfirmConnection) == true && feed.Selected == germany.Id && feed.RequiresConnectionConfirmation && feed.Favorites.Count == 0, "removed manual selection is retained and requires confirmation");
        check(Refresh(extra, france) == null, "second refresh has no replacement notice");
        feed.Selected = "auto";
        check(Refresh(germany) == null && feed.Selected == "auto", "refresh preserves automatic selection across a complete catalog replacement");
        // Some feeds reserialize UUIDs between requests. Check the outbound that a
        // reconnect would use, including after a disk round trip.
        const string uuid = "abcdefab-cdef-4abc-8def-abcdefabcdef";
        string germanLink = Link.Replace("11111111-1111-4111-8111-111111111111", uuid).Replace("example.com", "de.example");
        var live = Subscriptions.ParseNode(germanLink);
        string fullVless = new JsonObject { ["inbounds"] = new JsonArray(),
            ["outbounds"] = new JsonArray(XrayConfig.Outbound(live)) }.ToJsonString();
        var fullLive = Subscriptions.Parse(fullVless).Nodes.Single();
        var fullUpdated = Subscriptions.Parse(fullVless.Replace(uuid, uuid.ToUpperInvariant())).Nodes;
        check(SubscriptionNodes.Match([fullLive], fullUpdated).Single().Id == fullLive.Id,
            "full Xray VLESS configs retain the server when the provider reserializes its UUID");
        var password = Subscriptions.ParseNode("trojan://" + uuid + "@de.example:443#DE");
        var changedPassword = Subscriptions.ParseNode("trojan://" + uuid.ToUpperInvariant() + "@de.example:443#DE");
        var matchedPassword = SubscriptionNodes.Match([password], [changedPassword]).Single();
        check(matchedPassword.Id == password.Id && matchedPassword.Outbound["password"]!.ToString() == uuid.ToUpperInvariant(),
            "rotated TLS client password preserves row identity and case-sensitive new login");
        var regional = new SavedSubscription { Local = true, Nodes = [france, live], Selected = live.Id };
        var client = new Profile { Subscriptions = [regional] };
        client.Activate(regional.Id);
        regional.Refresh(new([france, Subscriptions.ParseNode(germanLink.Replace(uuid, uuid.ToUpperInvariant()))], []), DateTimeOffset.UtcNow);
        client.Activate(regional.Id);
        var loaded = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(client))!;
        loaded.Validate();
        var next = Xhttp.ConnectionNodes(loaded).Single();
        string Endpoint(Node node) => XrayRuntime.Build(node, 12345)["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]!.ToString();
        check(Endpoint(next) == Endpoint(live), $"UUID case change: reconnect outbound was {Endpoint(live)}, now {Endpoint(next)}");
        foreach (string selection in new[] { "auto", france.Id, live.Id })
        {
            var saved = new SavedSubscription { Local = true, Nodes = [france, live], Selected = selection };
            var kept = saved.Refresh(new([live, france], []), DateTimeOffset.UtcNow, live);
            check(kept == null && saved.Selected == selection && !saved.RequiresConnectionConfirmation,
                "unchanged live endpoint keeps its independent UI selection");
            var connection = new Profile { Subscriptions = [saved] };
            connection.Activate(saved.Id);
            if (selection != "auto") check(Endpoint(connection.ManualConnectionNode!) == Endpoint(live),
                "manual reconnect follows the live server even when another row is selected");
            var removed = saved.Refresh(new([france], []), DateTimeOffset.UtcNow, live);
            check(removed?.Contains(SavedSubscription.ConfirmConnection) == true && saved.RequiresConnectionConfirmation,
                "removing the live endpoint requires consent even with Auto or another highlighted row");
            connection.Activate(saved.Id);
            connection = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(connection))!;
            connection.Validate();
            check(connection.Selected == selection && connection.CurrentSubscription!.Selected == selection,
                "loss of the active server preserves the user choice across reload");
            check(Xhttp.ConnectionNodes(connection).Count == 0, "no replacement outbound prepared for an unconfirmed session");
            saved = JsonSerializer.Deserialize<SavedSubscription>(JsonSerializer.Serialize(saved))!;
            check(saved.Refresh(new([france], []), DateTimeOffset.UtcNow, live) == null && saved.RequiresConnectionConfirmation,
                "repeated update and reload preserve the confirmation requirement without a second notice");
            saved.Refresh(new([france, live], []), DateTimeOffset.UtcNow);
            connection.Subscriptions = [saved];
            connection.Activate(saved.Id);
            check(!saved.RequiresConnectionConfirmation && (selection == "auto" || connection.ManualConnectionNode?.Id == live.Id),
                "a returning live endpoint is eligible again without redirecting manual reconnect to the fallback");
        }
        SavedSubscription WithoutEndpoint(Node endpoint, string selection)
        {
            var state = new SavedSubscription { Local = true, Selected = selection, Nodes = [france, endpoint] };
            state.Refresh(new([france], []), DateTimeOffset.UtcNow, endpoint);
            return JsonSerializer.Deserialize<SavedSubscription>(JsonSerializer.Serialize(state))!;
        }
        var berlin = Subscriptions.ParseNode(germanLink.Replace("#old", "#Berlin").Replace(uuid, uuid.ToUpperInvariant()));
        foreach (var (original, returned) in new[] { (live, berlin), (full, renamed) })
        {
            foreach (string choice in new[] { france.Id, original.Id, "auto" })
            {
                var state = WithoutEndpoint(original, choice);
                state.Refresh(new([france], []), DateTimeOffset.UtcNow);
                state.Refresh(new([returned, france], []), DateTimeOffset.UtcNow);
                var restarted = new Profile { Subscriptions = [state] };
                restarted.Activate(state.Id);
                check(!state.RequiresConnectionConfirmation && restarted.Selected == choice &&
                    state.Nodes[0].Id == original.Id && state.Nodes[0].Name == returned.Name &&
                    (choice == "auto" || Xhttp.ConnectionNodes(restarted).SingleOrDefault()?.Id == original.Id),
                    "return after reload: rename or provider metadata change restores only the same endpoint, preserving the UI choice");
            }
        }
        var legacy = new SavedSubscription { Local = true, Nodes = [france], Selected = france.Id, ResumeNodeId = live.Id };
        legacy.Refresh(new([france, berlin], []), DateTimeOffset.UtcNow, live);
        check(!legacy.RequiresConnectionConfirmation && legacy.Nodes[1].Id == live.Id,
            "Dev 5 missing ID can recover from the still-running session");
        foreach (var alternatives in new[] {
            new[] { berlin, Subscriptions.ParseNode(germanLink.Replace("#old", "#Second")) },
            new[] { Subscriptions.ParseNode(germanLink.Replace(":443", ":8443")) },
            new[] { Subscriptions.ParseNode(germanLink.Replace("de.example", "elsewhere.example")) } })
        {
            var state = WithoutEndpoint(live, france.Id);
            state.Refresh(new([france, .. alternatives], []), DateTimeOffset.UtcNow);
            check(state.RequiresConnectionConfirmation && state.Selected == france.Id && state.Nodes.All(n => n.Id != live.Id),
                "after a gap, duplicates or changed address/port cannot authorize a different connection");
        }
        var duplicateRow = Subscriptions.ParseNode(germanLink.Replace("#old", "#Second"));
        var alias = new SavedSubscription { Nodes = [live, duplicateRow], Selected = duplicateRow.Id };
        alias.Refresh(new([duplicateRow], []), DateTimeOffset.UtcNow, live);
        alias.Refresh(new([berlin], []), DateTimeOffset.UtcNow);
        check(alias.RequiresConnectionConfirmation && alias.Nodes.Single().Id == duplicateRow.Id,
            "a known row keeps its ID when it shares a missing endpoint's connection parameters");
        var unknown = new SavedSubscription { Nodes = [france], Selected = france.Id, ResumeNodeId = live.Id };
        unknown.Refresh(new([france, berlin], []), DateTimeOffset.UtcNow);
        check(unknown.RequiresConnectionConfirmation, "Dev 5 missing ID without an endpoint never guesses on rename");
        unknown.Refresh(new([france, live], []), DateTimeOffset.UtcNow);
        check(!unknown.RequiresConnectionConfirmation, "Dev 5 missing ID still accepts an exact return");
        var rotating = new SavedSubscription { Local = true, Nodes = [france, live], Selected = live.Id };
        var rotatingProfile = new Profile { Subscriptions = [rotating] };
        foreach (string replacement in new[] { "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222" })
        {
            var sameName = Subscriptions.ParseNode(germanLink.Replace(uuid, replacement));
            rotating.Refresh(new([france, sameName], []), DateTimeOffset.UtcNow, live);
            rotatingProfile.Activate(rotating.Id);
            rotatingProfile = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(rotatingProfile))!;
            rotatingProfile.Validate();
            rotating = rotatingProfile.CurrentSubscription!;
            check(rotating.Selected == live.Id && rotatingProfile.Selected == live.Id && rotating.Nodes.Last().Id == live.Id
                && !rotating.RequiresConnectionConfirmation && Xhttp.ConnectionNodes(rotatingProfile).Single().Outbound["uuid"]!.ToString() == replacement,
                "consecutive credential rotations with the same label preserve the saved choice and prepare the updated login at the original endpoint");
        }
        var orphanedHighlight = new SavedSubscription { Nodes = [france, live], Selected = france.Id };
        check(orphanedHighlight.Refresh(new([live], []), DateTimeOffset.UtcNow, live) == null &&
            orphanedHighlight.Selected == live.Id && !orphanedHighlight.RequiresConnectionConfirmation,
            "a removed highlight moves to the verified running target when it is still available");
        var background = new SavedSubscription { Nodes = [france, live], Selected = live.Id };
        background.Refresh(new([france], []), DateTimeOffset.UtcNow);
        check(background.RequiresConnectionConfirmation && background.Selected == live.Id,
            "an offline manual choice also survives removal without automatic replacement");
        const string replacementId = "22222222-2222-4222-8222-222222222222";
        var changedLogin = Subscriptions.ParseNode(germanLink.Replace(uuid, replacementId).Replace("#old", "#Berlin"));
        var afterRotation = SubscriptionNodes.Match([live], [changedLogin]).Single();
        var reappeared = SubscriptionNodes.Match([afterRotation], [live, changedLogin]);
        check(reappeared[0].Id != live.Id && reappeared[1].Id == live.Id &&
            SubscriptionNodes.Match(reappeared, [changedLogin, live])[1].Id == reappeared[0].Id,
            "old raw link cannot steal the migrated row ID, regardless of feed order");
        var thirdLogin = Subscriptions.ParseNode(germanLink.Replace(uuid, Guid.Empty.ToString()));
        check(SubscriptionNodes.Match([live, changedLogin], [thirdLogin, changedLogin])[0].Id != live.Id &&
            SubscriptionNodes.Match([live], [changedLogin, thirdLogin]).All(n => n.Id != live.Id),
            "ambiguous rotations cannot be decided by exact matches elsewhere or feed order");
        string? diagnostic = null;
        var report = new SavedSubscription { Nodes = [live], Selected = live.Id };
        report.Refresh(new([changedLogin, thirdLogin], []), DateTimeOffset.UtcNow, live, text => diagnostic = text);
        check(diagnostic == "credential-rotations=0, reality-matches=0, target=ambiguous",
            "ambiguous reconciliation logs only a counter and fixed reason without identifying data");
        var rotatedJson = Subscriptions.Parse(fullVless.Replace(uuid, replacementId)).Nodes.Single();
        check(SubscriptionNodes.Match([fullLive], [rotatedJson]).Single().Id == fullLive.Id,
            "native Xray single VLESS peer accepts a client UUID rotation");
        var multi = JsonNode.Parse(fullVless)!.AsObject();
        var secondProxy = XrayConfig.Outbound(france);
        secondProxy["tag"] = "second";
        multi["outbounds"]!.AsArray().Add(secondProxy);
        string multipleProxies = multi.ToJsonString();
        var chain = Subscriptions.Parse(multipleProxies).Nodes.Single();
        check(SubscriptionNodes.Match([chain], Subscriptions.Parse(multipleProxies.Replace(uuid, replacementId)).Nodes).Single().Id != chain.Id,
            "a changed credential in a multi-proxy config is not evidence of the same route");
        const string hyLink = "hy2://old@de.example:443?obfs=salamander&obfs-password=shared#DE";
        var hy = Subscriptions.ParseNode(hyLink);
        string hyRotated = hyLink.Replace("//old@", "//new@");
        check(SubscriptionNodes.Match([hy], [Subscriptions.ParseNode(hyRotated)]).Single().Id == hy.Id &&
            SubscriptionNodes.Match([hy], [Subscriptions.ParseNode(hyRotated.Replace("shared", "different"))]).Single().Id != hy.Id,
            "Hysteria2 TLS login may rotate but the transport secret must match");
        string reality = germanLink.Replace("security=tls&type=ws&path=%2Fws", "security=reality&pbk=first&sid=1111&sni=test.example");
        var secure = Subscriptions.ParseNode(reality);
        foreach (var replaced in new[] { reality.Replace("pbk=first", "pbk=other"), reality.Replace("sid=1111", "sid=2222"), reality.Replace("test.example", "other.example") })
            check(SubscriptionNodes.Match([secure], [Subscriptions.ParseNode(replaced.Replace(uuid, replacementId))]).Single().Id != secure.Id,
                "a new peer key, short ID or SNI cannot authorize a rotated UUID");
        var ss = Subscriptions.ParseNode("ss://YWVzLTEyOC1nY206cGFzc3dvcmQ=@example.com:8388#DE");
        var ssChanged = Subscriptions.ParseNode("ss://YWVzLTEyOC1nY206b3RoZXI=@example.com:8388#DE");
        check(SubscriptionNodes.Match([ss], [ssChanged]).Single().Id != ss.Id, "a shared Shadowsocks key is not a client login");
        const string yaml = "proxies:\n  - name: Old\n    type: vless\n    server: example.com\n    port: 443\n    uuid: 11111111-1111-4111-8111-111111111111\n    tls: true\n";
        var clash = Subscriptions.Parse(yaml).Nodes.Single();
        check(SubscriptionNodes.Match([clash], Subscriptions.Parse(yaml.Replace("name: Old", "name: New")).Nodes).Single().Id == clash.Id, "Clash rename preserves its previously saved ID");
    }
}
