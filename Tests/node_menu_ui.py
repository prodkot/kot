"""Production server popup in Chromium. Synthetic bridge, no VPN or private data."""
import ast
import json
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT/'Tests/generated/node-menu'
MOCK = next(ast.literal_eval(n.value) for n in ast.parse((ROOT/'Tests/ui.py').read_text()).body
            if isinstance(n, ast.Assign) and any(getattr(t, 'id', '') == 'mock' for t in n.targets))
MOCK = 'window.requests=[];' + MOCK.replace('switch(msg.action){', '''
 requests.push(msg);
 switch(msg.action){
 case 'readNodeJson':reply(true,'',{text:'{"test":"synthetic"}',revision:'r',note:'Test'});return;
 case 'readNodeQr':reply(true,'',{name:'Test',link:'vless://fixture@example.com:443',image:''});return;
''')
NODES = [{'id': 'a', 'name': 'Германия · очень длинное название сервера ' * 8, 'protocol': 'vless'},
         {'id': 'b', 'name': 'Швеция', 'protocol': 'vless'},
         {'id': 'c', 'name': 'Без QR', 'protocol': 'xray', 'providerRouting': True}]
TRIGGER = '[data-node=a] .qr-node'
POSE = '''()=>{
 const s=getComputedStyle(nodeMenu),b=nodeMenu.getBoundingClientRect();
 const r=document.createRange();r.selectNodeContents(nodeMenuJson);const g=r.getBoundingClientRect();
 const anchor=document.querySelector('[data-node=a] .qr-node').getBoundingClientRect();
 return {opacity:+s.opacity,transform:s.transform,anchor:[anchor.x,anchor.y,anchor.width,anchor.height],rect:[b.x,b.y,b.width,b.height],
   glyph:[g.x,g.y,g.width,g.height],origin:s.transformOrigin};
}'''


def setup(page, theme):
    page.add_init_script(MOCK)
    page.goto((ROOT/'Windows/ui/index.html').as_uri())
    expect(page.locator('#startupScreen')).to_be_hidden()
    page.evaluate('data=>{fixtureSnapshot(data);fixtureNavigate("servers")}', {
        'settings': {'theme': theme, 'mode': 'tun'}, 'nodes': NODES, 'selected': 'a',
        'activeSubscription': 'sub', 'state': 'connected', 'activeNode': NODES[0], 'nextNode': 'a'})
    page.wait_for_timeout(300)  # Existing page entry, not a product delay.


def settled(page):
    page.wait_for_function('()=>nodeMenu.getAnimations().length===0')


def closed(page):
    expect(page.locator('#nodeMenu')).to_be_hidden()
    assert page.evaluate('nodeMenu.getAnimations().length===0 && !nodeMenu.inert')
    expect(page.locator(TRIGGER)).not_to_have_attribute('aria-expanded', 'true')


def same_pose(a, b):
    assert abs(a['opacity'] - b['opacity']) < .002, (a,b)
    for key in ['rect','glyph']:
        assert all(abs(x-y)<.08 for x,y in zip(a[key],b[key])), (a,b)


def geometry(page, trigger=TRIGGER):
    data = page.locator(trigger).evaluate('''button=>{
      const b=button.getBoundingClientRect(),m=nodeMenu.getBoundingClientRect();
      const s=getComputedStyle(nodeMenu),z=m.width/parseFloat(s.width);
      const items=[nodeMenuJson,nodeMenuQr].map(e=>({b:e.getBoundingClientRect().toJSON(),
        border:getComputedStyle(e).borderTopWidth,overflow:e.scrollWidth>e.clientWidth}));
      const line=nodeMenu.querySelector('[role=separator]');
      return {b:b.toJSON(),m:m.toJSON(),z,w:innerWidth,h:innerHeight,items,
        up:nodeMenu.dataset.upwards==='true',line:getComputedStyle(line).borderTopWidth,
        color:getComputedStyle(line).borderTopColor,outer:s.borderTopColor};
    }''')
    b,m,z = data['b'],data['m'],data['z']
    assert m['x'] >= 0 and m['y'] >= 0 and m['right'] <= data['w']+.1 and m['bottom'] <= data['h']+.1, data
    assert abs(m['right'] - b['right']) < .2, data
    assert abs((b['top']-m['bottom'] if data['up'] else m['top']-b['bottom']) - 4*z)<.2, data
    # Chromium snaps a 1 CSS px border to device pixels under fractional zoom.
    assert 0 < float(data['line'][:-2])*z <= max(1,z) and data['color']==data['outer'], data
    for item in data['items']:
        assert item['border']=='0px' and not item['overflow'], data
        assert item['b']['left'] >= m['left'] and item['b']['right'] <= m['right'], data
    return data


