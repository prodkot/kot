using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    readonly System.Windows.Forms.Timer browserWatch = new() { Interval = 1000 };
    readonly Queue<DateTimeOffset> browserFailures = new();
    TaskCompletionSource? browserExited;
    DateTimeOffset browserDeadline;
    bool browserRecovering, applicationStarted, interfaceStarted, updateReceiptAcknowledged;
    int unresponsiveEvents, browserGeneration;
    DateTimeOffset lastUnresponsive;
    internal bool InterfaceReady => bridgeReady && !loading.Visible;
    internal uint BrowserProcessId => web.CoreWebView2?.BrowserProcessId ?? 0;
    internal CoreWebView2 InterfaceCore => web.CoreWebView2;
    internal Task CloseForTests() => ExitApp();

    void WatchBrowser(CoreWebView2Environment environment, CoreWebView2 core)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); browserExited = exited;
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        core.ProcessFailed += (_, e) =>
        {
            if (exiting || web.IsDisposed || web.CoreWebView2 != core) return;
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                if (DateTimeOffset.UtcNow - lastUnresponsive > TimeSpan.FromSeconds(40)) unresponsiveEvents = 0;
                lastUnresponsive = DateTimeOffset.UtcNow;
                if (++unresponsiveEvents < 3) return;
            }
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                RecoverBrowser("Процесс интерфейса: " + e.ProcessFailedKind);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && !exiting && !web.IsDisposed && web.CoreWebView2 == core)
                RecoverBrowser("Не загружен интерфейс: " + e.WebErrorStatus);
        };
        browserDeadline = DateTimeOffset.UtcNow.AddSeconds(30); browserWatch.Start();
    }
    void InitBrowserWatch()
    {
        browserWatch.Tick += (_, _) =>
        {
            if (!exiting && !browserRecovering && DateTimeOffset.UtcNow >= browserDeadline) RecoverBrowser("Интерфейс не ответил за 30 секунд.");
        };
    }
    void BrowserReady()
    {
        if (!updateReceiptAcknowledged) { UpdateInstaller.Ready(); updateReceiptAcknowledged = true; }
        browserWatch.Stop(); unresponsiveEvents = 0; loading.ClearFailure(); loading.Hide();
    }
    void BrowserFailure(Exception error)
    {
        AppLog.Error("interface", error); bridgeReady = false; browserWatch.Stop();
        if (exiting || IsDisposed) return;
        loading.ShowFailure("Не удалось открыть интерфейс. Соединением можно управлять через меню kot. в трее.", () =>
        {
            browserFailures.Clear(); RecoverBrowser("Повторный запуск интерфейса.");
        });
        loading.BringToFront();
    }
    void RecoverBrowser(string reason)
    {
        if (exiting || IsDisposed || browserRecovering) return;
        browserGeneration++; bridgeReady = false; browserWatch.Stop(); AppLog.Write("interface", reason);
        while (browserFailures.TryPeek(out var old) && DateTimeOffset.UtcNow - old > TimeSpan.FromMinutes(2)) browserFailures.Dequeue();
        if (browserFailures.Count >= 3) { BrowserFailure(new Kot.Core.UserError("Интерфейс несколько раз завершился с ошибкой.")); return; }
        browserFailures.Enqueue(DateTimeOffset.UtcNow);
        pendingTrayPage ??= visiblePage;
        loading.ClearFailure(); loading.Show(); loading.BringToFront(); browserRecovering = true;
        // Leave the WebView event callback before disposing its control.
        _ = RestartBrowser();
    }
    async Task RestartBrowser()
    {
        try
        {
            await Task.Yield();
            var previous = web; var exited = browserExited; browserExited = null;
            Controls.Remove(previous); previous.Dispose();
            if (exited != null) await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            if (exiting) return;
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web); loading.BringToFront();
            await Initialize();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) { BrowserFailure(ex); }
        finally { browserRecovering = false; }
    }
}
