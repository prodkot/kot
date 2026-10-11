using System.Text.Json;
using System.Text.Json.Serialization;
namespace Kot.Core;

public sealed record AppTraffic(string App, string Route, long Download, long Upload);
public sealed class TrafficBreakdown
{
    public List<AppTraffic> Entries { get; set; } = [];
    public void Add(AppTraffic item)
    {
        int index = Entries.FindIndex(e => e.App == item.App && e.Route == item.Route);
        if (index < 0) Entries.Add(item);
        else { var old = Entries[index]; Entries[index] = old with { Download = Sum(old.Download, item.Download), Upload = Sum(old.Upload, item.Upload) }; }
    }
    public static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    public void Validate()
    {
        if (Entries == null || Entries.Count > 128 || Entries.Any(e => e == null || string.IsNullOrEmpty(e.App) || e.App.Length > 128 || e.App.Any(char.IsControl) || e.Route is not ("vpn" or "direct" or "unknown") || e.Download < 0 || e.Upload < 0) || Entries.Select(e => (e.App, e.Route)).Distinct().Count() != Entries.Count)
            throw new UserError("Неверный подробный учёт трафика.");
    }
}
public sealed record TrafficDay(DateOnly Date, TrafficBreakdown Traffic);
public sealed class DetailedArchive
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public DateTimeOffset? FirstRecorded { get; set; }
    [JsonRequired] public TrafficBreakdown AllTime { get; set; } = new();
    [JsonRequired] public TrafficBreakdown Session { get; set; } = new();
    [JsonRequired] public List<TrafficDay> Days { get; set; } = [];
    public void Validate()
    {
        if (Version != 1 || AllTime == null || Session == null || Days == null || Days.Count > 32 || Days.Any(d => d == null || d.Traffic == null) || Days.Select(d => d.Date).Distinct().Count() != Days.Count) throw new UserError("Неверная подробная история.");
        AllTime.Validate(); Session.Validate(); foreach (var day in Days) day.Traffic.Validate();
        foreach (var period in Days.Select(d => d.Traffic).Append(Session))
            foreach (var entry in period.Entries)
            {
                var total = AllTime.Entries.FirstOrDefault(e => e.App == entry.App && e.Route == entry.Route);
                if (total == null || entry.Download > total.Download || entry.Upload > total.Upload) throw new UserError("Повреждены подробные счётчики.");
            }
        foreach (var total in AllTime.Entries)
        {
            var entries = Days.SelectMany(d => d.Traffic.Entries).Where(e => e.App == total.App && e.Route == total.Route);
            if (total.Download < long.MaxValue && entries.Sum(e => (decimal)e.Download) > total.Download || total.Upload < long.MaxValue && entries.Sum(e => (decimal)e.Upload) > total.Upload) throw new UserError("Дневные счётчики превышают общий трафик.");
        }
    }
}
// Global API totals include closed connections. Attribute observed deltas only up to that
// authoritative total; put the remainder in Unknown instead of inventing an application.
public sealed class DetailedUsageTracker(DetailedArchive archive, TimeZoneInfo? zone = null)
{
    readonly TimeZoneInfo timezone = zone ?? TimeZoneInfo.Local;
    Dictionary<string, (long Down, long Up)> previous = [];
    int? generation; long down, up, counterTime;
    public DetailedArchive Archive { get; } = archive;
    DateOnly Day(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, timezone).DateTime);
    public void Start(int session, long milliseconds, DateTimeOffset utc)
    {
        if (generation == session) return;
        if (generation != null) throw new InvalidOperationException("End the previous detail session first.");
        generation = session; down = up = 0; counterTime = milliseconds; previous.Clear(); Archive.Session = new(); Archive.FirstRecorded ??= utc;
    }
    public void End(int session, long milliseconds, DateTimeOffset utc, ConnectionSnapshot? snapshot)
    { Observe(session, milliseconds, utc, snapshot); if (generation == session) { generation = null; previous.Clear(); } }
    public bool Observe(int session, long milliseconds, DateTimeOffset utc, ConnectionSnapshot? snapshot)
    {
        if (generation != session || milliseconds < counterTime || snapshot == null) return false;
        if (snapshot.DownloadTotal < 0 || snapshot.UploadTotal < 0) throw new ArgumentOutOfRangeException(nameof(snapshot));
        long deltaDown = snapshot.DownloadTotal >= down ? snapshot.DownloadTotal - down : snapshot.DownloadTotal;
        long deltaUp = snapshot.UploadTotal >= up ? snapshot.UploadTotal - up : snapshot.UploadTotal;
        if (snapshot.DownloadTotal < down || snapshot.UploadTotal < up) previous.Clear();
        List<AppTraffic> raw = []; Dictionary<string, (long Down, long Up)> current = [];
        foreach (var c in snapshot.Connections.Take(300))
        {
            if (c.Id.Length == 0 || c.Download < 0 || c.Upload < 0 || current.ContainsKey(c.Id)) continue;
            var old = previous.GetValueOrDefault(c.Id); current[c.Id] = (c.Download, c.Upload);
            string app = string.Concat(c.Process.Where(ch => !char.IsControl(ch))).ToLowerInvariant(); if (app.Length == 0) app = "Не определено"; if (app.Length > 128) app = app[..128];
            string route = c.Chains.Contains("direct") ? "direct" : c.Chains.Any(t => t == "proxy" || t.StartsWith("node-", StringComparison.Ordinal)) ? "vpn" : "unknown";
            raw.Add(new(app, route, c.Download >= old.Down ? c.Download - old.Down : c.Download, c.Upload >= old.Up ? c.Upload - old.Up : c.Upload));
        }
        decimal rawDown = raw.Sum(e => (decimal)e.Download), rawUp = raw.Sum(e => (decimal)e.Upload);
        long attributedDown = 0, attributedUp = 0;
        foreach (var entry in raw)
        {
            long d = rawDown > deltaDown ? (long)(entry.Download * (deltaDown / rawDown)) : entry.Download;
            long u = rawUp > deltaUp ? (long)(entry.Upload * (deltaUp / rawUp)) : entry.Upload;
            if (d == 0 && u == 0) continue;
            attributedDown += d; attributedUp += u;
            Add(entry with { Download = d, Upload = u }, milliseconds - counterTime, utc);
        }
        if (attributedDown < deltaDown || attributedUp < deltaUp) Add(new("Не определено", "unknown", deltaDown - attributedDown, deltaUp - attributedUp), milliseconds - counterTime, utc);
        down = snapshot.DownloadTotal; up = snapshot.UploadTotal; counterTime = milliseconds; previous = current; return true;
    }
    void Add(AppTraffic value, long duration, DateTimeOffset end)
    {
        if (Archive.AllTime.Entries.All(e => e.App != value.App || e.Route != value.Route) && Archive.AllTime.Entries.Count >= 125) value = value with { App = "Другие приложения" };
        Archive.AllTime.Add(value); Archive.Session.Add(value);
        foreach (var part in UsageIntervals.Parts(duration, end, timezone))
        {
            long Share(long n) => duration == 0 ? n : (long)(n * ((decimal)part.End / duration)) - (long)(n * ((decimal)part.Start / duration));
            var day = Archive.Days.FirstOrDefault(d => d.Date == part.Date);
            if (day == null) { day = new(part.Date, new()); Archive.Days.Add(day); }
            day.Traffic.Add(value with { Download = Share(value.Download), Upload = Share(value.Upload) });
        }
        Archive.Days = Archive.Days.OrderBy(d => d.Date).TakeLast(32).ToList();
    }
    public TrafficBreakdown Period(int days, DateTimeOffset now)
    {
        if (days is not (7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        DateOnly today = Day(now); TrafficBreakdown result = new();
        foreach (var day in Archive.Days.Where(d => d.Date <= today && d.Date >= today.AddDays(1 - days))) foreach (var e in day.Traffic.Entries) result.Add(e);
        return result;
    }
}
public static class UsageIntervals
{
    public sealed record Part(DateOnly Date, long Start, long End);
    public static IEnumerable<Part> Parts(long duration, DateTimeOffset end, TimeZoneInfo zone)
    {
        DateOnly Day(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
        if (duration == 0) { yield return new(Day(end), 0, 0); yield break; }
        DateTimeOffset start = end.AddMilliseconds(-duration); DateOnly first = Day(start), last = Day(end);
        if (last < first) { yield return new(last, 0, duration); yield break; }
        if (first < last.AddDays(-31)) first = last.AddDays(-31);
        for (DateOnly date = first; date <= last; date = date.AddDays(1))
        {
            DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), next = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1); while (zone.IsInvalidTime(next)) next = next.AddMinutes(1);
            DateTimeOffset from = TimeZoneInfo.ConvertTimeToUtc(local, zone), to = TimeZoneInfo.ConvertTimeToUtc(next, zone);
            long a = Math.Clamp((long)(from - start).TotalMilliseconds, 0, duration), b = Math.Clamp((long)(to - start).TotalMilliseconds, 0, duration);
            if (b > a) yield return new(date, a, b);
        }
    }
}
public static class DetailedUsageFile
{
    const int MaxBytes = 2 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public static DetailedArchive Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path); if (stream.Length > MaxBytes) throw new UserError("Подробная история слишком большая.");
        try { var archive = JsonSerializer.Deserialize<DetailedArchive>(stream, Json) ?? throw new UserError("Пустая подробная история."); archive.Validate(); return archive; }
        catch (JsonException) { throw new UserError("Не удалось прочитать подробную историю."); }
    }
    public static void Save(string path, DetailedArchive archive)
    {
        archive.Validate(); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(archive, Json); if (bytes.Length > MaxBytes) throw new UserError("Подробная история слишком большая.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
