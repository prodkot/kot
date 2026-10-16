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
        using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        var options = new ServiceOptions { CheckUpdates = false }; bool networkBusy = false;
        using (var reserve = new TcpListener(IPAddress.Loopback, 0)) { reserve.Start(); options.Telegram.Port = ((IPEndPoint)reserve.LocalEndpoint).Port; }
        var manager = new ManagedServices(() => options, () => networkBusy, () => { }, lifecycle.Token, folder);
        async Task Action(string id, string action, string[]? selected = null)
        {
            manager.Begin(id, action, selected); await manager.Wait(id).WaitAsync(limit.Token);
            string error = Item(manager, id).GetProperty("error").GetString() ?? "";
            check(error.Length == 0, id + " " + action + (error.Length > 0 ? ": " + error : " completes without an error"));
        }
        try
        {
            string own = Path.Combine(folder,"cleanup-check"), outside = Path.Combine(folder,"outside-check");
            Directory.CreateDirectory(own); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(own,"keep.txt"),"keep"); File.WriteAllText(Path.Combine(outside,"keep.txt"),"keep");
            bool outsideRejected = false;
            try { ManagedPackageFiles.Remove(own, outside); } catch (UserError) { outsideRejected = true; }
            check(outsideRejected && File.Exists(Path.Combine(outside,"keep.txt")), "package cleanup rejects paths outside its ownership boundary");
            string link = Path.Combine(own,"link"); Directory.CreateSymbolicLink(link, outside); bool linkRejected = false;
            try { ManagedPackageFiles.Remove(own, own); } catch (UserError) { linkRejected = true; }
            check(linkRejected && File.Exists(Path.Combine(own,"keep.txt")) && File.Exists(Path.Combine(outside,"keep.txt")), "package cleanup rejects redirected directories before deleting any files");
            Directory.Delete(link);
            string manifest = Path.Combine(own,"current.json"); File.WriteAllText(manifest,"keep"); bool lockedManifestRejected = false;
            using (var locked = new FileStream(manifest,FileMode.Open,FileAccess.Read,FileShare.Read))
            { try { ManagedPackageFiles.Remove(folder, own, manifest); } catch (IOException) { lockedManifestRejected = true; } }
            check(lockedManifestRejected && File.Exists(manifest) && File.Exists(Path.Combine(own,"keep.txt")), "locked installation metadata fails before package deletion and is never queued against a future installation");
            ManagedPackageFiles.Remove(folder, own); ManagedPackageFiles.Remove(folder, outside);
            await Action("zapret", "install");
            var strategies = Item(manager, "zapret").GetProperty("strategies").EnumerateArray().Select(s => s.GetString()!).ToArray();
            check(strategies.Length >= 10 && strategies.Contains("general.bat"), "official zapret package contains validated selectable strategies");
            await Action("zapret", "start");
            check(manager.IsRunning("zapret"), "real winws and WinDivert remain running with the selected strategy");
            using (var process = System.Diagnostics.Process.GetProcessById(manager.ProcessId("zapret")!.Value))
            {
                await manager.StopZapretForVpn().WaitAsync(limit.Token);
                check(process.HasExited && !manager.IsRunning("zapret"), "VPN preparation awaits actual winws exit and releases the owned process job");
            }
            manager.Begin("zapret", "start");
            using (var process = System.Diagnostics.Process.GetProcessById(manager.ProcessId("zapret")!.Value))
            {
                Task stopping = manager.StopZapretForVpn(); bool startRejected = false;
                try { manager.Begin("zapret", "start"); } catch (UserError) { startRejected = true; }
                await stopping.WaitAsync(limit.Token);
                check(startRejected && process.HasExited && !manager.IsRunning("zapret") && Item(manager,"zapret").GetProperty("error").GetString()!.Length == 0, "VPN preparation cancels a pending zapret start and prevents another start during shutdown");
            }
            await manager.StopZapretForVpn().WaitAsync(limit.Token);
            check(!manager.IsRunning("zapret"), "VPN preparation also succeeds when zapret is already stopped");
            options.Zapret.Targets = "https://example.com"; options.Zapret.TimeoutSeconds = 2;
            await Action("zapret", "start");
            manager.Begin("zapret", "test", ["general.bat"]);
            bool preparationRejected = false;
            try { await manager.StopZapretForVpn(); } catch (UserError) { preparationRejected = true; }
            check(preparationRejected && manager.Testing, "VPN preparation leaves an active strategy test untouched until the user finishes or cancels it");
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
            check(manager.IsRunning("telegram") && await ManagedServices.PortOpen(options.Telegram.Port, limit.Token), "official TgWsProxy opens the configured loopback port");
            options.Zapret.AutoStart = options.Telegram.AutoStart = true;
            int telegramPid = manager.ProcessId("telegram")!.Value;
            await manager.AutoStart(startZapret: false).WaitAsync(limit.Token);
            check(!manager.IsRunning("zapret") && manager.ProcessId("telegram") == telegramPid, "VPN autoconnect skips zapret autostart while preserving the Telegram proxy");
            await Action("zapret", "start");
            await manager.StopZapretForVpn().WaitAsync(limit.Token);
            check(!manager.IsRunning("zapret") && manager.ProcessId("telegram") == telegramPid && await ManagedServices.PortOpen(options.Telegram.Port, limit.Token) && options.Zapret.AutoStart && options.Telegram.AutoStart, "VPN preparation stops only zapret and preserves Telegram, its listener and both autostart settings");
            check(!JsonSerializer.Serialize(manager.Model(true), Json).Contains(options.Telegram.Secret), "Telegram secret is absent from the UI snapshot and service logs");
            await Action("telegram", "restart");
            await Action("telegram", "stop");
            check(!await ManagedServices.PortOpen(options.Telegram.Port, limit.Token) && Directory.GetFiles(folder, "config.json", SearchOption.AllDirectories).Length == 0, "Telegram stop closes its listener and removes the plaintext service config");
            using (var occupied = new TcpListener(IPAddress.Loopback, options.Telegram.Port))
            {
                occupied.Start(); manager.Begin("telegram", "start"); await manager.Wait("telegram").WaitAsync(limit.Token);
                check(!manager.IsRunning("telegram") && Item(manager,"telegram").GetProperty("error").GetString()!.Contains("занят"), "occupied Telegram port is reported without starting another copy");
            }
            await Action("telegram", "start");
            using (var process = System.Diagnostics.Process.GetProcessById(manager.ProcessId("telegram")!.Value))
            { process.Kill(); await process.WaitForExitAsync(limit.Token); }
            // Process wrappers can report exit before NativeCore's original handle is signaled.
            // Keep checking the real service state until cleanup finishes, with a fixed deadline.
            var exitWait = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                await manager.Tick();
                if (manager.ProcessId("telegram") == null) break;
                await Task.Delay(25, limit.Token);
            } while (exitWait.Elapsed < TimeSpan.FromSeconds(5));
            check(!manager.IsRunning("telegram") && Item(manager,"telegram").GetProperty("error").GetString()!.Contains("завершился"), "unexpected Telegram exit is reflected in the service state");
            await Action("telegram", "start");
            manager.Begin("zapret", "start");
            Task pendingPreparation = manager.StopZapretForVpn();
            lifecycle.Cancel();
            await manager.Tick();
            check(manager.IsRunning("telegram"), "cancelled lifetime polling completes without an unhandled exception or a false service failure");
            await manager.StopAll(true);
            await pendingPreparation;
            check(!manager.IsRunning("telegram") && !manager.IsRunning("zapret") && !await ManagedServices.PortOpen(options.Telegram.Port, limit.Token), "application shutdown leaves neither managed service nor a loopback listener");
            // Installed builds may queue a loaded WinDivert file for reboot cleanup.
            manager = new ManagedServices(() => options, () => false, () => { }, limit.Token, folder);
            await Action("zapret", "remove"); await Action("telegram", "remove");
            check(Item(manager,"zapret").GetProperty("installedVersion").ValueKind == JsonValueKind.Null && Item(manager,"telegram").GetProperty("installedVersion").ValueKind == JsonValueKind.Null && !File.Exists(Path.Combine(folder,"zapret","current.json")) && !File.Exists(Path.Combine(folder,"telegram","current.json")), "removal clears both installed packages and their manifests despite the loaded WinDivert driver");
            check(File.Exists(Path.Combine(folder,"tests.json")) && options.Zapret.Strategy == "general.bat", "package removal preserves saved strategy history and user settings");
            check(!Directory.Exists(Path.Combine(folder,"telegram")) && Directory.GetFiles(folder,"winws.exe",SearchOption.AllDirectories).Length == 0, "removal deletes user-space executable files immediately");
        }
        catch (Exception ex) { Console.WriteLine("FAIL managed service check before cleanup: " + ex); throw; }
        finally { await manager.StopAll(true); ManagedPackageFiles.Remove(folder, folder); }
    }
}
