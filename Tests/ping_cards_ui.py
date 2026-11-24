"""Production DOM, real Chromium frames; delayed synthetic native replies, no VPN claim."""
import ast
import json
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = Path(os.environ.get('KOT_UI_PREVIEW', ROOT / 'Tests/generated/ping-cards'))
OUTPUT.mkdir(parents=True, exist_ok=True)
MOCK = next(ast.literal_eval(n.value) for n in ast.parse((ROOT/'Tests/ui.py').read_text()).body
            if isinstance(n, ast.Assign) and any(getattr(t, 'id', '') == 'mock' for t in n.targets))
MOCK = MOCK.replace('window.fixtureNavigate=', '''window.pingCalls=[];window.pendingReplies=[];
const original=chrome.webview.postMessage;
chrome.webview.postMessage=msg=>{
  if(msg.action==='pingNode'){pingCalls.push(msg.data.id);pendingReplies.push(msg);return;}
  original(msg);
};
window.finishReplies=(ok=true)=>{for(const msg of pendingReplies.splice(0))callback({data:{kind:'reply',id:msg.id,ok,error:ok?'':'Synthetic rejection'}});};
window.pingStatus=(a,b)=>{model.nodes[0].ping={status:a,ms:a==='ok'?470:null,mode:'http',successes:1,attempts:1};
model.nodes[1].ping={status:b,ms:b==='ok'?89:null,mode:'http',successes:1,attempts:1};
model.ping.busy=[a,b].some(s=>['checking','queued'].includes(s));fixtureSnapshot({});};
window.fixtureNavigate=''')

READ = '''()=>[...serverList.children].map((row,i)=>{
const ping=row.querySelector('.ping-node'),card=row.querySelector('.server-card'),menu=row.querySelector('.qr-node');
const box=e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height]};
return {same:row===savedRows[i],samePing:ping===savedPings[i],sameMenu:menu===savedMenus[i],
background:getComputedStyle(row).backgroundColor,opacity:getComputedStyle(row).opacity,
pointer:getComputedStyle(row).pointerEvents,disabled:card.disabled,inert:row.inert,
selected:card.getAttribute('aria-pressed'),pingDisabled:ping.disabled,menuDisabled:menu.disabled,
color:getComputedStyle(ping).color,box:box(row),pingBox:box(ping),iconBox:box(ping.querySelector('svg')),menuBox:box(menu)};})'''
SAVE = '''window.savedRows=[...serverList.children];window.savedPings=savedRows.map(r=>r.querySelector('.ping-node'));
window.savedMenus=savedRows.map(r=>r.querySelector('.qr-node'));'''

def boot(browser, theme, width, scale=1, motion='no-preference'):
    page = browser.new_page(viewport={'width':width,'height':720}, reduced_motion=motion)
    page.set_default_timeout(4000)
    page.errors=[]
    page.on('pageerror', lambda e: page.errors.append(str(e)))
    page.add_init_script(MOCK)
    page.goto((ROOT/'Windows/ui/index.html').as_uri())
    expect(page.locator('#startupScreen')).to_be_hidden()
    page.evaluate('''({theme,scale})=>{
      fixtureSnapshot({hasSubscription:true,activeSubscription:'fixture',subscriptions:[{id:'fixture',name:'Fixture',count:2}],
      nodes:[{id:'a',name:'Нидерланды · XHTTP',protocol:'vless'},{id:'b',name:'Нидерланды · XHTTP 2 с длинным названием сервера',protocol:'vless'}],
      selected:'b',state:'connected',activeNode:{id:'b',name:'Нидерланды · XHTTP 2',protocol:'vless'},
      nextNode:'b',settings:{theme,accent:'lime',mode:'tun',tray:true}});
      document.documentElement.style.zoom=scale;fixtureNavigate('servers');}''', {'theme':theme,'scale':scale})
    page.wait_for_timeout(350)
    return page

def frame_check(page, before, target):
    frames=page.evaluate('''async read=>{const get=eval('('+read+')'),frames=[];
      for(let i=0;i<24;i++){await new Promise(requestAnimationFrame);frames.push(get());}return frames;}''', READ)
    for frame in frames:
        for index, row in enumerate(frame):
            assert row['same'] and row['samePing'] and row['sameMenu'], row
            assert not row['disabled'] and not row['menuDisabled'] and not row['inert'],row
            assert row['opacity']=='1' and row['pointer']!='none',row
            assert row['selected']==before[index]['selected'],row
            assert row['background']==before[index]['background'],(before,frame)
            for key in ['box','pingBox','iconBox','menuBox']:
                assert all(abs(a-b)<.05 for a,b in zip(row[key],before[index][key])),(key,before,frame)
        if target is not None:assert frame[target]['color']==before[target]['color'],frame
    return frames

