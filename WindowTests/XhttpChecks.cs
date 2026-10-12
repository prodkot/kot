using Kot.Core;
using Kot.Windows;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
namespace Kot.WindowTests;

public static class XhttpChecks
{
    [DllImport("iphlpapi.dll")] static extern uint GetBestInterface(uint address, out uint index);
    [DllImport("iphlpapi.dll")] static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
    static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    static async Task<bool> Open(string host, int port, CancellationToken ct)
    {
        using var socket = new TcpClient();
        try { await socket.ConnectAsync(host, port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct); return true; }
        catch (Exception ex) when (ex is SocketException or TimeoutException) { return false; }
    }
    static async Task<bool> Exchange(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync("1.1.1.1", 443, timeout.Token);
            using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "one.one.one.one" }, timeout.Token);
            await tls.WriteAsync("HEAD / HTTP/1.1\r\nHost: one.one.one.one\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, true);
            return (await reader.ReadLineAsync(timeout.Token))?.StartsWith("HTTP/1.", StringComparison.Ordinal) == true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }
    public static async Task Run(string singBox, string xray, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100)); var ct = deadline.Token;
        using var web = new TcpListener(IPAddress.Loopback, 0); web.Start(); int webPort = ((IPEndPoint)web.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await web.AcceptTcpClientAsync(ct); using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray(), ct);
                }
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }, ct);
        const string uuid = "11111111-1111-4111-8111-111111111111";
        int serverPort = Port(); string file = Path.Combine(Path.GetTempPath(), "kot-xhttp-server-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, $$$$"""{"log":{"loglevel":"warning"},"inbounds":[{"listen":"127.0.0.1","port":{{{{serverPort}}}},"protocol":"vless","settings":{"clients":[{"id":"{{{{uuid}}}}"}],"decryption":"none"},"streamSettings":{"network":"xhttp","xhttpSettings":{"path":"/fixture","mode":"auto"}}}],"outbounds":[{"protocol":"freedom"}]}""");
        var node = Subscriptions.ParseNode($"vless://{uuid}@127.0.0.1:{serverPort}?type=xhttp&path=%2Ffixture&mode=packet-up&security=none#XHTTP");
        NativeCore? server = null, tun = null; XraySession? session = null;
        string tunFile = Path.Combine(Path.GetTempPath(), "kot-xhttp-tun-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Store.Folder);
        int before = Directory.GetFiles(Store.Folder, "xhttp-*.json").Length;
        try
        {
            server = new NativeCore(xray, ["run", "-c", file], Path.GetDirectoryName(xray)!, "XHTTP fixture server", _ => { });
            while (!await Open("127.0.0.1", serverPort, ct)) { if (server.HasExited) throw new Exception(await server.ReadRecentOutput(true)); await Task.Delay(100, ct); }
            await using (var probe = await ProbeCore.Start(node, ct, singBox, xray))
            {
                using var client = ConnectionTools.Client(probe.Port);
                check(await client.GetStringAsync($"http://127.0.0.1:{webPort}/", ct) == "OK", "native probe carries HTTP through sing-box and an authenticated XHTTP bridge");
                check(await Latency.Http($"http://127.0.0.1:{webPort}/", probe.Port, 3000, ct) >= 0, "native XHTTP HTTP latency measurement succeeds");
            }
            check(Directory.GetFiles(Store.Folder, "xhttp-*.json").Length == before, "XHTTP probe removes its credential-bearing configuration");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool rejected = false;
                try { await using var ignored = await XraySession.Start([node], cancelled.Token, xray); } catch (OperationCanceledException) { rejected = true; }
                check(rejected && Directory.GetFiles(Store.Folder, "xhttp-*.json").Length == before, "cancelled XHTTP startup leaves no configuration or process");
            }
            var invalid = node with { Outbound = (System.Text.Json.Nodes.JsonObject)node.Outbound.DeepClone() };
            invalid.Outbound["tls"] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true, ["utls"] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true, ["fingerprint"] = "invalid-fixture" } };
            bool failed = false;
            try { await using var ignored = await XraySession.Start([invalid], ct, xray); } catch (UserError) { failed = true; }
            check(failed && Directory.GetFiles(Store.Folder, "xhttp-*.json").Length == before, "Xray preflight rejects unsupported settings and cleans up");
            session = await XraySession.Start([node], ct, xray) ?? throw new Exception("Missing bridge");
            int bridgePort = session.Bridges[node.Id].Port;
            var profile = new Profile { Nodes = [node], Selected = node.Id, ConnectionMode = "tun" };
            File.WriteAllText(tunFile, Configuration.BuildConnection(profile, Port(), xhttp: session.Bridges, xrayExecutable: xray).ToJsonString());
            check(await Open("1.1.1.1", 443, ct), "XHTTP TUN baseline reaches a real TCP destination");
            KillSwitch.ArmForTests(Environment.ProcessPath!, singBox, extraAllowed: xray);
            tun = new NativeCore(singBox, tunFile, _ => { });
            ulong luid = 0;
            while (luid == 0) { if (tun.HasExited) throw new Exception(await tun.ReadRecentOutput(true)); try { luid = KillSwitch.TunnelInterface(); } catch (UserError) { await Task.Delay(100, ct); } }
            KillSwitch.ArmForTests(Environment.ProcessPath!, singBox, luid, xray);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (tun.HasExited) throw new Exception(await tun.ReadRecentOutput(true));
                if (GetBestInterface(0x01010101, out var index) == 0 && ConvertInterfaceIndexToLuid(index, out var route) == 0 && route == luid && await Exchange(ct)) break;
                await Task.Delay(200, ct);
            }
            check(true, "real Windows TUN exchanges HTTPS via XHTTP without looping Xray traffic");
            await session.DisposeAsync(); session = null;
            check(!await Open("127.0.0.1", bridgePort, ct), "disposing XHTTP stops its native job and closes the bridge");
            // A live TUN stack can acknowledge TCP before its outbound connects.
            // Require a real server response, rather than trusting that handshake.
            check(KillSwitch.Detect() && !await Exchange(ct), "XHTTP core termination preserves the guard and cannot exchange data directly");
            await tun.Stop(); tun.Dispose(); tun = null; KillSwitch.Release();
            KillSwitch.ArmForTests(Environment.ProcessPath!, singBox, extraAllowed: Environment.ProcessPath!);
            check(await Open("1.1.1.1", 443, ct), "WFP permits the exact additional core executable");
            KillSwitch.ArmForTests(Environment.ProcessPath!, singBox);
            check(!await Open("1.1.1.1", 443, ct), "rearming without XHTTP removes the additional WFP permit");
            using (var first = KillSwitch.AllowXhttpProbe(Environment.ProcessPath!))
            {
                using (var second = KillSwitch.AllowXhttpProbe(Environment.ProcessPath!))
                    check(await Open("1.1.1.1", 443, ct), "concurrent XHTTP probes obtain a scoped WFP permit");
                check(await Open("1.1.1.1", 443, ct), "one finished probe keeps another probe's WFP permit");
            }
            check(KillSwitch.Detect() && !await Open("1.1.1.1", 443, ct), "the last XHTTP probe removes its permit and preserves the active guard");
        }
        finally
        {
            deadline.Cancel(); web.Stop(); KillSwitch.Release();
            try { await serve; }
            finally
            {
                if (tun != null) { try { await tun.Stop(); } finally { tun.Dispose(); } }
                if (session != null) await session.DisposeAsync();
                if (server != null) { try { await server.Stop(); } finally { server.Dispose(); } }
                File.Delete(file); File.Delete(tunFile);
            }
        }
    }
}
