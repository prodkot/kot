using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Kot.Core;

public static class SubscriptionNodes
{
    // IDs already stored in a profile are also used by favorites, UI commands and live sessions.
    // Match within one subscription, reserving exact matches before considering renamed entries.
    public static List<Node> Match(IReadOnlyList<Node> previous, IReadOnlyList<Node> incoming)
        => Match(previous, incoming, null, null, null, null, out _, out _);

    internal static List<Node> Match(IReadOnlyList<Node> previous, IReadOnlyList<Node> incoming,
        string? missingId, string? missingFingerprint, string? missingTechnical, string? missingReality,
        out int rotations, out int realityMatches)
    {
        var ids = new string?[incoming.Count];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var reserved = previous.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (missingId != null) reserved.Add(missingId);

        void Bind(int index, string id) { ids[index] = id; used.Add(id); }
        var bySource = previous.ToLookup(n => n.SourceId ?? n.Id);
        for (int i = 0; i < incoming.Count; i++)
        {
            var node = incoming[i];
            var rows = bySource[node.Id];
            var old = rows.Count() == 1 ? rows.First() : null;
            if (old != null && EndpointFingerprint(old) == EndpointFingerprint(node)) Bind(i, old.Id);
            else if (node.Id == missingId && (missingFingerprint == null || EndpointFingerprint(node) == missingFingerprint))
                Bind(i, node.Id);
        }

        // A missing session participates in each round. Its ID cannot displace a
        // known row or resolve a duplicate just because another row was claimed.
        void MatchUnique(Func<Node, string?> key, string? missingKey = null, bool countAllRows = false)
        {
            if (ids.All(id => id != null)) return;
            var old = previous.Where(n => countAllRows || !used.Contains(n.Id)).ToLookup(key);
            var allNew = Enumerable.Range(0, ids.Length).Where(i => countAllRows || ids[i] == null)
                .ToLookup(i => key(incoming[i]));
            foreach (var group in allNew)
            {
                if (group.Key == null || group.Count() != 1) continue;
                int i = group.First();
                if (ids[i] != null) continue;
                var candidates = old[group.Key];
                if (group.Key == missingKey && missingId != null && !used.Contains(missingId))
                {
                    if (!candidates.Any() && !previous.Any(n => key(n) == missingKey) &&
                        incoming.Count(n => key(n) == missingKey) == 1) Bind(i, missingId);
                    continue;
                }
                if (candidates.Count() == 1 && !used.Contains(candidates.First().Id)) Bind(i, candidates.First().Id);
            }
        }
        // A provider can move several countries between a shared pool. Resolve
        // unique label + security anchors before an unchanged endpoint can bind
        // a different country's ID. Never narrow uniqueness to unclaimed rows.
        var oldNames = previous.ToLookup(IdentityName);
        var newNames = incoming.ToLookup(IdentityName);
        var realityKeys = previous.Concat(incoming).Distinct().ToDictionary(n => n,
            n => oldNames[IdentityName(n)].Count() <= 1 && newNames[IdentityName(n)].Count() == 1
                ? RealityFingerprint(n) : null);
        int exactMatches = used.Count;
        MatchUnique(n => realityKeys[n], missingReality, countAllRows: true);
        realityMatches = used.Count - exactMatches;
        MatchUnique(n => Fingerprint(n, includePolicy: true));
        MatchUnique(EndpointFingerprint, missingFingerprint);
        int strongMatches = used.Count;
        MatchUnique(TechnicalFingerprint, missingTechnical, countAllRows: true);
        rotations = used.Count - strongMatches;

        // A raw link from an earlier version may reappear as a separate row.
        // Its ID is still owned by the migrated (or temporarily absent) row.
        var incomingIds = incoming.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<Node>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            var node = incoming[i];
            string id = ids[i] ?? node.Id;
            if (ids[i] == null && reserved.Contains(id))
            {
                do { id = Hash("kot-distinct-row:" + id); }
                while (reserved.Contains(id) || incomingIds.Contains(id) || used.Contains(id));
                used.Add(id);
            }
            result.Add(id == node.Id ? node : node with { Id = id, SourceId = node.Id });
        }
        return result;
    }

    internal static string EndpointFingerprint(Node node) => Fingerprint(node, includePolicy: false);

    static string IdentityName(Node node) => node.Name.Trim().Normalize(NormalizationForm.FormC);

    internal static string? UniqueRealityFingerprint(Node node, IReadOnlyList<Node> catalog)
        => catalog.Count(n => IdentityName(n) == IdentityName(node)) == 1 ? RealityFingerprint(node) : null;

    // Narrow exception for provider IP/REALITY shortId rotation. Keep the name,
    // login, port, peer key, SNI, transport, policy and every other option. An
    // address/name/login/key rotation together has no safe evidence here.
    internal static string? RealityFingerprint(Node node)
    {
        string name = IdentityName(node);
        if (name.Length == 0) return null;
        JsonObject identity, target, login, reality;
        string addressKey, loginKey, shortIdKey, publicKey, sni;
        if (node.Protocol == "vless")
        {
            identity = (JsonObject)node.Outbound.DeepClone();
            if (identity["tls"] is not JsonObject tls || tls["enabled"]?.ToString() != "true" ||
                tls["reality"] is not JsonObject r || r["enabled"]?.ToString() != "true") return null;
            target = login = identity; reality = r;
            addressKey = "server"; loginKey = "uuid"; shortIdKey = "short_id";
            publicKey = r["public_key"]?.ToString() ?? ""; sni = tls["server_name"]?.ToString() ?? "";
            if (identity["server_port"] == null) return null;
        }
        else if (node.Protocol == "xray" && node.FullConfig != null)
        {
            identity = (JsonObject)node.FullConfig.DeepClone();
            var proxies = identity["outbounds"]!.AsArray().OfType<JsonObject>().Where(XrayConfig.IsProxy).Take(2).ToArray();
            if (proxies.Length != 1 || proxies[0]["protocol"]?.ToString() != "vless" ||
                proxies[0]["settings"] is not JsonObject settings ||
                proxies[0]["streamSettings"] is not JsonObject stream || stream["security"]?.ToString() != "reality" ||
                stream["realitySettings"] is not JsonObject r) return null;
            target = login = settings; reality = r;
            if (settings["vnext"] is JsonArray targets)
            {
                if (targets.Count != 1 || targets[0] is not JsonObject peer || settings["address"] != null ||
                    peer["users"] is not JsonArray users || users.Count != 1 || users[0] is not JsonObject user) return null;
                target = peer; login = user;
            }
            addressKey = "address"; loginKey = "id"; shortIdKey = "shortId";
            publicKey = r["publicKey"]?.ToString() ?? r["password"]?.ToString() ?? "";
            sni = r["serverName"]?.ToString() ?? "";
            if (target["port"] == null) return null;
            identity.Remove("remarks"); identity.Remove("ps"); identity.Remove("meta");
        }
        else return null;
        if (!System.Net.IPAddress.TryParse(target[addressKey]?.ToString(), out _) || string.IsNullOrWhiteSpace(sni) ||
            !Guid.TryParseExact(login[loginKey]?.ToString(), "D", out var uuid) || uuid == Guid.Empty) return null;
        // Invalid or absent key material must not make all peers look identical.
        if (publicKey.Length != 43 || publicKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return null;
        string sid = reality[shortIdKey]?.ToString() ?? "";
        if (sid.Length > 16 || sid.Length % 2 != 0 || !sid.All(Uri.IsHexDigit)) return null;
        login[loginKey] = uuid.ToString("D");
        target.Remove(addressKey); reality.Remove(shortIdKey);
        return Digest(new JsonObject { ["name"] = name, ["config"] = identity });
    }

    // Remove exactly one client login. Preserve the remote endpoint, peer keys,
    // transport, encryption and every other option. For native JSON also keep
    // routing and every outbound, and decline unknown/multi-peer layouts.
    internal static string? TechnicalFingerprint(Node node)
    {
        if (node.Protocol != "xray")
        {
            var copy = (JsonObject)node.Outbound.DeepClone();
            if (string.IsNullOrEmpty(copy["server"]?.ToString()) || copy["server_port"] == null) return null;
            if (node.Protocol is "vless" or "vmess")
            {
                if (!Guid.TryParseExact(copy["uuid"]?.ToString(), "D", out _)) return null;
                copy.Remove("uuid");
            }
            else if (node.Protocol is "trojan" or "hysteria2" && copy["tls"]?["enabled"]?.GetValue<bool>() == true)
            {
                if (!copy.Remove("password")) return null;
            }
            else return null;
            return Digest(copy);
        }
        if (node.FullConfig == null) return null;
        var config = (JsonObject)node.FullConfig.DeepClone();
        var proxies = config["outbounds"]!.AsArray().OfType<JsonObject>().Where(XrayConfig.IsProxy).Take(2).ToArray();
        if (proxies.Length != 1 || proxies[0]["settings"] is not JsonObject settings) return null;
        var proxy = proxies[0];
        string? protocol = proxy["protocol"]?.ToString();
        bool password = protocol == "trojan";
        if (protocol is not ("vless" or "vmess" or "trojan") ||
            password && proxy["streamSettings"]?["security"]?.ToString() != "tls") return null;
        JsonObject target = settings, login = settings;
        if (settings[password ? "servers" : "vnext"] is JsonArray targets)
        {
            if (targets.Count != 1 || targets[0] is not JsonObject peer || settings["address"] != null) return null;
            target = login = peer;
            if (!password)
            {
                if (peer["users"] is not JsonArray users || users.Count != 1 || users[0] is not JsonObject user) return null;
                login = user;
            }
        }
        if (string.IsNullOrEmpty(target["address"]?.ToString()) || target["port"] == null) return null;
        if (!password && !Guid.TryParseExact(login["id"]?.ToString(), "D", out _)) return null;
        if (!login.Remove(password ? "password" : "id")) return null;
        config.Remove("remarks"); config.Remove("ps"); config.Remove("meta");
        return Digest(config);
    }

    static string Fingerprint(Node node, bool includePolicy)
    {
        JsonNode identity = node.Outbound;
        if (node.Protocol is "vless" or "vmess" && Guid.TryParseExact(node.Outbound["uuid"]?.ToString(), "D", out var uuid))
        {
            identity = node.Outbound.DeepClone();
            identity["uuid"] = uuid.ToString("D");
        }
        if (node.Protocol == "xray" && node.FullConfig is { } config)
        {
            if (includePolicy || config["outbounds"]!.AsArray().OfType<JsonObject>().Count(XrayConfig.IsProxy) != 1)
            {
                var copy = (JsonObject)config.DeepClone();
                foreach (var outbound in copy["outbounds"]!.AsArray().OfType<JsonObject>()) NormalizeXrayUserIds(outbound);
                copy.Remove("remarks"); copy.Remove("ps"); copy.Remove("meta");
                identity = copy;
            }
            else
            {
                // Keep the array form for compatibility with stored single-proxy fingerprints.
                // A provider can change policy separately from the proxy.
                var proxies = new JsonArray();
                foreach (var outbound in config["outbounds"]!.AsArray().OfType<JsonObject>().Where(XrayConfig.IsProxy))
                {
                    var proxy = (JsonObject)outbound.DeepClone();
                    NormalizeXrayUserIds(proxy);
                    proxy.Remove("tag");
                    proxies.Add(proxy);
                }
                identity = proxies;
            }
        }
        return Digest(identity);
    }

    static string Digest(JsonNode identity) => Hash(Canonical(identity)!.ToJsonString());
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static void NormalizeXrayUserIds(JsonObject outbound)
    {
        if (outbound["protocol"]?.ToString() is not ("vless" or "vmess") || outbound["settings"] is not JsonObject settings) return;
        static void NormalizeId(JsonObject user)
        {
            if (Guid.TryParseExact(user["id"]?.ToString(), "D", out var uuid)) user["id"] = uuid.ToString("D");
        }
        NormalizeId(settings);
        if (settings["vnext"] is not JsonArray targets) return;
        foreach (var target in targets.OfType<JsonObject>())
            if (target["users"] is JsonArray users)
                foreach (var user in users.OfType<JsonObject>()) NormalizeId(user);
    }

    static JsonNode? Canonical(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => value?.DeepClone()
    };
}