def cards(browser, theme, width, scale, motion):
    page=boot(browser,theme,width,scale,motion)
    try:
        for target in ['a','b']:
            index=0 if target=='a' else 1
            page.evaluate("pingStatus('idle','idle');finishReplies();pingCalls=[]")
            ping=page.locator(f'[data-node={target}] .ping-node')
            other=page.locator(f'[data-node={"b" if target=="a" else "a"}] .ping-node')
            ping.hover();page.wait_for_timeout(350)
            page.evaluate(SAVE);before=page.evaluate(READ)
            # Real mouse burst before any native reply. Only this node becomes busy.
            box=ping.bounding_box()
            for _ in range(5):page.mouse.click(box['x']+box['width']/2,box['y']+box['height']/2)
            assert page.evaluate('pingCalls')==[target]
            expect(ping).to_be_disabled();expect(other).to_be_enabled()
            frame_check(page,before,index)
            states=['checking','idle'] if target=='a' else ['idle','checking']
            page.evaluate('s=>{pingStatus(...s);finishReplies();}',states)
            trace=frame_check(page,before,index)
            ping.evaluate('e=>{for(let i=0;i<10;i++)e.click()}')
            assert page.evaluate('pingCalls')==[target]
            if width==630 and scale==1 and target=='a':
                page.locator('#serverList').screenshot(path=str(OUTPUT/f'{theme}-checking.png'))
                (OUTPUT/f'{theme}-frames.json').write_text(json.dumps({'before':before,'frames':trace},indent=2))
            # Error and subsequent retry must not replace the row or shift neighbours.
            states[index]='error';page.evaluate('s=>pingStatus(...s)',states)
            frame_check(page,before,None)
            expect(ping).to_be_enabled()
            states[index]='checking';page.evaluate('s=>pingStatus(...s)',states)
            # No global busy gate: independent second request is accepted once.
            other.click();other.evaluate('e=>e.click()')
            assert len(page.evaluate('pingCalls'))==2
            states[1-index]='queued';page.evaluate('s=>{pingStatus(...s);finishReplies();}',states)
            expect(other).to_be_disabled()
            # The menu anchor stays alive through progress updates and keyboard dismissal.
            trigger=page.locator(f'[data-node={target}] .qr-node')
            trigger.focus();trigger.press('ArrowDown')
            expect(page.locator('#nodeMenu')).to_be_visible()
            states[index]='ok';states[1-index]='checking';page.evaluate('s=>pingStatus(...s)',states)
            expect(page.locator('#nodeMenu')).to_be_visible()
            expect(page.locator('#nodeMenu [role=menuitem]').first).to_be_focused()
            page.keyboard.press('Escape');expect(page.locator('#nodeMenu')).to_be_hidden();expect(trigger).to_be_focused()
            states[1-index]='error';page.evaluate('s=>pingStatus(...s)',states)
            expect(ping).to_be_enabled();expect(other).to_be_enabled()
            assert page.locator('[data-node=b] .server-card').get_attribute('aria-pressed')=='true'
            # A keyboard repeat while waiting for acceptance is also deduplicated.
            other.focus();other.press('Enter');page.keyboard.press('Enter');page.keyboard.press('Space')
            assert len(page.evaluate('pingCalls'))==3
            page.evaluate('finishReplies(false)');expect(other).to_be_enabled()
            other.click();assert len(page.evaluate('pingCalls'))==4
            page.evaluate('finishReplies();pingStatus("cancelled","cancelled")')
            expect(ping).to_be_enabled();expect(other).to_be_enabled()
        # Selecting a card during a ping remains possible and uses the existing action.
        page.evaluate("pingStatus('checking','idle')")
        page.locator('[data-node=a] .server-card').click()
        expect(page.locator('#page-home')).to_be_visible()
        page.evaluate("fixtureNavigate('servers')")
        expect(page.locator('[data-node=a] .server-card')).to_have_attribute('aria-pressed','true')
        assert not page.errors,page.errors
    finally:page.close()
    print(f'PASS ping cards: {theme} {width}px zoom={scale} motion={motion}; both rows, frames, burst, menu, selection, error/retry/cancel',flush=True)

def keyed_list(browser):
    page=boot(browser,'dark',960)
    try:
        page.evaluate("pingStatus('ok','ok')")
        page.locator('[data-node=a] .qr-node').focus()
        page.evaluate(SAVE)
        page.evaluate("fixtureSnapshot({ping:{busy:false,settings:{sort:'ping'}}})")
        assert page.locator('.server-row').first.get_attribute('data-node')=='b'
        expect(page.locator('[data-node=a] .qr-node')).to_be_focused()
        page.locator('#serverSearch').fill('XHTTP 2');expect(page.locator('.server-row')).to_have_count(1)
        page.locator('#serverSearch').fill('');expect(page.locator('.server-row')).to_have_count(2)
        page.evaluate("fixtureSnapshot({nodes:[]})");expect(page.locator('.server-row')).to_have_count(0)
        assert not page.errors,page.errors
    finally:page.close()
    print('PASS ping keyed rows: sort, focus, filter, removal',flush=True)

if __name__=='__main__':
    with sync_playwright() as pw:
        browser=pw.chromium.launch(args=['--no-sandbox'])
        try:
            print('Chromium '+browser.version,flush=True)
            for theme in ['dark','light']:
                for width,scale in [(630,1),(960,1),(800,1.25),(800,1.5)]:cards(browser,theme,width,scale,'no-preference')
                cards(browser,theme,630,1,'reduce')
            keyed_list(browser)
        finally:browser.close()
