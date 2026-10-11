using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kot.Core;

public sealed record UsageTotals([property: JsonRequired] long Download = 0, [property: JsonRequired] long Upload = 0, [property: JsonRequired] long ConnectedMilliseconds = 0, [property: JsonRequired] long Sessions = 0)
{
    public UsageTotals Add(UsageTotals value) => new(Sum(Download, value.Download), Sum(Upload, value.Upload), Sum(ConnectedMilliseconds, value.ConnectedMilliseconds), Sum(Sessions, value.Sessions));
    static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    public bool Valid => Download >= 0 && Upload >= 0 && ConnectedMilliseconds >= 0 && Sessions >= 0;
}
public sealed record UsageDay([property: JsonRequired] DateOnly Date, [property: JsonRequired] UsageTotals Totals);
public sealed record UsageSession([property: JsonRequired] DateTimeOffset Started, [property: JsonRequired] DateTimeOffset Checkpoint, [property: JsonRequired] DateTimeOffset? Ended, [property: JsonRequired] UsageTotals Totals);
public sealed class UsageArchive
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public DateTimeOffset? FirstRecorded { get; set; }
    [JsonRequired] public UsageTotals AllTime { get; set; } = new();
    [JsonRequired] public UsageSession? LastSession { get; set; }
    [JsonRequired] public List<UsageDay> Days { get; set; } = [];
    public void Validate()
    {
        static bool Within(UsageTotals a, UsageTotals b) => a.Download <= b.Download && a.Upload <= b.Upload && a.ConnectedMilliseconds <= b.ConnectedMilliseconds && a.Sessions <= b.Sessions;
        if (Version != 1 || AllTime == null || !AllTime.Valid || Days == null || Days.Count > 32
            || Days.Any(d => d == null || d.Totals == null || !d.Totals.Valid || !Within(d.Totals, AllTime))
            || Days.Select(d => d.Date).Distinct().Count() != Days.Count
            || !Within(Days.Aggregate(new UsageTotals(), (total, day) => total.Add(day.Totals)), AllTime)
            || LastSession != null && (LastSession.Totals == null || !LastSession.Totals.Valid || LastSession.Totals.Sessions != 1 || !Within(LastSession.Totals, AllTime)))
            throw new UserError("Неверный файл истории трафика.");
    }
}

