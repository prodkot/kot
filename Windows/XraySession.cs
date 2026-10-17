using Kot.Core;
using System.Net.Sockets;
using System.Security.Cryptography;
namespace Kot.Windows;

// Supervised Xray workers. Each complete provider configuration has isolated routing and DNS.
public sealed class XraySession : IAsyncDisposable
{
    readonly List<NativeCore> cores = [];
    readonly List<string> paths = [];
    readonly Dictionary<string, XhttpBridge> bridges = [];
    public IReadOnlyDictionary<string, XhttpBridge> Bridges => bridges;
    public XhttpBridge Egress { get; private set; } = null!;
    public string Executable { get; private set; } = "";
    public async Task WaitForExitAsync() { if (cores.Count > 0) await await Task.WhenAny(cores.Select(c => c.WaitForExitAsync())); }
    public async Task<string> RecentOutput() => AppLog.Clean(string.Join("\n", await Task.WhenAll(cores.Select(c => c.ReadRecentOutput(c.HasExited)))));
    public async Task EnsureRunning()
    {
        if (!cores.Any(c => c.HasExited)) return;
        throw new UserError("Xray завершился: " + CoreDiagnostics.Summary(await RecentOutput()));
    }
    public static async Task Validate(IReadOnlyList<Node> nodes, CancellationToken ct)
    {
        var selected = nodes.Where(n => n.FullConfig != null).ToArray();
        if (selected.Length == 0) return;
        string executable = Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
        if (!File.Exists(executable)) throw new UserError("Ядро Xray не найдено. Переустановите kot.");
        using var bridge = LoopbackPort.Reserve(ct); using var health = LoopbackPort.Reserve(ct); using var egress = LoopbackPort.Reserve(ct);
        var clean = CoreDiagnostics.CreateRedactor(selected);
        Directory.CreateDirectory(Store.Folder);
        foreach (var node in selected)
        {
            string path = Path.Combine(Store.Folder, "xray-check-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var config = XrayConfig.Runtime(node, new(bridge.Port, "validation", new string('a', 64), health.Port), new(egress.Port, "validation", new string('b', 64)));
                await File.WriteAllTextAsync(path, config.ToJsonString(), ct);
                await Check(executable, path, ProviderAssets.Cache.DirectoryFor(node), clean, ct);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
    static async Task Check(string executable, string path, string? assets, Func<string, string> clean, CancellationToken ct)
    {
        using var check = new NativeCore(executable, ["run", "-test", "-c", path], Path.GetDirectoryName(executable)!, "kot. Xray configuration check", text => AppLog.Write("xray-check", clean(text)), assets);
        try { await check.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { await check.Stop(); throw; }
        if (check.ExitCode != 0) throw new UserError("Xray не принял конфигурацию подписки: " + CoreDiagnostics.Summary(clean(await check.ReadRecentOutput(true))));
    }
    public static async Task<XraySession?> Start(IReadOnlyList<Node> nodes, CancellationToken ct, string? executable = null, params int[] excludedPorts)
    {
        var selected = nodes.Where(Xhttp.Uses).DistinctBy(n => n.Id).ToArray();
        if (selected.Length == 0) return null;
        if (selected.Count(n => n.FullConfig != null) > 16) throw new UserError("В Авто можно использовать до 16 серверов с полными правилами. Выберите нужные серверы в избранное или подключитесь к одному вручную.");
        var session = new XraySession { Executable = Path.GetFullPath(executable ?? Path.Combine(AppContext.BaseDirectory, "core", "xray.exe")) };
        List<LoopbackPort> reservations = [];
        try
        {
            if (!File.Exists(session.Executable)) throw new UserError("Ядро Xray не найдено. Переустановите kot.");
            XhttpBridge Reserve()
            {
                var reserved = LoopbackPort.Reserve(ct, excludedPorts); reservations.Add(reserved);
                string user = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                return new(reserved.Port, user, password);
            }
            session.Egress = Reserve();
            foreach (var node in selected)
            {
                NodeSecurity.Validate(node);
                var bridge = Reserve();
                if (node.FullConfig != null) bridge = bridge with { HealthPort = Reserve().Port };
                session.bridges.Add(node.Id, bridge);
            }
            Directory.CreateDirectory(Store.Folder);
            var jobs = new List<(System.Text.Json.Nodes.JsonObject Config, string? Assets)>();
            var transport = selected.Where(n => n.FullConfig == null).ToArray();
            if (transport.Length > 0) jobs.Add((Xhttp.Build(transport, session.bridges, session.Egress), null));
            foreach (var node in selected.Where(n => n.FullConfig != null))
            {
                if (!ProviderAssets.Cache.Available(node)) throw new UserError("Сначала загрузите геобазы маршрутизации подписки.");
                jobs.Add((XrayConfig.Runtime(node, session.bridges[node.Id], session.Egress), ProviderAssets.Cache.DirectoryFor(node)));
            }
            foreach (var (config, assets) in jobs)
            {
                string path = Path.Combine(Store.Folder, "xray-" + Guid.NewGuid().ToString("N") + ".json"); session.paths.Add(path);
                await File.WriteAllTextAsync(path, config.ToJsonString(), ct);
                await Check(session.Executable, path, assets, CoreDiagnostics.CreateRedactor(selected), ct);
            }
            foreach (var reserved in reservations) reserved.Dispose();
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < jobs.Count; i++)
                session.cores.Add(new NativeCore(session.Executable, ["run", "-c", session.paths[i]], Path.GetDirectoryName(session.Executable)!, "kot. Xray core", text => AppLog.Write("xray-core", text), jobs[i].Assets));
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct); ready.CancelAfter(TimeSpan.FromSeconds(10));
            foreach (var bridge in session.bridges.Values)
            foreach (int port in new[] { bridge.Port, bridge.HealthPort, bridge.DnsPort }.Where(p => p > 0))
            {
                while (true)
                {
                    ready.Token.ThrowIfCancellationRequested(); await session.EnsureRunning();
                    try { await Latency.Tcp("127.0.0.1", port, 250, ready.Token); break; }
                    catch (Exception ex) when (!ready.IsCancellationRequested && ex is SocketException or OperationCanceledException) { }
                    await Task.Delay(100, ready.Token);
                }
            }
            foreach (var path in session.paths) File.Delete(path); return session;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await session.DisposeAsync(); throw new UserError("Xray не запустился за 10 секунд."); }
        catch { await session.DisposeAsync(); throw; }
        finally { foreach (var reserved in reservations) reserved.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        var old = cores.ToArray(); cores.Clear();
        foreach (var child in old)
        {
            try { await child.Stop(); }
            catch (Exception ex) { AppLog.Error("xray cleanup", ex); }
            finally { child.Dispose(); }
        }
        foreach (var path in paths)
            try { File.Delete(path); } catch (Exception ex) { AppLog.Error("xray config cleanup", ex); }
        paths.Clear();
    }
}
