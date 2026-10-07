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
    void InitTelemetry()
    {
        telemetryTimer.Tick += async (_, _) =>
        {
            if (readingTelemetry || exiting || !bridgeReady) return;
            readingTelemetry = true;
            try
            {
                if (tunnel.State == "connected")
                {
                    int session = tunnel.Session; var result = await tunnel.ReadConnections(lifetime.Token);
                    if (result != null && session == tunnel.Session)
                    { liveSession = session; liveConnections = result; traffic = trafficSampler.Sample(session, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, result); telemetryError = ""; }
                    else ResetTelemetry();
                }
                else ResetTelemetry();
            }
            catch (OperationCanceledException) { if (!exiting) TelemetryFailure(); }
            catch (Exception ex) { if (telemetryError.Length == 0) AppLog.Error("telemetry", ex); TelemetryFailure(); }
            finally { readingTelemetry = false; if (!exiting) { Snapshot(); ScheduleUpdateCheck(); } }
        };
        telemetryTimer.Start();
    }
    void ResetTelemetry() { trafficSampler.Reset(); traffic = TrafficReading.Empty; liveConnections = null; telemetryError = ""; }
    void TelemetryFailure() { trafficSampler.Reset(); traffic = TrafficReading.Empty; liveConnections = null; telemetryError = "Статистика временно недоступна."; }
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
