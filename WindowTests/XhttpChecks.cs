using Kot.Core;
using Kot.Windows;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
namespace Kot.WindowTests;

// Exercises the shipped single-core path, including its native Windows TUN.
public static class XhttpChecks
{
    static int Port() { using var reserved = LoopbackPort.Reserve(); return reserved.Port; }
    static async Task<bool> Open(string host, int port, CancellationToken ct)
    {
        using var socket = new TcpClient();
        try { await socket.ConnectAsync(host, port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct); return true; }
        catch (Exception ex) when (ex is SocketException or TimeoutException) { return false; }
    }
    static async Task<bool> Exchange(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
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
    public static async Task Run(string xray, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(110)); var ct = deadline.Token;
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
        PhysicalNetwork.Capture();
        const string uuid = "11111111-1111-4111-8111-111111111111";
        int serverPort = Port(); string file = Path.Combine(Path.GetTempPath(), "kot-xhttp-server-" + Guid.NewGuid().ToString("N") + ".json");
        var config = JsonNode.Parse($$$$"""{"log":{"loglevel":"warning"},"inbounds":[{"listen":"127.0.0.1","port":{{{{serverPort}}}},"protocol":"vless","settings":{"clients":[{"id":"{{{{uuid}}}}"}],"decryption":"none"},"streamSettings":{"network":"xhttp","xhttpSettings":{"path":"/fixture","mode":"auto"}}}],"outbounds":[{"protocol":"freedom","settings":{"finalRules":[{"action":"allow","ip":["127.0.0.0/8","::1/128"]}]}}]}""")!;
        config["outbounds"]![0]!["streamSettings"] = new JsonObject { ["sockopt"] = new JsonObject { ["interface"] = PhysicalNetwork.Interface } };
        File.WriteAllText(file, config.ToJsonString());
        var node = Subscriptions.ParseNode($"vless://{uuid}@127.0.0.1:{serverPort}?type=xhttp&path=%2Ffixture&mode=packet-up&security=none#XHTTP");
        Directory.CreateDirectory(Store.Folder);
        int before = Directory.GetFiles(Store.Folder, "probe-*.json").Length;
        using var fixturePermission = KillSwitch.AllowProbe(xray);
        NativeCore? server = null;
        try
        {
            server = new NativeCore(xray, ["run", "-c", file], Path.GetDirectoryName(xray)!, "XHTTP fixture server", _ => { });
            while (!await Open("127.0.0.1", serverPort, ct)) { if (server.HasExited) throw new Exception(await server.ReadRecentOutput(true)); await Task.Delay(100, ct); }
            await using (var probe = await ProbeCore.Start(node, ct, xray))
            {
                using var client = ConnectionTools.Client(probe.Port);
                check(await client.GetStringAsync($"http://127.0.0.1:{webPort}/", ct) == "OK", "one native Xray probe carries a real HTTP request over XHTTP");
                check(await Latency.Http($"http://127.0.0.1:{webPort}/", probe.Port, 3000, ct) >= 0, "native XHTTP HTTP latency measurement succeeds");
            }
            check(Directory.GetFiles(Store.Folder, "probe-*.json").Length == before, "XHTTP probe removes its credential-bearing configuration");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool rejected = false;
                try { await using var ignored = await ProbeCore.Start(node, cancelled.Token, xray); } catch (OperationCanceledException) { rejected = true; }
                check(rejected && Directory.GetFiles(Store.Folder, "probe-*.json").Length == before, "cancelled native XHTTP startup leaves no configuration or process");
            }
            string bundled = Path.Combine(AppContext.BaseDirectory, "core"); Directory.CreateDirectory(bundled);
            File.Copy(xray, Path.Combine(bundled, "xray.exe"), true);
            File.Copy(Path.Combine(Path.GetDirectoryName(xray)!, "wintun.dll"), Path.Combine(bundled, "wintun.dll"), true);
            var profile = new Profile { Nodes = [node], Selected = node.Id, ConnectionMode = "tun", KillSwitch = true };
            var connection = new Tunnel([$"http://127.0.0.1:{webPort}/"], TimeSpan.FromSeconds(5));
            try
            {
                await connection.Connect(profile).WaitAsync(TimeSpan.FromSeconds(25), ct);
                if (connection.State != "connected") Console.WriteLine(connection.Error + "\n" + connection.CoreDetails);
                check(connection.State == "connected" && connection.HasCore && KillSwitch.Detect(), "production native Tunnel.Connect completes XHTTP health checks with global WFP armed");
                check(await connection.Probe(ct), "connected native session carries a real forced-proxy HTTP response");
                check(await Exchange(ct), "real Windows TUN exchanges HTTPS via native XHTTP without transport recursion");
                var sample = await connection.ReadConnections(ct);
                check(sample != null && sample.DownloadTotal > 0 && sample.Count == -1, "production authenticated Xray metrics report actual TUN traffic");
                await using (var probe = await ProbeCore.Start(node, ct, xray))
                {
                    using var client = ConnectionTools.Client(probe.Port);
                    check(await client.GetStringAsync($"http://127.0.0.1:{webPort}/", ct) == "OK", "a temporary native probe also works while the production TUN is active");
                }
                await connection.Stop();
                check(KillSwitch.Detect(), "stopping the session preserves the guard until an intentional release");
                check(!await Exchange(ct), "after native TUN shutdown the guard prevents direct HTTPS leakage");
            }
            finally { await connection.Stop(); KillSwitch.Release(); }
            var unreachable = node with { Outbound = (JsonObject)node.Outbound.DeepClone() };
            unreachable.Outbound["server_port"] = Port();
            var failedProfile = new Profile { Nodes = [unreachable], Selected = unreachable.Id, ConnectionMode = "tun", KillSwitch = true };
            var failure = new Tunnel([$"http://127.0.0.1:{webPort}/"], TimeSpan.FromSeconds(2));
            try
            {
                await failure.Connect(failedProfile).WaitAsync(TimeSpan.FromSeconds(20), ct);
                check(failure.State == "idle" && failure.Error.Length > 0, "unreachable XHTTP exits connecting with an actionable error");
                check(failure.ProxyPort == 0 && !failure.HasCore && KillSwitch.Detect(), "failed native connection closes the sole core and preserves the guard");
                var pending = failure.Connect(failedProfile);
                await Task.Delay(300, ct); await failure.Stop(); await pending;
                check(failure.State == "idle" && failure.Error.Length == 0, "cancelling native startup completes without a timeout error");
                check(Directory.GetFiles(Store.Folder, "probe-*.json").Length == before && !File.Exists(Path.Combine(Store.Folder, "runtime.json")), "success, failure and cancellation remove credential configurations");
            }
            finally { await failure.Stop(); KillSwitch.Release(); }
            fixturePermission.Dispose();
            KillSwitch.ArmForTests(Environment.ProcessPath!, xray);
            check(!await Open("1.1.1.1", 443, ct), "WFP blocks an unpermitted executable");
            using (var first = KillSwitch.AllowProbe(Environment.ProcessPath!))
            {
                using (var second = KillSwitch.AllowProbe(Environment.ProcessPath!))
                    check(await Open("1.1.1.1", 443, ct), "concurrent probes obtain a scoped WFP permit");
                check(await Open("1.1.1.1", 443, ct), "one finished probe keeps the other probe's permit");
            }
            check(KillSwitch.Detect() && !await Open("1.1.1.1", 443, ct), "the last probe removes its permit and preserves the guard");
        }
        finally
        {
            deadline.Cancel(); web.Stop(); KillSwitch.Release();
            try { await serve; }
            finally { if (server != null) { try { await server.Stop(); } finally { server.Dispose(); } } File.Delete(file); }
        }
    }
}
