using Kot.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
internal static partial class ProxyFixture
{
    public static void Run(Action<bool, string> check)
    {
        SystemProxy.Restore(); var original = SystemProxy.Read();
        string marker = Path.Combine(Path.GetTempPath(), "kot-proxy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = new SystemProxy.Settings(13, "127.0.0.1:18731", "*.example.org;<local>", "https://example.invalid/proxy.pac");
            SystemProxy.Write(baseline); baseline = SystemProxy.Read();
            RequestCheck(check);
            SystemProxy.Enable(18732);
            check(SystemProxy.Read().Server == "127.0.0.1:18732" && SystemProxy.Read().Flags == 3 && SystemProxy.Read().AutoUrl == "", "system proxy replaces PAC and auto-detection for this connection");
            SystemProxy.Restore(); SystemProxy.Restore();
            check(SystemProxy.Read() == baseline, "disconnect restores proxy, bypass, PAC and auto-detection exactly; repeated restore is safe");
            SystemProxy.Enable(18732);
            var external = new SystemProxy.Settings(3, "127.0.0.1:18733", "localhost", "");
            SystemProxy.Write(external); external = SystemProxy.Read();
            SystemProxy.Restore();
            check(SystemProxy.Read() == external, "disconnect preserves later changes made outside kot");
            SystemProxy.Write(baseline);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--proxy-owner"); start.ArgumentList.Add("18732"); start.ArgumentList.Add(marker);
            using var child = Process.Start(start)!;
            try
            {
                var timer = Stopwatch.StartNew();
                while (!File.Exists(marker) && !child.HasExited && timer.Elapsed.TotalSeconds < 15) Thread.Sleep(100);
                check(File.Exists(marker) && SystemProxy.Read().Server == "127.0.0.1:18732", "separate process applies a recoverable system proxy");
                child.Kill(); child.WaitForExit(); timer.Restart();
                while (SystemProxy.Read() != baseline && timer.Elapsed.TotalSeconds < 15) Thread.Sleep(100);
                check(SystemProxy.Read() == baseline, "watcher restores Windows proxy after abrupt client termination");
            }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }

        }
        finally { SystemProxy.Restore(); SystemProxy.Write(original); File.Delete(marker); }
    }
    static void RequestCheck(Action<bool, string> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string request = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "";
            while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nkot"));
            return request;
        });
        try
        {
            SystemProxy.Enable(port);
            var session = InternetOpen("kot proxy fixture", 0, null, null, 0);
            check(session != IntPtr.Zero, "WinINet session opens with Windows proxy settings");
            try
            {
                uint timeout = 5000;
                check(InternetSetOption(session, 2, ref timeout, 4) && InternetSetOption(session, 6, ref timeout, 4), "WinINet fixture has bounded connection and receive timeouts");
                var request = InternetOpenUrl(session, "http://kot-proxy-fixture.invalid/", null, 0, 0x84000000, IntPtr.Zero);
                check(request != IntPtr.Zero, "HTTP request reaches the local proxy using Windows settings");
                try
                {
                    byte[] body = new byte[3];
                    check(InternetReadFile(request, body, 3, out var count) && count == 3 && Encoding.ASCII.GetString(body) == "kot", "system proxy returns the HTTP response");
                }
                finally { InternetCloseHandle(request); }
            }
            finally { InternetCloseHandle(session); }
            check(reply.GetAwaiter().GetResult().StartsWith("GET http://kot-proxy-fixture.invalid/ "), "Windows sends the absolute target URL to the configured proxy");
        }
        finally { SystemProxy.Restore(); listener.Stop(); try { reply.GetAwaiter().GetResult(); } catch { } }
    }
    [DllImport("wininet.dll", EntryPoint = "InternetOpenW", CharSet = CharSet.Unicode)] static extern IntPtr InternetOpen(string agent, uint access, string? proxy, string? bypass, uint flags);
    [DllImport("wininet.dll", EntryPoint = "InternetOpenUrlW", CharSet = CharSet.Unicode)] static extern IntPtr InternetOpenUrl(IntPtr session, string url, string? headers, uint length, uint flags, IntPtr context);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW")] static extern bool InternetSetOption(IntPtr session, uint option, ref uint value, uint length);
    [DllImport("wininet.dll")] static extern bool InternetReadFile(IntPtr request, byte[] buffer, uint length, out uint count);
    [DllImport("wininet.dll")] static extern bool InternetCloseHandle(IntPtr handle);
}
