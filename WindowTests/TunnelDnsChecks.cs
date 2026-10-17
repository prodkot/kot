using Kot.Core;
using Kot.Windows;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
namespace Kot.WindowTests;

public static class TunnelDnsChecks
{
    static async Task<bool> Datagram(string server, string host, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var address = IPAddress.Parse(server);
        using var socket = new UdpClient(address.AddressFamily);
        socket.Connect(address, 53);
        var query = new List<byte> { 0x4b, 0x54, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (string label in host.Split('.')) { query.Add((byte)label.Length); query.AddRange(Encoding.ASCII.GetBytes(label)); }
        query.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        try
        {
            await socket.SendAsync(query.ToArray(), deadline.Token);
            var response = (await socket.ReceiveAsync(deadline.Token)).Buffer;
            return response.Length > 12 && response[0] == 0x4b && response[1] == 0x54 && (response[2] & 0x80) != 0 && (response[3] & 15) == 0 && (response[6] != 0 || response[7] != 0);
        }
        catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    public static async Task Run(string xray, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        var node = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@127.0.0.1:19441?security=none");
        // Random reserved names cannot be answered by a pre-existing Windows cache,
        // a hosts file, public DNS or a previous fixture. Only this core knows them.
        foreach (bool oldAddress in new[] { true, false, false })
        {
            using var reserved = LoopbackPort.Reserve();
            var config = XrayRuntime.Build(node, reserved.Port, tun: true);
            string host = "kot-" + Guid.NewGuid().ToString("N") + ".invalid";
            string wireHost = "wire-" + Guid.NewGuid().ToString("N") + ".invalid";
            config["dns"]!["hosts"] = new JsonObject { [host] = new JsonArray("192.0.2.123", "2001:db8::123"), [wireHost] = "192.0.2.124" };
            var settings = config["inbounds"]!.AsArray().Single(i => i?["protocol"]?.ToString() == "tun")!["settings"]!;
            if (oldAddress) settings["dns"] = new JsonArray("172.28.231.1", "fdfe:dcba:231::1");
            var servers = settings["dns"]!.AsArray().Select(s => s!.ToString()).ToArray();
            string file = Path.Combine(Path.GetTempPath(), "kot-dns-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, config.ToJsonString()); NativeCore? core = null;
            try
            {
                KillSwitch.ArmForTests(Environment.ProcessPath!, xray);
                reserved.Dispose(); core = new NativeCore(xray, ["run", "-c", file], Path.GetDirectoryName(xray)!, "DNS regression", Console.WriteLine);
                bool ready = false;
                for (int i = 0; i < 100 && !core.HasExited; i++)
                {
                    ready = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == "kot-tun" && n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().DnsAddresses.Select(a => a.ToString()).Order().SequenceEqual(servers.Order()));
                    if (ready) break;
                    await Task.Delay(100, ct);
                }
                check(ready && !core.HasExited, "production builder installs the configured Windows DNS addresses");
                KillSwitch.ArmForTests(Environment.ProcessPath!, xray, KillSwitch.TunnelInterface());
                // Confirm the whole core has started, not just an adapter left by the previous run.
                for (int i = 0; ; i++)
                {
                    using var listener = new TcpClient();
                    try { await listener.ConnectAsync(IPAddress.Loopback, reserved.Port, ct); break; }
                    catch (SocketException) when (i < 50 && !core.HasExited) { await Task.Delay(100, ct); }
                }
                if (oldAddress)
                {
                    check(!await Datagram(servers[0], wireHost, ct) && !await Datagram(servers[1], wireHost, ct), "regression reproduced: DNS at either adapter-local address never reaches Xray");
                    check(!await TunnelDns.Probe("https://" + host + "/", ct), "Windows resolver health rejects the old broken TUN DNS configuration");
                }
                else
                {
                    foreach (string server in servers) check(await Datagram(server, wireHost, ct), "native TUN DNS answers over " + server);
                    using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct); lookup.CancelAfter(TimeSpan.FromSeconds(8));
                    var addresses = await Dns.GetHostAddressesAsync(host, lookup.Token);
                    check(addresses.Contains(IPAddress.Parse("192.0.2.123")) && addresses.Contains(IPAddress.Parse("2001:db8::123")), "uncached Windows system DNS receives A and AAAA from the production TUN with WFP enabled");
                    check(await TunnelDns.Probe("https://" + host + "/", ct), "TUN health accepts working Windows DNS after reconnect");
                }
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
                try { await TunnelDns.Probe("https://" + host + "/", cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                check(stopped, "DNS health cancellation propagates without a background retry");
            }
            finally
            {
                try { if (core != null) { try { await core.Stop(); } finally { core.Dispose(); } } }
                finally { KillSwitch.Release(); File.Delete(file); }
            }
            check(!KillSwitch.Detect(), "DNS regression teardown releases its persistent filters");
        }
    }
}
