using Kot.Core;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    async Task ApplyLocalImport(string text, string name, CancellationToken ct)
    {
        if (name.Length > 60) throw new UserError("Название слишком длинное.");
        ct.ThrowIfCancellationRequested();
        var result = await Task.Run(() => Subscriptions.Parse(text), ct);
        result = await ProviderRouting.ResolveLocal(result, ct, RequestProxyPort);
        await ProviderAssets.Prepare(result.Nodes, ct, RequestProxyPort);
        result = await XraySession.Validate(result, ct);
        ct.ThrowIfCancellationRequested();
        if (profile.Subscriptions.Count >= 50) throw new UserError("Максимум 50 подписок.");
        await StopTools(); ct.ThrowIfCancellationRequested();
        await ping.Stop(clear: true); ct.ThrowIfCancellationRequested();
        await tunnel.Stop();
        try
        {
            ct.ThrowIfCancellationRequested();
            SaveProfile(p =>
            {
                var saved = new SavedSubscription { Local = true, Name = name.Length == 0 ? "Локальные серверы" : name, Nodes = result.Nodes, Updated = DateTimeOffset.UtcNow, Selected = result.Nodes[0].Id };
                p.Subscriptions.Add(saved); p.Activate(saved.Id);
            });
            warnings = result.Warnings; AppLog.Register(profile); AppLog.Write("import", "local nodes=" + result.Nodes.Count);
        }
        finally { ResumeDesired(); }
    }
    async Task ApplySubscription(string address, string name, SubscriptionResult result, bool importing, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        result = await SubscriptionValidation.Prepare(result,
            (nodes, token) => ProviderAssets.Prepare(nodes, token, RequestProxyPort), XraySession.Validate, ct);
        ct.ThrowIfCancellationRequested();
        var saved = profile.Subscriptions.FirstOrDefault(s => !s.Local && s.Address == address);
        bool switchProfile = importing && saved?.Id != profile.ActiveSubscription;
        if (switchProfile && saved == null && profile.Subscriptions.Count >= 50) throw new UserError("Максимум 50 подписок.");
        await StopTools();
        ct.ThrowIfCancellationRequested();
        string Fingerprint(Node n) => n.Id + ":" + n.FullConfig?.ToJsonString() + ":" + n.GeoIpUrl + ":" + n.GeoSiteUrl + ":" + n.GeoRevision;
        bool restart = switchProfile || (profile.Selected == "auto" ? !profile.Nodes.Select(Fingerprint).Order().SequenceEqual(result.Nodes.Select(Fingerprint).Order())
            : result.Nodes.FirstOrDefault(n => n.Id == profile.Selected) is not { } updated || profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected) is not { } current || Fingerprint(updated) != Fingerprint(current));
        await ping.Stop(clear: true); ct.ThrowIfCancellationRequested();
        if (restart) await tunnel.Stop();
        try
        {
            ct.ThrowIfCancellationRequested();
            SaveProfile(p =>
            {
                if (switchProfile)
                {
                    var candidate = p.Subscriptions.FirstOrDefault(s => s.Id == saved?.Id);
                    if (candidate == null) { candidate = new SavedSubscription { Address = address, Name = name.Length > 0 ? name : "Подписка" }; p.Subscriptions.Add(candidate); }
                    candidate.Nodes = result.Nodes; candidate.Usage = result.Usage; candidate.Updated = DateTimeOffset.UtcNow;
                    if (candidate.Selected != "auto" && candidate.Nodes.All(n => n.Id != candidate.Selected)) candidate.Selected = result.Nodes[0].Id;
                    p.Activate(candidate.Id);
                }
                else
                {
                    p.Address = address; p.Local = false; p.SubscriptionUsage = result.Usage; p.Name = name.Length > 0 ? name : "Подписка"; p.Nodes = result.Nodes; p.Updated = DateTimeOffset.UtcNow;
                    if (p.Selected != "auto" && result.Nodes.All(n => n.Id != p.Selected)) p.Selected = result.Nodes[0].Id;
                }
                p.Favorites.RemoveAll(id => p.Nodes.All(n => n.Id != id));
            });
            warnings = result.Warnings; AppLog.Register(profile); AppLog.Write("subscription", "saved: " + result.Nodes.Count + " nodes; skipped=" + warnings.Count);
        }
        finally { if (restart) ResumeDesired(); }
    }
    void ApplyInactiveSubscription(string subscriptionId, SubscriptionResult result)
    {
        SaveProfile(p =>
        {
            var updated = p.Subscriptions.Single(s => s.Id == subscriptionId);
            updated.Nodes = result.Nodes; updated.Usage = result.Usage; updated.Updated = DateTimeOffset.UtcNow;
            if (updated.Selected != "auto" && updated.Nodes.All(n => n.Id != updated.Selected)) updated.Selected = updated.Nodes[0].Id;
            updated.Favorites.RemoveAll(id => updated.Nodes.All(n => n.Id != id));
        });
    }
}
