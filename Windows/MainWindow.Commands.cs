using Kot.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    void SaveProfile(Action<Profile> change) => profile = persistence.Change(profile, change);
    async Task<string?> Mutate(string action, JsonElement data)
    {
        switch (action)
        {
            case "routingSelect": case "routingSave": case "routingImport": case "routingRefresh": case "routingRemove": case "routingExport":
                await MutateRouting(action, data); break;
            case "geoIpRefresh": case "geoIpSettings":
                await MutateGeoIp(action, data); break;
            case "import": case "refresh":
                bool importing = action == "import";
                string address = importing ? data.GetProperty("url").GetString()?.Trim() ?? "" : "";
                var target = importing ? profile.Subscriptions.FirstOrDefault(s => !s.Local && s.Address == address)
                    : profile.Subscriptions.FirstOrDefault(s => s.Id == (data.TryGetProperty("subscriptionId", out var requested) ? requested.GetString() : profile.ActiveSubscription))
                        ?? throw new UserError("Подписка уже удалена.");
                if (!importing && target!.Local) throw new UserError("Локальный профиль обновляется повторным импортом файла.");
                if (!importing) address = target!.Address;
                string name = importing ? data.GetProperty("name").GetString()?.Trim() ?? "Подписка" : target!.Name;
                AppLog.Write("subscription", action + ": starting, hwid=" + profile.SendHwid);
                if (name.Length > 60) throw new UserError("Название слишком длинное.");
                using (var fetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    importAttempt = fetchCancellation;
                    try
                    {
                        var device = profile.SendHwid ? DeviceIdentity.Get() : null;
                        var result = await Subscriptions.Download(address, fetchCancellation.Token, subscriptionTransport?.Invoke(), device: device, proxyPort: RequestProxyPort);
                        return await ApplySubscription(target?.Id, address, name, result, importing, fetchCancellation.Token);
                    }
                    finally { if (importAttempt == fetchCancellation) importAttempt = null; }
                }
            case "importText": await ApplyLocalImport(data.GetProperty("text").GetString() ?? "", data.GetProperty("name").GetString()?.Trim() ?? ""); break;
            case "importServersFile": await ImportServersFile(data.GetProperty("name").GetString()?.Trim() ?? ""); break;
            case "pingSettings":
                var options = JsonSerializer.Deserialize<PingOptions>(data.GetRawText(), Json) ?? throw new UserError("Не переданы настройки пинга.");
                options.Validate(); SaveProfile(p => p.Ping = options); await ping.Stop(clear: true); AppLog.Write("ping", "settings saved"); break;
            case "select":
                string id = data.GetProperty("id").GetString() ?? "";
                if (data.TryGetProperty("subscriptionId", out var scope) && scope.GetString() != profile.ActiveSubscription)
                    throw new UserError("Подписка изменилась. Выберите сервер в текущей подписке.");
                if (id != "auto" && !profile.Nodes.Any(n => n.Id == id)) throw new UserError("Сервер уже удалён. Обновите подписку.");
                if (id != profile.Selected || id != "auto" && tunnel.SessionNode is { } active && active.Id != id) { await StopTools(); await tunnel.Stop(); try { SaveProfile(p => p.Selected = id); } finally { ResumeDesired(); } }
                break;
            case "settings": await ChangeSetting(data); break;
            case "switchSubscription":
                string subscriptionId = data.GetProperty("id").GetString() ?? "";
                if (profile.Subscriptions.All(s => s.Id != subscriptionId)) throw new UserError("Подписка уже удалена.");
                if (subscriptionId != profile.ActiveSubscription)
                {
                    await StopTools(); await ping.Stop(clear: true); await tunnel.Stop();
                    try { SaveProfile(p => p.Activate(subscriptionId)); warnings = []; AppLog.Register(profile); }
                    finally { ResumeDesired(); }
                }
                break;
            case "favorite":
                string favorite = data.GetProperty("id").GetString() ?? "";
                if (profile.Nodes.All(n => n.Id != favorite)) throw new UserError("Сервер уже удалён.");
                SaveProfile(p => { if (!p.Favorites.Remove(favorite)) p.Favorites.Add(favorite); }); if (profile.Selected == "auto" && reconnect.Desired) { await tunnel.Stop(); ResumeDesired(); } break;
            case "automationSettings":
                var automation = JsonSerializer.Deserialize<AutomationOptions>(data.GetRawText(), Json) ?? throw new UserError("Не переданы настройки.");
                automation.Validate(); bool autoRestart = profile.Selected == "auto" && (automation.AutoMinutes != profile.Automation.AutoMinutes || automation.AutoToleranceMs != profile.Automation.AutoToleranceMs);
                SaveProfile(p => p.Automation = automation); if (autoRestart) { await tunnel.Stop(); ResumeDesired(); } break;
            case "backupExport": await ExportBackup(data.GetProperty("password").GetString() ?? ""); break;
            case "backupImport": await ImportBackup(data.GetProperty("password").GetString() ?? ""); break;
            case "downloadUpdate": await DownloadUpdate(data.GetProperty("version").GetString() ?? ""); break;
            case "deferUpdate": DeferUpdate(data.GetProperty("version").GetString() ?? ""); break;
            case "remove":
                string removedSubscription = data.GetProperty("id").GetString() ?? "";
                if (profile.Subscriptions.All(s => s.Id != removedSubscription)) throw new UserError("Подписка уже удалена.");
                if (removedSubscription != profile.ActiveSubscription)
                {
                    SaveProfile(p => p.RemoveSubscription(removedSubscription)); refreshAttempts.Remove(removedSubscription); AppLog.Register(profile); break;
                }
                await StopTools(); await ping.Stop(clear: true); await tunnel.Stop();
                try
                {
                    SaveProfile(p => p.RemoveSubscription(removedSubscription));
                    warnings = []; if (profile.Subscriptions.Count == 0) reconnect.Stop();
                    refreshAttempts.Remove(removedSubscription); AppLog.Register(profile);
                }
                finally { ResumeDesired(); }
                break;
            case "editSubscription":
                string editedSubscription = data.GetProperty("id").GetString() ?? "";
                SaveProfile(p => p.EditSubscription(editedSubscription, data.GetProperty("name").GetString() ?? "", data.GetProperty("address").GetString() ?? ""));
                refreshAttempts.Remove(editedSubscription); AppLog.Register(profile); break;
            default: throw new UserError("Неизвестная команда.");
        }
        return null;
    }
}
