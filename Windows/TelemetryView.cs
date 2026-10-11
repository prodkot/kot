using Kot.Core;
using System.Diagnostics;
namespace Kot.Windows;
public sealed partial class MainWindow
{
    readonly System.Windows.Forms.Timer telemetryTimer = new() { Interval = 2000 };
    readonly TrafficSampler trafficSampler = new();
    TrafficReading traffic = TrafficReading.Empty;
    ConnectionSnapshot? liveConnections;
    string visiblePage = "home", telemetryError = "";
    bool readingTelemetry;
    int liveSession = -1;
    UsageTracker usage = new(new UsageArchive());
    readonly string usagePath = Path.Combine(Store.Folder, "traffic-history.json");
    string statisticsError = "";
    bool statisticsWritable = true;
    long lastStatisticsSave;
    DetailedUsageTracker details = new(new DetailedArchive());
    readonly string detailsPath = Path.Combine(Store.Folder, "traffic-details.json");
    string detailsError = "";
    bool detailsWritable = true;
    long lastDetailsSave;
    sealed record SpeedPoint(DateTimeOffset Time, double? Download, double? Upload);
    readonly Queue<SpeedPoint> speedHistory = new();
    void InitTelemetry()
    {
        try { usage = new(UsageFile.Load(usagePath)); }
        catch (Exception ex)
        {
            statisticsWritable = false;
            statisticsError = "Не удалось прочитать историю. Старый файл сохранён; новый учёт ведётся только в памяти.";
            AppLog.Error("statistics load", ex);
        }
        try { details = new(DetailedUsageFile.Load(detailsPath)); }
        catch (Exception ex) { detailsWritable = false; detailsError = "Подробная история не прочитана. Старый файл сохранён; новые данные учитываются в памяти."; AppLog.Error("details load", ex); }
        tunnel.Changed += () =>
        {
            if (tunnel.State != "connected" || usage.ActiveSession == tunnel.Session) return;
            usage.Start(tunnel.Session, tunnel.ConnectedMilliseconds, tunnel.ConnectedUtc);
            details.Start(tunnel.Session, tunnel.ConnectedMilliseconds, tunnel.ConnectedUtc); speedHistory.Clear();
            SaveStatistics(true);
        };
        tunnel.SessionEnded += (session, milliseconds, utc, counters) =>
        {
            usage.End(session, milliseconds, utc, counters); details.End(session, milliseconds, utc, counters); SaveStatistics(true);
        };
        telemetryTimer.Tick += async (_, _) =>
        {
            if (readingTelemetry || exiting) return;
            readingTelemetry = true;
            try
            {
                if (tunnel.State == "connected")
                {
                    int session = tunnel.Session; var result = await tunnel.ReadConnections(lifetime.Token);
                    if (result != null && session == tunnel.Session)
                    {
                        liveSession = session; liveConnections = result; traffic = trafficSampler.Sample(session, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, result); telemetryError = "";
                        long sampledAt = Tunnel.MonotonicMilliseconds; DateTimeOffset sampledUtc = DateTimeOffset.UtcNow;
                        usage.Observe(session, sampledAt, sampledUtc, result);
                        details.Observe(session, sampledAt, sampledUtc, result);
                    }
                    else ResetTelemetry();
                }
                else ResetTelemetry();
            }
            catch (OperationCanceledException) { if (!exiting) TelemetryFailure(); }
            catch (Exception ex) { if (telemetryError.Length == 0) AppLog.Error("telemetry", ex); TelemetryFailure(); }
            finally
            {
                readingTelemetry = false;
                if (!exiting)
                {
                    if (tunnel.State == "connected") usage.Observe(tunnel.Session, Tunnel.MonotonicMilliseconds, DateTimeOffset.UtcNow);
                    if (tunnel.State == "connected") { speedHistory.Enqueue(new(DateTimeOffset.UtcNow, traffic.DownloadRate, traffic.UploadRate)); while (speedHistory.Count > 90) speedHistory.Dequeue(); }
                    SaveStatistics(); Snapshot(); ScheduleUpdateCheck();
                }
            }
        };
        telemetryTimer.Start();
    }
    void ResetTelemetry() { trafficSampler.Reset(); traffic = TrafficReading.Empty; liveConnections = null; telemetryError = ""; }
    void TelemetryFailure() { trafficSampler.Reset(); traffic = TrafficReading.Empty; liveConnections = null; telemetryError = "Статистика временно недоступна."; }
    void SaveStatistics(bool force = false)
    {
        long now = Tunnel.MonotonicMilliseconds;
        if (detailsWritable && (force || now - lastDetailsSave >= 10000 && usage.ActiveSession != null))
        {
            lastDetailsSave = now;
            try { DetailedUsageFile.Save(detailsPath, details.Archive); detailsError = ""; }
            catch (Exception ex) { if (detailsError.Length == 0) AppLog.Error("details save", ex); detailsError = "Подробные счётчики работают, но история не сохранена на диск."; }
        }
        if (!statisticsWritable || !force && (usage.ActiveSession == null && statisticsError.Length == 0 || now - lastStatisticsSave < 10000)) return;
        lastStatisticsSave = now;
        try { UsageFile.Save(usagePath, usage.Archive); statisticsError = ""; }
        catch (Exception ex)
        {
            if (statisticsError.Length == 0) AppLog.Error("statistics save", ex);
            statisticsError = "История не сохранена на диск. Текущие счётчики продолжают работать.";
        }
    }
    object StatisticsModel()
    {
        bool running = tunnel.State == "connected" && usage.ActiveSession == tunnel.Session;
        var archive = usage.Archive; DateTimeOffset now = DateTimeOffset.UtcNow;
        object Totals(UsageTotals value) => new { value.Download, value.Upload, connectedSeconds = value.ConnectedMilliseconds / 1000d, value.Sessions };
        return new
        {
            running, elapsedSeconds = running ? Math.Max(0, Tunnel.MonotonicMilliseconds - tunnel.ConnectedMilliseconds) / 1000d : (archive.LastSession?.Totals.ConnectedMilliseconds ?? 0) / 1000d,
            session = Totals(archive.LastSession?.Totals ?? new()), week = Totals(usage.Period(7, now)), month = Totals(usage.Period(30, now)), allTime = Totals(archive.AllTime),
            archive.FirstRecorded, started = archive.LastSession?.Started, ended = archive.LastSession?.Ended,
            error = statisticsError, warning = running ? telemetryError : "",
            detail = visiblePage == "statistics" ? new
            {
                details.Archive.FirstRecorded, error = detailsError, session = details.Archive.Session.Entries, week = details.Period(7, now).Entries, month = details.Period(30, now).Entries, allTime = details.Archive.AllTime.Entries,
                days = archive.Days.OrderBy(d => d.Date).Select(d => new { date = d.Date.ToString("yyyy-MM-dd"), d.Totals.Download, d.Totals.Upload }), speeds = speedHistory.ToArray()
            } : null
        };
    }
    object TelemetryModel()
    {
        var value = tunnel.State == "connected" && liveSession == tunnel.Session ? traffic : TrafficReading.Empty;
        return new
        {
            value.Available, value.DownloadRate, value.UploadRate, value.DownloadTotal, value.UploadTotal, value.Count,
            error = tunnel.State == "connected" ? telemetryError : "",
            connections = visiblePage == "logs" && liveSession == tunnel.Session && tunnel.State == "connected" && liveConnections != null ? liveConnections.Connections.Select(c => new
            { c.Id, c.Destination, c.Network, c.Inbound, c.Process, c.Download, c.Upload, c.Start, rule = AppLog.Clean(c.Rule), chains = c.Chains.Select(RouteLabel).ToArray() }).ToArray() : null
        };
    }
    string RouteLabel(string tag)
    {
        if (tag == "direct") return "Напрямую";
        if (tag == "proxy") return profile.Selected == "auto" ? "Авто" : profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Name ?? "Туннель";
        if (tag.StartsWith("node-", StringComparison.Ordinal)) return profile.Nodes.FirstOrDefault(n => n.Id == tag[5..])?.Name ?? "Туннель";
        return AppLog.Clean(tag);
    }
}
