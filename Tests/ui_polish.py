"""Exercise real animation frames, modal layers and focus during live snapshots."""
import ast
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
tree = ast.parse((ROOT / 'Tests/ui.py').read_text())
MOCK = next(ast.literal_eval(node.value) for node in tree.body
            if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'mock' for t in node.targets))
MOCK = MOCK.replace("case 'readHwid':", "case 'readNodeQr':reply(true,'',{name:'Frankfurt',link:'vless://fixture@example.com:443',image:''});return;case 'readHwid':")
MOCK = MOCK.replace("msg.action==='downloadUpdate'?", "msg.action==='ready'?(window.fixtureReadyDelay||10):msg.action==='downloadUpdate'?")
NODES = [{'id': 'a', 'name': 'Frankfurt', 'protocol': 'vless', 'code': 'VL'},
         {'id': 'b', 'name': 'Amsterdam', 'protocol': 'trojan', 'code': 'TR'}]


def servers(page):
    page.evaluate('patch=>fixtureSnapshot(patch)', {'nodes': NODES, 'selected': 'a', 'hasSubscription': True})
    page.evaluate("fixtureNavigate('servers')")
    page.wait_for_timeout(300)
    button = page.locator('[data-node=a] .qr-node')
    button.focus()
    changed = [dict(n) for n in NODES]
    changed[1]['ping'] = {'status': 'ok', 'ms': 17, 'mode': 'http', 'successes': 2, 'attempts': 2}
    page.evaluate('nodes=>fixtureSnapshot({nodes})', changed)
    expect(button).to_be_focused()
    button.click()
    expect(page.locator('#shareQrLink')).to_have_value('vless://fixture@example.com:443')
    changed[1]['ping']['ms'] = 18
    page.evaluate('nodes=>fixtureSnapshot({nodes})', changed)
    page.locator('#closeShareQr').click()
    expect(page.locator('#shareQrDialog')).not_to_be_visible()
    expect(button).to_be_focused()


def connections(page):
    connection = {'id': 'one', 'destination': 'example.com:443', 'process': 'browser.exe',
                  'network': 'tcp', 'chains': ['proxy'], 'download': 10, 'upload': 20}
    page.evaluate('c=>fixtureSnapshot({state:"connected",telemetry:{connections:[c],count:1}})', connection)
    page.evaluate("fixtureNavigate('logs')")
    summary = page.locator('#connectionList summary')
    summary.click()
    summary.focus()
    connection['download'] = 30
    page.evaluate('c=>fixtureSnapshot({telemetry:{connections:[c],count:1}})', connection)
    expect(summary).to_be_focused()
    expect(page.locator('#connectionList details')).to_have_attribute('open', '')


def toast_layer(page):
    page.evaluate("fixtureNavigate('settings')")
    page.locator('#advancedButton').click()
    page.locator('#hwidButton').click()
    expect(page.locator('#hwidValue')).to_have_value('A' * 64)
    # Copy disables its own button while the RPC runs. Keep a different,
    # still-enabled control focused and verify the notification does not steal it.
    page.locator('#copyHwid').evaluate("e=>{e.click();document.getElementById('hwidValue').focus();}")
    expect(page.locator('#toast')).to_have_text('HWID скопирован')
    page.wait_for_timeout(320)
    assert page.locator('#toast').evaluate('''e=>{
      const r=e.getBoundingClientRect();
      return document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.closest('#toast')===e;
    }'''), 'toast is obscured by the modal backdrop'
    expect(page.locator('#hwidValue')).to_be_focused()
    page.locator('#closeDevice').click()
    expect(page.locator('#deviceDialog')).not_to_be_visible()
    expect(page.locator('#toast')).to_be_visible()
    expect(page.locator('#hwidButton')).to_be_focused()


def modal_handoff(page):
    page.evaluate("fixtureNavigate('settings')")
    page.locator('#advancedButton').click()
    page.locator('#backupButton').click()
    page.locator('#backupPassword').focus()
    expect(page.locator('#advancedDialog')).not_to_be_visible()
    expect(page.locator('#backupPassword')).to_be_focused()
    page.keyboard.press('Escape')
    expect(page.locator('#backupDialog')).not_to_be_visible()
    expect(page.locator('#advancedButton')).to_be_focused()


