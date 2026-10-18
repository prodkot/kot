using Kot.Core;
using System.Net;
using System.Text.Json;

namespace Kot.Windows;

static class GeoIpStore
{
    public static readonly GeoIpCache Cache = new(Path.Combine(Store.Folder, "geoip-cidr"));
    static string CoreExe => Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
    public static async Task<string[]> Update(string[] countries, bool missingOnly, CancellationToken ct, int proxyPort = 0)
    {
        if (countries.Length == 0) return [];
        if (KillSwitch.Active && proxyPort == 0)
        {
            if (missingOnly)
            {
                try { Cache.Paths(countries); return []; }
                catch (UserError) { throw new UserError("Сначала загрузите GeoIP до включения Kill switch. Рабочие базы затем доступны без интернета."); }
            }
            throw new UserError("Для обновления GeoIP при активном Kill switch подключитесь к VPN.");
        }
        return await Cache.Update(countries, missingOnly, (path, token) => GeoIp.ValidateFile(path, CoreExe, token), ct,
            (code, token) => GeoIp.Download(code, token, new HttpClientHandler
            {
                AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false,
                UseProxy = proxyPort > 0, Proxy = proxyPort > 0 ? new WebProxy("http://127.0.0.1:" + proxyPort) : null
            }));
    }
}

public sealed partial class MainWindow
{
    bool geoIpBusy;
    string geoIpError = "";
    DateTimeOffset? geoIpAttempt;
    string[] GeoIpCountries() => profile.RoutingProfiles.SelectMany(p => p.Rules.Countries()).Distinct().Order().ToArray();
    string[] ActiveGeoIpCountries() => profile.RoutingProfiles.FirstOrDefault(p => p.Id == profile.ActiveRoutingProfile)?.Rules.Countries() ?? [];
    int GeoIpProxyPort => tunnel.State == "connected" ? tunnel.ProxyPort : 0;
    object GeoIpModel() => new { busy = geoIpBusy, error = geoIpError, refreshHours = profile.GeoIp.RefreshHours, countries = GeoIpStore.Cache.Status(GeoIpCountries()) };
    async Task RefreshGeoIp()
    {
        string[] countries = GeoIpCountries();
        if (countries.Length == 0) throw new UserError("Добавьте страны во вкладке «Страны» и сохраните профиль маршрутизации.");
        geoIpBusy = true; geoIpError = ""; geoIpAttempt = DateTimeOffset.UtcNow; Snapshot();
        try
        {
            string[] changed = await GeoIpStore.Update(countries, false, lifetime.Token, GeoIpProxyPort);
            lifetime.Token.ThrowIfCancellationRequested();
            // Download and validate the whole batch while the current tunnel keeps working.
            if (changed.Intersect(ActiveGeoIpCountries()).Any()) { await tunnel.Stop(); ResumeDesired(); }
            GeoIpStore.Cache.Prune();
            AppLog.Write("geoip-cidr", "country databases refreshed: " + countries.Length);
        }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested) geoIpError = "Не удалось обновить GeoIP. Рабочая база сохранена. " + Store.Friendly(ex);
            throw;
        }
        finally { geoIpBusy = false; Snapshot(); }
    }
    async Task MutateGeoIp(string action, JsonElement data)
    {
        if (action == "geoIpRefresh") { await RefreshGeoIp(); return; }
        var options = new GeoIpOptions { RefreshHours = data.GetProperty("refreshHours").GetInt32() }; options.Validate();
        var old = profile.GeoIp; profile.GeoIp = options;
        try { Store.Save(profile); }
        catch { profile.GeoIp = old; throw; }
        geoIpAttempt = null;
    }
    async Task TickGeoIp(DateTimeOffset now, bool online)
    {
        if (!online || connecting || exiting || geoIpBusy || geoIpAttempt is { } tried && now - tried < TimeSpan.FromMinutes(15)
            || !GeoIpStore.Cache.Due(GeoIpCountries(), profile.GeoIp.RefreshHours, now) || !await operations.WaitAsync(0)) return;
        try
        {
            try { await RefreshGeoIp(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("geoip background refresh", ex); }
        }
        finally { operations.Release(); }
    }
}
