using Kot.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
namespace Kot.Windows;

// One supervised Xray job for the connection or isolated probe. No system proxy or TUN here.
public sealed class XraySession : IAsyncDisposable
{
    NativeCore? core;
    readonly string path = Path.Combine(Store.Folder, "xhttp-" + Guid.NewGuid().ToString("N") + ".json");
    readonly Dictionary<string, XhttpBridge> bridges = [];
    public IReadOnlyDictionary<string, XhttpBridge> Bridges => bridges;
    public XhttpBridge Egress { get; private set; } = null!;
    public string Executable { get; private set; } = "";
    public Task WaitForExitAsync() => core?.WaitForExitAsync() ?? Task.CompletedTask;
    public async Task<string> RecentOutput() => core == null ? "" : AppLog.Clean(await core.ReadRecentOutput(core.HasExited));
    public async Task EnsureRunning()
    {
        if (core == null || !core.HasExited) return;
        throw new UserError("Xray завершился: " + CoreDiagnostics.Summary(AppLog.Clean(await core.ReadRecentOutput(true))));
    }
    public static async Task<XraySession?> Start(IReadOnlyList<Node> nodes, CancellationToken ct, string? executable = null, params int[] excludedPorts)
    {
        var selected = nodes.Where(Xhttp.Uses).DistinctBy(n => n.Id).ToArray();
        if (selected.Length == 0) return null;
        var session = new XraySession { Executable = Path.GetFullPath(executable ?? Path.Combine(AppContext.BaseDirectory, "core", "xray.exe")) };
        List<TcpListener> reservations = [];
        try
        {
            if (!File.Exists(session.Executable)) throw new UserError("Ядро Xray для XHTTP не найдено. Переустановите kot.");
            XhttpBridge Reserve()
            {
                ct.ThrowIfCancellationRequested();
                TcpListener listener; int port;
                do { listener = new(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; if (excludedPorts.Contains(port)) listener.Stop(); } while (excludedPorts.Contains(port));
                reservations.Add(listener);
                string user = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                return new(port, user, password);
            }
            session.Egress = Reserve();
            foreach (var node in selected) { NodeSecurity.Validate(node); session.bridges.Add(node.Id, Reserve()); }
            Directory.CreateDirectory(Store.Folder);
            await File.WriteAllTextAsync(session.path, Xhttp.Build(selected, session.bridges, session.Egress).ToJsonString(), ct);
            using (var check = new NativeCore(session.Executable, ["run", "-test", "-c", session.path], Path.GetDirectoryName(session.Executable)!, "kot. Xray configuration check", text => AppLog.Write("xray-check", text)))
            {
                try { await check.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), ct); }
                catch { await check.Stop(); throw; }
                if (check.ExitCode != 0) throw new UserError("Xray не принял XHTTP-конфигурацию: " + CoreDiagnostics.Summary(AppLog.Clean(await check.ReadRecentOutput(true))));
            }
            foreach (var listener in reservations) listener.Stop();
            ct.ThrowIfCancellationRequested();
            session.core = new NativeCore(session.Executable, ["run", "-c", session.path], Path.GetDirectoryName(session.Executable)!, "kot. XHTTP core", text => AppLog.Write("xray-core", text));
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct); ready.CancelAfter(TimeSpan.FromSeconds(10));
            foreach (var bridge in session.bridges.Values)
            {
                while (true)
                {
                    ready.Token.ThrowIfCancellationRequested(); await session.EnsureRunning();
                    try { await Latency.Tcp("127.0.0.1", bridge.Port, 250, ready.Token); break; }
                    catch (Exception ex) when (!ready.IsCancellationRequested && ex is SocketException or OperationCanceledException) { }
                    await Task.Delay(100, ready.Token);
                }
            }
            File.Delete(session.path); return session;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await session.DisposeAsync(); throw new UserError("Xray не запустился за 10 секунд."); }
        catch { await session.DisposeAsync(); throw; }
        finally { foreach (var listener in reservations) listener.Stop(); }
    }
    public async ValueTask DisposeAsync()
    {
        var old = core; core = null;
        try { if (old != null) await old.Stop(); }
        catch (Exception ex) { AppLog.Error("xray cleanup", ex); }
        finally { old?.Dispose(); try { File.Delete(path); } catch (Exception ex) { AppLog.Error("xray config cleanup", ex); } }
    }
}
