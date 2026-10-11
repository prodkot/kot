using Kot.Core;
namespace Kot.Tests;

public static class DetailedStatisticsTests
{
    static ConnectionInfo C(string id, string app, string route, long down, long up = 0) => new(id, "example.com:443", "tcp", "tun", app, [route], "", down, up, null);
    static ConnectionSnapshot S(long down, long up, params ConnectionInfo[] connections) => new(down, up, connections.Length, connections);
    static long Down(TrafficBreakdown traffic) => traffic.Entries.Sum(e => e.Download);
    static long Up(TrafficBreakdown traffic) => traffic.Entries.Sum(e => e.Upload);
    public static void Run(Action<bool, string> check, string folder)
    {
        Directory.CreateDirectory(folder); DateTimeOffset now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        var archive = new DetailedArchive(); var tracker = new DetailedUsageTracker(archive, TimeZoneInfo.Utc); tracker.Start(1, 0, now);
        tracker.Observe(1, 1000, now.AddSeconds(1), S(100, 20, C("a", "Browser.EXE", "proxy", 50, 10), C("b", "game.exe", "direct", 30, 5)));
        check(Down(archive.Session) == 100 && Up(archive.Session) == 20 && archive.Session.Entries.Any(e => e.App == "browser.exe" && e.Route == "vpn" && e.Download == 50), "detailed statistics separate apps and routes while preserving authoritative totals");
        check(archive.Session.Entries.Single(e => e.Route == "unknown").Download == 20, "closed or unobserved connections are honestly attributed to unknown");
        tracker.Observe(1, 2000, now.AddSeconds(2), S(140, 25, C("a", "browser.exe", "proxy", 80, 15)));
        check(archive.Session.Entries.Single(e => e.App == "browser.exe").Download == 80 && Down(archive.Session) == 140, "app counters add deltas instead of recounting lifetime bytes");
        check(!tracker.Observe(2, 3000, now, S(1000, 1000)) && !tracker.Observe(1, 500, now, S(1000, 1000)) && !tracker.Observe(1, 3000, now, null), "stale generation, reversed time and missing samples cannot alter app traffic");
        tracker.Observe(1, 4000, now.AddSeconds(4), S(150, 30, C("new", "late.exe", "proxy", 1000, 1000), C("new", "duplicate.exe", "direct", 1000)));
        check(Down(archive.Session) == 150 && Up(archive.Session) == 30 && archive.Session.Entries.All(e => e.App != "duplicate.exe"), "late and duplicate connections cannot overcount the core total");
        tracker.Observe(1, 5000, now.AddSeconds(5), S(5, 2, C("reset", "fresh.exe", "node-abc", 5, 2)));
        check(Down(archive.Session) == 155 && Up(archive.Session) == 32 && archive.Session.Entries.Any(e => e.App == "fresh.exe" && e.Route == "vpn"), "core reset starts a new counter baseline without losing previous traffic");
        tracker.End(1, 6000, now.AddSeconds(6), S(10, 4)); tracker.Start(3, 10000, now.AddMinutes(1)); tracker.End(1, 11000, now, S(999, 999)); tracker.End(3, 12000, now.AddMinutes(1).AddSeconds(2), S(7, 3));
        check(Down(archive.AllTime) == 167 && Down(archive.Session) == 7 && Up(archive.AllTime) == 37, "reconnect preserves all-time app usage, resets session and rejects old disconnects");
        archive.Validate();

        var boundary = new DetailedUsageTracker(new(), TimeZoneInfo.Utc); var before = DateTimeOffset.Parse("2026-10-11T23:59:59Z");
        boundary.Start(1, 0, before); boundary.End(1, 2000, before.AddSeconds(2), S(101, 41, C("midnight", "app.exe", "direct", 101, 41)));
        check(boundary.Archive.Days.Count == 2 && Down(boundary.Archive.Days[0].Traffic) == 50 && Down(boundary.Archive.Days[1].Traffic) == 51 && Up(boundary.Archive.AllTime) == 41, "detailed daily buckets split midnight without losing byte remainders");
        var periods = new DetailedUsageTracker(new(), TimeZoneInfo.Utc);
        for (int d = 40; d >= 0; d--) { var start = now.AddDays(-d); periods.Start(d, 0, start); periods.End(d, 1000, start.AddSeconds(1), S(100, 20, C("d", "app.exe", "proxy", 100, 20))); }
        check(periods.Archive.Days.Count == 32 && Down(periods.Period(7, now)) == 700 && Down(periods.Period(30, now)) == 3000 && Down(periods.Archive.AllTime) == 4100, "detailed calendar windows prune old days but retain all-time data");
        check(Down(periods.Period(7, now.AddDays(50))) == 0, "detailed periods expire on disconnected days");
        var limit = new DetailedUsageTracker(new(), TimeZoneInfo.Utc); limit.Start(1, 0, now);
        limit.End(1, 1000, now.AddSeconds(1), S(300, 0, Enumerable.Range(0, 300).Select(i => C(i.ToString(), "app" + i, i % 2 == 0 ? "proxy" : "direct", 1)).ToArray()));
        check(limit.Archive.AllTime.Entries.Count <= 128 && Down(limit.Archive.AllTime) == 300 && limit.Archive.AllTime.Entries.Any(e => e.App == "Другие приложения"), "bounded app history retains traffic when application cardinality is large");
        var overflow = new DetailedUsageTracker(new(), TimeZoneInfo.Utc); overflow.Start(1, 0, now); overflow.End(1, 1000, now.AddSeconds(1), S(long.MaxValue, 0, C("a", "a", "proxy", long.MaxValue), C("b", "b", "proxy", long.MaxValue)));
        check(Down(overflow.Archive.AllTime) == long.MaxValue && overflow.Archive.AllTime.Entries.All(e => e.Download >= 0), "large connection counters reconcile without overflow");
        var local = new DetailedUsageTracker(new(), TimeZoneInfo.CreateCustomTimeZone("detail+3", TimeSpan.FromHours(3), "detail+3", "detail+3")); local.Start(1, 0, before); local.End(1, 1000, before.AddSeconds(1), S(1, 0));
        check(local.Archive.Days[0].Date == new DateOnly(2026, 10, 12), "detailed days use the local time zone");
        var dst = new DetailedUsageTracker(new(), TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin")); var begin = DateTimeOffset.Parse("2026-03-28T23:00:00Z");
        dst.Start(1, 0, begin); dst.End(1, 23 * 3600000, begin.AddHours(23), S(230, 23));
        check(dst.Archive.Days.Count == 1 && Down(dst.Archive.Days[0].Traffic) == 230, "detailed history handles a 23-hour DST day");

        string path = Path.Combine(folder, "traffic-details.json"); DetailedUsageFile.Save(path, archive); var loaded = DetailedUsageFile.Load(path);
        check(Down(loaded.AllTime) == 167 && loaded.FirstRecorded == now && Down(loaded.Session) == 7, "detailed statistics survive a process restart");
        string valid = File.ReadAllText(path);
        foreach (string bad in new[] { "{}", "null", "{", valid.Replace("\"version\":1", "\"version\":2"), valid.Replace("\"download\":", "\"badField\":"), valid.Replace("\"route\":\"vpn\"", "\"route\":\"bad\"") })
        {
            File.WriteAllText(path, bad); bool rejected = false; try { DetailedUsageFile.Load(path); } catch (UserError) { rejected = true; }
            check(rejected && File.ReadAllText(path) == bad, "corrupt detailed history is preserved and reported");
        }
        File.WriteAllText(path, valid); string collision = Path.Combine(folder, "detail-target-directory"); Directory.CreateDirectory(collision);
        bool failed = false; try { DetailedUsageFile.Save(collision, archive); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        check(failed && Down(DetailedUsageFile.Load(path).AllTime) == 167 && !Directory.GetFiles(folder, "*.tmp-*").Any(), "failed detail replacement cleans temporary files and preserves existing data on Windows and Unix");
        check(DetailedUsageFile.Load(Path.Combine(folder, "new-details.json")).EntriesEmpty(), "first install starts detailed statistics without inventing historical apps");
    }
    static bool EntriesEmpty(this DetailedArchive archive) => archive.AllTime.Entries.Count == 0 && archive.FirstRecorded == null;
}
