using Kot.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.Windows;

public sealed partial class MainWindow
{
    readonly Dictionary<string, DateTimeOffset> routingAttempts = [];
    readonly Dictionary<string, string> routingErrors = [];
    static string RoutingFingerprint(RoutingRules rules)
    {
        var json = (JsonObject)JsonNode.Parse(RoutingProfiles.Serialize(rules))!;
        json.Remove("name"); return json.ToJsonString();
    }
    string RoutingFingerprint() => profile.ActiveRoutingProfile.Length == 0 ? "standard" : RoutingFingerprint(profile.RoutingProfiles.First(p => p.Id == profile.ActiveRoutingProfile).Rules);
    object RoutingModel() => new { active = profile.ActiveRoutingProfile, profiles = profile.RoutingProfiles, errors = routingErrors };
    async Task PutRouting(RoutingProfile candidate, bool activate)
    {
        candidate.Validate();
        int index = profile.RoutingProfiles.FindIndex(p => p.Id == candidate.Id);
        if (index < 0 && profile.RoutingProfiles.Count >= 20) throw new UserError("Максимум 20 профилей маршрутизации.");
        await GeoIpStore.Update(candidate.Rules.Countries(), true, lifetime.Token, GeoIpProxyPort);
        lifetime.Token.ThrowIfCancellationRequested();
        string before = RoutingFingerprint();
        bool affectsConnection = activate || profile.ActiveRoutingProfile == candidate.Id;
        bool restart = affectsConnection && before != RoutingFingerprint(candidate.Rules);
        if (restart) await tunnel.Stop();
        string previousActive = profile.ActiveRoutingProfile;
        var previous = index < 0 ? null : profile.RoutingProfiles[index];
        if (index < 0) profile.RoutingProfiles.Add(candidate); else profile.RoutingProfiles[index] = candidate;
        if (activate) profile.ActiveRoutingProfile = candidate.Id;
        try { Store.Save(profile); }
        catch
        {
            if (index < 0) profile.RoutingProfiles.Remove(candidate); else profile.RoutingProfiles[index] = previous!;
            profile.ActiveRoutingProfile = previousActive;
            if (restart) ResumeDesired();
            throw;
        }
        routingErrors.Remove(candidate.Id); AppLog.Register(profile);
        if (GeoIpCountries().Length == 0) { geoIpError = ""; geoIpAttempt = null; }
        if (restart) ResumeDesired();
    }
    async Task RefreshRouting(RoutingProfile saved, CancellationToken ct)
    {
        if (saved.SourceUrl.Length == 0) throw new UserError("У профиля нет источника обновлений.");
        routingAttempts[saved.Id] = DateTimeOffset.UtcNow;
        var rules = await RoutingProfiles.Download(saved.SourceUrl, ct);
        ct.ThrowIfCancellationRequested();
        if (exiting) return;
        await PutRouting(new RoutingProfile { Id = saved.Id, Rules = rules, SourceUrl = saved.SourceUrl, RefreshHours = saved.RefreshHours, Updated = DateTimeOffset.UtcNow }, false);
        AppLog.Write("routing", "routing profile refreshed");
    }
    async Task MutateRouting(string action, JsonElement data)
    {
        switch (action)
        {
            case "routingSelect":
                string selected = data.GetProperty("id").GetString() ?? "";
                if (selected.Length > 0 && profile.RoutingProfiles.All(p => p.Id != selected)) throw new UserError("Профиль маршрутизации уже удалён.");
                if (selected != profile.ActiveRoutingProfile)
                {
                    if (selected.Length > 0) await GeoIpStore.Update(profile.RoutingProfiles.First(p => p.Id == selected).Rules.Countries(), true, lifetime.Token, GeoIpProxyPort);
                    lifetime.Token.ThrowIfCancellationRequested();
                    await tunnel.Stop(); string oldSelection = profile.ActiveRoutingProfile; profile.ActiveRoutingProfile = selected;
                    try { Store.Save(profile); }
                    catch { profile.ActiveRoutingProfile = oldSelection; ResumeDesired(); throw; }
                    ResumeDesired();
                }
                break;
            case "routingSave":
                string id = data.GetProperty("id").GetString() ?? "";
                var saved = id.Length > 0 ? profile.RoutingProfiles.FirstOrDefault(p => p.Id == id) ?? throw new UserError("Профиль маршрутизации уже удалён.") : null;
                var rules = RoutingProfiles.Parse(data.GetProperty("rules").GetRawText());
                string source = data.GetProperty("sourceUrl").GetString()?.Trim() ?? "";
                var candidate = new RoutingProfile { Id = saved?.Id ?? Guid.NewGuid().ToString("N"), Rules = rules, SourceUrl = source, RefreshHours = data.GetProperty("refreshHours").GetInt32(), Updated = saved?.Updated };
                candidate.Validate();
                if (source.Length > 0 && source != saved?.SourceUrl)
                {
                    candidate.Rules = await RoutingProfiles.Download(source, lifetime.Token); candidate.Updated = DateTimeOffset.UtcNow;
                    lifetime.Token.ThrowIfCancellationRequested();
                }
                if (!exiting) await PutRouting(candidate, true);
                break;
            case "routingImport":
                string content = data.GetProperty("content").GetString()?.Trim() ?? "";
                string url = content.StartsWith('{') ? "" : content;
                RoutingRules imported;
                if (url.Length == 0) imported = RoutingProfiles.Parse(content);
                else { RoutingProfiles.Source(url); imported = await RoutingProfiles.Download(url, lifetime.Token); }
                lifetime.Token.ThrowIfCancellationRequested();
                var existing = url.Length == 0 ? null : profile.RoutingProfiles.FirstOrDefault(p => p.SourceUrl == url);
                if (!exiting) await PutRouting(new RoutingProfile { Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Rules = imported, SourceUrl = url, RefreshHours = 24, Updated = DateTimeOffset.UtcNow }, true);
                break;
            case "routingRefresh":
                var refreshing = FindRouting(data);
                try { await RefreshRouting(refreshing, lifetime.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { routingErrors[refreshing.Id] = "Не удалось обновить маршрутизацию. " + Store.Friendly(ex); throw; }
                break;
            case "routingRemove":
                var removing = FindRouting(data); bool active = profile.ActiveRoutingProfile == removing.Id;
                if (active) await tunnel.Stop();
                int oldIndex = profile.RoutingProfiles.IndexOf(removing);
                profile.RoutingProfiles.Remove(removing); if (active) profile.ActiveRoutingProfile = "";
                try { Store.Save(profile); }
                catch { profile.RoutingProfiles.Insert(oldIndex, removing); if (active) { profile.ActiveRoutingProfile = removing.Id; ResumeDesired(); } throw; }
                routingAttempts.Remove(removing.Id); routingErrors.Remove(removing.Id);
                AppLog.Register(profile);
                if (GeoIpCountries().Length == 0) { geoIpError = ""; geoIpAttempt = null; }
                if (active) ResumeDesired();
                break;
            case "routingExport":
                var exporting = FindRouting(data);
                using (var dialog = new SaveFileDialog { Filter = "Профиль маршрутизации (*.json)|*.json", FileName = "kot-routing.json" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllTextAsync(dialog.FileName, RoutingProfiles.Serialize(exporting.Rules));
                break;
        }
    }
    RoutingProfile FindRouting(JsonElement data) => profile.RoutingProfiles.FirstOrDefault(p => p.Id == data.GetProperty("id").GetString()) ?? throw new UserError("Профиль маршрутизации уже удалён.");
    async Task TickRouting(DateTimeOffset now, bool online)
    {
        if (!online || connecting || exiting || !await operations.WaitAsync(0)) return;
        try
        {
            var due = profile.RoutingProfiles.FirstOrDefault(p => p.Due(now) && (!routingAttempts.TryGetValue(p.Id, out var tried) || now - tried >= TimeSpan.FromMinutes(15)));
            if (due == null) return;
            try { await RefreshRouting(due, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("routing background refresh", ex); routingErrors[due.Id] = "Не удалось обновить маршрутизацию. " + Store.Friendly(ex); }
        }
        finally { operations.Release(); }
    }
}
