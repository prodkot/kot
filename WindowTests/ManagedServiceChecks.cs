using Kot.Core;
using Kot.Windows;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Kot.WindowTests;

internal static class ManagedServiceChecks
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static JsonElement Item(ManagedServices manager, string id) => JsonSerializer.SerializeToElement(manager.Model(true), Json).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);
    public static async Task Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "kot-managed-services-" + Guid.NewGuid().ToString("N"));
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var options = new ServiceOptions { CheckUpdates = false }; bool networkBusy = false;
        using (var reserve = new TcpListener(IPAddress.Loopback, 0)) { reserve.Start(); options.Telegram.Port = ((IPEndPoint)reserve.LocalEndpoint).Port; }
        var manager = new ManagedServices(() => options, () => networkBusy, () => { }, limit.Token, folder);
        async Task Action(string id, string action, string[]? selected = null)
        {
            manager.Begin(id, action, selected); await manager.Wait(id).WaitAsync(limit.Token);
            string error = Item(manager, id).GetProperty("error").GetString() ?? "";
            check(error.Length == 0, id + " " + action + (error.Length > 0 ? ": " + error : " completes without an error"));
        }
        try
        {
            await Action("zapret", "install");
            var strategies = Item(manager, "zapret").GetProperty("strategies").EnumerateArray().Select(s => s.GetString()!).ToArray();
            check(strategies.Length >= 10 && strategies.Contains("general.bat"), "official zapret package contains validated selectable strategies");
            await Action("zapret", "start");
            check(manager.IsRunning("zapret"), "real winws and WinDivert remain running with the selected strategy");
            await Action("zapret", "stop");
            check(!manager.IsRunning("zapret"), "zapret stop releases the owned process job");
            options.Zapret.Targets = "https://example.com"; options.Zapret.TimeoutSeconds = 2;
            await Action("zapret", "start");
            manager.Begin("zapret", "test", ["general.bat"]);
            await Task.Delay(160, limit.Token); manager.Cancel("zapret"); await manager.Wait("zapret").WaitAsync(limit.Token);
            check(!manager.Testing && manager.IsRunning("zapret") && Item(manager,"zapret").GetProperty("error").GetString()!.Contains("отменена"), "cancelled strategy selection restores the previously running service");
            check(File.Exists(Path.Combine(folder, "tests.json")) && JsonSerializer.Deserialize<StrategyTestRun>(manager.Export(), Json)!.Cancelled, "partial cancelled strategy run survives in saved history");
            networkBusy = true; bool rejected = false;
            try { manager.Begin("zapret", "test", ["general.bat"]); } catch (UserError) { rejected = true; }
            check(rejected && manager.IsRunning("zapret"), "VPN or kill switch prevents a misleading direct-network strategy test"); networkBusy = false;
            await Action("zapret", "stop");
            // Same selected strategy is tried again without relying on an ISP-specific success score.
            await Action("zapret", "test", ["general.bat"]);
            var run = JsonSerializer.Deserialize<StrategyTestRun>(manager.Export(), Json)!;
            check(run.Results.Count == 1 && run.Baseline.Count == 1 && !manager.Testing && !manager.IsRunning("zapret"), "completed strategy selection records actual HTTPS probes and preserves the stopped state");
            var restored = new ManagedServices(() => options, () => false, () => { }, limit.Token, folder);
            check(JsonSerializer.Deserialize<StrategyTestRun>(restored.Export(), Json)!.Started == run.Started, "saved strategy history reloads after restarting the manager");
            await restored.StopAll(true);
            await Action("telegram", "install");
            string[] license = Directory.GetFiles(Path.Combine(folder, "telegram"), "LICENSE.txt", SearchOption.AllDirectories);
            check(license.Length == 1 && File.ReadAllText(license[0]).Contains("Copyright (c) 2026 Flowseal"), "managed Telegram package keeps its MIT attribution");
            await Action("telegram", "start");
            check(manager.IsRunning("telegram") && await ManagedServices.PortOpen(options.Telegram.Port, limit.Token), "official portable TgWsProxy opens the configured loopback port");
            check(!JsonSerializer.Serialize(manager.Model(true), Json).Contains(options.Telegram.Secret), "Telegram secret is absent from the UI snapshot and service logs");
            await Action("telegram", "restart");
            await Action("telegram", "stop");
            check(!await ManagedServices.PortOpen(options.Telegram.Port, limit.Token) && Directory.GetFiles(folder, "config.json", SearchOption.AllDirectories).Length == 0, "Telegram stop closes its listener and removes the plaintext portable config");
            using (var occupied = new TcpListener(IPAddress.Loopback, options.Telegram.Port))
            {
                occupied.Start(); manager.Begin("telegram", "start"); await manager.Wait("telegram").WaitAsync(limit.Token);
                check(!manager.IsRunning("telegram") && Item(manager,"telegram").GetProperty("error").GetString()!.Contains("занят"), "occupied Telegram port is reported without starting another copy");
            }
            await Action("telegram", "start");
            using (var process = System.Diagnostics.Process.GetProcessById(manager.ProcessId("telegram")!.Value))
            { process.Kill(); await process.WaitForExitAsync(limit.Token); }
            await manager.Tick();
            check(!manager.IsRunning("telegram") && Item(manager,"telegram").GetProperty("error").GetString()!.Contains("завершился"), "unexpected Telegram exit is reflected in the service state");
            await Action("telegram", "start");
            await manager.StopAll(true);
            check(!manager.IsRunning("telegram") && !manager.IsRunning("zapret") && !await ManagedServices.PortOpen(options.Telegram.Port, limit.Token), "application shutdown leaves neither managed service nor a loopback listener");
        }
        finally { await manager.StopAll(true); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
