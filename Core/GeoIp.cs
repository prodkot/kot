using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kot.Core;

public sealed class GeoIpOptions
{
    public int RefreshHours { get; set; } = 24;
    public void Validate()
    {
        if (RefreshHours is < 0 or > 168) throw new UserError("Интервал обновления GeoIP: от 0 до 168 часов. 0 отключает автообновление.");
    }
}

public static class GeoIp
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const string ProjectUrl = "https://github.com/Loyalsoldier/geoip";
    // Country files published by the project's rule-set branch. No arbitrary download URLs or local paths.
    static readonly HashSet<string> Supported = new(("ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bl bm bn bo bq br bs bt bw by bz ca cd cf cg ch ci ck cl cm cn co cr cu cv cw cy cz de dj dk dm do dz ec ee eg er es et fi fj fk fm fo fr ga gb gd ge gf gg gh gi gl gm gn gp gq gr gt gu gw gy hk hn hr ht hu id ie il im in io iq ir is it je jm jo jp ke kg kh ki km kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mf mg mh mk ml mm mn mo mp mq mr ms mt mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pe pf pg ph pk pl pm pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg si sk sl sm sn so sr ss st sv sx sy sz tc td tg th tj tk tl tm tn to tr tt tv tw tz ua ug us uy uz va vc ve vg vi vn vu wf ws ye yt za zm zw").Split(' '), StringComparer.Ordinal);
    public static string[] Countries(string input)
    {
        var codes = RoutingRules.Entries(input).Select(code => code.ToLowerInvariant()).ToArray();
        if (codes.Length > 100) throw new UserError("Максимум 100 стран в одном списке GeoIP.");
        foreach (string code in codes)
            if (!Supported.Contains(code)) throw new UserError("Неизвестная страна GeoIP. Используйте двухбуквенные коды, например RU, CN, US или GB.");
        return codes;
    }
    public static Uri Source(string code)
    {
        var codes = Countries(code);
        if (codes.Length != 1) throw new UserError("Нужен один код страны GeoIP.");
        return new Uri("https://raw.githubusercontent.com/Loyalsoldier/geoip/release/text/" + codes[0] + ".txt");
    }
    public static async Task<byte[]> Download(string code, CancellationToken ct, HttpMessageHandler? testTransport = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        using var handler = testTransport ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseProxy = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientIdentity.SubscriptionUserAgent);
        using var response = await client.GetAsync(Source(code), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new UserError($"Источник GeoIP {code.ToUpperInvariant()} вернул HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaxBytes) throw new UserError("Файл GeoIP слишком большой.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using MemoryStream buffer = new(); byte[] block = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(block, deadline.Token); if (count == 0) break;
            if (buffer.Length + count > MaxBytes) throw new UserError("Файл GeoIP слишком большой.");
            buffer.Write(block, 0, count);
        }
        byte[] bytes = buffer.ToArray();
        Prefixes(Encoding.UTF8.GetString(bytes));
        return bytes;
    }
    public static string[] Prefixes(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new UserError("Файл GeoIP слишком большой.");
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 1 or > 300000) throw new UserError("Неверная база GeoIP.");
        foreach (string line in lines)
        {
            var parts = line.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || parts[0].Contains('%') || !int.TryParse(parts[1], out int bits) || bits < 0 || bits > ip.GetAddressBytes().Length * 8)
                throw new UserError("Неверная IP-подсеть в базе GeoIP.");
        }
        return lines.Distinct().ToArray();
    }
    public static async Task ValidateFile(string path, string coreExe, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new UserError("Файл GeoIP слишком большой.");
        Prefixes(await File.ReadAllTextAsync(path, ct));
    }
}

public sealed record GeoIpEntry(string Hash, DateTimeOffset Updated, string PreviousHash = "");
public sealed record GeoIpStatus(string Code, bool Ready, DateTimeOffset? Updated);