def matrix(page, theme):
    count = 0
    for width,height in [(960,640),(630,600),(630,480)]:
        page.set_viewport_size({'width':width,'height':height})
        for zoom in [1,1.25,1.5]:
            page.evaluate('z=>document.documentElement.style.zoom=z',zoom)
            for sidebar in ['expanded','collapsed']:
                page.evaluate('s=>document.querySelector(".app").dataset.sidebar=s',sidebar)
                page.wait_for_timeout(300)
                for node in ['a','c']:
                    t=page.locator(f'[data-node={node}] .qr-node')
                    t.scroll_into_view_if_needed();t.click();settled(page)
                    data=geometry(page,f'[data-node={node}] .qr-node')
                    if width==630 and height==600 and zoom==1:
                        page.screenshot(path=str(OUT/f'{theme}-{sidebar}-{node}.png'))
                    page.keyboard.press('Escape');closed(page)
                    count+=1
    print(f'PASS {theme}: {count} viewport/scale/sidebar/edge combinations',flush=True)


def interaction(page):
    page.set_viewport_size({'width':630,'height':600})
    page.evaluate('document.documentElement.style.zoom="";document.querySelector(".app").dataset.sidebar="collapsed"')
    a,b=page.locator(TRIGGER),page.locator('[data-node=b] .qr-node')
    a.scroll_into_view_if_needed();a.focus()
    page.keyboard.press('ArrowUp');expect(page.locator('#nodeMenuQr')).to_be_focused()
    page.keyboard.press('Home');expect(page.locator('#nodeMenuJson')).to_be_focused()
    page.keyboard.press('End');expect(page.locator('#nodeMenuQr')).to_be_focused()
    page.keyboard.press('ArrowDown');expect(page.locator('#nodeMenuJson')).to_be_focused()
    page.keyboard.press('ArrowUp');expect(page.locator('#nodeMenuQr')).to_be_focused()
    page.keyboard.press('Escape');closed(page);expect(a).to_be_focused()
    page.keyboard.press('Enter');expect(page.locator('#nodeMenuJson')).to_be_focused()
    page.keyboard.press('Tab');closed(page)
    assert not page.locator('#nodeMenu').evaluate('e=>e.contains(document.activeElement)')
    a.click();page.locator('#serverSearch').click();closed(page);expect(page.locator('#serverSearch')).to_be_focused()
    # Physical repeated clicks and changing row don't invoke either action/selection.
    # The popup overlays the adjacent row, as a normal anchored dropdown does.
    # Use a visible third trigger for pointer changes and the adjacent one via keyboard.
    c=page.locator('[data-node=c] .qr-node')
    for _ in range(4):
        a.click();c.click();expect(a).to_have_attribute('aria-expanded','false')
        expect(c).to_have_attribute('aria-expanded','true');c.click();closed(page)
    a.click();b.focus();page.keyboard.press('Space')
    expect(a).to_have_attribute('aria-expanded','false');expect(b).to_have_attribute('aria-expanded','true')
    page.keyboard.press('Escape');closed(page)
    a.click();page.evaluate('fixtureSnapshot({journal:"test"})');settled(page)
    expect(a).to_have_attribute('aria-expanded','true')
    page.keyboard.press('Escape');closed(page)
    page.locator('[data-node=c] .qr-node').click()
    expect(page.locator('#nodeMenuQr')).to_be_disabled()
    page.keyboard.press('End');expect(page.locator('#nodeMenuJson')).to_be_focused()
    page.keyboard.press('Escape');closed(page)
    # Dismissal has no invisible hit target over the old popup rectangle.
    a.click();settled(page);box=page.locator('#nodeMenu').bounding_box();a.click();closed(page)
    assert page.evaluate('p=>!document.elementFromPoint(p.x,p.y)?.closest("#nodeMenu")',
                         {'x':box['x']+20,'y':box['y']+20})
    for operation in ['fixtureNavigate("home")','nodeMenu.hidePopover()',
                      'fixtureSnapshot({activeSubscription:"removed"})']:
        page.evaluate('fixtureSnapshot({activeSubscription:"sub"});fixtureNavigate("servers")')
        a.click();page.evaluate(operation);closed(page)
    page.evaluate('fixtureSnapshot({activeSubscription:"sub"})')
    a.click();page.set_viewport_size({'width':640,'height':600});closed(page)
    a.click();page.evaluate('document.querySelector(".main").dispatchEvent(new Event("scroll"))');closed(page)
    assert page.evaluate("requests.every(m=>['ready','uiReady','heartbeat','view'].includes(m.action))"), 'menu changed application state'
    # Same actions/target IDs/payloads as Dev10, including focus after modal close.
    a.click();page.locator('#nodeMenuJson').click()
    expect(page.locator('#nodeJsonCode')).to_contain_text('synthetic')
    page.locator('#closeNodeJson').click();expect(page.locator('#nodeJsonDialog')).to_be_hidden();expect(a).to_be_focused()
    b.click();page.locator('#nodeMenuQr').click()
    expect(page.locator('#shareQrLink')).to_have_value('vless://fixture@example.com:443')
    page.locator('#closeShareQr').click();expect(page.locator('#shareQrDialog')).to_be_hidden();expect(b).to_be_focused()
    reads=page.evaluate('requests.filter(m=>["readNodeJson","readNodeQr"].includes(m.action))')
    assert reads[0]['data']=={'id':'a','subscriptionId':'sub','view':'parsed','full':True},reads
    assert reads[1]['data']['id']=='b',reads
    expect(page.locator('[data-node=a] .server-card')).to_have_attribute('aria-pressed','true')
    expect(page.locator('[data-node=b] .server-card')).to_have_attribute('aria-pressed','false')
    print('PASS keyboard, focus, dismissal, quick row changes, unchanged JSON/QR and selection',flush=True)


