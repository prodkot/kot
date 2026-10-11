using Kot.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace Kot.Windows;
public sealed partial class MainWindow
{
    readonly ManagedServices services;
    async Task ServiceMessage(string action, JsonElement data)
    {
        string id = data.TryGetProperty("service", out var service) ? service.GetString() ?? "" : "zapret";
        switch (action)
        {
            case "serviceAction":
                string operation = data.GetProperty("operation").GetString() ?? "";
                if (operation is not ("check" or "install" or "start" or "stop" or "restart" or "remove" or "test")) throw new UserError("Неизвестная операция сервиса.");
                string[]? selected = data.TryGetProperty("strategies", out var list) ? list.EnumerateArray().Select(s => s.GetString() ?? "").ToArray() : null;
                if (id == "telegram" && operation is "install" or "start" or "restart") Store.Save(profile);
                services.Begin(id, operation, selected); break;
            case "serviceCancel": services.Cancel(id); break;
            case "serviceSettings":
                services.EnsureEditable(id);
                if (id == "zapret")
                {
                    var zapret = JsonSerializer.Deserialize<ZapretOptions>(data.GetProperty("settings"), Json) ?? throw new UserError("Настройки zapret не прочитаны.");
                    zapret.Validate(); services.EnsureStrategy(zapret.Strategy); var old = profile.Services.Zapret; profile.Services.Zapret = zapret;
                    try { Store.Save(profile); } catch { profile.Services.Zapret = old; throw; }
                }
                else if (id == "telegram")
                {
                    var telegram = JsonSerializer.Deserialize<TelegramProxyOptions>(data.GetProperty("settings"), Json) ?? throw new UserError("Настройки Telegram не прочитаны.");
                    if (!data.GetProperty("settings").TryGetProperty("secret", out var secret) || string.IsNullOrEmpty(secret.GetString())) telegram.Secret = profile.Services.Telegram.Secret;
                    telegram.Validate(); var old = profile.Services.Telegram; profile.Services.Telegram = telegram;
                    try { Store.Save(profile); } catch { profile.Services.Telegram = old; throw; }
                    AppLog.Secret(old.Secret); AppLog.Secret(telegram.Secret);
                }
                else throw new UserError("Неизвестный сервис.");
                if (data.TryGetProperty("restart", out var restart) && restart.GetBoolean()) services.Begin(id, "restart");
                break;
            case "serviceUpdateSettings":
                bool previous = profile.Services.CheckUpdates; profile.Services.CheckUpdates = data.GetProperty("enabled").GetBoolean();
                try { Store.Save(profile); } catch { profile.Services.CheckUpdates = previous; throw; } break;
            case "serviceSource": Process.Start(new ProcessStartInfo(ServicePackages.Page(id)) { UseShellExecute = true }); break;
            case "serviceRotateSecret":
                services.EnsureEditable("telegram"); string oldSecret = profile.Services.Telegram.Secret;
                profile.Services.Telegram.Secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                try { Store.Save(profile); } catch { profile.Services.Telegram.Secret = oldSecret; throw; }
                AppLog.Secret(oldSecret); AppLog.Secret(profile.Services.Telegram.Secret); if (services.IsRunning("telegram")) services.Begin("telegram", "restart"); break;
            case "serviceCopyLink": Clipboard.SetText(profile.Services.Telegram.Link); break;
            case "serviceOpenTelegram":
                services.EnsureEditable("telegram");
                if (!services.IsRunning("telegram") || !await ManagedServices.PortOpen(profile.Services.Telegram.Port, lifetime.Token)) throw new UserError("Сначала запустите Telegram-прокси.");
                Process.Start(new ProcessStartInfo(profile.Services.Telegram.Link) { UseShellExecute = true }); break;
            case "serviceSelectTest": services.SelectTest(data.GetProperty("started").GetDateTimeOffset()); break;
            case "serviceCopyResults": Clipboard.SetText(services.Export()); break;
            case "serviceExportResults":
                using (var dialog = new SaveFileDialog { Filter = "Результаты zapret (*.json)|*.json", FileName = "kot-zapret-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) await File.WriteAllTextAsync(dialog.FileName, services.Export());
                break;
            case "serviceClearLog": services.ClearLog(id); break;
            default: throw new UserError("Неизвестное действие сервисов.");
        }
    }
}
