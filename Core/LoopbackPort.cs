using System.Net;
using System.Net.Sockets;
namespace Kot.Core;

// SOCKS listeners need the same port for TCP and UDP. Windows can reserve a
// port for only one protocol, so a successful TCP bind is not sufficient.
public sealed class LoopbackPort : IDisposable
{
    readonly TcpListener tcp;
    readonly Socket udp;
    public int Port { get; }
    LoopbackPort(TcpListener tcp, Socket udp)
    { this.tcp = tcp; this.udp = udp; Port = ((IPEndPoint)tcp.LocalEndpoint).Port; }
    public static LoopbackPort? TryReserve(int port)
    {
        var tcp = new TcpListener(IPAddress.Loopback, port); Socket? udp = null;
        try
        {
            tcp.ExclusiveAddressUse = true; tcp.Start();
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            udp.Bind(tcp.LocalEndpoint);
            return new(tcp, udp);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        { tcp.Stop(); udp?.Dispose(); return null; }
        catch { tcp.Stop(); udp?.Dispose(); throw; }
    }
    public static LoopbackPort Reserve(CancellationToken ct = default, params int[] excludedPorts)
    {
        for (int attempt = 0; attempt < 128; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // After a conflict, leave the OS dynamic range too: a long UDP
            // exclusion range must not trap repeated TCP-assigned candidates.
            var reserved = TryReserve(attempt == 0 ? 0 : Random.Shared.Next(1024, 65536));
            if (reserved == null) continue;
            if (!excludedPorts.Contains(reserved.Port)) return reserved;
            reserved.Dispose();
        }
        throw new UserError("Не удалось выбрать свободный локальный порт TCP/UDP.");
    }
    public void Dispose() { tcp.Stop(); udp.Dispose(); }
}
