"""Production DOM/Chromium geometry with synthetic replies, no real credentials."""
import ast
import json
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
mock = next(ast.literal_eval(n.value) for n in ast.parse((ROOT/'Tests/ui.py').read_text()).body
            if isinstance(n, ast.Assign) and any(getattr(t, 'id', '') == 'mock' for t in n.targets))
mock = 'window.requests=[];window.snapshots=[];window.held=[];' + mock
mock = mock.replace('callback=cb', "callback=e=>{if(e.data.kind==='snapshot')snapshots.push(JSON.stringify(e.data.data));cb(e);}")
mock = mock.replace('switch(msg.action){', r'''
 requests.push(msg);
 function text(d) {
   const long=window.variant==='long'||(window.variant!=='short'&&d.view==='xray');
   return JSON.stringify({kind:d.view,credential:'private-test-value',
     options:long?Array.from({length:80},(_,i)=>i):[],
     long:long?'<img src=x onerror=alert(1)>'+'a'.repeat(1500):'short'},null,2);
 }
 switch(msg.action){
 case 'readNodeJson': {
   const d=msg.data, value=text(d), mode=window.failure;
   const data=mode==='missing'
     ? {text:null,note:'Отсутствует сохранённый outbound. '.repeat(40),revision:null}
     : {text:value,note:d.view==='parsed'?'Сохранённый outbound в формате kot после разбора ссылки или YAML.':'Прокси-outbound для подключения по текущей записи сервера.',revision:d.view};
   const send=()=>mode==='error'?reply(false,'private-test-error'):reply(true,'',data);
   if(window.hold)held.push({view:d.view,send});else send();
   return;
 }
 case 'copyNodeJson': {
   const send=()=>{
     if(window.stale){reply(false,'private-test-error');return;}
     window.clip=text(msg.data);reply();
   };
   if(window.holdCopy)window.deliverCopy=send;else send();
   return;
 }
''')


def setup(page, theme, source=ROOT):
    page.add_init_script(mock)
    page.goto((source/'Windows/ui/index.html').as_uri())
    expect(page.locator('#startupScreen')).to_be_hidden()
    page.evaluate("""theme=>{
      fixtureSnapshot({settings:{theme,mode:'tun',bypass:''},state:'connected',selected:'other',
        activeSubscription:'sub',activeNode:{id:'other',name:'Second'},nextNode:'other',
        nodes:[{name:'First',protocol:'xray',id:'inspected',providerRouting:true},
               {name:'Second',protocol:'vless',id:'other'}]}); fixtureNavigate('servers');
    }""", theme)


def open_view(page):
    page.locator('[data-node=inspected] .qr-node').click()
    expect(page.locator('#nodeMenuQr')).to_be_disabled()
    page.locator('#nodeMenuJson').click()


def geometry(page):
    return page.evaluate("""()=>Object.fromEntries(['nodeJsonDialog','nodeJsonScroll','copyNodeJson'].map(id=>{
      const b=document.getElementById(id).getBoundingClientRect();return [id,[b.x,b.y,b.width,b.height]];
    }))""")


def equal(a, b):
    assert all(abs(x-y)<.15 for name in a for x,y in zip(a[name],b[name])), (a,b)


def frame_trace(page, start):
    if start:
        page.evaluate("""()=>{window.frames=[];window.tracing=true;
          function tick(){if(!tracing)return;const b=nodeJsonDialog.getBoundingClientRect();
            frames.push([b.x,b.y,b.width,b.height]);requestAnimationFrame(tick);}tick();}""")
    else:
        frames = page.evaluate('()=>{tracing=false;return frames;}')
        assert len(frames)>2
        assert all(abs(v-frames[0][i])<.15 for frame in frames for i,v in enumerate(frame)), 'dialog moved between frames'


def bounds(page):
    assert page.evaluate("""()=>{
      const d=nodeJsonDialog.getBoundingClientRect(),s=nodeJsonScroll.getBoundingClientRect(),
        b=copyNodeJson.getBoundingClientRect(),p=document.querySelector('.node-json-panel').getBoundingClientRect(),
        close=closeNodeJson.getBoundingClientRect();
      return d.x>=0&&d.y>=0&&d.right<=innerWidth+.1&&d.bottom<=innerHeight+.1&&
        p.x>=d.x&&p.right<=d.right&&p.bottom<=d.bottom&&s.height>40&&s.bottom<=b.top&&
        b.x>=p.x&&b.right<p.right&&b.bottom<p.bottom&&close.x>d.x&&close.right<d.right&&
        nodeJsonDialog.scrollHeight<=nodeJsonDialog.clientHeight+1&&
        nodeJsonDialog.querySelector('.dialog-body').scrollWidth<=nodeJsonDialog.querySelector('.dialog-body').clientWidth+1;
    }"""), 'clipped dialog, copy overlap or overflow outside scroll region'


