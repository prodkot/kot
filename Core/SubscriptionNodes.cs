using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Kot.Core;

public static class SubscriptionNodes
{
    // Keep persisted IDs, including those written by 0.7.x. This migrates
    // preferences during refresh without changing the on-disk format or
    // generating IDs which an older client could not recognize on rollback.
    public static List<Node> Match(IReadOnlyList<Node> previous, IReadOnlyList<Node> incoming)
    {
        var result = incoming.ToArray();
        var used = new HashSet<string>(incoming.Select(n => n.Id), StringComparer.Ordinal);
        var unchanged = previous.Select(n => n.Id).Intersect(used).ToHashSet();
        var oldGroups = previous.Where(n => !unchanged.Contains(n.Id)).GroupBy(Identity).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in incoming.Select((node, index) => (node, index)).Where(x => !unchanged.Contains(x.node.Id)).GroupBy(x => Identity(x.node)))
        {
            if (!oldGroups.TryGetValue(group.Key, out var old)) continue;
            var next = group.ToArray();
            // Exact IDs were matched first. A remaining one-to-one match is
            // safe; ambiguous duplicates retain their independent incoming IDs.
            if (old.Length != 1 || next.Length != 1 || !used.Add(old[0].Id)) continue;
            result[next[0].index] = next[0].node with { Id = old[0].Id };
        }
        return result.ToList();
    }
    public static string Identity(Node node)
    {
        JsonObject? config = node.FullConfig == null ? null : (JsonObject)node.FullConfig.DeepClone();
        // These are also the only cosmetic top-level sections dropped by
        // XrayConfig.Runtime. DNS, routing, credentials and transports stay.
        config?.Remove("remarks"); config?.Remove("ps"); config?.Remove("meta");
        var value = new JsonObject { ["protocol"] = node.Protocol, ["outbound"] = node.Outbound.DeepClone(),
            ["config"] = config, ["geoIpUrl"] = node.GeoIpUrl, ["geoSiteUrl"] = node.GeoSiteUrl, ["geoRevision"] = node.GeoRevision };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value)!.ToJsonString())));
    }
    static JsonNode? Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => value?.DeepClone()
    };
}
