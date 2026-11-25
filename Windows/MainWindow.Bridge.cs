using Kot.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    void Send(object value)
    {
        if (exiting || !bridgeReady || web.IsDisposed || web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, Json)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { RecoverBrowser("Связь с интерфейсом потеряна."); }
    }
    string ConnectionError()
    {
        if (tunnel.Error.Length > 0 && tunnel.Error != SavedSubscription.ConfirmConnection) return tunnel.Error;
        if (connectionNotice.Length > 0) return connectionNotice;
        return tunnel.State == "idle" && profile.CurrentSubscription?.RequiresConnectionConfirmation == true
            ? SavedSubscription.ConfirmConnection : "";
    }
    object Model() => new
    {
        name = profile.Name, hasSubscription = profile.Subscriptions.Count > 0, localSubscription = profile.Local, subscriptionUsage = SubscriptionUsageModel(), tools = ToolsModel(),
        nodes = profile.Nodes.Select(n => new { n.Id, n.Name, n.Protocol, providerRouting = n.FullConfig != null, ping = ping.Get(n.Id), code = n.Protocol == "xray" ? "XR" : n.Protocol == "shadowsocks" ? "SS" : n.Protocol == "hysteria2" ? "HY" : n.Protocol == "trojan" ? "TR" : n.Protocol == "vmess" ? "VM" : "VL" }),
        subscriptions = profile.Subscriptions.Select(s => new { s.Id, s.Name, count = s.Nodes.Count }), activeSubscription = profile.ActiveSubscription,
        services = services.Model(visiblePage == "services"), telemetry = TelemetryModel(), statistics = StatisticsModel(), journal = visiblePage == "logs" ? AppLog.Tail() : null, updates = UpdateModel(),
        activeNode = tunnel.SessionNode is { } active ? new { active.Id, active.Name } : null,
        nextNode = profile.ManualConnectionNode?.Id,
        needsSelection = profile.CurrentSubscription?.RequiresConnectionConfirmation == true,
        favorites = profile.Favorites, automaticNode = tunnel.AutomaticNode, automation = profile.Automation,
        routing = RoutingModel(), geoIp = GeoIpModel(), backgroundError = string.Join('\n', new[] { backgroundError, routingErrors.Values.FirstOrDefault(), geoIpError }.Where(s => !string.IsNullOrEmpty(s))),
        selected = profile.Selected, state = tunnel.State == "idle" && reconnect.Desired ? "waiting" : tunnel.State, error = ConnectionError(),
        settings = new { profile.Theme, mode = profile.ConnectionMode, profile.Startup, profile.AutoConnect, profile.Tray, profile.SendHwid, profile.KillSwitch, killSwitchActive = KillSwitch.Active, bypass = string.Join('\n', profile.Bypass) },
        ping = new { busy = ping.Busy, done = ping.Done, total = ping.Total, error = ping.Error, settings = profile.Ping },
        updated = profile.Updated?.ToLocalTime().ToString("dd.MM HH:mm"), warnings, version = ClientIdentity.Version, buildNumber = ClientIdentity.BuildNumber, displayVersion = ClientIdentity.DisplayVersion
    };
    void Snapshot()
    {
        UpdateTray();
        var data = Model();
        if (browserLoaded) Send(new { kind = "snapshot", data });
        else Send(new { kind = "snapshot", data, startupOriginMs = loading.AnimationOriginMs });
    }
    async Task Message(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (exiting || e.Source != "https://kot.local/index.html") return;
        int id = 0;
        int generation = browserGeneration;
        void Reply(object value) { if (generation == browserGeneration) Send(value); }
        try
        {
            string raw = e.WebMessageAsJson;
            if (raw.Length > 300_000) throw new UserError("Слишком большой запрос интерфейса.");
            using var d = JsonDocument.Parse(raw); var root = d.RootElement;
            id = root.GetProperty("id").GetInt32(); string action = root.GetProperty("action").GetString() ?? "";
            JsonElement data = root.GetProperty("data");
            if (action == "ready")
            {
                interfaceStarted = true; bridgeReady = true; Snapshot(); Reply(new { kind = "reply", id, ok = true });
                if (pendingTrayPage is { } page) { Send(new { kind = "navigate", page }); pendingTrayPage = null; }
                return;
            }
            if (!bridgeReady) return;
            switch (action)
            {
                case "heartbeat": browserDeadline = DateTimeOffset.UtcNow.AddSeconds(45); Reply(new { kind = "reply", id, ok = true }); return;
                case "uiReady": BrowserReady(); break;
                case "drag": if (data.GetProperty("double").GetBoolean()) ToggleMaximize(); else { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } break;
                case "resize":
                    var hit = data.GetProperty("edge").GetString() switch { "w" => 10, "e" => 11, "n" => 12, "nw" => 13, "ne" => 14, "s" => 15, "sw" => 16, "se" => 17, _ => 0 };
                    if (hit != 0 && WindowState == FormWindowState.Normal) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)hit, IntPtr.Zero); } break;
                case "minimize": MinimizeWindow(); break;
                case "maximize": ToggleMaximize(); break;
                case "close": Close(); break;
                case "toggle": await ToggleConnection(); break;
                case "view":
                    string page = data.GetProperty("page").GetString() ?? ""; if (page is not ("home" or "servers" or "statistics" or "diagnostics" or "logs" or "services" or "settings")) throw new UserError("Неизвестная страница."); visiblePage = page;
                    if (page == "settings") {
                        await operations.WaitAsync(lifetime.Token);
                        try {
                            bool enabled = await Startup.IsEnabled();
                            if (!exiting && profile.Startup != enabled) SaveProfile(p => p.Startup = enabled);
                        } finally { operations.Release(); }
                    }
                    break;
                case "serviceAction": case "serviceCancel": case "serviceSettings": case "serviceUpdateSettings": case "serviceRotateSecret":
                case "serviceSource": case "serviceCopyLink": case "serviceOpenTelegram": case "serviceCopyResults": case "serviceExportResults": case "serviceClearLog": case "serviceSelectTest":
                    await ServiceMessage(action, data); break;
                case "runDiagnostics": StartTools("diagnostics", data); break;
                case "testConnection": StartConnectionCheck(); break;
                case "readSubscription":
                    var subscription = profile.Subscriptions.FirstOrDefault(s => s.Id == data.GetProperty("id").GetString()) ?? throw new UserError("Подписка уже удалена.");
                    Reply(new { kind = "reply", id, ok = true, data = new { subscription.Id, subscription.Name, subscription.Address, subscription.Local } }); return;
                case "cancelTools": toolsAttempt?.Cancel(); break;
                case "readQrFile": case "readQrClipboard":
                    string? qrText = await ReadQr(action == "readQrClipboard");
                    Reply(new { kind = "reply", id, ok = true, data = new { text = qrText } }); return;
                case "readNodeJson": case "copyNodeJson":
                    // Keep exception details, values and clipboard contents outside the log/diagnostic path.
                    try
                    {
                        var nodeId = data.GetProperty("id").GetString();
                        if (data.GetProperty("subscriptionId").GetString() != profile.ActiveSubscription ||
                            profile.Nodes.FirstOrDefault(n => n.Id == nodeId) is not { } jsonNode)
                        {
                            Reply(new { kind = "reply", id, ok = false, error = "Сервер уже недоступен." }); return;
                        }
                        bool full = data.GetProperty("full").GetBoolean();
                        var view = NodeJsonView.Read(jsonNode, data.GetProperty("view").GetString() ?? "", full);
                        if (action == "copyNodeJson")
                        {
                            if (!view.TryCopy(data.GetProperty("revision").GetString(), Clipboard.SetText))
                            {
                                Reply(new { kind = "reply", id, ok = false, error = "Конфигурация изменилась. Откройте её заново." }); return;
                            }
                            Reply(new { kind = "reply", id, ok = true }); return;
                        }
                        Reply(new { kind = "reply", id, ok = true, data = view }); return;
                    }
                    catch (Exception)
                    {
                        Reply(new { kind = "reply", id, ok = false, error = action == "copyNodeJson"
                            ? "Не удалось скопировать JSON." : "Не удалось прочитать outbound из сохранённых данных." }); return;
                    }
                case "readNodeQr":
                    Reply(new { kind = "reply", id, ok = true, data = ReadShareQr(data) }); return;
                case "copyNodeLink": case "copyNodeQr": case "saveNodeQr": ExportShareQr(data, action); break;
                case "checkUpdates": await CheckUpdates(); break;
                case "cancelUpdate": updateAttempt?.Cancel(); break;
                case "cancelImport": importAttempt?.Cancel(); break;
                case "pingAll": lastPing = DateTimeOffset.UtcNow; ping.Start(profile.Nodes.ToList(), profile.Ping); break;
                case "pingNode":
                    var node = profile.Nodes.FirstOrDefault(n => n.Id == data.GetProperty("id").GetString()) ?? throw new UserError("Сервер уже удалён.");
                    ping.StartNode(node, profile.Ping); break;
                case "cancelPing": lastPing = DateTimeOffset.UtcNow; ping.Cancel(); break;
                case "readHwid":
                    var device = DeviceIdentity.Get();
                    Reply(new { kind = "reply", id, ok = true, data = new { hwid = device.Hwid, name = device.Model } });
                    return;
                case "copyHwid":
                    Clipboard.SetText(DeviceIdentity.Get().Hwid); break;
                case "copyLog":
                    Clipboard.SetText(FullLog()); break;
                case "copyDiagnostics": Clipboard.SetText(JsonSerializer.Serialize(ToolsModel(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true })); break;
                case "diagnostics":
                    Clipboard.SetText(JsonSerializer.Serialize(new { app = "kot. " + ClientIdentity.DisplayVersion, build = ClientIdentity.Build, buildNumber = ClientIdentity.BuildNumber, core = XrayRuntime.Description, os = Environment.OSVersion.VersionString, hwidEnabled = profile.SendHwid, state = tunnel.State, protocol = tunnel.SessionNode?.Protocol ?? profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Protocol, nodeCount = profile.Nodes.Count, updated = profile.Updated, lastError = tunnel.Error, coreExitCode = tunnel.CoreExitCode, coreLog = tunnel.CoreDetails, skipped = warnings.Count }, new JsonSerializerOptions { WriteIndented = true })); break;
                default:
                    await operations.WaitAsync(lifetime.Token);
                    string? notice;
                    try { notice = exiting ? null : await Mutate(action, data); } finally { operations.Release(); }
                    if (notice != null)
                    {
                        Snapshot(); Reply(new { kind = "reply", id, ok = true, data = new { selectionNotice = notice } }); return;
                    }
                    break;
            }
            Snapshot(); Reply(new { kind = "reply", id, ok = true });
        }
        catch (Exception ex) { AppLog.Error("UI request", ex); Reply(new { kind = "reply", id, ok = false, error = Store.Friendly(ex) }); Snapshot(); }
    }
    string FullLog()
    {
        var header = new
        {
            app = "kot. " + ClientIdentity.DisplayVersion, build = ClientIdentity.Build, buildNumber = ClientIdentity.BuildNumber, core = XrayRuntime.Description, os = Environment.OSVersion.VersionString,
            hwidEnabled = profile.SendHwid, state = tunnel.State,
            protocol = tunnel.SessionNode?.Protocol ?? profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected)?.Protocol,
            nodeCount = profile.Nodes.Count, updated = profile.Updated, lastError = tunnel.Error,
            coreExitCode = tunnel.CoreExitCode, coreLog = tunnel.CoreDetails, skipped = warnings.Count,
            traffic, telemetryError, subscriptions = profile.Subscriptions.Count, automatic = profile.Selected == "auto", automation = profile.Automation, backgroundError, reconnectDesired = reconnect.Desired, retryFailures = reconnect.Failures,
            ping = new { busy = ping.Busy, done = ping.Done, total = ping.Total, mode = profile.Ping.Mode, timeoutMs = profile.Ping.TimeoutMs, attempts = profile.Ping.Attempts, parallelism = profile.Ping.Parallelism }
        };
        AppLog.Write("app", "log copied");
        return AppLog.Clean("kot. - полный журнал текущего запуска\n" +
            JsonSerializer.Serialize(header, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) +
            "\n\n" + AppLog.Snapshot());
    }
}
