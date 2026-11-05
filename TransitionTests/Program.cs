using Kot.Core;
using Kot.Windows;

try
{
    if (args.Length == 0 || args.Contains("guard")) await GuardChecks.Run();
    if (args.Length == 0 || args.Contains("import")) await ImportChecks.Run();
    if (args.Length == 0 || args.Contains("routing")) await RoutingChecks.Run();
    if (args.Length == 0 || args.Contains("identity")) await IdentityChecks.Run();
    Console.WriteLine("PASS transition regressions (real production handlers, simulated OS and core)");
}
finally { if (Directory.Exists(Store.Folder)) Directory.Delete(Store.Folder, true); }

static class GuardChecks
{
    public static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    static Profile Profile(bool guard, bool automatic = false)
    {
        var first = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@example.com:443?security=none#one");
        var second = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@other.example.com:443?security=none#two");
        return new() { Nodes = [first, second], Selected = automatic ? "auto" : first.Id, KillSwitch = guard,
            Automation = new() { AutoMinutes = 1 } };
    }
    public static async Task Run()
    {
        foreach (bool initial in new[] { false, true })
        {
            foreach (string boundary in new[] { "prepare", "check", "health" })
            {
                Fixture.Reset();
                var pause = new Pause();
                if (boundary == "prepare") ProviderAssets.Pause = pause;
                else if (boundary == "check") XraySession.Pause = pause;
                else Fixture.Health = pause;
                KillSwitch.Active = initial;
                var window = new MainWindow(Profile(initial));
                var connect = window.Begin();
                await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await window.ChangeGuard(!initial);
                Check(window.SavedGuard == !initial && KillSwitch.Active == !initial, boundary + ": setting applied immediately");
                pause.Continue.TrySetResult();
                await connect.WaitAsync(TimeSpan.FromSeconds(5));
                Check(window.Connection.State == "connected" && KillSwitch.Active == window.SavedGuard,
                    boundary + ": connection keeps latest guard " + initial + " -> " + !initial);
                await window.Connection.Stop();
                Check(KillSwitch.Active == !initial, "stop preserves the chosen protection");
            }
            Fixture.Reset();
            var auto = Profile(initial, automatic: true);
            using var probe = new FakeProxy();
            ProbeCore.PortForTests = probe.Port;
            ProbeCore.Allowed = auto.Nodes[0].Id;
            var automatic = new MainWindow(auto);
            await automatic.Begin().WaitAsync(TimeSpan.FromSeconds(5));
            Check(automatic.Connection.AutomaticNode == auto.Nodes[0].Id, "Auto starts the available node");
            await automatic.ChangeGuard(!initial);
            ProbeCore.Allowed = auto.Nodes[1].Id;
            typeof(Tunnel).GetField("lastAutomatic", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(automatic.Connection, 0L);
            Check(await automatic.Connection.ReadAutomaticNode(CancellationToken.None), "Auto reconnects to a different node");
            Check(automatic.Connection.AutomaticNode == auto.Nodes[1].Id && KillSwitch.Active == !initial,
                "Auto reconnect uses latest saved guard " + initial + " -> " + !initial);
            await automatic.Connection.Stop();

            Fixture.Reset(); KillSwitch.Active = initial;
            var failed = new MainWindow(Profile(initial));
            var held = new Pause(); ProviderAssets.Pause = held;
            var pending = failed.Begin(); await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Store.FailSave = true;
            try { await failed.ChangeGuard(!initial); throw new Exception("save should fail"); }
            catch (IOException) { }
            finally { Store.FailSave = false; }
            Check(failed.SavedGuard == initial && KillSwitch.Active == initial, "failed save restores guard and preference");
            failed.Connection.Cancel(); held.Continue.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(failed.Connection.State == "idle" && KillSwitch.Active == initial, "cancelled generation cannot reapply stale guard");

            Fixture.Reset(); XraySession.FailCheck = true;
            var retry = new MainWindow(Profile(initial));
            await retry.Begin();
            await retry.ChangeGuard(!initial);
            XraySession.FailCheck = false;
            await retry.Begin().WaitAsync(TimeSpan.FromSeconds(5));
            Check(retry.Connection.State == "connected" && KillSwitch.Active == !initial, "failed start and retry respect the saved guard");
            await retry.Connection.Stop();
        }
        Fixture.Reset(); KillSwitch.Active = true;
        var idle = new MainWindow(Profile(true)); Store.FailSave = true;
        try { await idle.ChangeGuard(false); throw new Exception("save should fail"); }
        catch (IOException) { }
        finally { Store.FailSave = false; }
        Check(idle.SavedGuard && KillSwitch.Active, "failed disable while idle restores persistent protection");
    }
}
