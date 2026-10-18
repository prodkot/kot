using Kot.Core;
using System.Text.Json;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    async Task ChangeSetting(JsonElement data)
    {
        string key = data.GetProperty("key").GetString() ?? ""; var value = data.GetProperty("value");
        bool restart = false, oldStartup = profile.Startup, oldGuard = KillSwitch.Active;
        Action<Profile> edit;
        switch (key)
        {
            case "theme":
                string theme = value.GetString()!; if (theme is not ("dark" or "light")) throw new UserError("Неизвестная тема.");
                edit = p => p.Theme = theme; break;
            case "mode":
                string mode = value.GetString()!; if (mode is not ("tun" or "proxy")) throw new UserError("Неизвестный режим.");
                restart = mode != profile.ConnectionMode; edit = p => p.ConnectionMode = mode; break;
            case "bypass":
                var domains = Configuration.Domains(value.GetString() ?? ""); restart = !domains.SequenceEqual(profile.Bypass) || profile.Mode != "smart";
                edit = p => { p.Bypass = domains; p.Mode = "smart"; }; break;
            case "startup": bool startup = value.GetBoolean(); edit = p => p.Startup = startup; break;
            case "autoConnect": bool autoConnect = value.GetBoolean(); edit = p => p.AutoConnect = autoConnect; break;
            case "tray": bool tray = value.GetBoolean(); edit = p => p.Tray = tray; break;
            case "killSwitch": bool guard = value.GetBoolean(); edit = p => p.KillSwitch = guard; break;
            case "sendHwid": bool hwid = value.GetBoolean(); edit = p => p.SendHwid = hwid; break;
            default: throw new UserError("Неизвестная настройка.");
        }
        if (restart) { await StopTools(); await tunnel.Stop(); }
        try
        {
            if (key == "startup") await Startup.Set(value.GetBoolean());
            if (key == "killSwitch")
            {
                if (value.GetBoolean() && tunnel.State != "idle") ArmCurrentConnection();
                if (!value.GetBoolean()) KillSwitch.Release();
            }
            SaveProfile(edit);
        }
        catch
        {
            if (key == "startup") await Startup.Set(oldStartup);
            if (key == "killSwitch") { if (oldGuard) ArmCurrentConnection(); else KillSwitch.Release(); }
            throw;
        }
        finally { if (restart) ResumeDesired(); }
    }
    void ArmCurrentConnection() => KillSwitch.Arm(tunnel.State == "connected" && profile.ConnectionMode == "tun" ? KillSwitch.TunnelInterface() : 0);
}
