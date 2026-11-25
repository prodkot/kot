using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Kot.Windows;

// Unlike the loopback health proxy, this request must enter the Windows TUN.
// Binding alone could hide a missing default route, so also check the route
// Windows would select for the destination without an explicit binding.
internal static class TunnelHealth
{
    const SocketOptionName UnicastInterface = (SocketOptionName)31; // Windows IP_UNICAST_IF / IPV6_UNICAST_IF.
    static readonly IPAddress Source4 = IPAddress.Parse("172.28.231.1");
    static readonly IPAddress Source6 = IPAddress.Parse("fdfe:dcba:231::1");
    [DllImport("iphlpapi.dll")]
    static extern uint GetBestInterfaceEx(byte[] address, out uint index);

    internal static bool AdapterReady()
    {
        try { return Adapter() != null; }
        catch (NetworkInformationException) { return false; }
    }
    static NetworkInterface? Adapter() => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
        n.Name == "kot-tun" && n.OperationalStatus == OperationalStatus.Up
        && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(Source4)));

    static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var adapter = Adapter() ?? throw new IOException("TUN adapter is not ready.");
        var properties = adapter.GetIPProperties();
        var destination = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(destination.Host, out var literal) ? [literal]
            : await Dns.GetHostAddressesAsync(destination.Host, ct);
        Exception? last = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            ct.ThrowIfCancellationRequested();
            if (IPAddress.IsLoopback(address)) continue;
            bool ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
            if (!ipv4 && address.AddressFamily != AddressFamily.InterNetworkV6) continue;
            var source = ipv4 ? Source4 : Source6;
            if (!properties.UnicastAddresses.Any(a => a.Address.Equals(source))) continue;
            int index = ipv4 ? properties.GetIPv4Properties().Index : properties.GetIPv6Properties().Index;
            if (index <= 0) continue; // Zero would silently select the default interface.
            var endpoint = new IPEndPoint(address, destination.Port);
            var serialized = endpoint.Serialize(); var bytes = new byte[serialized.Size];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = serialized[i];
            if (GetBestInterfaceEx(bytes, out uint route) != 0 || route != (uint)index) continue;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Windows IP_UNICAST_IF uses network byte order for IPv4 and
                // host byte order for IPv6. No fallback to an unbound socket.
                socket.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                    UnicastInterface, ipv4 ? IPAddress.HostToNetworkOrder(index) : index);
                socket.Bind(new IPEndPoint(source, 0));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.ConnectAsync(endpoint, deadline.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            { socket.Dispose(); last = ex; }
            catch { socket.Dispose(); throw; }
        }
        throw new IOException("No working route through kot-tun.", last);
    }
    internal static async Task<bool> Probe(string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
                ConnectCallback = Connect
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode && AdapterReady();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NetworkInformationException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { AppLog.Write("tun-health", ex.GetType().Name); return false; }
    }
}