// Connection generations and monotonic timestamps are supplied by the tunnel.
// API errors never reset the baseline; closed connections remain in the API totals.
public sealed class UsageTracker(UsageArchive archive, TimeZoneInfo? zone = null)
{
    readonly TimeZoneInfo timezone = zone ?? TimeZoneInfo.Local;
    int? session;
    long lastTime, counterTime, download, upload;
    public UsageArchive Archive { get; } = archive;
    public int? ActiveSession => session;
    DateOnly Day(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, timezone).DateTime);
    public void Start(int generation, long milliseconds, DateTimeOffset utc)
    {
        if (session == generation) return;
        if (session != null) throw new InvalidOperationException("Finish the previous statistics session first.");
        session = generation; lastTime = counterTime = milliseconds; download = upload = 0;
        Archive.FirstRecorded ??= utc;
        Archive.LastSession = new(utc, utc, null, new(Sessions: 1));
        var started = new UsageTotals(Sessions: 1); Archive.AllTime = Archive.AllTime.Add(started); AddDay(Day(utc), started);
    }
    public bool Observe(int generation, long milliseconds, DateTimeOffset utc, ConnectionSnapshot? counters = null)
    {
        if (session != generation || milliseconds < lastTime) return false;
        if (counters != null && (counters.DownloadTotal < 0 || counters.UploadTotal < 0)) throw new ArgumentOutOfRangeException(nameof(counters));
        long elapsed = milliseconds - lastTime;
        var time = new UsageTotals(ConnectedMilliseconds: elapsed);
        Distribute(time, elapsed, utc);
        long down = 0, up = 0;
        if (counters != null)
        {
            // A counter reset starts a new baseline for this direction, not a negative delta.
            down = counters.DownloadTotal >= download ? counters.DownloadTotal - download : counters.DownloadTotal;
            up = counters.UploadTotal >= upload ? counters.UploadTotal - upload : counters.UploadTotal;
            Distribute(new(down, up), milliseconds - counterTime, utc);
            download = counters.DownloadTotal; upload = counters.UploadTotal; counterTime = milliseconds;
        }
        var added = new UsageTotals(down, up, elapsed);
        Archive.AllTime = Archive.AllTime.Add(added);
        Archive.LastSession = Archive.LastSession! with { Checkpoint = utc, Totals = Archive.LastSession.Totals.Add(added) };
        lastTime = milliseconds;
        return true;
    }
    public void End(int generation, long milliseconds, DateTimeOffset utc, ConnectionSnapshot? counters = null)
    {
        if (session != generation) return;
        Observe(generation, Math.Max(lastTime, milliseconds), utc, counters);
        Archive.LastSession = Archive.LastSession! with { Ended = utc }; session = null;
    }
    public UsageTotals Period(int days, DateTimeOffset utc)
    {
        if (days is not (7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        DateOnly today = Day(utc), first = today.AddDays(1 - days);
        return Archive.Days.Where(d => d.Date >= first && d.Date <= today).Aggregate(new UsageTotals(), (total, day) => total.Add(day.Totals));
    }
    void AddDay(DateOnly day, UsageTotals value)
    {
        int index = Archive.Days.FindIndex(d => d.Date == day);
        if (index < 0) Archive.Days.Add(new(day, value)); else Archive.Days[index] = new(day, Archive.Days[index].Totals.Add(value));
        Archive.Days = Archive.Days.OrderBy(d => d.Date).TakeLast(32).ToList();
    }
    void Distribute(UsageTotals value, long duration, DateTimeOffset end)
    {
        if (duration == 0) { AddDay(Day(end), value); return; }
        // Allocate intervals across local midnights. DST changes the length of a day,
        // while the duration itself always comes from the monotonic clock.
        DateTimeOffset start = end.AddMilliseconds(-duration);
        DateOnly first = Day(start), last = Day(end);
        if (last < first) { AddDay(last, value); return; }
        first = first < last.AddDays(-31) ? last.AddDays(-31) : first;
        for (DateOnly date = first; date <= last; date = date.AddDays(1))
        {
            DateTime local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            DateTime next = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            // Some time zones skip midnight on a DST transition.
            while (timezone.IsInvalidTime(local)) local = local.AddMinutes(1);
            while (timezone.IsInvalidTime(next)) next = next.AddMinutes(1);
            DateTimeOffset from = TimeZoneInfo.ConvertTimeToUtc(local, timezone), to = TimeZoneInfo.ConvertTimeToUtc(next, timezone);
            long a = Math.Clamp((long)(from - start).TotalMilliseconds, 0, duration), b = Math.Clamp((long)(to - start).TotalMilliseconds, 0, duration);
            if (b <= a) continue;
            long Part(long n) => (long)((decimal)n * b / duration) - (long)((decimal)n * a / duration);
            AddDay(date, new(Part(value.Download), Part(value.Upload), Part(value.ConnectedMilliseconds)));
        }
    }
}

public static class UsageFile
{
    public const int MaxBytes = 65536;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, IgnoreReadOnlyProperties = true };
    public static UsageArchive Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxBytes) throw new UserError("Слишком большой файл истории трафика.");
            var archive = JsonSerializer.Deserialize<UsageArchive>(stream, Json) ?? throw new UserError("Пустой файл истории трафика.");
            archive.Validate(); return archive;
        }
        catch (JsonException) { throw new UserError("Не удалось прочитать историю трафика."); }
    }
    public static void Save(string path, UsageArchive archive)
    {
        archive.Validate(); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(archive, Json);
        if (bytes.Length > MaxBytes) throw new UserError("Слишком большой файл истории трафика.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
