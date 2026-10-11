using Kot.Core;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    sealed record DiagnosticStep(string Name, string Status, string Detail, long? Ms = null);
    readonly List<DiagnosticStep> diagnosticSteps = [];
    readonly List<int?> qualitySamples = [];
    CancellationTokenSource? toolsAttempt;
    Task toolsRunning = Task.CompletedTask;
    string toolsKind = "", toolsStatus = "", toolsError = "", toolsServer = "";
    QualitySummary? qualitySummary;
    DownloadSample? qualityDownload;
    string qualitySpeedError = "";
    long qualityBytes;
    object ToolsModel() => new { busy = toolsAttempt != null, kind = toolsKind, status = toolsStatus, error = toolsError, server = toolsServer, steps = diagnosticSteps.ToArray(), samples = qualitySamples.ToArray(), quality = qualitySummary, download = qualityDownload, speedError = qualitySpeedError, bytes = qualityBytes };
    Node ToolNode(string id = "")
    {
        bool automatic = id.Length == 0 || id == "auto";
        if (automatic) id = profile.Selected == "auto" ? tunnel.AutomaticNode : profile.Selected;
        return profile.Nodes.FirstOrDefault(n => n.Id == id) ?? (automatic ? profile.Nodes.FirstOrDefault() : null) ?? throw new UserError(profile.Nodes.Count == 0 ? "Сначала добавьте серверы." : "Сервер уже удалён. Выберите другой.");
    }
    void StartTools(string kind, JsonElement data)
    {
        if (toolsAttempt != null) throw new UserError("Дождитесь проверки или остановите её.");
        var node = ToolNode(data.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "");
        Uri website = ConnectionTools.Website(data.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "https://www.gstatic.com/generate_204");
        diagnosticSteps.Clear(); qualitySamples.Clear(); qualitySummary = null; qualityDownload = null; qualityBytes = 0; qualitySpeedError = "";
        toolsKind = kind; toolsStatus = "Подготовка проверки"; toolsError = "";
        int activePort = kind == "diagnostics" && tunnel.State == "connected" ? tunnel.ProxyPort : 0;
        if (activePort > 0) node = ToolNode();
        toolsServer = activePort > 0 ? "Текущее VPN-соединение" : node.Name;
        var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); toolsAttempt = owner;
        toolsRunning = RunTools(kind, node, website, activePort, owner);
    }
    async Task RunTools(string kind, Node node, Uri website, int activePort, CancellationTokenSource owner)
    {
        ProbeCore? probe = null; var ct = owner.Token;
        async Task Step(string name, Func<Task<string>> check)
        {
            ct.ThrowIfCancellationRequested(); int index = diagnosticSteps.Count; diagnosticSteps.Add(new(name, "checking", "Проверка")); toolsStatus = name; Snapshot(); var watch = Stopwatch.StartNew();
            try { string detail = await check(); diagnosticSteps[index] = new(name, "ok", detail, watch.ElapsedMilliseconds); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { diagnosticSteps[index] = new(name, "cancelled", "Отменено"); throw; }
            catch (Exception ex) { AppLog.Error("diagnostic " + name, ex); diagnosticSteps[index] = new(name, "error", ConnectionTools.Explain(ex), watch.ElapsedMilliseconds); }
            Snapshot();
        }
        try
        {
            if (kind == "diagnostics")
            {
                string host = node.Outbound["server"]!.ToString();
                await Step("DNS Windows для сервера", async () => { using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(5000); var ips = await Dns.GetHostAddressesAsync(host, deadline.Token); return ips.Length > 0 ? string.Join(", ", ips.Take(4).Select(x => x.ToString())) : throw new UserError("DNS не вернул адрес сервера."); });
                if (node.Protocol == "hysteria2") diagnosticSteps.Add(new("Порт сервера", "skip", "Hysteria2 использует UDP. Доступность проверяется запросами через сервер."));
                else await Step("TCP-порт сервера", async () => { int ms = await Latency.Tcp(host, node.Outbound["server_port"]!.GetValue<int>(), 5000, ct); return "Порт доступен · " + ms + " мс"; });
            }
            if (activePort == 0) { toolsStatus = "Запуск проверки сервера"; Snapshot(); probe = await ProbeCore.Start(node, ct); activePort = probe.Port; }
            using var client = ConnectionTools.Client(activePort);
            if (kind == "diagnostics")
            {
                await Step("DNS через VPN (Google DoH)", () => ConnectionTools.DnsThroughVpn(client, website.IdnHost, ct));
                await Step("Внешний IP через VPN (ipify)", () => ConnectionTools.PublicIp(client, ct));
                await Step("Сайт через VPN", async () => { int ms = await Latency.Http(website.AbsoluteUri, activePort, 8000, ct); return website.IdnHost + " доступен · " + ms + " мс"; });
                toolsStatus = diagnosticSteps.Any(s => s.Status == "error") ? "Проверка завершена: есть ошибки" : "Все проверки прошли";
            }
            else
            {
                for (int i = 0; i < 10; i++)
                {
                    ct.ThrowIfCancellationRequested(); toolsStatus = $"HTTP-проверка {i + 1} из 10"; Snapshot();
                    try { qualitySamples.Add(await Latency.Http(website.AbsoluteUri, activePort, 5000, ct)); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { qualitySamples.Add(null); AppLog.Error("quality sample", ex); }
                    qualitySummary = ConnectionTools.Summarize(qualitySamples); Snapshot(); if (i < 9) await Task.Delay(250, ct);
                }
                toolsStatus = "Загрузка 1 МиБ через сервер"; Snapshot(); long progressAt = 0;
                try { qualityDownload = await ConnectionTools.Download(client, ct, bytes => { qualityBytes = bytes; long now = Tunnel.MonotonicMilliseconds; if (now - progressAt >= 250) { progressAt = now; Snapshot(); } }); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { qualitySpeedError = ConnectionTools.Explain(ex); AppLog.Error("quality download", ex); }
                toolsStatus = "Проверка качества завершена";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { toolsStatus = "Проверка отменена"; }
        catch (Exception ex) { AppLog.Error("connection tools", ex); toolsError = ConnectionTools.Explain(ex); toolsStatus = "Не удалось завершить проверку"; }
        finally
        {
            if (probe != null) await probe.DisposeAsync();
            if (toolsAttempt == owner) toolsAttempt = null; owner.Dispose(); if (!exiting) Snapshot();
        }
    }
    async Task StopTools() { toolsAttempt?.Cancel(); await toolsRunning; }
    void NotifySubscriptionExpiry(DateTimeOffset now)
    {
        foreach (var s in profile.Subscriptions)
        {
            int? days = s.Usage?.DaysLeft(now); if (days == null || days > 3) continue;
            string marker = s.Usage!.Expires!.Value.ToUnixTimeSeconds() + ":" + (days <= 0 ? "expired" : "soon");
            if (s.ExpiryNotice == marker) continue;
            s.ExpiryNotice = marker;
            tray.ShowBalloonTip(6000, "Подписка " + s.Name, days <= 0 ? "Срок подписки закончился." : "Подписка заканчивается через " + days + " дн.", ToolTipIcon.Info);
            Store.Save(profile);
        }
    }
    object? SubscriptionUsageModel()
    {
        var u = profile.SubscriptionUsage; if (u == null) return null;
        return new { u.Upload, u.Download, u.Total, u.Used, u.Remaining, u.Expires, u.CheckedAt, daysLeft = u.DaysLeft(DateTimeOffset.UtcNow) };
    }
}
