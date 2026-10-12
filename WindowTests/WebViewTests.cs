using Kot.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Text.Json;
namespace Kot.WindowTests;

static class WebViewTests
{
    static void PumpUntil(Func<bool> done, string operation, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException(operation);
            Application.DoEvents(); Thread.Sleep(10);
        }
    }
    static T Complete<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, "WebView task timed out"); return task.GetAwaiter().GetResult(); }
    static void Complete(Task task) { PumpUntil(() => task.IsCompleted, "WebView task timed out"); task.GetAwaiter().GetResult(); }
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "kot-webview-" + Guid.NewGuid().ToString("N"));
        using var form = new Form { ClientSize = new Size(960, 640), ShowInTaskbar = false };
        using var web = new WebView2 { Dock = DockStyle.Fill };
        using var icon = new Icon(Path.Combine(AppContext.BaseDirectory, "kot.ico"));
        using var loading = new StartupScreen("dark", icon, () => { }, () => { }, () => { }, () => { });
        Process? browser = null;
        try
        {
            form.Controls.Add(web); form.Controls.Add(loading); loading.BringToFront(); form.Show();
            check(loading.Visible, "native loading screen is visible before WebView2 initialization");
            var env = Complete(WebViewHost.CreateEnvironment(folder));
            Complete(web.EnsureCoreWebView2Async(env));
            var core = web.CoreWebView2;
            browser = Process.GetProcessById(checked((int)core.BrowserProcessId));
            check(ProcessParent.Id(browser.Handle) == Environment.ProcessId, "private WebView2 browser remains a child of the kot host");
            core.SetVirtualHostNameToFolderMapping("kot.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            string? readyError = null, result = null; bool ready = false;
            core.WebMessageReceived += (_, e) =>
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                var root = message.RootElement;
                if (root.TryGetProperty("fixtureResult", out var returned)) { result = returned.GetString(); return; }
                string? action = root.GetProperty("action").GetString();
                try
                {
                    if (action == "uiReady") loading.Hide();
                    if (action == "readHwid")
                    {
                        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "reply", id = root.GetProperty("id").GetInt32(), ok = true, data = new { hwid = new string('A', 64), name = "kot fixture" } }));
                        return;
                    }
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "reply", id = root.GetProperty("id").GetInt32(), ok = true }));
                    if (action != "ready") return;
                    core.PostWebMessageAsJson("""
                    {"kind":"snapshot","data":{"nodes":[],"selected":"","state":"idle","hasSubscription":false,"settings":{"theme":"dark","accent":"lime","mode":"tun","tray":true},"version":"fixture"}}
                    """);
                    ready = true;
                }
                catch (Exception error) { readyError = error.Message; ready = true; }
            };
            core.Navigate("https://kot.local/index.html");
            PumpUntil(() => ready, "production UI did not establish its WebView2 bridge");
            check(readyError == null, "production bridge delivers a snapshot in native WebView2");
            // Execute on the actual Windows runtime: UI, font/CSP, top layer and interrupted animation.
            Complete(core.ExecuteScriptAsync("""
            (async()=>{
              const $=id=>document.getElementById(id),pause=ms=>new Promise(r=>setTimeout(r,ms));
              await pause(100);await document.fonts.ready;
              if(!$('startupScreen').hidden||document.querySelector('.main').inert||document.querySelector('.sidebar').inert)throw Error('startup overlay');
              if($('selectedName').textContent!=='Добавить подписку'||!document.fonts.check('600 14px Manrope','Соединение'))throw Error('UI/font');
              document.querySelector('[data-page=settings]').click();$('advancedButton').click();
              await pause(35);const before=Number(getComputedStyle($('advancedDialog')).opacity);
              $('closeAdvanced').click();const motion=$('advancedDialog').getAnimations()[0];
              if(motion&&Math.abs(Number(motion.effect.getKeyframes()[0].opacity)-before)>.03)throw Error('interrupted animation');
              await pause(300);if($('advancedDialog').open)throw Error('dialog stuck');
              $('advancedButton').click();$('hwidButton').click();
              if($('deviceDialog').closest('dialog')!==$('advancedDialog')||!$('advancedDialog').open||!$('advancedContent').hidden)throw Error('nested HWID');
              for(let i=0;i<100&&!$('hwidValue').value;i++)await pause(20);
              if($('hwidValue').value!=='A'.repeat(64))throw Error('HWID bridge');
              $('copyHwid').click();$('hwidValue').focus();await pause(300);
              if($('toast').textContent!=='HWID скопирован'||document.activeElement!==$('hwidValue'))throw Error('toast focus');
              const r=$('toast').getBoundingClientRect();
              if(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.closest('#toast')!==$('toast'))throw Error('popover layer');
              $('closeDevice').click();
              if(!$('deviceDialog').hidden||$('advancedContent').hidden||!$('advancedDialog').open)throw Error('HWID back');
              $('closeAdvanced').click();
              await pause(300);return 'ok';
            })().catch(e=>'error: '+e.message).then(result=>chrome.webview.postMessage({fixtureResult:result}))
            """));
            PumpUntil(() => result != null, "native UI regression did not complete");
            check(result == "ok", "production page, modal popover, font and interrupted animation work in Windows WebView2: " + result);
            check(!loading.Visible, "first rendered profile snapshot dismisses the native loading screen");
        }
        finally
        {
            web.Dispose(); form.Close();
            if (browser != null)
            {
                try { PumpUntil(() => browser.HasExited, "private browser did not exit after UI disposal", 10); }
                finally { if (!browser.HasExited) { browser.Kill(entireProcessTree: true); browser.WaitForExit(5000); } browser.Dispose(); }
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