def run(page, theme):
    setup(page,theme)
    assert not page.evaluate("requests.some(m=>m.action==='readNodeJson')"), 'config requested before opening'
    page.evaluate('window.hold=true')
    open_view(page)
    expect(page.locator('#nodeJsonNote')).to_have_text('Загрузка…')
    expect(page.locator('#copyNodeJson')).to_be_disabled()
    page.wait_for_timeout(350)  # Only settle the existing opening animation, no product delay.
    initial = geometry(page)
    assert page.locator('#nodeJsonSecrets, #nodeJsonConfirm').count()==0
    frame_trace(page,True)
    page.wait_for_function('()=>held.length===1')
    page.evaluate('held.shift().send();window.hold=false')
    code, copy = page.locator('#nodeJsonCode'), page.locator('#copyNodeJson')
    tabs = page.locator('[data-json-view]')
    expect(code).to_contain_text('private-test-value')
    assert code.locator('.json-key').count()==4
    equal(initial,geometry(page)); bounds(page)
    copy.click()
    expect(page.locator('#nodeJsonFeedback')).to_have_text('Скопировано')
    assert page.evaluate('clip')==code.text_content()
    page.wait_for_timeout(350); equal(initial,geometry(page))
    tabs.last.click()
    expect(code).to_contain_text('<img src=x onerror=alert(1)>')
    assert code.locator('img').count()==0
    equal(initial,geometry(page)); bounds(page)
    assert page.locator('#nodeJsonScroll').evaluate('e=>{e.scrollTop=e.scrollHeight;e.scrollLeft=e.scrollWidth;return e.scrollTop>0&&e.scrollLeft>0}')
    copy.click()
    expect(page.locator('#nodeJsonFeedback')).to_have_text('Скопировано')
    assert page.evaluate('clip')==code.text_content() and json.loads(page.evaluate('clip'))['options'][-1]==79
    page.wait_for_timeout(350); equal(initial,geometry(page))
    # Native browser selection is no longer intercepted by a consent dialog.
    assert page.evaluate("""()=>{const e=new ClipboardEvent('copy',{bubbles:true,cancelable:true});
      nodeJsonCode.dispatchEvent(e);return !e.defaultPrevented;}""")
    # Out-of-order replies for repeated tab changes may not replace the chosen view.
    page.evaluate('window.hold=true')
    for _ in range(6):
        tabs.first.click(); tabs.last.click()
    page.wait_for_function('()=>held.length===12')
    equal(initial,geometry(page))
    page.evaluate('held.pop().send()')
    expect(code).to_contain_text('private-test-value')
    assert json.loads(code.text_content())['kind']=='xray'
    page.evaluate('while(held.length)held.pop().send();window.hold=false')
    assert json.loads(code.text_content())['kind']=='xray'
    for failure in ['error','missing',None]:
        page.evaluate('(value)=>window.failure=value',failure)
        tabs.first.click()
        if failure:
            expect(page.locator('#nodeJsonNote')).to_contain_text('Не удалось' if failure=='error' else 'Отсутствует')
            expect(copy).to_be_disabled(); expect(code).to_be_empty()
        else:
            expect(code).to_contain_text('private-test-value')
        equal(initial,geometry(page)); bounds(page)
        tabs.last.click()
        expect(page.locator('#nodeJsonScroll')).to_have_attribute('aria-busy','false')
    page.evaluate('window.stale=true'); copy.click()
    expect(page.locator('#nodeJsonFeedback')).to_have_text('Не скопировано')
    expect(page.locator('#nodeJsonNote')).to_contain_text('Копирование не удалось')
    assert 'private-test-error' not in page.locator('body').inner_text()
    page.wait_for_timeout(350); equal(initial,geometry(page))
    page.evaluate('window.stale=false;window.holdCopy=true')
    count=page.evaluate("requests.filter(m=>m.action==='copyNodeJson').length")+1
    copy.click(); expect(copy).to_have_attribute('aria-busy','true')
    expect(copy).to_be_focused()
    page.wait_for_function("n=>requests.filter(m=>m.action==='copyNodeJson').length===n",arg=count)
    copy.click(force=True)
    page.wait_for_timeout(30)
    assert page.evaluate("requests.filter(m=>m.action==='copyNodeJson').length")==count
    tabs.first.click(); expect(code).to_contain_text('private-test-value')
    page.evaluate('deliverCopy();window.holdCopy=false')
    expect(page.locator('#nodeJsonFeedback')).to_be_empty()
    page.wait_for_timeout(350); frame_trace(page,False)
    # Stable shape across explicit short/long replies, both themes and live resizes.
    output = ROOT/'Tests/generated/node-json'
    output.mkdir(parents=True,exist_ok=True)
    for width,height in [(800,600),(630,600),(480,480),(360,400),(960,640),(1280,800)]:
        page.set_viewport_size({'width':width,'height':height})
        for variant in ['long','short']:
            page.evaluate('(value)=>window.variant=value',variant)
            tabs.last.click(); expect(page.locator('#nodeJsonScroll')).to_have_attribute('aria-busy','false')
            b=geometry(page); bounds(page)
            tabs.first.click(); expect(page.locator('#nodeJsonScroll')).to_have_attribute('aria-busy','false')
            equal(b,geometry(page)); bounds(page)
            if width==630 and variant=='long':
                page.screenshot(path=str(output/f'{theme}-630-long.png'))
        if width in (630,800):
            page.screenshot(path=str(output/f'{theme}-{width}.png'))
    page.set_viewport_size({'width':800,'height':600})
    for zoom in [1,1.25,1.5]:
        page.evaluate('(z)=>document.documentElement.style.zoom=z',zoom)
        bounds(page)
        b=geometry(page); tabs.last.click(); expect(code).to_contain_text('private-test-value')
        equal(b,geometry(page)); tabs.first.click()
    page.evaluate("document.documentElement.style.zoom='' ")
    # Theme switches don't move any controls, even with the dialog already open.
    b=geometry(page)
    page.evaluate("fixtureSnapshot({settings:{theme:'"+('light' if theme=='dark' else 'dark')+"',mode:'tun'}})")
    equal(b,geometry(page))
    page.locator('#closeNodeJson').focus()
    for locator in [tabs.first,tabs.last,page.locator('#nodeJsonScroll'),copy]:
        page.keyboard.press('Tab'); expect(locator).to_be_focused()
    page.keyboard.press('Shift+Tab'); expect(page.locator('#nodeJsonScroll')).to_be_focused()
    tabs.last.focus(); page.keyboard.press('Enter'); expect(tabs.last).to_have_attribute('aria-pressed','true')
    expect(copy).to_be_enabled(); copy.focus(); page.keyboard.press('Space'); expect(page.locator('#nodeJsonFeedback')).to_have_text('Скопировано')
    # Close with a pending read; late result cannot revive the old dialog/session.
    page.evaluate('window.hold=true'); tabs.first.click(); page.wait_for_function('()=>held.length===1')
    page.keyboard.press('Escape')
    expect(page.locator('#nodeJsonDialog')).to_be_hidden()
    expect(code).to_be_empty()
    expect(page.locator('[data-node=inspected] .qr-node')).to_be_focused()
    page.evaluate('window.hold=false'); open_view(page)
    expect(code).to_contain_text('private-test-value')
    page.evaluate('held.shift().send()')
    assert json.loads(code.text_content())['kind']=='parsed'
    page.evaluate('fixtureSnapshot({activeSubscription:"another"})')
    expect(page.locator('#nodeJsonDialog')).to_be_hidden(); expect(code).to_be_empty()
    assert page.evaluate("requests.every(m=>['view','ready','uiReady','readNodeJson','copyNodeJson','heartbeat'].includes(m.action))")
    assert page.evaluate("requests.filter(m=>m.action.endsWith('NodeJson')).every(m=>m.data.full===true)")
    assert all('private-test' not in json.dumps(m) for m in page.evaluate('requests'))
    assert all('private-test' not in s for s in page.evaluate('snapshots'))
    assert 'private-test' not in page.evaluate('JSON.stringify(localStorage)+JSON.stringify(sessionStorage)')
    expect(page.locator('[data-node=other] .server-card')).to_have_attribute('aria-pressed','true')


