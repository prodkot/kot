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
    static void CheckDialogFrames(CoreWebView2 core, Action<bool, string> check, string screenshots)
    {
        if (Complete(core.ExecuteScriptAsync("matchMedia('(prefers-reduced-motion: reduce)').matches")) == "true") return;
        Complete(core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click(); advancedButton.click()"));
        const string seek = """
            fraction => {
                const dialog = document.getElementById('advancedDialog');
                for (const animation of dialog.getAnimations({subtree:true}).filter(a => a.effect.target === dialog)) {
                    animation.pause();
                    animation.currentTime = fraction * animation.effect.getTiming().duration;
                }
                const rect = dialog.getBoundingClientRect();
                return [rect.top, rect.left, rect.width, rect.height, Number(getComputedStyle(dialog).opacity),
                        Number(getComputedStyle(dialog, '::backdrop').opacity)];
            }
            """;
        Complete(core.ExecuteScriptAsync($"({seek})(0.54); closeAdvanced.click()"));
        double[] Capture(string phase, double fraction)
        {
            var frame = Complete(core.ExecuteScriptAsync($"({seek})({JsonSerializer.Serialize(fraction)})"));
            Complete(core.ExecuteScriptAsync("window.motionFramePainted=false; requestAnimationFrame(() => requestAnimationFrame(() => window.motionFramePainted=true))"));
            PumpUntil(() => Complete(core.ExecuteScriptAsync("window.motionFramePainted")) == "true", "painting captured dialog");
            using var image = File.Create(Path.Combine(screenshots, $"dialog-reverse-{phase}.png"));
            Complete(core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image));
            return JsonSerializer.Deserialize<double[]>(frame)!;
        }
        var before = Capture("before", 0.44);
        Complete(core.ExecuteScriptAsync("advancedButton.click()"));
        var after = Capture("after", 0);
        check(before.Zip(after).All(pair => Math.Abs(pair.First - pair.Second) < .01),
            "WebView2 preserves the interrupted surface and backdrop on modal reopen");
        Complete(core.ExecuteScriptAsync("closeAdvanced.click()"));
        PumpUntil(() => Complete(core.ExecuteScriptAsync("advancedDialog.open")) == "false", "closing captured dialog");
    }
    public static void Run(Action<bool, string> check, string screenshots)
    {
        string folder = Path.Combine(Path.GetTempPath(), "kot-webview-" + Guid.NewGuid().ToString("N"));
        using var form = new Form { ClientSize = new Size(960, 640), ShowInTaskbar = false };
        using var web = new WebView2 { Dock = DockStyle.Fill };
        using var icon = new Icon(Path.Combine(AppContext.BaseDirectory, "kot.ico"));
        using var loading = new StartupScreen("light", icon, () => { }, () => { }, () => { }, () => { }, sequence: ["hop", "roll", "nudge"]);
        Process? browser = null;
        bool browserResourcesReleased = false;
        try
        {
            form.Controls.Add(web); form.Controls.Add(loading); loading.BringToFront(); form.Show();
            check(loading.Visible, "native loading screen is visible before WebView2 initialization");
            var env = Complete(WebViewHost.CreateEnvironment(folder));
            env.BrowserProcessExited += (_, _) => browserResourcesReleased = true;
            Complete(web.EnsureCoreWebView2Async(env));
            var core = web.CoreWebView2;
            browser = Process.GetProcessById(checked((int)core.BrowserProcessId));
            check(ProcessParent.Id(browser.Handle) == Environment.ProcessId, "private WebView2 browser remains a child of the kot host");
            core.SetVirtualHostNameToFolderMapping("kot.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            string? readyError = null, result = null; bool ready = false; double startupOriginMs = 0;
            core.WebMessageReceived += async (_, e) =>
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                var root = message.RootElement;
                if (root.TryGetProperty("fixtureResult", out var returned)) { result = returned.GetString(); return; }
                string? action = root.GetProperty("action").GetString();
                try
                {
                    if (action == "uiReady")
                    {
                        string samePhase = await core.ExecuteScriptAsync($$"""
                        matchMedia('(prefers-reduced-motion: reduce)').matches ||
                          Math.abs(window.fixtureLoader.state.elapsed-(Date.now()-{{JsonSerializer.Serialize(startupOriginMs)}}))<50
                        """);
                        check(samePhase == "true", "WebView2 continues the native splash clock before the cover is hidden");
                        loading.Hide();
                    }
                    if (action == "readHwid")
                    {
                        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "reply", id = root.GetProperty("id").GetInt32(), ok = true, data = new { hwid = new string('A', 64), name = "kot fixture" } }));
                        return;
                    }
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "reply", id = root.GetProperty("id").GetInt32(), ok = true }));
                    if (action != "ready") return;
                    string firstPaint = await core.ExecuteScriptAsync("""
                    (()=>document.documentElement.classList.contains('theme-light')&&
                      getComputedStyle(document.getElementById('startupScreen')).backgroundColor==='rgb(246, 246, 242)'&&
                      getComputedStyle(document.querySelector('#startupScreen .loader-point')).fill==='rgb(85, 117, 42)'&&
                      (matchMedia('(prefers-reduced-motion: reduce)').matches||window.fixtureLoader.state.scheduled))()
                    """);
                    check(firstPaint == "true", "saved light theme and shared square trajectory apply before the first snapshot in native WebView2");
                    startupOriginMs = loading.AnimationOriginMs;
                    core.PostWebMessageAsJson($$$"""
                    {"kind":"snapshot","startupOriginMs":{{{JsonSerializer.Serialize(startupOriginMs)}}},"data":{"nodes":[],"selected":"","state":"idle","hasSubscription":false,"settings":{"theme":"light","accent":"lime","mode":"tun","tray":true},"version":"fixture"}}
                    """);
                    ready = true;
                }
                catch (Exception error) { readyError = error.Message; ready = true; }
            };
            Complete(core.AddScriptToExecuteOnDocumentCreatedAsync("""
            window.kotStartup={theme:'light',sequence:['hop','roll','nudge']};
            Object.defineProperty(window,'KotLoading',{configurable:true,set(value){
              const create=value.create;value.create=(host,options)=>{const owner=create(host,options);window.fixtureLoader??=owner;window.fixtureLastLoader=owner;return owner;};
              Object.defineProperty(window,'KotLoading',{value,configurable:true});
            }});
            """));
            core.Navigate("https://kot.local/index.html");
            PumpUntil(() => ready, "production UI did not establish its WebView2 bridge");
            check(readyError == null, "production bridge delivers a snapshot in native WebView2");
            // Execute on the actual Windows runtime: UI, font/CSP, top layer and interrupted animation.
            Complete(core.ExecuteScriptAsync("""
            (async()=>{
              const $=id=>document.getElementById(id),pause=ms=>new Promise(r=>setTimeout(r,ms));
              await document.fonts.ready;
              let sawReveal=false;
              for(let i=0;i<150&&!$('startupScreen').hidden;i++){
                if(document.body.classList.contains('revealing')){
                  if(!document.querySelector('.main').inert||!document.querySelector('.sidebar').inert)throw Error('startup input released early');
                  sawReveal ||= getComputedStyle(document.querySelector('.main')).filter!=='none';
                  if(!matchMedia('(prefers-reduced-motion: reduce)').matches &&
                     !window.fixtureLoader.state.scheduled)
                    throw Error('startup point stopped during exit');
                }
                await pause(20);
              }
              if(!$('startupScreen').hidden||document.querySelector('.main').inert||document.querySelector('.sidebar').inert)throw Error('startup overlay');
              if(fixtureLoader.state.active||fixtureLoader.state.scheduled)throw Error('loader callbacks retained');
              if(!matchMedia('(prefers-reduced-motion: reduce)').matches&&!sawReveal)throw Error('startup transition missing');
              if(document.body.classList.contains('revealing')||getComputedStyle(document.querySelector('.main')).filter!=='none')throw Error('startup blur retained');
              if($('selectedName').textContent!=='Добавить подписку'||!document.fonts.check('600 14px Manrope','Соединение'))throw Error('UI/font');
              // Exercise the actual logo handler and animation in the installed renderer.
              let meows=0;window.Audio=class {play(){meows++;return Promise.resolve();}};
              const brand=$('brandButton');brand.focus();
              for(let i=0;i<46;i++)brand.click();
              if(brand.querySelector('.brand-egg-point'))throw Error('47 scene fired early');
              brand.click();const scene=window.fixtureLastLoader;
              if(!brand.querySelector('.brand-egg-point')||document.activeElement!==brand)throw Error('47 scene/focus');
              if(!matchMedia('(prefers-reduced-motion: reduce)').matches){
                await pause(250);
                window.dispatchEvent(new CustomEvent('kot-window-visibility',{detail:false}));
                const elapsed=scene.state.elapsed;await pause(100);
                if(scene.state.scheduled||scene.state.elapsed!==elapsed)throw Error('hidden loader keeps running');
                window.dispatchEvent(new CustomEvent('kot-window-visibility',{detail:true}));
              }
              for(let i=0;i<395;i++)brand.click();if(meows)throw Error('443 reward early');
              brand.click();if(meows!==1)throw Error('443 reward lost');
              for(let i=0;i<47;i++)brand.click(); // A second cycle during the first scene queues at rest.
              for(let i=0;i<400&&brand.lastElementChild.style.transform;i++)await pause(20);
              if(brand.lastElementChild.style.transform||window.fixtureLastLoader.state.scheduled)throw Error('logo scene retained');
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
            Directory.CreateDirectory(screenshots);
            CheckDialogFrames(core, check, screenshots);
            foreach (string theme in new[] { "dark", "light" })
            {
                // Use both sidebar layouts in the installed Windows renderer.
                form.ClientSize = theme == "dark" ? new Size(960, 640) : new Size(640, 540);
                Complete(core.ExecuteScriptAsync("document.querySelector('[data-page=home]').click()"));
                int sidebarWidth = theme == "light" ? 64 : 188;
                PumpUntil(() => Complete(core.ExecuteScriptAsync(
                    $"document.querySelector('.sidebar').getBoundingClientRect().width === {sidebarWidth}")) == "true",
                    "sidebar did not finish resizing");
                var point = Complete(core.ExecuteScriptAsync("""
                    (()=>{const r=document.getElementById('sidebarPower').getBoundingClientRect();
                    return {type:'mouseMoved',x:r.x+r.width/2,y:r.y+r.height/2};})()
                    """));
                Complete(core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", point));
                foreach (string state in new[] { "idle", "waiting", "connecting", "connected", "disconnecting", "idle" })
                {
                    result = null;
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "snapshot", data = new { nodes = new[] { new { id = "de", name = "Германия · Франкфурт", protocol = "vless" } }, selected = "de", state, hasSubscription = true, settings = new { theme, mode = "tun", tray = true }, version = "fixture" } }));
                    Complete(core.ExecuteScriptAsync($$"""
                    (async()=>{
                      const state={{JsonSerializer.Serialize(state)}},light={{(theme == "light" ? "true" : "false")}},title=document.querySelector('.title-brand');
                      for(let i=0;i<100&&(title.dataset.state!==state||document.body.classList.contains('theme-light')!==light);i++)await new Promise(r=>setTimeout(r,20));
                      if(title.dataset.state!==state||document.body.classList.contains('theme-light')!==light)throw Error('snapshot not applied');
                      const expected=document.createElement('span');expected.style.color=state==='connected'?'var(--accent)':'var(--muted)';document.body.append(expected);
                      const matches=getComputedStyle(title.querySelector('.brand-dot')).backgroundColor===getComputedStyle(expected).color;expected.remove();
                      if(!matches||document.querySelector('#page-home [data-open-add]'))throw Error('connection title or home add button');
                      const side=document.getElementById('sidebarPower'),tip=document.getElementById('sidebarServer');
                      const labels={idle:'Подключить',waiting:'Отменить',connecting:'Подключение…',connected:'Отключить',disconnecting:'Отключение…'};
                      const busy=state==='connecting'||state==='disconnecting';
                      if(side.ariaLabel!==labels[state]||side.ariaBusy!==String(busy)||side.ariaDisabled!==String(busy))throw Error('sidebar state');
                      for(let i=0;i<50&&getComputedStyle(tip).opacity!=='1';i++)await new Promise(r=>setTimeout(r,20));
                      const t=tip.getBoundingClientRect(),b=side.getBoundingClientRect();
                      if(tip.textContent!=='Германия · Франкфурт'||getComputedStyle(tip).opacity!=='1'||
                         t.top<0||t.left<0||t.right>innerWidth||t.bottom>b.top)throw Error('hover tooltip');
                      if(light&&(Math.abs(t.left-b.left)>0.1||t.width<=b.width||tip.scrollWidth>tip.clientWidth||tip.scrollHeight>tip.clientHeight))
                        throw Error('collapsed sidebar must show the whole server name above the icon');
                      if(!light&&(Math.abs((t.left+t.width/2)-(b.left+b.width/2))>0.1||t.width>b.width+0.1))
                        throw Error('tooltip must stay centered within the button width');
                      const label=getComputedStyle(document.getElementById('sidebarPowerLabel'));
                      if(label.visibility!==(light?'hidden':'visible')||label.opacity!==(light?'0':'1')||
                         document.getElementById('sidebarToggle').getAttribute('aria-expanded')!==String(!light))throw Error('sidebar layout');
                      return 'ok';
                    })().catch(e=>'error: '+e.message).then(result=>chrome.webview.postMessage({fixtureResult:result}))
                    """));
                    PumpUntil(() => result != null, "native title state check did not complete");
                    check(result == "ok", "Windows WebView2 sidebar, tooltip and connection title: " + theme + " " + state + " " + result);
                    using var image = File.Create(Path.Combine(screenshots, $"sidebar-{theme}-{state}.png"));
                    Complete(core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image));
                }
            }
        }
        finally
        {
            web.Dispose(); form.Close();
            if (browser != null)
            {
                // The browser PID can exit before its child processes release
                // the profile. WebView2 signals the completed group shutdown.
                try { PumpUntil(() => browserResourcesReleased && browser.HasExited, "private browser did not release its profile after UI disposal", 10); }
                finally { if (!browser.HasExited) { browser.Kill(entireProcessTree: true); browser.WaitForExit(5000); } browser.Dispose(); }
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
