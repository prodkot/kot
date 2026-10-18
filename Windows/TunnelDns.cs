using System.Net;
using System.Net.Sockets;
using Kot.Core;
namespace Kot.Windows;

internal static class TunnelDns
{
    // HTTP CONNECT lets the server resolve names. A successful proxy request
    // alone says nothing about the Windows resolver used by TUN applications.
    internal static async Task<bool> Probe(string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string host = new Uri(target).DnsSafeHost;
        if (IPAddress.TryParse(host, out _)) return true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, deadline.Token);
            if (addresses.Length > 0) return true;
            AppLog.Write("health-dns", "Windows resolver returned no addresses");
        }
        catch (SocketException ex) { AppLog.Write("health-dns", "Windows resolver: " + ex.SocketErrorCode); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { AppLog.Write("health-dns", "Windows resolver timed out"); }
        return false;
    }
}
