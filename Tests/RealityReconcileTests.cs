using Kot.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.Tests;

static class RealityReconcileTests
{
    // Documentation-range endpoints and deliberately synthetic credentials only.
    const string Login = "abcdefab-cdef-4abc-8def-abcdefabcdef";
    const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    static Node Peer(string address, string sid, string label = "Германия", string login = Login, string key = Key)
        => Subscriptions.ParseNode($"vless://{login}@{address}:443?security=reality&pbk={key}&sid={sid}&sni=peer.example&fp=firefox&flow=xtls-rprx-vision#{Uri.EscapeDataString(label)}");
    static Node Native(Node node)
        => Subscriptions.Parse(new JsonObject { ["remarks"] = node.Name, ["inbounds"] = new JsonArray(),
            ["outbounds"] = new JsonArray(XrayConfig.Outbound(node)) }.ToJsonString()).Nodes.Single();
    static Profile Reload(Profile profile)
    {
        var loaded = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!;
        loaded.Validate();
        return loaded;
    }
    public static void Run(Action<bool, string> check)
    {
        foreach (bool native in new[] { false, true })
        {
            Node Format(Node node) => native ? Native(node) : node;
            var a = Format(Peer("192.0.2.10", "11111111"));
            var b = Format(Peer("192.0.2.11", "22222222"));
            var c = Format(Peer("192.0.2.10", "33333333", login: Login.ToUpperInvariant()));
            var se = Format(Peer("192.0.2.20", "44444444", "Швеция"));
            foreach (var independent in new[] { Format(Peer("192.0.2.11", "11111111")),
                Format(Peer("192.0.2.10", "22222222")) })
                check(SubscriptionNodes.Match([se, a], [se, independent])[1].Id == a.Id,
                    "IP and shortId can rotate independently without changing the logical server");
            foreach (var path in new[] { new[] { a, b, c }, new[] { b, c }, new[] { a, c } })
            {
                var live = path[0];
                string original = JsonSerializer.Serialize(live);
                var sub = new SavedSubscription { Local = true, Nodes = [se, live], Selected = live.Id, Favorites = [live.Id] };
                var profile = new Profile { Subscriptions = [sub] };
                profile.Activate(sub.Id);
                foreach (var incoming in path.Skip(1))
                {
                    string? notice = sub.Refresh(new([se, incoming], []), DateTimeOffset.UtcNow, live);
                    profile.Activate(sub.Id);
                    check(notice == null && !sub.RequiresConnectionConfirmation && sub.Selected == live.Id &&
                        sub.Nodes[1].Id == live.Id && sub.Favorites.Single() == live.Id,
                        "REALITY IP/shortId rotation keeps the selected row and favorite without a missing-server warning");
                    profile = Reload(profile); sub = profile.CurrentSubscription!;
                    var next = Xhttp.ConnectionNodes(profile).Single();
                    check(JsonNode.DeepEquals(XrayRuntime.Build(next, 12345)["outbounds"], XrayRuntime.Build(incoming, 12345)["outbounds"]),
                        "reconnect and restart build the incoming Germany outbound, including the new IP and independent shortId");
                    check(JsonSerializer.Serialize(live) == original, "refresh never mutates the live tunnel configuration");
                }
            }
            // Shared login/key/SNI do not identify a country. Resolve name anchors
            // before endpoint comparisons, including a pool exchanging endpoints.
            var swapped = SubscriptionNodes.Match([a, se], [Format(Peer("192.0.2.10", "11111111", "Швеция")),
                Format(Peer("192.0.2.20", "44444444"))]);
            check(swapped[0].Id == se.Id && swapped[1].Id == a.Id, "shared credentials and exchanged addresses cannot exchange country IDs");
            var sameName = Format(Peer("192.0.2.30", "55555555"));
            check(SubscriptionNodes.Match([a, sameName], [b, sameName])[0].Id != a.Id,
                "duplicate old names stay ambiguous even after an exact row was claimed");
            check(SubscriptionNodes.Match([a], [b, sameName]).All(n => n.Id != a.Id), "duplicate new names do not authorize roaming");
            foreach (var unsafeNode in new[] { Format(Peer("192.0.2.11", "22222222", "Швеция")),
                Format(Peer("192.0.2.11", "22222222", login: "22222222-2222-4222-8222-222222222222")),
                Format(Peer("192.0.2.11", "22222222", key: "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBA")) })
                check(SubscriptionNodes.Match([a], [unsafeNode]).Single().Id != a.Id, "name, login or peer key changes cannot authorize a simultaneous IP rotation");
            foreach (string changed in new[] { "sni=other.example&fp=firefox&flow=xtls-rprx-vision",
                "sni=peer.example&fp=chrome&flow=xtls-rprx-vision", "sni=peer.example&fp=firefox&flow=" })
            {
                var unsafeNode = Format(Subscriptions.ParseNode($"vless://{Login}@192.0.2.11:443?security=reality&pbk={Key}&sid=22222222&{changed}#Германия"));
                check(SubscriptionNodes.Match([a], [unsafeNode]).Single().Id != a.Id,
                    "IP rotation retains SNI, fingerprint and flow as required anchors");
            }
            var ambiguous = new SavedSubscription { Local = true, Nodes = [a, sameName], Selected = a.Id };
            var ambiguousProfile = new Profile { Subscriptions = [ambiguous] }; ambiguousProfile.Activate(ambiguous.Id);
            ambiguous.Refresh(new([b, sameName], []), DateTimeOffset.UtcNow, a); ambiguousProfile.Activate(ambiguous.Id);
            ambiguousProfile = Reload(ambiguousProfile); ambiguous = ambiguousProfile.CurrentSubscription!;
            ambiguous.Refresh(new([b], []), DateTimeOffset.UtcNow);
            check(ambiguous.RequiresConnectionConfirmation && ambiguous.MissingRealityFingerprint == null,
                "ambiguous old names cannot become a guessed match after other rows disappear and the app restarts");
            var missing = new SavedSubscription { Local = true, Nodes = [se, a], Selected = a.Id };
            var stored = new Profile { Subscriptions = [missing] }; stored.Activate(missing.Id);
            missing.Refresh(new([se], []), DateTimeOffset.UtcNow, a); stored.Activate(missing.Id);
            stored = Reload(stored); missing = stored.CurrentSubscription!;
            check(missing.RequiresConnectionConfirmation && Xhttp.ConnectionNodes(stored).Count == 0 && stored.Selected == a.Id,
                "missing Germany survives restart without selecting or connecting Sweden");
            missing.Refresh(new([se], []), DateTimeOffset.UtcNow);
            missing.Refresh(new([se, b], []), DateTimeOffset.UtcNow); stored.Activate(missing.Id);
            check(!missing.RequiresConnectionConfirmation && stored.ManualConnectionNode?.Id == a.Id,
                "a uniquely returning REALITY node can rotate IP/shortId across absence and restart");
            check(missing.MissingRealityFingerprint == null, "a recognized target clears the missing REALITY digest");
        }
    }
}
