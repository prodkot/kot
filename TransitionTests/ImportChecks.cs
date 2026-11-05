using Kot.Core;
using Kot.Windows;

static class ImportChecks
{
    const string Link = "vless://11111111-1111-4111-8111-111111111111@example.com:443?security=none#old";
    public static async Task Run()
    {
        var imports = new ImportOperations();
        using var old = imports.Begin(Guid.NewGuid().ToString(), CancellationToken.None);
        using var next = imports.Begin(Guid.NewGuid().ToString(), CancellationToken.None);
        GuardChecks.Check(old.Token.IsCancellationRequested && !next.Token.IsCancellationRequested, "new import cancels old operation");
        imports.Cancel(old.Id); old.Dispose();
        GuardChecks.Check(!next.Token.IsCancellationRequested, "late old cancellation/disposal cannot change a new import");
        bool reused = false;
        try { imports.Begin(old.Id, CancellationToken.None); } catch (UserError) { reused = true; }
        GuardChecks.Check(reused && !next.Token.IsCancellationRequested, "retired operation IDs cannot be reused or supersede the current import");
        imports.Cancel(next.Id);
        GuardChecks.Check(next.Token.IsCancellationRequested, "queued import can be cancelled before work starts");
        using var lifetime = new CancellationTokenSource();
        using var dying = imports.Begin(Guid.NewGuid().ToString(), lifetime.Token); lifetime.Cancel();
        GuardChecks.Check(dying.Token.IsCancellationRequested, "application lifetime cancels its import");

        foreach (bool remote in new[] { false, true })
        foreach (bool toolsBoundary in new[] { false, true })
        {
            Fixture.Reset();
            var node = Subscriptions.ParseNode(Link);
            var profile = new Profile { Address = "https://example.com/sub", Nodes = [node], Selected = node.Id };
            profile.CaptureActive();
            var window = new MainWindow(profile);
            await window.Begin().WaitAsync(TimeSpan.FromSeconds(5));
            int session = window.Connection.Session;
            var pause = new Pause();
            if (toolsBoundary) window.ToolsPause = pause; else XraySession.ValidationPause = pause;
            using var op = imports.Begin(Guid.NewGuid().ToString(), CancellationToken.None);
            var work = remote
                ? window.Refresh(profile.Address, Subscriptions.Parse(Link.Replace("example.com:443", "new.example.com:443")), op.Token)
                : window.LocalImport(Link.Replace("#old", "#new"), op.Token);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            imports.Cancel(op.Id); pause.Continue.TrySetResult();
            try { await work; throw new Exception("cancelled import must fail"); }
            catch (OperationCanceledException) { }
            GuardChecks.Check(window.Saved.Nodes.Single().Name == "old" && window.Saved.Subscriptions.Count == 1
                && window.Connection.State == "connected" && window.Connection.Session == session,
                $"cancelled {(remote ? "remote" : "file/text")} import at {(toolsBoundary ? "tools" : "validation")} cannot save or stop an active session");
            window.ToolsPause = null; await window.Connection.Stop();
        }
    }
}
