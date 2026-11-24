using Kot.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.Tests;

// Fresh process per action. Synthetic input/output is piped, never logged. The
// Windows bridge is not hosted here: the view projects its profile/session fields
// into the real browser UI, while the runtime is exercised by a real Xray process.
static class RealityCycleFixture
{
    public static void Run(string folder)
    {
        var input = JsonNode.Parse(Console.In.ReadToEnd())!.AsObject();
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var live = input["live"]?.Deserialize<Node>(json);
        var store = new ProfilePersistence(folder, bytes =>
        {
            var p = JsonSerializer.Deserialize<Profile>(bytes)!; p.Validate(); return p;
        }, p => JsonSerializer.SerializeToUtf8Bytes(p));
        var profile = store.Load();
        string? notice = null, diagnostic = null;
        if (input["feed"] is { } text)
        {
            // Exercise the actual parser, including its content-derived source IDs.
            var feed = Subscriptions.Parse(text.GetValue<string>());
            profile = store.Change(profile, p =>
            {
                if (p.Subscriptions.Count == 0)
                {
                    var initial = new SavedSubscription { Address = "https://feed.example/sub", Name = "Тестовая подписка",
                        Nodes = feed.Nodes, Selected = feed.Nodes.Single(n => n.Name == "Германия").Id };
                    p.Subscriptions.Add(initial); p.Activate(initial.Id);
                }
                else
                {
                    var sub = p.CurrentSubscription!;
                    notice = sub.Refresh(feed, DateTimeOffset.UtcNow, live, value => diagnostic = value);
                    p.Activate(sub.Id);
                }
            });
        }
        var next = Xhttp.ConnectionNodes(profile).SingleOrDefault();
        var runtime = next == null ? null : XrayRuntime.Build(next, input["health"]!.GetValue<int>(), proxyPort: input["proxy"]!.GetValue<int>());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            notice, diagnostic, runtime, next,
            view = new
            {
                version = ClientIdentity.Version, displayVersion = ClientIdentity.DisplayVersion,
                buildNumber = ClientIdentity.BuildNumber,
                selected = profile.Selected, nextNode = profile.ManualConnectionNode?.Id,
                nodes = profile.Nodes.Select(n => new { id = n.Id, name = n.Name, protocol = n.Protocol }),
                activeNode = live == null ? null : new { id = live.Id, name = live.Name },
                state = live == null ? "idle" : "connected", needsSelection = profile.CurrentSubscription!.RequiresConnectionConfirmation,
                error = notice ?? "", name = profile.CurrentSubscription.Name,
                activeSubscription = profile.ActiveSubscription, hasSubscription = true,
                subscriptions = profile.Subscriptions.Select(s => new { id = s.Id, name = s.Name, count = s.Nodes.Count })
            }
        }));
    }
}
