using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
namespace Kot.Windows;

static class PhysicalNetwork
{
    // Captured before routes change; probes must never feed back into kot-tun.
    public static string Interface { get; private set; } = "";
    public static void Capture()
    {
        if (GetBestInterface(0x01010101, out uint index) != 0) return;
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            try { if (network.GetIPProperties().GetIPv4Properties()?.Index == index) { if (network.Name != "kot-tun") Interface = network.Name; return; } }
            catch (NetworkInformationException) { }
        }
    }
    [DllImport("iphlpapi.dll")] static extern uint GetBestInterface(uint address, out uint index);
}
