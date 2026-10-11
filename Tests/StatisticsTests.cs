using Kot.Core;

namespace Kot.Tests;

public static class StatisticsTests
{
    public static void Run(Action<bool, string> check, string folder)
    {
        ConnectionSnapshot Counters(long down, long up) => new(down, up, 0, []);
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        var history = new UsageArchive(); var tracker = new UsageTracker(history, TimeZoneInfo.Utc);
        tracker.Start(1, 1000, now); tracker.Start(1, 1000, now);
        check(history.AllTime.Sessions == 1 && tracker.ActiveSession == 1, "statistics start only once for a successful generation");
        tracker.Observe(1, 3000, now.AddSeconds(2), Counters(100, 40));
        tracker.Observe(1, 3000, now.AddSeconds(2), Counters(100, 40));
        check(history.AllTime == new UsageTotals(100, 40, 2000, 1), "statistics repeated samples do not double count time or bytes");
        tracker.Observe(1, 2500, now, Counters(200, 80));
        tracker.Observe(99, 4000, now, Counters(999, 999));
        check(history.AllTime == new UsageTotals(100, 40, 2000, 1), "statistics reject stale generations and out-of-order samples");
        tracker.Observe(1, 5000, now.AddSeconds(4));
        tracker.Observe(1, 7000, now.AddSeconds(6), Counters(200, 80));
        check(history.AllTime == new UsageTotals(200, 80, 6000, 1), "statistics recover the full byte delta after missing API samples");
        tracker.Observe(1, 8000, now.AddSeconds(7), Counters(10, 90));
        check(history.AllTime.Download == 210 && history.AllTime.Upload == 90, "statistics handle independent counter resets without negative usage");
        tracker.End(1, 9000, now.AddSeconds(8), Counters(25, 100));
        tracker.End(1, 9000, now.AddSeconds(8), Counters(25, 100));
        check(history.AllTime == new UsageTotals(225, 100, 8000, 1) && tracker.ActiveSession == null && history.LastSession!.Ended != null, "statistics include final disconnect counters and finish only once");
        tracker.Start(3, 10000, now.AddMinutes(1)); tracker.Observe(3, 11000, now.AddMinutes(1).AddSeconds(1), Counters(50, 20));
        check(history.LastSession!.Totals == new UsageTotals(50, 20, 1000, 1) && history.AllTime == new UsageTotals(275, 120, 9000, 2), "statistics reconnect resets session and preserves all-time usage");
        tracker.End(1, 12000, now, Counters(999, 999));
        check(tracker.ActiveSession == 3 && history.AllTime.Download == 275, "statistics an old disconnect cannot end the new session");

        var boundary = new UsageTracker(new(), TimeZoneInfo.Utc);
        var midnight = now.Date.AddDays(1); DateTimeOffset before = new(midnight.AddSeconds(-1), TimeSpan.Zero);
        boundary.Start(1, 0, before); boundary.End(1, 2000, before.AddSeconds(2), Counters(100, 40));
        check(boundary.Archive.Days.Count == 2 && boundary.Archive.Days[0].Totals == new UsageTotals(50, 20, 1000, 1) && boundary.Archive.Days[1].Totals == new UsageTotals(50, 20, 1000), "statistics split bytes and duration across midnight without losing remainders");
        var local = new UsageTracker(new(), TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "test+3", "test+3"));
        local.Start(1, 0, DateTimeOffset.Parse("2026-10-11T21:30:00Z"));
        check(local.Archive.Days[0].Date == new DateOnly(2026, 10, 12), "statistics calendar periods follow the device time zone");
        if (!OperatingSystem.IsWindows())
        {
            var dst = new UsageTracker(new(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
            DateTimeOffset begin = DateTimeOffset.Parse("2026-03-28T23:00:00Z");
            dst.Start(1, 0, begin); dst.End(1, 23 * 3600000, begin.AddHours(23), Counters(230, 23));
            check(dst.Archive.Days.Count == 1 && dst.Archive.AllTime.ConnectedMilliseconds == 23 * 3600000 && dst.Archive.Days[0].Totals.Download == 230, "statistics DST day uses actual monotonic duration");
        }
        var periods = new UsageTracker(new(), TimeZoneInfo.Utc);
        for (int day = 40; day >= 0; day--)
        {
            DateTimeOffset start = now.AddDays(-day); periods.Start(day, 0, start); periods.End(day, 1000, start.AddSeconds(1), Counters(100, 20));
        }
        check(periods.Archive.Days.Count == 32 && periods.Period(7, now) == new UsageTotals(700, 140, 7000, 7), "statistics last 7 calendar days include today and exclude the eighth day");
        check(periods.Period(30, now) == new UsageTotals(3000, 600, 30000, 30) && periods.Archive.AllTime.Download == 4100, "statistics month pruning keeps the all-time total");
        check(periods.Period(7, now.AddDays(50)) == new UsageTotals(), "statistics windows expire while disconnected");
        periods.Archive.Validate();
        var clock = new UsageTracker(new(), TimeZoneInfo.Utc);
        clock.Start(1, 1000, now); clock.End(1, 61000, now.AddHours(-1), Counters(60, 10));
        check(clock.Archive.AllTime.ConnectedMilliseconds == 60000, "statistics wall-clock corrections do not change connection duration");
        check(new UsageTotals(long.MaxValue - 1, 4, 5, 6).Add(new(10, long.MaxValue, long.MaxValue, long.MaxValue)) == new UsageTotals(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue), "statistics saturated counters never wrap negative");

        string path = Path.Combine(folder, "traffic-history.json");
        UsageFile.Save(path, history); var loaded = UsageFile.Load(path); var restarted = new UsageTracker(loaded, TimeZoneInfo.Utc);
        check(loaded.AllTime == history.AllTime && loaded.LastSession == history.LastSession && restarted.ActiveSession == null, "statistics reload preserves totals but never resumes a crashed session clock");
        restarted.Start(42, 1000, now.AddMinutes(2)); restarted.End(42, 3000, now.AddMinutes(2).AddSeconds(2), Counters(5, 2));
        UsageFile.Save(path, loaded);
        check(UsageFile.Load(path).AllTime == new UsageTotals(280, 122, 11000, 3), "statistics new process adds only new session bytes");
        check(!Directory.GetFiles(folder, "traffic-history.json.tmp-*").Any(), "statistics atomic save leaves no temporary files");
        string valid = File.ReadAllText(path);
        foreach (string bad in new[] { "null", "{}", "{", valid.Replace("\"version\":1", "\"version\":2"), valid.Replace("\"download\":280", "\"download\":-1"), valid.Replace("\"allTime\"", "\"unexpected\""), new string('x', UsageFile.MaxBytes + 1) })
        {
            File.WriteAllText(path, bad); bool rejected = false;
            try { UsageFile.Load(path); } catch (UserError) { rejected = true; }
            check(rejected && File.ReadAllText(path) == bad, "statistics reject malformed history and preserve the original file");
        }
        File.WriteAllText(path, valid);
        string locked = Path.Combine(folder, "statistics-target-directory"); Directory.CreateDirectory(locked);
        // A file-to-directory collision is IOException on Unix and
        // UnauthorizedAccessException on Windows; both must preserve the history.
        bool failed = false; try { UsageFile.Save(locked, loaded); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        check(failed && UsageFile.Load(path).AllTime == loaded.AllTime && !Directory.GetFiles(folder, "statistics-target-directory.tmp-*").Any(), "statistics failed atomic replacement preserves previous history and cleans temporary data");
        check(UsageFile.Load(Path.Combine(folder, "not-created-history.json")).AllTime == new UsageTotals(), "statistics first install starts with zero history");
    }
}
