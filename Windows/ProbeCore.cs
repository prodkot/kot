using Kot.Core;
using System.Net.Sockets;
namespace Kot.Windows;

// An isolated HTTP listener: no TUN, no changes to the system proxy or active connection.
public sealed class ProbeCore : IAsyncDisposable
{
    NativeCore? core;
    IDisposable? xrayPermission;
    public int Port { get; private set; }
    public static async Task<ProbeCore> Start(Node node, CancellationToken ct, string? executable = null, string? xrayExecutable = null)
    {
        var probe = new ProbeCore();
        try
        {
            PhysicalNetwork.Capture();
            await ProviderAssets.Prepare([node], ct);
            using var reservation = LoopbackPort.Reserve(ct); probe.Port = reservation.Port;
            string exe = executable ?? xrayExecutable ?? Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
            probe.xrayPermission = KillSwitch.AllowProbe(exe);
            var config = XrayRuntime.Build(node, probe.Port, physicalInterface: PhysicalNetwork.Interface);
            ct.ThrowIfCancellationRequested();
            reservation.Dispose();
            probe.core = XraySession.Start(exe, config.ToJsonString(), ProviderAssets.Cache.DirectoryFor(node), "kot. Xray probe", text => AppLog.Write("probe-core " + node.Id, text));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
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
        xrayPermission?.Dispose(); xrayPermission = null;
    }
}
