using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Kot.Core;

public static class SubscriptionNodes
{
    // IDs already stored in a profile are also used by favorites, UI commands and live sessions.
    // Match within one subscription, reserving exact matches before considering renamed entries.
    public static List<Node> Match(IReadOnlyList<Node> previous, IReadOnlyList<Node> incoming)
    {
        var result = incoming.ToArray();
        var reserved = incoming.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var unmatched = previous.Where(n => !reserved.Contains(n.Id)).ToList();
        var oldIds = previous.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var pending = Enumerable.Range(0, result.Length).Where(i => !oldIds.Contains(result[i].Id)).ToList();

        void MatchUnique(Func<Node, string> key)
        {
            if (unmatched.Count == 0 || pending.Count == 0) return;
            var old = unmatched.ToLookup(key);
            var matched = new HashSet<int>();
            foreach (var group in pending.GroupBy(i => key(result[i])))
            {
                if (group.Count() != 1) continue;
                var candidates = old[group.Key].ToArray();
                if (candidates.Length != 1 || !reserved.Add(candidates[0].Id)) continue;
                int index = group.First();
                result[index] = result[index] with { Id = candidates[0].Id };
                matched.Add(index);
            }
            unmatched.RemoveAll(n => reserved.Contains(n.Id));
            pending.RemoveAll(matched.Contains);
        }

        MatchUnique(n => Fingerprint(n, includePolicy: true));
        unmatched.RemoveAll(n => n.Protocol != "xray");
        pending.RemoveAll(i => result[i].Protocol != "xray");
        MatchUnique(n => Fingerprint(n, includePolicy: false));
        return result.ToList();
    }

    static string Fingerprint(Node node, bool includePolicy)
    {
        JsonNode identity = node.Outbound;
        if (node.Protocol == "xray" && node.FullConfig is { } config)
        {
            if (includePolicy)
            {
                var copy = (JsonObject)config.DeepClone();
                copy.Remove("remarks"); copy.Remove("ps"); copy.Remove("meta");
                identity = copy;
            }
            else
            {
                // Retain all proxy legs, credentials and transports. A policy or label may change
                // independently. Only use this less specific match for an unambiguous pair.
                var proxies = new JsonArray();
                foreach (var outbound in config["outbounds"]!.AsArray().OfType<JsonObject>().Where(XrayConfig.IsProxy))
                {
                    var proxy = (JsonObject)outbound.DeepClone();
                    proxy.Remove("tag");
                    proxies.Add(proxy);
                }
                identity = proxies;
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(identity)!.ToJsonString())));
    }

    static JsonNode? Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => value?.DeepClone()
    };
}
