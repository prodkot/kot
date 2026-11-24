using Kot.Core;
using System.Text.Json;

namespace Kot.Tests;

// Each invocation is a fresh client process using the production profile store,
// refresh, next-node resolver and Xray config builder, with a loopback-only feed.
static class SubscriptionCycleFixture
{
    internal static void Run(string[] args)
    {
        const string original = "abcdefab-cdef-4abc-8def-abcdefabcdef";
        const string rotated = "22222222-2222-4222-8222-222222222222";
        Node Peer(string uuid, string port, string label) =>
            Subscriptions.ParseNode($"vless://{uuid}@127.0.0.1:{port}?security=none#{label}");
        var germany = Peer(original, args[3], "Germany");
        var sweden = Peer(original, args[4], "Sweden");
        var berlin = Peer(rotated, args[3], "Berlin");
        var store = new ProfilePersistence(args[2], bytes =>
        {
            var p = JsonSerializer.Deserialize<Profile>(bytes)!;
            p.Validate();
            return p;
        }, p => JsonSerializer.SerializeToUtf8Bytes(p));
        Profile profile;
        string? notice = null;
        if (args[1] == "initial")
        {
            var sub = new SavedSubscription { Local = true, Nodes = [sweden, germany], Selected = germany.Id };
            profile = new Profile { Subscriptions = [sub] };
            profile.Activate(sub.Id);
            store.Save(profile);
        }
        else if (args[1] == "restart") profile = store.Load();
        else if (args[1] == "select-sweden")
            profile = store.Change(store.Load(), p =>
            {
                p.Selected = sweden.Id;
                var sub = p.CurrentSubscription!;
                sub.ResumeNodeId = sub.MissingEndpointFingerprint = sub.MissingTechnicalFingerprint = sub.MissingRealityFingerprint = null;
            });
        else
        {
            List<Node> feed = args[1] switch
            {
                "case" => [sweden, Peer(original.ToUpperInvariant(), args[3], "Berlin")],
                "rotate" or "return" => [sweden, berlin],
                "gone" => [sweden],
                "ambiguous" => [sweden, berlin, Peer(Guid.Empty.ToString(), args[3], "Germany")],
                "other-address" => [sweden, Peer(original, args[4], "Germany")],
                _ => throw new ArgumentException("Unknown feed")
            };
            profile = store.Change(store.Load(), p =>
            {
                var sub = p.CurrentSubscription!;
                notice = sub.Refresh(new(feed, []), DateTimeOffset.UtcNow, args[1] == "return" ? null : germany);
                p.Activate(sub.Id);
            });
        }
        var next = Xhttp.ConnectionNodes(profile).SingleOrDefault();
        Console.WriteLine(JsonSerializer.Serialize(new {
            selectedOriginal = profile.Selected == germany.Id, notice,
            next = next == null ? null : XrayRuntime.Build(next, int.Parse(args[5]), proxyPort: int.Parse(args[6]))
        }));
    }
}
