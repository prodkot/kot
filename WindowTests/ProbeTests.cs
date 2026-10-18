using Kot.Core;
using Kot.Windows;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace Kot.WindowTests;

public static class ProbeTests
{
    public static async Task Run(string executable, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int webPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int vlessPort = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        string file = Path.Combine(Path.GetTempPath(), "kot-probe-server-" + Guid.NewGuid().ToString("N") + ".json");
        const string uuid = "11111111-1111-4111-8111-111111111111";
        File.WriteAllText(file, $$$$"""{"inbounds":[{"protocol":"vless","listen":"127.0.0.1","port":{{{{vlessPort}}}},"settings":{"clients":[{"id":"{{{{uuid}}}}"}],"decryption":"none"}}],"outbounds":[{"protocol":"freedom","settings":{"finalRules":[{"action":"allow","ip":["127.0.0.0/8","::1/128"]}]}}]}""");
        int requests = 0;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (!deadline.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                    await using var stream = connection.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                    Interlocked.Increment(ref requests);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray(), deadline.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (deadline.IsCancellationRequested && ex is SocketException or ObjectDisposedException) { }
        }, deadline.Token);
        int ConfigCount() => Directory.Exists(Store.Folder) ? Directory.GetFiles(Store.Folder, "probe-*.json").Length : 0;
        int before = ConfigCount();
        NativeCore? server = null;
        try
        {
            server = new NativeCore(executable, file, _ => { });
            while (true)
            {
                if (server.HasExited) throw new Exception("Local VLESS fixture exited: " + await server.ReadRecentOutput(true));
                try { await Latency.Tcp("127.0.0.1", vlessPort, 250, deadline.Token); break; }
                catch (Exception ex) when (!deadline.IsCancellationRequested && ex is SocketException or OperationCanceledException) { }
                await Task.Delay(100, deadline.Token);
            }
            await using (var probe = await ProbeCore.Start(Subscriptions.ParseNode($"vless://{uuid}@127.0.0.1:{vlessPort}?security=none#Probe"), deadline.Token, executable))
            {
                using var client = ConnectionTools.Client(probe.Port);
                check(await client.GetStringAsync($"http://127.0.0.1:{webPort}/", deadline.Token) == "OK", "native temporary core carries an HTTP request through a real VLESS server");
                List<int?> samples = [];
                for (int i = 0; i < 10; i++) samples.Add(await Latency.Http($"http://127.0.0.1:{webPort}/", probe.Port, 3000, deadline.Token));
                check(ConnectionTools.Summarize(samples).Successes == 10 && Volatile.Read(ref requests) == 11, "native quality check records ten real requests through the isolated core");
            }
            check(ConfigCount() == before, "temporary core uses stdin without leaving a credential configuration");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
            try { await using var probe = await ProbeCore.Start(Subscriptions.ParseNode($"vless://{uuid}@127.0.0.1:{vlessPort}?security=none"), cancelled.Token, executable); } catch (OperationCanceledException) { stopped = true; }
            check(stopped && ConfigCount() == before, "cancelled native probe leaves no process or temporary configuration");
        }
        finally
        {
            deadline.Cancel(); listener.Stop();
            try { await serve; }
            finally
            {
                try { if (server != null) { try { await server.Stop(); } finally { server.Dispose(); } } }
                finally { File.Delete(file); }
            }
        }
    }
}
