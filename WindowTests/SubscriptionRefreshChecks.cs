using Kot.Core;
using Kot.Windows;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Kot.WindowTests;

static class SubscriptionRefreshChecks
{
    sealed class Feed(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
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
        var de = Subscriptions.ParseNode(germany);
        var fr = Subscriptions.ParseNode(france);
        var first = new SavedSubscription { Address = "https://one.example/feed", Nodes = [fr, de], Selected = fr.Id };
        var second = new SavedSubscription { Address = "https://two.example/feed", Updated = DateTimeOffset.UtcNow, Nodes = [de, fr], Selected = fr.Id };
        var initial = new Profile { ConnectionMode = "proxy", SendHwid = false, Subscriptions = [first, second] };
        initial.Activate(first.Id);
        initial.Services.CheckUpdates = false;
        initial.Automation.RefreshHours = 0;
        var tunnel = new Tunnel([healthUrl], TimeSpan.FromSeconds(5));
        string body = france + "\n" + germany, otherBody = body;
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
        void Until(Func<bool> ready, string operation)
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
        string Script(string js)
        {
            var task = app!.InterfaceCore.ExecuteScriptAsync(js);
            Until(() => task.IsCompleted, "WebView script");
            return task.GetAwaiter().GetResult();
        }
        void Show()
        {
            app = Open(); app.Show();
            Until(() => app.InterfaceReady, "Windows UI startup");
            Script("window.qaReplies={};chrome.webview.addEventListener('message',e=>{if(e.data.kind==='reply')qaReplies[e.data.id]=e.data})");
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
        void State(string scenario, string selected, string? live, int session)
        {
            string selector = "document.querySelector('.server-card[aria-pressed=true]')?.closest('[data-node]').dataset.node";
            Until(() => Script(selector) == JsonSerializer.Serialize(selected), scenario + ": selected row");
            var saved = disk.Load();
            check(saved.ActiveSubscription == first.Id && saved.Selected == selected &&
                saved.Subscriptions.Single(s => s.Id == first.Id).Selected == selected, scenario + ": persisted choice and UI");
            if (live != null)
            {
                Until(() => tunnel.State == "connected", scenario + ": connection ready");
                var probe = tunnel.Probe(ct); Until(() => probe.IsCompleted, "HTTP probe");
                check(tunnel.HasCore && tunnel.Session == session && tunnel.SessionNode?.Id == live && probe.GetAwaiter().GetResult(),
                    scenario + ": same live Xray session carries HTTP");
            }
            else check(!tunnel.HasCore && tunnel.SessionNode == null && tunnel.State == "idle", scenario + ": idle connection");
        }
        try
        {
            disk.Save(initial); Show();
            // Use the server card for the initial choice, including its actual event handler.
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
                body = string.Join('\n', links);
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
            otherBody = string.Join('\n', berlin, same);
            int a = Update(second.Id), b = Update(first.Id);
            Reply(a); Reply(b);
            check(disk.Load().Subscriptions.Single(s => s.Id == second.Id).Selected == fr.Id, "inactive subscription retained its own choice");
            State("concurrent subscriptions", de.Id, de.Id, session);

            fail = true; Reply(Update(first.Id), ok: false); fail = false;
            State("failed download", de.Id, de.Id, session);

            body = string.Join('\n', france, extra);
            var removal = Reply(Update(first.Id)).GetProperty("data").GetProperty("selectionNotice").GetString()!;
            check(removal.Contains("Berlin") && removal.Contains("France"), "one notice names the removed server and its replacement");
            State("selected server removed", fr.Id, de.Id, session);
            check(Script("document.getElementById('selectedCity').textContent.includes('Germany')") == "true", "home identifies the retained live endpoint");
            Reply(Post("select", new { id = fr.Id, subscriptionId = first.Id }));
            Until(() => tunnel.State == "connected" && tunnel.SessionNode?.Id == fr.Id, "manual switch to highlighted fallback");
            check(tunnel.Session != session, "explicit selection changed the live session");
            session = tunnel.Session;
            body = string.Join('\n', france, germany);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int refresh = Update(first.Id); Until(() => entered.Task.IsCompleted, "refresh entered network");
            int manual = Post("select", new { id = de.Id, subscriptionId = first.Id });
            release.SetResult(); Reply(refresh); Reply(manual);
            Until(() => tunnel.State == "connected" && tunnel.SessionNode?.Id == de.Id, "manual switch during refresh");
            check(tunnel.Session != session, "manual command won the refresh race");
            State("overlapping manual selection", de.Id, de.Id, tunnel.Session);
            Close(); Show();
            State("application restarted", de.Id, null, tunnel.Session);
        }
        finally
        {
            release?.TrySetResult();
            try { Close(); }
            finally { Directory.Delete(folder, recursive: true); }
        }
    }
}