def interrupted_dialog(page):
    page.evaluate("fixtureNavigate('settings')")
    result = page.evaluate('''async()=>{
      const el=document.getElementById('advancedDialog');
      document.getElementById('advancedButton').click();
      await new Promise(resolve=>setTimeout(resolve,35));
      const before=getComputedStyle(el).opacity;
      document.getElementById('closeAdvanced').click();
      const motion=el.getAnimations().find(a=>a.effect.target===el);
      return {before:Number(before),first:Number(motion.effect.getKeyframes()[0].opacity)};
    }''')
    assert abs(result['first'] - result['before']) < .03, result
    expect(page.locator('#advancedDialog')).not_to_be_visible()


def hidden_motion(page):
    page.evaluate('nodes=>fixtureSnapshot({nodes,state:"connected"})', NODES)
    page.evaluate("fixtureNavigate('servers');fixtureNavigate('settings');fixtureNavigate('diagnostics');")
    assert not page.evaluate('''document.getAnimations().some(a=>
      a.playState==='running'&&a.effect.target.closest('.view[hidden]'))'''), 'hidden page keeps animating'
    page.wait_for_timeout(450)
    assert not page.evaluate('document.getAnimations().some(a=>a.playState==="running")')
    page.evaluate("fixtureSnapshot({state:'idle'});fixtureSnapshot({state:'connected'})")
    assert not page.evaluate('''document.getAnimations().some(a=>
      a.playState==='running'&&a.effect.target.closest('.view[hidden]'))'''), 'state snapshots animate the hidden home page'


def interrupted_power(page):
    page.evaluate('nodes=>fixtureSnapshot({nodes,state:"connected"})', NODES)
    page.wait_for_timeout(35)
    page.evaluate("fixtureSnapshot({state:'idle'})")
    assert not page.locator('#powerIcon').evaluate("e=>e.getAnimations().some(a=>a.playState==='running')"), 'connected pulse persists after disconnect'


def layouts(page):
    for width, height in [(800, 600), (960, 640), (1280, 800)]:
        page.set_viewport_size({'width': width, 'height': height})
        for theme in ['dark', 'light']:
            page.evaluate('theme=>fixtureSnapshot({settings:{theme,accent:"lime",mode:"tun",tray:true}})', theme)
            for section in ['home', 'servers', 'statistics', 'diagnostics', 'logs', 'settings']:
                page.evaluate('section=>fixtureNavigate(section)', section)
                page.wait_for_timeout(310)
                assert page.evaluate('document.documentElement.scrollWidth<=innerWidth'), (width, theme, section)
                assert page.locator(f'#page-{section}').evaluate('''e=>{
                  const r=e.getBoundingClientRect(),main=document.querySelector('.main').getBoundingClientRect();
                  return r.left>=main.left-1&&r.right<=main.right+1;
                }'''), (width, theme, section)
    page.emulate_media(reduced_motion='reduce')
    page.evaluate("fixtureNavigate('settings');document.getElementById('advancedButton').click();document.getElementById('closeAdvanced').click();")
    expect(page.locator('#advancedDialog')).not_to_be_visible()
    assert not page.evaluate('document.getAnimations().some(a=>a.playState==="running")')