def baseline(browser, source):
    for theme in ['dark','light']:
        page=browser.new_page(viewport={'width':630,'height':600},reduced_motion='reduce')
        try:
            setup(page,theme,source);open_view(page)
            expect(page.locator('#nodeJsonCode')).to_contain_text('private-test-value')
            ready=geometry(page)
            out=ROOT/'Tests/generated/node-json';out.mkdir(parents=True,exist_ok=True)
            page.screenshot(path=str(out/f'baseline-{theme}-630.png'))
            page.evaluate('window.hold=true')
            page.locator('[data-json-view=xray]').click()
            expect(page.locator('#nodeJsonNote')).to_have_text('Загрузка…')
            loading=geometry(page)
            page.wait_for_function('()=>held.length===1');page.evaluate('held.shift().send()')
            expect(page.locator('#nodeJsonCode')).to_contain_text('private-test-value')
            loaded=geometry(page)
            assert ready['nodeJsonDialog']!=loading['nodeJsonDialog']!=loaded['nodeJsonDialog']
            print('Baseline '+theme+': ready/loading/long dialog rectangles '+json.dumps([v['nodeJsonDialog'] for v in [ready,loading,loaded]]),flush=True)
        finally:page.close()


if __name__=='__main__':
    with sync_playwright() as p:
        browser=p.chromium.launch(args=['--no-sandbox'])
        try:
            print('Chromium '+browser.version,flush=True)
            if os.environ.get('KOT_JSON_BASELINE'):
                baseline(browser,Path(os.environ['KOT_JSON_BASELINE']).resolve())
            else:
                for theme in ['dark','light']:
                    for motion in ['reduce','no-preference']:
                        page=browser.new_page(viewport={'width':630,'height':600},reduced_motion=motion)
                        errors=[]
                        page.on('pageerror',lambda e:errors.append(str(e)))
                        page.on('console',lambda m:errors.append('unexpected console output') if 'private-test' in m.text else None)
                        try:
                            run(page,theme)
                            assert not errors,errors
                            print('PASS JSON geometry, races, full copy, failures, resize/zoom, keyboard and isolation: '+theme+' '+motion,flush=True)
                        finally:page.close()
        finally:browser.close()
