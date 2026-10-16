using Kot.Core;
using System.Net;
using System.Net.Sockets;
namespace Kot.Tests;

static class LoopbackPortTests
{
    public static void Run(Action<bool, string> check)
    {
        using var available = LoopbackPort.Reserve(); int port = available.Port; available.Dispose();
        using var occupiedUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        occupiedUdp.Bind(new IPEndPoint(IPAddress.Loopback, port));
        using (var rejected = LoopbackPort.TryReserve(port))
            check(rejected == null, "TCP-free but UDP-occupied port is rejected before launching a core");
        using (var tcp = new TcpListener(IPAddress.Loopback, port))
        { tcp.Start(); check(true, "failed UDP reservation releases its partial TCP listener"); }
        occupiedUdp.Dispose();
        using (var reserved = LoopbackPort.TryReserve(port))
        {
            check(reserved != null, "released UDP conflict permits a dual-protocol reservation");
            bool tcpBlocked = false, udpBlocked = false;
            using var tcp = new TcpListener(IPAddress.Loopback, port);
            try { tcp.Start(); } catch (SocketException) { tcpBlocked = true; }
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            try { udp.Bind(new IPEndPoint(IPAddress.Loopback, port)); } catch (SocketException) { udpBlocked = true; }
            check(tcpBlocked && udpBlocked, "reservation holds both loopback protocols exclusively");
        }
        using (var released = LoopbackPort.TryReserve(port))
            check(released != null, "disposing a reservation releases both protocols");
        using var automatic = LoopbackPort.Reserve(default, port);
        check(automatic.Port != port, "automatic allocation honors excluded listener ports");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
        try { using var ignored = LoopbackPort.Reserve(cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "cancelled port allocation stops before opening sockets");
    }
}