// Immutable country files plus one atomically replaced index make a multi-country update all-or-nothing.
public sealed class GeoIpCache
{
    readonly string folder, indexPath;
    readonly SemaphoreSlim updates = new(1);
    readonly object sync = new();
    Dictionary<string, GeoIpEntry> entries = [];
    public GeoIpCache(string folder)
    {
        this.folder = Path.GetFullPath(folder); indexPath = Path.Combine(this.folder, "index.json");
        if (!File.Exists(indexPath)) return;
        try
        {
            if (new FileInfo(indexPath).Length > 128 * 1024) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, GeoIpEntry>>(File.ReadAllText(indexPath));
            if (saved == null || saved.Count > 238) return;
            Dictionary<string, GeoIpEntry> valid = [];
            foreach (var (code, entry) in saved)
            {
                if (GeoIp.Countries(code).Single() != code || entry == null || !HashValid(entry.Hash) || entry.PreviousHash == null || entry.PreviousHash.Length > 0 && !HashValid(entry.PreviousHash)) return;
                if (ValidFile(code, entry)) valid[code] = entry;
            }
            entries = valid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UserError or InvalidOperationException) { entries.Clear(); }
    }
    static bool HashValid(string hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    string PathFor(string code, string hash) => Path.Combine(folder, "geoip-" + code + "-" + hash + ".cidr");
    bool ValidFile(string code, GeoIpEntry entry)
    {
        string path = PathFor(code, entry.Hash);
        if (!File.Exists(path) || new FileInfo(path).Length is < 5 or > GeoIp.MaxBytes) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == entry.Hash;
    }
    public GeoIpStatus[] Status(IEnumerable<string> countries)
    {
        lock (sync) return countries.Distinct().Order().Select(code => entries.TryGetValue(code, out var entry) && File.Exists(PathFor(code, entry.Hash))
            ? new GeoIpStatus(code, true, entry.Updated) : new GeoIpStatus(code, false, null)).ToArray();
    }
    public Dictionary<string, string> Paths(IEnumerable<string> countries)
    {
        lock (sync)
        {
            Dictionary<string, string> result = [];
            foreach (string code in countries.Distinct())
            {
                if (!entries.TryGetValue(code, out var entry) || !ValidFile(code, entry)) throw new UserError($"Обновите базу GeoIP {code.ToUpperInvariant()}.");
                result[code] = PathFor(code, entry.Hash);
            }
            return result;
        }
    }
    public bool Due(IEnumerable<string> countries, int hours, DateTimeOffset now) => hours > 0 && Status(countries).Any(item => !item.Ready || now - item.Updated!.Value >= TimeSpan.FromHours(hours));
    public async Task<string[]> Update(IEnumerable<string> countries, bool missingOnly, Func<string, CancellationToken, Task> validate, CancellationToken ct,
        Func<string, CancellationToken, Task<byte[]>>? download = null)
    {
        string[] codes = countries.SelectMany(GeoIp.Countries).Distinct().Order().ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5)); ct = deadline.Token;
        await updates.WaitAsync(ct);
        List<string> temporary = [], created = []; bool committed = false;
        try
        {
            Dictionary<string, GeoIpEntry> next;
            lock (sync) next = new(entries);
            List<string> changed = [];
            foreach (string code in codes)
            {
                if (missingOnly && next.TryGetValue(code, out var available) && ValidFile(code, available)) continue;
                Directory.CreateDirectory(folder);
                string staging = Path.Combine(folder, "download-" + Guid.NewGuid().ToString("N") + ".cidr"); temporary.Add(staging);
                byte[] data = await (download == null ? GeoIp.Download(code, ct) : download(code, ct));
                if (data.Length is < 5 or > GeoIp.MaxBytes) throw new UserError("Неверный размер базы GeoIP.");
                await File.WriteAllBytesAsync(staging, data, ct); await validate(staging, ct); ct.ThrowIfCancellationRequested();
                string hash = Convert.ToHexStringLower(SHA256.HashData(data)), destination = PathFor(code, hash);
                next.TryGetValue(code, out var old);
                if (old?.Hash != hash) changed.Add(code);
                // Repair a corrupted cached file even when the source content has not changed.
                if (!File.Exists(destination) || !ValidFile(code, new(hash, DateTimeOffset.UtcNow)))
                {
                    File.Move(staging, destination, true); if (old?.Hash != hash) created.Add(destination);
                }
                next[code] = new(hash, DateTimeOffset.UtcNow, old?.Hash == hash ? old.PreviousHash : old?.Hash ?? "");
            }
            if (codes.Length == 0 || missingOnly && temporary.Count == 0) return [];
            string indexTemp = Path.Combine(folder, "index-" + Guid.NewGuid().ToString("N") + ".tmp"); temporary.Add(indexTemp);
            await File.WriteAllTextAsync(indexTemp, JsonSerializer.Serialize(next), ct); ct.ThrowIfCancellationRequested();
            File.Move(indexTemp, indexPath, true);
            lock (sync) entries = next;
            committed = true; return changed.ToArray();
        }
        finally
        {
            foreach (string path in temporary.Concat(committed ? [] : created)) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            updates.Release();
        }
    }
    // Call after switching the tunnel to the new index. Retain one previous generation per country.
    public void Prune()
    {
        if (!updates.Wait(0)) return;
        try
        {
            lock (sync)
            {
                if (!Directory.Exists(folder)) return;
                var keep = entries.SelectMany(item => new[] { PathFor(item.Key, item.Value.Hash), PathFor(item.Key, item.Value.PreviousHash) }).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string path in Directory.EnumerateFiles(folder, "geoip-*.cidr"))
                    if (!keep.Contains(path)) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { updates.Release(); }
    }
}