def home_stability(page):
    for width, height in [(800, 600), (960, 640), (1280, 800)]:
        page.set_viewport_size({'width': width, 'height': height})
        for theme in ['dark', 'light']:
            page.evaluate('theme=>fixtureSnapshot({nodes:[{id:"a",name:"Frankfurt",protocol:"vless"}],selected:"a",state:"idle",error:"",settings:{theme,mode:"tun",tray:true}})', theme)
            page.wait_for_timeout(400)
            selectors = ['#powerButton', '#homeServer', '#page-home .mode', '#traffic', '#homeSession']
            before = [page.locator(s).bounding_box() for s in selectors]
            page.evaluate('fixtureSnapshot({error:"Не удалось подключиться. " + "Подробности ошибки. ".repeat(100)})')
            for selector, original in zip(selectors, before):
                assert page.locator(selector).bounding_box() == original, (theme, width, selector)
            for selector in selectors[:-1]:
                box = page.locator(selector).bounding_box()
                assert box['y'] >= 38 and box['y'] + box['height'] <= height, (width, selector, box)
            page.mouse.move(width - 30, height - 40)
            page.mouse.wheel(0, 1500)
            page.wait_for_timeout(80)
            assert page.locator('.main').evaluate('e=>e.scrollTop===0&&e.scrollHeight===e.clientHeight'), (width, theme)
            assert page.evaluate('document.documentElement.scrollHeight===innerHeight')
            page.evaluate('fixtureSnapshot({error:""})')
            assert page.locator('#homeServer').bounding_box() == before[1]


def settings_navigation(page):
    page.set_viewport_size({'width': 800, 'height': 600})
    page.locator('[data-page=settings]').click()
    assert page.locator('.nav-button[data-page=diagnostics], .nav-button[data-page=logs]').count() == 0
    expect(page.locator('.nav-button[data-page=services]')).to_be_visible()
    assert page.locator('.setting-card').evaluate('e=>e.firstElementChild.id==="onlineUpdateButton"')
    assert abs(page.locator('#onlineUpdateButton').bounding_box()['height'] - page.locator('#statisticsButton').bounding_box()['height']) < 1
    for target in ['diagnostics', 'logs']:
        for key in ['Enter', 'Space']:
            link = page.locator(f'.settings-tools [data-page={target}]')
            link.focus(); link.press(key)
            expect(page.locator(f'#page-{target}')).to_be_visible()
            expect(page.locator('.nav-button[data-page=settings]')).to_have_attribute('aria-current', 'page')
            back = page.locator(f'#page-{target} [data-back-settings]')
            title = page.locator(f'#page-{target} h1').bounding_box()
            assert back.bounding_box()['x'] > title['x'] + title['width']
            back.focus(); back.press(key)
            expect(page.locator('#page-settings')).to_be_visible()
    for key in ['Enter', 'Space']:
        page.locator('#onlineUpdateButton').focus(); page.locator('#onlineUpdateButton').press(key)
        expect(page.locator('#onlineUpdateDialog')).to_be_visible()
        page.locator('#closeOnlineUpdate').click()
        expect(page.locator('#onlineUpdateDialog')).not_to_be_visible()
    assert page.locator('#hwidButton').evaluate('e=>e.closest("dialog").id==="advancedDialog"')


def startup_loading(page):
    page.add_init_script('window.fixtureReadyDelay=500;')
    page.reload(wait_until='domcontentloaded')
    expect(page.locator('#startupScreen')).to_be_visible()
    assert page.locator('.main').evaluate('e=>e.inert')
    assert page.locator('.sidebar').evaluate('e=>e.inert')
    expect(page.locator('#startupScreen')).not_to_be_visible()
    assert not page.locator('.main').evaluate('e=>e.inert')
    assert not page.locator('.sidebar').evaluate('e=>e.inert')


failures = []
with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        for test in [servers, connections, toast_layer, modal_handoff, interrupted_dialog, hidden_motion, interrupted_power, layouts, home_stability, settings_navigation, startup_loading]:
            page = browser.new_page(viewport={'width': 960, 'height': 640})
            errors = []
            page.on('pageerror', lambda e: errors.append(str(e)))
            page.add_init_script(MOCK)
            try:
                page.goto((ROOT / 'Windows/ui/index.html').as_uri())
                expect(page.locator('#selectedName')).to_have_text('Добавить подписку')
                test(page)
                assert not errors, errors
                print('PASS ' + test.__name__, flush=True)
            except Exception as error:
                failures.append(test.__name__ + ': ' + str(error))
                print('FAIL ' + failures[-1], flush=True)
            finally:
                page.close()
    finally:
        browser.close()
assert not failures, '\n'.join(failures)
