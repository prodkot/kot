using Kot.Windows;
using System.Diagnostics;
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
}
