using Kot.Core;
using Kot.Windows;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.WindowTests;

static class SubscriptionRefreshChecks
{
    sealed class Feed(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    const string SelectedRow = "document.querySelector('.server-card[aria-pressed=true]')?.closest('[data-node]').dataset.node";

    static void WaitFor(Func<bool> ready, string operation, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException(operation + "\n" + AppLog.Snapshot());
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
    static string Evaluate(MainWindow app, string js, CancellationToken ct)
    {
        var task = app.InterfaceCore.ExecuteScriptAsync(js);
        WaitFor(() => task.IsCompleted, "WebView script", ct);
        return task.GetAwaiter().GetResult();
    }

    public static void CheckRestart(string folder, string subscription, string selected, string healthUrl, Action<bool, string> check)
    {
        var disk = Store.Persistence(folder);
        var tunnel = new Tunnel([healthUrl], TimeSpan.FromSeconds(10));
        using var window = new MainWindow(false, false, null, tunnel, disk, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            window.Show();
            WaitFor(() => window.InterfaceReady, "restarted Windows UI", timeout.Token);
            WaitFor(() => Evaluate(window, SelectedRow, timeout.Token) == JsonSerializer.Serialize(selected),
                "restored selected row", timeout.Token);
            var saved = disk.Load();
            check(saved.ActiveSubscription == subscription && saved.Selected == selected &&
                saved.Subscriptions.Single(s => s.Id == subscription).Selected == selected,
                "application restarted in a new process: persisted choice and UI");
            check(!tunnel.HasCore && tunnel.SessionNode == null && tunnel.State == "idle",
                "application restarted in a new process: idle connection");
            Evaluate(window, "document.getElementById('powerButton').click()", timeout.Token);
            WaitFor(() => tunnel.State == "connected", "reconnect after application restart", timeout.Token);
            var probe = tunnel.Probe(timeout.Token);
            WaitFor(() => probe.IsCompleted, "HTTP through restarted Windows TUN", timeout.Token);
            check(tunnel.SessionNode?.Id == selected && probe.GetAwaiter().GetResult(),
                "new application process connects the restored target through Xray");
        }
        finally
        {
            var closed = window.CloseForTests();
            WaitFor(() => closed.IsCompleted, "restart fixture shutdown", CancellationToken.None);
            closed.GetAwaiter().GetResult();
        }
    }

    public static Task Run(string germany, string france, string extra, string healthUrl, Action<bool, string> check, CancellationToken ct)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            try { Exercise(germany, france, extra, healthUrl, check, ct); finished.SetResult(); }
            catch (Exception ex) { finished.SetException(ex); }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        return finished.Task;
    }

    static void Exercise(string germany, string france, string extra, string healthUrl, Action<bool, string> check, CancellationToken ct)
    {
        string folder = Directory.CreateTempSubdirectory("kot-selection-").FullName;
        var disk = Store.Persistence(folder);
        string Payload(params string[] links)
        {
            var configs = new JsonArray();
            foreach (string link in links)
            {
                var node = Subscriptions.ParseNode(link);
                configs.Add(new JsonObject
                {
                    ["remarks"] = node.Name, ["inbounds"] = new JsonArray(), ["outbounds"] = new JsonArray(XrayConfig.Outbound(node)),
                    ["dns"] = new JsonObject { ["hosts"] = new JsonObject { [new Uri(healthUrl).Host] = "192.0.2.123" }, ["servers"] = new JsonArray("https://1.1.1.1/dns-query") }
                });
            }
            return configs.ToJsonString();
        }
        var de = Subscriptions.Parse(Payload(germany)).Nodes.Single();
        var fr = Subscriptions.Parse(Payload(france)).Nodes.Single();
        var first = new SavedSubscription { Address = "https://one.example/feed", Nodes = [fr, de], Selected = fr.Id };
        var second = new SavedSubscription { Address = "https://two.example/feed", Updated = DateTimeOffset.UtcNow, Nodes = [de, fr], Selected = fr.Id };
        var initial = new Profile { ConnectionMode = "tun", SendHwid = false, Subscriptions = [first, second] };
        initial.Activate(first.Id);
        initial.Services.CheckUpdates = false;
        initial.Automation.RefreshHours = 0;
        var tunnel = new Tunnel([healthUrl], TimeSpan.FromSeconds(10));
        string body = Payload(france, germany), otherBody = body;
        bool fail = false;
        TaskCompletionSource? entered = null, release = null;
        HttpMessageHandler Transport() => new Feed(async (request, token) =>
        {
            entered?.TrySetResult();
            if (release != null) await release.Task.WaitAsync(token);
            return new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.Host == "one.example" ? body : otherBody) };
        });
        MainWindow Open() => new(false, false, null, tunnel, disk, Transport);
        MainWindow? app = null;
        void Until(Func<bool> ready, string operation) => WaitFor(ready, operation, ct);
        string Script(string js) => Evaluate(app!, js, ct);
        void Show()
        {
            app = Open(); app.Show();
            Until(() => app.InterfaceReady, "Windows UI startup");
            Script("window.qaReplies={};chrome.webview.addEventListener('message',e=>{if(e.data.kind==='reply')qaReplies[e.data.id]=e.data;else if(e.data.kind==='snapshot')window.qaModel=e.data.data})");
        }
        void Close()
        {
            if (app == null) return;
            var closed = app.CloseForTests();
            Until(() => closed.IsCompleted, "Windows UI shutdown"); closed.GetAwaiter().GetResult();
            app.Dispose(); app = null;
        }
        int serial = 0;
        int Post(string action, object data)
        {
            int id = --serial;
            Script("chrome.webview.postMessage(" + JsonSerializer.Serialize(new { id, action, data }) + ")");
            return id;
        }
        JsonElement Reply(int id, bool ok = true)
        {
            string key = $"qaReplies[{id}]";
            Until(() => Script("Boolean(" + key + ")") == "true", "bridge reply " + id);
            using var json = JsonDocument.Parse(Script(key));
            var result = json.RootElement.Clone();
            if (result.GetProperty("ok").GetBoolean() != ok) throw new Exception(result.ToString());
            return result;
        }
        int Update(string subscriptionId) => Post("refresh", new { subscriptionId });
        void State(string scenario, string selected, string live, int session)
        {
            var saved = disk.Load();
            string? row = saved.Nodes.Any(n => n.Id == selected) ? selected : null;
            Until(() => Script(SelectedRow + " ?? null") == JsonSerializer.Serialize(row), scenario + ": selected row");
            check(saved.ActiveSubscription == first.Id && saved.Selected == selected &&
                saved.Subscriptions.Single(s => s.Id == first.Id).Selected == selected, scenario + ": persisted choice and UI");
            string? expectedNext = saved.Nodes.Any(n => n.Id == live) ? live : null;
            check(Xhttp.ConnectionNodes(saved).SingleOrDefault()?.Id == expectedNext, scenario + ": next connection from disk");
            Until(() => tunnel.State == "connected", scenario + ": connection ready");
            var probe = tunnel.Probe(ct); Until(() => probe.IsCompleted, "HTTP probe");
            check(tunnel.HasCore && tunnel.Session == session && tunnel.SessionNode?.Id == live && probe.GetAwaiter().GetResult(),
                scenario + ": same live Xray session carries HTTP through Windows TUN");
        }
        try
        {
            first.ResumeNodeId = de.Id;
            disk.Save(initial); Show();
            Script("document.querySelector('[data-page=servers]').click()");
            Reply(Post("toggle", new { }));
            Until(() => tunnel.State == "connected", "resume Germany with France highlighted");
            State("power on with a saved resume target", fr.Id, de.Id, tunnel.Session);
            int previousSession = tunnel.Session;
            // Inspect the inactive row while the other connection is carrying traffic.
            var storedBeforeInspection = JsonSerializer.Serialize(disk.Load());
            var expected = NodeJsonView.Read(fr, "xray", true);
            Script($"document.querySelector('[data-node=\"{fr.Id}\"] .qr-node').click(); nodeMenuJson.click()");
            Script("document.querySelector('[data-json-view=xray]').click()");
            Until(() => JsonSerializer.Deserialize<string>(Script("nodeJsonCode.textContent")) == expected.Text, "full Xray outbound rendered immediately");
            try
            {
                Clipboard.SetText("fixture");
                Script("copyNodeJson.click()");
                Until(() => Clipboard.GetText() == expected.Text, "full Windows clipboard without another prompt");
                Script("document.querySelector('[data-json-view=parsed]').click()");
                var parsed = NodeJsonView.Read(fr, "parsed", true);
                Until(() => JsonSerializer.Deserialize<string>(Script("nodeJsonCode.textContent")) == parsed.Text, "full parsed outbound after switching");
                Script("copyNodeJson.click()");
                Until(() => Clipboard.GetText() == parsed.Text, "full parsed Windows clipboard");
            }
            finally { Clipboard.Clear(); Script("closeNodeJson.click()"); }
            check(storedBeforeInspection == JsonSerializer.Serialize(disk.Load()), "JSON inspection does not save or change the profile");
            string sharedSnapshot = Script("JSON.stringify(qaModel)");
            string journal = AppLog.Snapshot();
            var credential = fr.FullConfig!["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]![0]!["id"]!.GetValue<string>();
            check(!sharedSnapshot.Contains(credential, StringComparison.OrdinalIgnoreCase) &&
                !journal.Contains(credential, StringComparison.OrdinalIgnoreCase) &&
                !sharedSnapshot.Contains("streamSettings") && !sharedSnapshot.Contains("outbounds"),
                "JSON inspection leaves credentials outside common snapshots and logs");
            State("inspecting another server leaves routing and selected row intact", fr.Id, de.Id, previousSession);
            Reply(Post("toggle", new { }));
            Until(() => tunnel.State == "idle", "manual stop");
            Reply(Post("toggle", new { }));
            Until(() => tunnel.State == "connected", "manual reconnect");
            check(tunnel.Session != previousSession, "manual reconnect created a new session");
            State("manual reconnect with France highlighted", fr.Id, de.Id, tunnel.Session);
            Reply(Post("toggle", new { }));

            // Use the server card for the initial choice, including its actual event handler.
            Script("document.querySelector('[data-page=servers]').click()");
            Script($"document.querySelector('[data-node=\"{de.Id}\"] .server-card').click()");
            Until(() => disk.Load().Selected == de.Id, "manual selection");
            Reply(Post("toggle", new { }));
            Until(() => tunnel.State == "connected", "initial Xray connection");
            int session = tunnel.Session;
            Reply(Post("automationSettings", new { refreshHours = 1 }));
            Until(() => disk.Load().Updated.HasValue, "scheduled refresh committed");
            State("background refresh with active VPN", de.Id, de.Id, session);
            Reply(Post("automationSettings", new { refreshHours = 0 }));
            void Refresh(string scenario, params string[] links)
            {
                body = Payload(links);
                var reply = Reply(Update(first.Id));
                check(!reply.TryGetProperty("data", out _), scenario + ": no selection notice");
                State(scenario, de.Id, de.Id, session);
            }
            Refresh("unchanged feed with active VPN", france, germany);
            Refresh("reordered feed", germany, france);
            Refresh("new server at start", extra, france, germany);
            Refresh("unselected server removed", france, germany);
            string berlin = germany.Replace("#Germany", "#Berlin");
            Refresh("selected server renamed", france, berlin);
            string same = france.Replace("#France", "#Berlin");
            Refresh("same display name for two servers", same, berlin);

            // Concurrent updates target explicit subscriptions, including identical raw node IDs.
            otherBody = Payload(berlin, same);
            int a = Update(second.Id), b = Update(first.Id);
            Reply(a); Reply(b);
            check(disk.Load().Subscriptions.Single(s => s.Id == second.Id).Selected == fr.Id, "inactive subscription retained its own choice");
            State("concurrent subscriptions", de.Id, de.Id, session);

            fail = true; Reply(Update(first.Id), ok: false); fail = false;
            State("failed download", de.Id, de.Id, session);

            body = Payload(france, extra.Replace("#Austria", "#Berlin"));
            Script("document.querySelector('[data-page=servers]').click();document.getElementById('refreshButton').click()");
            Until(() => Script("document.getElementById('toast').textContent.includes('Berlin') && document.getElementById('toast').textContent.includes('выберите сервер')") == "true",
                "removal notice requests a choice");
            State("active server replaced under the same name", de.Id, de.Id, session);
            check(Script("document.getElementById('selectedCity').textContent.includes('Germany')") == "true", "home identifies the retained live endpoint");
            body = Payload(france, berlin);
            Reply(Update(first.Id));
            State("renamed endpoint returns to the same live tunnel", de.Id, de.Id, session);
            body = Payload(france);
            Reply(Update(first.Id));
            Reply(Post("select", new { id = fr.Id, subscriptionId = first.Id }));
            Until(() => tunnel.State == "connected" && tunnel.SessionNode?.Id == fr.Id, "manual switch to an available server");
            check(tunnel.Session != session, "explicit selection changed the live session");
            session = tunnel.Session;
            body = Payload(france, germany);
            Reply(Update(first.Id));
            State("Germany available again", fr.Id, fr.Id, session);
            body = Payload(berlin, france);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int refresh = Update(first.Id); Until(() => entered.Task.IsCompleted, "refresh entered network");
            Script("document.querySelector('[data-page=servers]').click()");
            Script($"document.querySelector('[data-node=\"{de.Id}\"] .server-card').click()");
            release.SetResult(); Reply(refresh);
            Until(() => tunnel.State == "connected" && tunnel.SessionNode?.Id == de.Id, "manual switch during refresh");
            check(tunnel.Session != session, "manual command won the refresh race");
            session = tunnel.Session;
            State("overlapping manual selection", de.Id, de.Id, session);
            // Losing the working tunnel after a refresh must not dial the
            // replacement, even via an immediate settings restart.
            body = Payload(france);
            Reply(Update(first.Id));
            State("removed live node until reconnect", de.Id, de.Id, session);
            check(disk.Load().CurrentSubscription!.RequiresConnectionConfirmation, "removal guard is on disk");
            int beforeRestart = tunnel.Session;
            Reply(Post("settings", new { key = "mode", value = "proxy" }));
            Until(() => tunnel.State == "idle" && Script("qaModel.state === 'idle' && qaModel.error === " +
                JsonSerializer.Serialize(SavedSubscription.ConfirmConnection)) == "true", "automatic reconnect paused for confirmation");
            check(!tunnel.HasCore && tunnel.Session == beforeRestart + 1, "reconnect did not start the fallback");
            // Check the actual tunnel entry as well, including a newly loaded profile.
            var denied = tunnel.Connect(disk.Load(), ct);
            Until(() => denied.IsCompleted, "unconfirmed connection rejected");
            denied.GetAwaiter().GetResult();
            check(!tunnel.HasCore && tunnel.State == "idle" && tunnel.Error == SavedSubscription.ConfirmConnection,
                "an unavailable saved selection cannot open an Xray session");
            Reply(Post("toggle", new { }));
            check(!tunnel.HasCore && tunnel.State == "idle" && disk.Load().Selected == de.Id &&
                disk.Load().CurrentSubscription!.RequiresConnectionConfirmation, "power button preserves the missing selection until a row is chosen");
            Reply(Post("select", new { id = fr.Id, subscriptionId = first.Id }));
            check(!disk.Load().CurrentSubscription!.RequiresConnectionConfirmation, "click on an available row confirms it");
            Until(() => Script("qaModel.state === 'idle' && qaModel.error === '' && document.getElementById('connectionError').hidden && qaModel.selected === "
                + JsonSerializer.Serialize(fr.Id)) == "true", "confirmed selection clears the prompt before connecting");
            Reply(Post("settings", new { key = "mode", value = "tun" }));
            Reply(Post("toggle", new { }));
            Until(() => tunnel.State == "connected" && tunnel.SessionNode?.Id == fr.Id, "explicit reconnect uses the confirmed endpoint");
            body = Payload(france, germany);
            Reply(Update(first.Id));
            Reply(Post("select", new { id = de.Id, subscriptionId = first.Id }));
            Until(() => tunnel.State == "connected" && tunnel.SessionNode?.Id == de.Id, "restore selection before restart");
            body = Payload(france);
            Reply(Update(first.Id));
            Reply(Post("toggle", new { }));
            Reply(Post("toggle", new { }));
            Until(() => Script("qaModel.error === " + JsonSerializer.Serialize(SavedSubscription.ConfirmConnection)) == "true",
                "manual connect explains the missing endpoint");
            int stoppedSession = tunnel.Session;
            body = Payload(france, berlin);
            Reply(Update(first.Id));
            Until(() => Script("qaModel.state === 'idle' && qaModel.error === '' && document.getElementById('connectionError').hidden") == "true",
                "returning endpoint clears the prompt without clicking a row");
            check(!tunnel.HasCore && tunnel.Session == stoppedSession && disk.Load().ManualConnectionNode?.Id == de.Id,
                "refresh after manual stop only restores the next target");
            // This fixture shares one server socket across accounts. Drop the other
            // account before rotating the login, so both catalogs identify one peer.
            body = Payload(berlin);
            Reply(Update(first.Id));
            Reply(Post("toggle", new { }));
            Until(() => tunnel.State == "connected", "Germany connected before login rotation");
            int rotationSession = tunnel.Session;
            string rotatedBerlin = berlin.Replace(new Uri(berlin).UserInfo, new Uri(france).UserInfo);
            body = Payload(rotatedBerlin);
            Reply(Update(first.Id));
            State("unique login rotation", de.Id, de.Id, rotationSession);
            check(disk.Load().ManualConnectionNode?.SourceId == Subscriptions.Parse(Payload(rotatedBerlin)).Nodes.Single().Id,
                "next connection uses the incoming configuration under Germany's saved ID");
            Reply(Post("toggle", new { }));
            Close();
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Kot.WindowTests.exe"))
            {
                UseShellExecute = false, ArgumentList = { "--selection-restart", folder, first.Id, de.Id, healthUrl }
            };
            using var restarted = Process.Start(start) ?? throw new Exception("Could not start the restart check");
            try
            {
                if (!restarted.WaitForExit(90000)) throw new TimeoutException("restarted application");
                check(restarted.ExitCode == 0, "new application process restored Germany");
            }
            finally
            {
                if (!restarted.HasExited) { restarted.Kill(entireProcessTree: true); restarted.WaitForExit(5000); }
            }
        }
        finally
        {
            release?.TrySetResult();
            try { Close(); }
            finally { Directory.Delete(folder, recursive: true); }
        }
    }
}
