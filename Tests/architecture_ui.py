"""Transport contract and shared design parameters, Chromium only, no native VPN."""
from pathlib import Path
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]


def transport(page):
    page.clock.install(time=1000000)
    page.clock.pause_at(1001000)
    page.add_script_tag(path=str(ROOT / 'Windows/ui/native-bridge.js'))
    page.evaluate("""() => {
      window.sent=[]; window.results=[]; window.navigated=[]; window.snapshots=[];
      const host={postMessage:m=>sent.push(m),addEventListener:(_,fn)=>window.receive=m=>fn({data:m})};
      window.bridge=KotNativeBridge(host);
      bridge.start({snapshot:m=>snapshots.push(m),navigate:p=>navigated.push(p)});
      window.call=action=>bridge.rpc(action,{sample:true}).then(
        value=>results.push({action,value}),error=>results.push({action,error:error.message}));
      call('first');call('second');
    }""")
    assert page.evaluate('sent.map(m=>[m.id,m.action,m.data.sample])') == [[1, 'first', True], [2, 'second', True]]
    page.evaluate("""() => {
      receive({kind:'reply',id:999,ok:true,data:'unexpected'});
      receive({kind:'reply',id:2,ok:true,data:'second reply'});
      receive({kind:'reply',id:1,ok:false,error:'native failure'});
      receive({kind:'reply',id:1,ok:true,data:'duplicate'});
      receive({kind:'snapshot',data:{state:'idle'}});
      receive({kind:'navigate',page:'settings'});
      receive({kind:'navigate',page:'untrusted-page'});
      receive({kind:'heartbeat'});
      receive({kind:'reply',id:sent.at(-1).id,ok:true});
    }""")
    assert page.evaluate('results') == [{'action': 'second', 'value': 'second reply'}, {'action': 'first', 'error': 'native failure'}]
    assert page.evaluate('navigated') == ['settings']
    assert page.evaluate('snapshots') == [{'state': 'idle'}]
    assert page.evaluate('sent.at(-1).action') == 'heartbeat'
    page.evaluate("() => { call('slow');call('downloadUpdate'); }")
    page.clock.run_for(119999)
    assert page.evaluate('results.length') == 2
    page.clock.run_for(1)
    assert page.evaluate('results.at(-1).action') == 'slow'
    assert page.evaluate('results.at(-1).error')
    page.evaluate("receive({kind:'reply',id:4,ok:true,data:'late'})")
    assert page.evaluate('results.length') == 3
    page.clock.run_for(599999)
    assert page.evaluate('results.length') == 3
    page.clock.run_for(1)
    assert page.evaluate('results.at(-1).action') == 'downloadUpdate'
    assert page.evaluate('results.at(-1).error')
    assert page.evaluate("async () => {try{await KotNativeBridge(null).rpc('ready');return false;}catch(e){return !!e.message;}}")


def tokens(page):
    # Use the actual shared component selectors. No duplicated visual definitions.
    page.set_content('<button class="quiet-button">Quiet</button><button class="primary">Primary</button>'
                     '<dialog class="dialog" open><input><select><option>One</option></select></dialog>')
    for name in ['tokens.css', 'client.css']:
        page.add_style_tag(path=str(ROOT / 'Windows/ui/styles' / name))
    for theme in ['dark', 'light']:
        page.evaluate('(light)=>document.documentElement.classList.toggle("theme-light",light)', theme == 'light')
        page.evaluate("""() => {
          const s=document.documentElement.style;
          s.setProperty('--control-height','53px');s.setProperty('--field-height','57px');
          s.setProperty('--radius-compact','15px');
        }""")
        assert page.locator('body > button').evaluate_all('(els)=>els.map(e=>getComputedStyle(e).minHeight)') == ['53px', '53px']
        assert page.locator('body > button').evaluate_all('(els)=>els.map(e=>getComputedStyle(e).borderRadius)') == ['15px', '15px']
        assert page.locator('input,select').evaluate_all('(els)=>els.map(e=>getComputedStyle(e).height)') == ['57px', '57px']
        page.locator('input').focus()
        assert page.locator('input').evaluate('(e)=>getComputedStyle(e).outlineStyle') == 'solid'
    page.emulate_media(reduced_motion='reduce')
    assert page.locator('.primary').evaluate('(e)=>parseFloat(getComputedStyle(e).transitionDuration)') <= .001


if __name__ == '__main__':
    with sync_playwright() as p:
        browser = p.chromium.launch(args=['--no-sandbox'])
        try:
            errors = []
            page = browser.new_page()
            page.on('pageerror', lambda e: errors.append(str(e)))
            transport(page)
            tokens(page)
            assert not errors, errors
        finally:
            browser.close()
    print('PASS architecture UI: RPC correlation/errors/deadlines, shared controls, focus, reduced motion (browser only)')
