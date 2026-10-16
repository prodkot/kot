using Kot.Core;
using Kot.Windows;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
    static async Task RequestBridge(XhttpBridge bridge, int webPort, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{bridge.Port}") { Credentials = new NetworkCredential(bridge.Username, bridge.Password) }, UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try { using var response = await client.GetAsync($"http://127.0.0.1:{webPort}/", ct); }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { Console.WriteLine("QUIC fixture request: " + ex.GetType().Name + ": " + ex.Message); }
    }
    static async Task QuicRouting(string xray, Node h3, XraySession session, int webPort, ConcurrentQueue<string> lines, CancellationToken ct, Action<bool, string> check)
    {
        // Google is only a QUIC routing target, not an XHTTP test server. Check
        // the selected outbound, without depending on a remote HTTP response.
        const string target = "8.8.8.8:443";
        string legacyFile = Path.Combine(Store.Folder, "xhttp-legacy-" + Guid.NewGuid().ToString("N") + ".json");
        var bridge = new XhttpBridge(Port(), "legacy-fixture", new string('l', 64));
        try
        {
            File.WriteAllText(legacyFile, Xhttp.Build([h3], new Dictionary<string, XhttpBridge> { [h3.Id] = bridge }).ToJsonString());
            using var legacy = new NativeCore(xray, ["run", "-c", legacyFile], Path.GetDirectoryName(xray)!, "legacy QUIC routing fixture", _ => { });
            while (!await Open("127.0.0.1", bridge.Port, ct)) { if (legacy.HasExited) throw new Exception(await legacy.ReadRecentOutput(true)); await Task.Delay(100, ct); }
            lines.Clear();
            await RequestBridge(bridge, webPort, ct);
            // Observe the previous process-based bypass for comparison. Its
            // result depends on the Windows socket and process lookup behavior.
            bool loop = lines.Any(line => line.Contains("outbound/socks[proxy]: outbound packet connection to " + target));
            Console.WriteLine("LEGACY QUIC process bypass enters proxy: " + loop);
            if (!loop) Console.WriteLine("Legacy Xray routing output:\n" + CoreDiagnostics.Redact(await legacy.ReadRecentOutput(false), null));
            await legacy.Stop();
        }
        finally { File.Delete(legacyFile); }
        lines.Clear();
        await RequestBridge(session.Bridges[h3.Id], webPort, ct);
        var observed = lines.ToArray();
        static string ConnectionId(string line) => Regex.Match(line, @"\[(\d+) [^\]]+\]").Groups[1].Value;
        // Authenticated SOCKS logs include the username before the destination;
        // direct UDP logs omit the destination. Correlate the connection IDs.
        var transportIds = observed.Where(line => line.Contains("inbound/socks[xray-direct-in]:") && line.Contains("inbound packet connection to " + target))
            .Select(ConnectionId).Where(id => id.Length > 0).ToHashSet();
        bool direct = observed.Any(line => transportIds.Contains(ConnectionId(line)) && line.Contains("outbound/direct[direct]: outbound packet connection"));
        if (!direct)
        {
            Console.WriteLine("Xray HTTP/3 routing output:\n" + await session.RecentOutput());
            Console.WriteLine("sing-box HTTP/3 routing output:\n" + string.Join('\n', lines));
        }
        check(direct, "HTTP/3 transport reaches the physical direct dialer with Windows TUN and WFP active");
        check(!observed.Any(line => line.Contains("outbound/socks[proxy]:") && (transportIds.Contains(ConnectionId(line)) || line.Contains("outbound packet connection to " + target))), "HTTP/3 transport never loops back into its own XHTTP outbound");
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
            var h3 = Subscriptions.ParseNode($"vless://{uuid}@8.8.8.8:443?type=xhttp&security=tls&sni=dns.google&fp=firefox&alpn=h3&path=%2Fkot-fixture&mode=auto&extra=" + Uri.EscapeDataString("""{"xmux":{"maxConnections":3,"hMaxRequestTimes":"600-900","hMaxReusableSecs":"1800-3000"}}"""));
            session = await XraySession.Start([node, h3], ct, xray) ?? throw new Exception("Missing bridge");
            int bridgePort = session.Bridges[node.Id].Port;
            var profile = new Profile { Nodes = [node], Selected = node.Id, ConnectionMode = "tun" };
            var configuration = Configuration.BuildConnection(profile, Port(), xhttp: session.Bridges, xhttpEgress: session.Egress);
            // Retain the previous bypass only for the comparison fixture. The
            // authenticated relay must select direct without relying on it.
            Xhttp.BypassCore(configuration, xray);
            File.WriteAllText(tunFile, configuration.ToJsonString());
            check(await Open("1.1.1.1", 443, ct), "XHTTP TUN baseline reaches a real TCP destination");
            KillSwitch.ArmForTests(Environment.ProcessPath!, singBox, extraAllowed: xray);
            var lines = new ConcurrentQueue<string>();
            tun = new NativeCore(singBox, tunFile, line => { lines.Enqueue(CoreDiagnostics.Redact(line, null)); while (lines.Count > 300) lines.TryDequeue(out _); });
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
            await QuicRouting(xray, h3, session, webPort, lines, ct, check);
            await session.DisposeAsync(); session = null;
            check(!await Open("127.0.0.1", bridgePort, ct), "disposing XHTTP stops its native job and closes the bridge");
            // A live TUN stack can acknowledge TCP before its outbound connects.
            // Require a real server response, rather than trusting that handshake.
            check(KillSwitch.Detect() && !await Exchange(ct), "XHTTP core termination preserves the guard and cannot exchange data directly");
            await tun.Stop(); tun.Dispose(); tun = null; KillSwitch.Release();
            // Exercise the application's actual startup and health checks with
            // global WFP filters, not only a separately launched TUN fixture.
            string bundled = Path.Combine(AppContext.BaseDirectory, "core"); Directory.CreateDirectory(bundled);
            File.Copy(singBox, Path.Combine(bundled, "sing-box.exe"), true);
            File.Copy(xray, Path.Combine(bundled, "xray.exe"), true);
            var connection = new Tunnel([$"http://127.0.0.1:{webPort}/"], TimeSpan.FromSeconds(5));
            profile.KillSwitch = true;
            try
            {
                await connection.Connect(profile).WaitAsync(TimeSpan.FromSeconds(20), ct);
                check(connection.State == "connected" && connection.UsesXhttp && KillSwitch.Detect(), "production Tunnel.Connect completes XHTTP health checks with global WFP armed");
                check(await connection.Probe(ct), "connected production XHTTP session carries a real HTTP response");
                await connection.Stop();
                check(KillSwitch.Detect(), "stopping the session preserves the guard until an intentional release");
            }
            finally { await connection.Stop(); KillSwitch.Release(); }
            var unreachable = node with { Outbound = (System.Text.Json.Nodes.JsonObject)node.Outbound.DeepClone() };
            unreachable.Outbound["server_port"] = Port();
            var failedProfile = new Profile { Nodes = [unreachable], Selected = unreachable.Id, ConnectionMode = "tun", KillSwitch = true };
            var failure = new Tunnel([$"http://127.0.0.1:{webPort}/"], TimeSpan.FromSeconds(2));
            try
            {
                await failure.Connect(failedProfile).WaitAsync(TimeSpan.FromSeconds(20), ct);
                check(failure.State == "idle" && failure.Error.Length > 0 && failure.CoreDetails.Contains("Xray:"), "unreachable XHTTP exits connecting and includes native Xray diagnostics");
                check(failure.ProxyPort == 0 && !failure.UsesXhttp && KillSwitch.Detect(), "failed production XHTTP cleans up both cores and preserves the guard");
                var pending = failure.Connect(failedProfile);
                await Task.Delay(300, ct); await failure.Stop(); await pending;
                check(failure.State == "idle" && failure.Error.Length == 0, "cancelling production XHTTP completes without a timeout error");
                check(Directory.GetFiles(Store.Folder, "xhttp-*.json").Length == before && !File.Exists(Path.Combine(Store.Folder, "runtime.json")), "production XHTTP success, failure and cancellation remove credential configurations");
            }
            finally { await failure.Stop(); KillSwitch.Release(); }
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
