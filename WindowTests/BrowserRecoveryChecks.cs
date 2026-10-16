using Kot.Core;
using Kot.Windows;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace Kot.WindowTests;

static class BrowserRecoveryChecks
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    static void Pump(Func<bool> done, string message, int seconds = 45)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException(message + "\n" + AppLog.Snapshot());
            Application.DoEvents(); Thread.Sleep(10);
        }
    }
    static string Script(MainWindow app, string script)
    {
        var task = app.InterfaceCore.ExecuteScriptAsync(script); Pump(() => task.IsCompleted, "script timeout"); return task.GetAwaiter().GetResult();
    }
    public static void Run(Action<bool, string> check, string screenshots)
    {
        if (Directory.Exists(Store.Folder) && Directory.EnumerateFileSystemEntries(Store.Folder).Any())
            throw new InvalidOperationException("Browser recovery checks require an empty KotVPN data folder; existing data was left untouched.");
        Directory.CreateDirectory(Store.Folder); Directory.CreateDirectory(screenshots);
        var profile = new Profile(); profile.Services.CheckUpdates = false;
        using var app = new MainWindow(false, initialProfile: profile);
        var owned = new List<Process>();
        try
        {
            app.Show(); Pump(() => app.InterfaceReady, "initial UI did not become ready");
            owned.Add(Process.GetProcessById(checked((int)app.BrowserProcessId)));
            for (int i = 0; i < 4; i++)
            {
                Script(app, "document.querySelector('[data-page=settings]').click()");
                Pump(() => Script(app, "document.getElementById('page-settings').hidden") == "false", "settings page did not open");
                int old = checked((int)app.BrowserProcessId), generation = app.InterfaceGeneration;
                using var process = Process.GetProcessById(old);
                check(ProcessParent.Id(process.Handle) == Environment.ProcessId, "crash fixture owns its WebView2 browser");
                if (i == 0)
                {
                    var renderer = app.InterfaceCore.Environment.GetProcessInfos().First(p => p.Kind == CoreWebView2ProcessKind.Renderer);
                    using var child = Process.GetProcessById(renderer.ProcessId); child.Kill(); child.WaitForExit(5000);
                }
                else { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                if (i < 3)
                {
                    Pump(() => app.InterfaceGeneration > generation && app.InterfaceReady && app.BrowserProcessId != old, "UI did not recover from process termination");
                    owned.Add(Process.GetProcessById(checked((int)app.BrowserProcessId)));
                    check(Script(app, "document.getElementById('page-settings').hidden") == "false", "renderer/browser recovery restores the last page " + i);
                }
                else
                {
                    Pump(() => app.Controls.Find("retryInterface", true).Any(c => c.Visible), "repeated crash did not expose native recovery");
                    var retry = (Button)app.Controls.Find("retryInterface", true).Single();
                    check(retry.Enabled && app.Controls.Find("close", true).Single().Visible, "native recovery retains retry and window controls");
                    app.Activate(); Application.DoEvents();
                    check(WindowFromPoint(retry.PointToScreen(new Point(retry.Width / 2, retry.Height / 2))) == retry.Handle, "failed browser does not obscure the native retry button");
                    var close = app.Controls.Find("close", true).Single();
                    check(WindowFromPoint(close.PointToScreen(new Point(close.Width / 2, close.Height / 2))) == close.Handle, "native close button remains above the failure panel");
                    app.Refresh(); Application.DoEvents(); DwmFlush();
                    using var bitmap = new Bitmap(app.Width, app.Height);
                    using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(app.Location, Point.Empty, bitmap.Size);
                    bitmap.Save(Path.Combine(screenshots, "interface-recovery.png"), ImageFormat.Png);
                    int failedGeneration = app.InterfaceGeneration;
                    retry.PerformClick();
                    check(app.InterfaceGeneration > failedGeneration, "native retry click starts a new recovery attempt");
                    Pump(() => app.InterfaceReady, "manual retry did not restore UI");
                    owned.Add(Process.GetProcessById(checked((int)app.BrowserProcessId)));
                    check(Script(app, "document.getElementById('page-settings').hidden") == "false", "manual retry resets the crash limit and restores usable UI");
                }
            }
        }
        catch
        {
            File.WriteAllText(Path.Combine(screenshots, "browser-recovery-error.txt"), AppLog.Snapshot());
            throw;
        }
        finally
        {
            var closing = app.CloseForTests(); Pump(() => closing.IsCompleted, "application shutdown did not finish"); closing.GetAwaiter().GetResult(); app.Dispose();
            foreach (var process in owned)
            {
                try { if (!process.HasExited && !process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
                finally { process.Dispose(); }
            }
            // The fixture refused all pre-existing data before creating this folder.
            for (int i = 0; i < 30 && Directory.Exists(Store.Folder); i++)
            {
                try { Directory.Delete(Store.Folder, true); } catch (IOException) { Application.DoEvents(); Thread.Sleep(100); }
            }
        }
    }
}
