using Kot.Core;
using Kot.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace Kot.WindowTests;

internal static class PortableChecks
{
    static void Until(Func<bool> done, string operation, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException(operation); Application.DoEvents(); Thread.Sleep(20); }
    }
    static T Complete<T>(Task<T> task) { Until(() => task.IsCompleted, "portable task"); return task.GetAwaiter().GetResult(); }
    static void Complete(Task task) { Until(() => task.IsCompleted, "portable task"); task.GetAwaiter().GetResult(); }
    static string Script(CoreWebView2 web, string script) => JsonSerializer.Deserialize<string>(Complete(web.ExecuteScriptAsync(script))) ?? "";
    static bool Connect()
    {
        using var socket = new System.Net.Sockets.TcpClient();
        try { socket.ConnectAsync("1.1.1.1", 443).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); return true; } catch { return false; }
    }
    public static void Run(Action<bool, string> check, string screenshots)
    {
        check(AppRuntime.Portable, "fixture references the actual portable build");
        AppRuntime.Prepare(); Directory.CreateDirectory(screenshots);
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotVPN", "profile.bin");
        bool existed = File.Exists(installed); byte[]? original = existed ? File.ReadAllBytes(installed) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!); byte[] sentinel = "installed 0.6.1 profile must stay untouched"u8.ToArray(); File.WriteAllBytes(installed, sentinel);
        try
        {
            check(Store.Folder == Path.Combine(AppContext.BaseDirectory, "data") && !Store.Folder.StartsWith(Path.GetDirectoryName(installed)!, StringComparison.OrdinalIgnoreCase), "portable profile is always beside the executable, independent of working directory");
            check(Path.GetTempPath().StartsWith(Store.Folder, StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")!.StartsWith(Store.Folder, StringComparison.OrdinalIgnoreCase), "temporary files and child .NET extraction stay in data");
            check(AppRuntime.Scope != "KotVPN" && ShellIdentity.AppId != "Prodkot.Kot.Client" && AppRuntime.TunnelName != "kot-tun", "instance, shell and TUN identities are separate from installed kot");
            ShellIdentity.Initialize(); check(ShellIdentity.CurrentId() == ShellIdentity.AppId, "portable shell identity reaches Windows");
            bool rejected = false; try { Complete(Startup.Set(true)); } catch (UserError) { rejected = true; }
            Complete(Startup.Set(false)); check(rejected && !Complete(Startup.IsEnabled()), "portable never registers or removes an installed startup task");
            rejected = false; try { UpdateInstaller.Apply(); } catch (UserError) { rejected = true; }
            check(rejected, "Setup update is rejected before touching installer paths");
            var profile = new Profile { Name = "portable fixture", Theme = "dark", Startup = true, Tray = false }; profile.Services.CheckUpdates = false;
            Store.Save(profile); check(Store.Load().Name == profile.Name && !Store.Load().Startup && !File.ReadAllBytes(Path.Combine(Store.Folder, "profile.bin")).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(profile)), "local profile survives restart with encryption and without Windows startup");
            var device = DeviceIdentity.Get(); check(DeviceIdentity.Get().Hwid == device.Hwid && File.Exists(Path.Combine(Store.Folder, "device.bin")), "device identity is saved and reloaded in the same portable folder");
            using var window = new MainWindow(false) { ClientSize = new Size(800, 600) };
            var web = window.Controls.OfType<WebView2>().Single(); Process? browser = null;
            try
            {
                window.Show(); Until(() => web.CoreWebView2 != null, "portable WebView2 startup");
                var core = web.CoreWebView2;
                Until(() => Script(core, "document.getElementById('titleVersion')?.textContent||''").Contains("portable"), "portable production snapshot");
                browser = Process.GetProcessById(checked((int)core.BrowserProcessId));
                check(browser.MainModule!.FileName.StartsWith(AppRuntime.BrowserFolder, StringComparison.OrdinalIgnoreCase) && core.Environment.UserDataFolder.StartsWith(Store.Folder, StringComparison.OrdinalIgnoreCase), "real Windows WebView2 uses the bundled runtime and local browser profile");
                check(window.Text == "kot. portable", "test window is visibly distinguishable from installed kot");
                Script(core, "document.querySelector('.nav-button[data-page=settings]').click();'ok'");
                check(Script(core, "String(document.querySelector('[data-setting=startup]').disabled&&document.getElementById('onlineUpdateButton').disabled)") == "true", "production portable UI disables persistent startup and installer updates");
                foreach (string theme in new[] { "dark", "light" })
                {
                    Script(core, "document.querySelector('[data-theme=" + theme + "]').click();'ok'");
                    Until(() => Store.Load().Theme == theme && Script(core, "String(document.body.classList.contains('theme-light'))") == (theme == "light" ? "true" : "false"), "saved portable theme");
                    using var output = File.Create(Path.Combine(screenshots, "portable-" + theme + "-800x600.png")); Complete(core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output));
                }
                Script(core, "document.getElementById('statisticsButton').click();'ok'");
                check(Script(core, "String(!document.getElementById('page-statistics').hidden)") == "true", "portable opens the existing statistics page");
                Script(core, "document.getElementById('statisticsBack').click();'ok'");
                check(Script(core, "String(!document.getElementById('page-settings').hidden&&!!document.querySelector('.nav-button[data-page=services]'))") == "true", "statistics returns to settings and Services stays in the menu");
            }
            finally
            {
                try { window.Close(); Until(() => window.IsDisposed, "portable shutdown"); }
                finally
                {
                    if (browser != null)
                    {
                        try { Until(() => browser.HasExited, "portable browser disposal", 15); }
                        finally { if (!browser.HasExited) { browser.Kill(true); browser.WaitForExit(5000); } browser.Dispose(); }
                    }
                }
            }
            check(Connect(), "portable WFP fixture has direct TCP before protection");
            string marker = Path.Combine(Store.Folder, "temp", "wfp-ready");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }; start.ArgumentList.Add("--portable-wfp-owner"); start.ArgumentList.Add(marker);
            using (var owner = Process.Start(start)!)
            {
                try
                {
                    Until(() => File.Exists(marker) || owner.HasExited, "portable WFP owner startup"); check(!owner.HasExited && !Connect(), "dynamic WFP filters really block direct TCP while the portable owner runs");
                    owner.Kill(); owner.WaitForExit(5000); Until(Connect, "dynamic WFP release after abrupt exit", 10);
                    check(Connect(), "Windows removes portable filters after abrupt process termination");
                }
                finally { if (!owner.HasExited) { owner.Kill(true); owner.WaitForExit(5000); } File.Delete(marker); }
            }
            check(File.ReadAllBytes(installed).AsSpan().SequenceEqual(sentinel), "installed 0.6.1 profile is byte-for-byte unchanged");
            var cleanup = Stopwatch.StartNew();
            while (Directory.Exists(Store.Folder))
            {
                try { Directory.Delete(Store.Folder, true); }
                catch (IOException) when (cleanup.Elapsed.TotalSeconds < 15) { Thread.Sleep(100); }
            }
            AppRuntime.Prepare();
            check(Store.Load().Name == "Подписка" && Store.Load().Nodes.Count == 0 && !File.Exists(Path.Combine(Store.Folder, "device.bin")), "deleting the portable data folder leaves a fresh profile with no restored installed data");
        }
        finally { KillSwitch.Release(); if (original != null) File.WriteAllBytes(installed, original); else File.Delete(installed); }
    }
    public static void WfpOwner(string marker)
    {
        KillSwitch.ArmForTests(Environment.ProcessPath!, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
        File.WriteAllText(marker, "ready"); Thread.Sleep(60000);
    }
}