def motion(page, theme):
    page.emulate_media(reduced_motion='no-preference')
    page.set_viewport_size({'width':630,'height':600})
    a=page.locator(TRIGGER)
    a.scroll_into_view_if_needed()
    # Inspect actual WAAPI samples including text, in both directions.
    samples=[]
    for direction in ['enter','exit']:
        if direction=='exit':a.click();settled(page)
        page.evaluate('''direction=>{
          document.querySelector('[data-node=a] .qr-node').click();
          window.sampleMotion=nodeMenu.getAnimations()[0];sampleMotion.pause();sampleMotion.currentTime=0;
        }''',direction)
        timing=page.evaluate('sampleMotion.effect.getTiming()')
        assert timing['duration']==200 and timing['easing']=='cubic-bezier(0.22, 1, 0.36, 1)',timing
        for ms in [0,40,100,160,199]:
            page.evaluate('ms=>sampleMotion.currentTime=ms',ms)
            pose=page.evaluate(POSE);samples.append({'direction':direction,'ms':ms,**pose})
            page.screenshot(path=str(OUT/f'{theme}-{direction}-{ms}.png'))
        page.evaluate('sampleMotion.finish()')
        if direction=='enter':settled(page);page.keyboard.press('Escape');closed(page)
        else:closed(page)
    (OUT/f'{theme}-frames.json').write_text(json.dumps(samples,indent=2))
    # Opening -> exit -> reopen at early/middle/late times, no pose jumps.
    for ms in [20,100,170]:
        result=page.evaluate('''ms=>{
          const button=document.querySelector('[data-node=a] .qr-node');button.click();
          let animation=nodeMenu.getAnimations()[0];animation.pause();animation.currentTime=ms;
          const pose='''+POSE+''';const opening=pose();button.click();const closing=pose();
          animation=nodeMenu.getAnimations()[0];animation.pause();animation.currentTime=ms;
          const exit=pose();button.click();const reopened=pose();return {opening,closing,exit,reopened};
        }''',ms)
        same_pose(result['opening'],result['closing']);same_pose(result['exit'],result['reopened'])
        settled(page);expect(a).to_have_attribute('aria-expanded','true')
        a.click();closed(page)
    # Natural-speed playback and RAF measurements, separate from paused samples.
    trace=page.evaluate('''async()=>{
      const button=document.querySelector('[data-node=a] .qr-node'),frames=[];
      const pose='''+POSE+''';
      for(const phase of ['enter','exit']){
        button.click();const start=performance.now();
        while(performance.now()-start<240){await new Promise(requestAnimationFrame);
          frames.push({phase,ms:performance.now()-start,open:nodeMenu.matches(':popover-open'),...pose()});}
      }return frames;
    }''')
    for phase in ['enter','exit']:
        frames=[f for f in trace if f['phase']==phase and f['open']]
        assert len(frames)>=3 and len({round(f['opacity'],3) for f in frames})>=3,frames
        alpha=[f['opacity'] for f in frames]
        assert all((y>=x-.001 if phase=='enter' else y<=x+.001) for x,y in zip(alpha,alpha[1:])),alpha
    assert all(f['anchor']==trace[0]['anchor'] for f in trace), 'trigger moved with the popup'
    (OUT/f'{theme}-live.json').write_text(json.dumps(trace,indent=2));closed(page)
    # Bottom-edge popup unfolds upward from the trigger, with the same typography.
    page.set_viewport_size({'width':630,'height':480})
    c=page.locator('[data-node=c] .qr-node');c.scroll_into_view_if_needed()
    page.evaluate('''()=>{document.querySelector('[data-node=c] .qr-node').click();
      const a=nodeMenu.getAnimations()[0];a.pause();a.currentTime=40;}''')
    expect(page.locator('#nodeMenu')).to_have_attribute('data-upwards','true')
    assert page.locator('#nodeMenu').evaluate('e=>new DOMMatrix(getComputedStyle(e).transform).m42>0')
    page.screenshot(path=str(OUT/f'{theme}-upwards-40.png'))
    page.evaluate('nodeMenu.getAnimations()[0].finish()');settled(page)
    geometry(page,'[data-node=c] .qr-node');page.keyboard.press('Escape');closed(page)
    page.set_viewport_size({'width':630,'height':600});a.scroll_into_view_if_needed()
    a.click();settled(page)
    page.keyboard.press('End');expect(page.locator('#nodeMenuQr')).to_be_focused()
    assert page.locator('#nodeMenuQr').evaluate('e=>getComputedStyle(e).outlineStyle')=='solid'
    page.screenshot(path=str(OUT/f'{theme}-keyboard-focus.png'))
    page.locator('#nodeMenuJson').hover(position={'x':8,'y':20})
    page.wait_for_timeout(300)
    page.screenshot(path=str(OUT/f'{theme}-hover.png'))
    page.keyboard.press('Escape');closed(page)
    # Disabling motion during exit must finish the hide, even after cancellation.
    a.click();settled(page)
    page.evaluate('''()=>{document.querySelector('[data-node=a] .qr-node').click();
      const a=nodeMenu.getAnimations()[0];a.pause();a.currentTime=70;}''')
    page.emulate_media(reduced_motion='reduce');closed(page)
    a.click();assert page.evaluate('nodeMenu.getAnimations().length===0')
    a.click();closed(page)
    print(f'PASS {theme}: intermediate paint, early/mid/late reversal, natural playback, reduced-motion cleanup',flush=True)


if __name__=='__main__':
    OUT.mkdir(parents=True,exist_ok=True)
    with sync_playwright() as p:
        browser=p.chromium.launch(args=['--no-sandbox'])
        try:
            print('Chromium '+browser.version,flush=True)
            for theme in ['dark','light']:
                page=browser.new_page(viewport={'width':630,'height':600},reduced_motion='reduce')
                errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
                try:
                    setup(page,theme);matrix(page,theme);interaction(page);motion(page,theme)
                    assert not errors,errors
                finally:page.close()
        finally:browser.close()
