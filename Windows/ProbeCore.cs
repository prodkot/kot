using Kot.Core;
using System.Net;
using System.Net.Sockets;
namespace Kot.Windows;

// An isolated HTTP listener: no TUN, no changes to the system proxy or active connection.
public sealed class ProbeCore : IAsyncDisposable
{
    NativeCore? core;
    XraySession? xray;
    IDisposable? xrayPermission;
    readonly string path = Path.Combine(Store.Folder, "probe-" + Guid.NewGuid().ToString("N") + ".json");
    public int Port { get; private set; }
    public static async Task<ProbeCore> Start(Node node, CancellationToken ct, string? executable = null, string? xrayExecutable = null)
    {
        var probe = new ProbeCore();
        try
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); probe.Port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Directory.CreateDirectory(Store.Folder);
            if (Xhttp.Uses(node)) probe.xrayPermission = KillSwitch.AllowXhttpProbe(xrayExecutable ?? Path.Combine(AppContext.BaseDirectory, "core", "xray.exe"));
            probe.xray = await XraySession.Start([node], ct, xrayExecutable, probe.Port);
            var config = Configuration.Build(node, probe.Port, false, [], tun: false, xhttp: probe.xray?.Bridges);
            if (probe.xray != null) Xhttp.AddEgress(config, probe.xray.Egress);
            await File.WriteAllTextAsync(probe.path, config.ToJsonString(), ct);
            ct.ThrowIfCancellationRequested();
            probe.core = new NativeCore(executable ?? Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"), probe.path, text => AppLog.Write("probe-core " + node.Id, text));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (probe.xray != null) await probe.xray.EnsureRunning();
                if (probe.core.HasExited) throw new UserError("Ядро проверки завершилось: " + CoreDiagnostics.Summary(AppLog.Clean(await probe.core.ReadRecentOutput(true))));
                try { await Latency.Tcp("127.0.0.1", probe.Port, 250, ct); return probe; }
                catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
                await Task.Delay(100, ct);
            }
            throw new UserError("Ядро проверки не открыло локальный порт.");
        }
        catch { await probe.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (core != null) { try { await core.Stop(); } catch (Exception ex) { AppLog.Error("probe stop", ex); } finally { core.Dispose(); core = null; } }
        if (xray != null) { await xray.DisposeAsync(); xray = null; }
        xrayPermission?.Dispose(); xrayPermission = null;
        try { File.Delete(path); } catch (Exception ex) { AppLog.Error("probe cleanup", ex); }
    }
}
