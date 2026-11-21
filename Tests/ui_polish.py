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


def journal_focus(page):
    page.evaluate('fixtureSnapshot({journal:"[app] ready"})')
    page.evaluate("fixtureNavigate('logs')")
    journal = page.locator('#journalText')
    journal.focus()
    page.evaluate('fixtureSnapshot({journal:"[app] ready\\n[app] connected"})')
    expect(journal).to_be_focused()
    expect(journal).to_contain_text('connected')


def drag_prefix(page, element, length):
    element.scroll_into_view_if_needed()
    x1, x2, y = element.evaluate('''(el, length) => {
      const range = document.createRange();
      range.setStart(el.firstChild, 0);
      range.setEnd(el.firstChild, length);
      const rect = range.getBoundingClientRect();
      return [rect.left, rect.right, rect.top + rect.height / 2];
    }''', length)
    page.mouse.move(x1, y)
    page.mouse.down()
    page.mouse.move(x2, y, steps=8)
    page.mouse.up()


def quality_address(page):
    page.emulate_media(reduced_motion='reduce')
    url = 'https://www.gstatic.com/generate_204'
    page.evaluate("fixtureSnapshot({state:'connected'});fixtureNavigate('servers')")
    page.locator('#qualityButton').click()
    address = page.locator('#qualityAddress')
    for width, height in [(960, 640), (640, 540)]:
        page.set_viewport_size({'width': width, 'height': height})
        for value in [url, 'https://example.org/' + 'x' * 1800]:
            page.evaluate("address=>fixtureSnapshot({tools:{kind:'connection',address}})", value)
            expect(address).to_have_text(value)
            assert address.evaluate('e=>e.scrollWidth===e.clientWidth && e.scrollHeight===e.clientHeight')
            if value == url:
                assert address.bounding_box()['height'] == 40
                address.hover()
                page.mouse.wheel(0, 100)
                page.wait_for_timeout(100)
                assert address.evaluate('e=>e.scrollTop===0')
                drag_prefix(page, address, len(url))
                page.evaluate('fixtureSnapshot({})')
                assert page.evaluate('getSelection().toString()') == url
            else:
                assert address.bounding_box()['height'] > 40
                expect(page.locator('#closeQuality')).to_be_in_viewport()


def copy_log_selection(page):
    page.emulate_media(reduced_motion='reduce')
    search = page.locator('#logSearch')

    def snapshot(suffix):
        page.evaluate('patch=>fixtureSnapshot(patch)', {
            'journal': 'client log' + suffix,
            'services': {'items': [{'id': service, 'logs': [service + ' log' + suffix]}
                                   for service in ['zapret', 'telegram']]}
        })

    for provider in ['client', 'zapret', 'telegram']:
        prefix = provider + ' log'
        if provider == 'client':
            page.evaluate("fixtureNavigate('logs')")
            drag_prefix(page, page.locator('#page-logs h1'), 3)
            assert page.evaluate('getSelection().toString()') == ''
            log = page.locator('#journalText')
        else:
            page.evaluate("fixtureNavigate('services')")
            page.locator(f'[data-service-tab={provider}]').click()
            page.locator(f'#{provider}Panel .service-log summary').click()
            log = page.locator(f'#{provider}Log')
        snapshot(' entry')
        expect(log).to_have_text(prefix + ' entry')
        drag_prefix(page, log, len(prefix))
        snapshot(' updated')
        assert page.evaluate('getSelection().toString()') == prefix
        page.keyboard.press('Control+c')
        if provider != 'client':
            log.click()
            expect(log).to_have_text(prefix + ' updated')
            page.evaluate("fixtureNavigate('logs')")
        search.fill('')
        search.focus()
        page.keyboard.press('Control+v')
        expect(search).to_have_value(prefix)


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
    # The P5.1 return interpolates the interrupted pulse instead of snapping.
    animations = page.locator('#powerIcon').evaluate("e=>e.getAnimations().filter(a=>a.playState==='running').map(a=>a.effect.getTiming().duration)")
    assert animations == [300], animations
    page.wait_for_timeout(340)
    assert not page.locator('#powerIcon').evaluate("e=>e.getAnimations().some(a=>a.playState==='running')"), 'return persists after disconnect'


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


def connection_chrome(page):
    page.set_viewport_size({'width': 800, 'height': 600})
    assert page.locator('#page-home [data-open-add]').count() == 0
    for theme in ['dark', 'light']:
        page.evaluate('theme=>fixtureSnapshot({settings:{theme,mode:"tun",tray:true}})', theme)
        for state in ['idle', 'waiting', 'connecting', 'connected', 'disconnecting', 'idle', 'connected']:
            page.evaluate('state=>fixtureSnapshot({state})', state)
            assert page.locator('.title-brand .brand-dot').evaluate('''(e,connected)=>{
              const colors=getComputedStyle(document.body),expected=document.createElement('span');
              expected.style.color=colors.getPropertyValue(connected?'--accent':'--muted');
              document.body.append(expected);
              const matches=getComputedStyle(e).backgroundColor===getComputedStyle(expected).color;
              expected.remove();return matches;
            }''', state == 'connected'), (theme, state)
    page.evaluate("fixtureSnapshot({state:'idle'})")
    expect(page.locator('#sidebarPower')).to_have_accessible_name('Подключить')
    page.evaluate("fixtureNavigate('servers')")
    page.locator('#page-servers [data-open-add]').click()
    expect(page.locator('#addDialog')).to_be_visible()


def sidebar_power(page):
    side, main = page.locator('#sidebarPower'), page.locator('#powerButton')
    tip = page.get_by_role('tooltip', include_hidden=True)
    expect(side).to_be_disabled()
    expect(side).to_have_accessible_description('Сервер не выбран')
    page.evaluate('nodes=>fixtureSnapshot({nodes,selected:"a"})', NODES)
    expect(side).to_have_accessible_description('Frankfurt')
    page.evaluate('''() => {
      const send=chrome.webview.postMessage;
      window.toggleCount=0;
      chrome.webview.postMessage=message=>{
        if(message.action!=='toggle')return send(message);
        toggleCount++;window.releaseToggle=()=>send(message);
      };
    }''')
    side.click()
    page.evaluate("fixtureSnapshot({});sidebarPower.click();powerButton.click()")
    expect(side).to_be_disabled()
    expect(main).to_be_disabled()
    assert page.evaluate('toggleCount') == 1
    page.evaluate('releaseToggle()')
    expect(side).to_have_accessible_name('Подключение…')
    expect(main).to_be_enabled()
    expect(main).to_have_accessible_name('Отменить')
    expect(side).to_be_disabled()
    expect(side.locator('circle')).to_be_visible()
    main.click()
    page.evaluate('releaseToggle()')
    expect(side).to_have_accessible_name('Подключить')
    expect(side).to_be_enabled()
    page.evaluate("fixtureSnapshot({state:'connected',selected:'b'})")
    expect(side).to_have_accessible_name('Отключить')
    expect(side).to_have_accessible_description('Amsterdam')
    side.press('Enter')
    page.evaluate("fixtureSnapshot({state:'disconnecting'});sidebarPower.click();powerButton.click()")
    expect(side).to_have_accessible_name('Отключение…')
    expect(side.locator('circle')).to_be_visible()
    expect(main).to_be_disabled()
    assert page.evaluate('toggleCount') == 3
    page.evaluate("window.fixtureToggleError=true;releaseToggle()")
    expect(page.locator('#toast')).to_have_text('Ошибка подключения')
    expect(side).to_be_disabled()
    page.evaluate("fixtureSnapshot({state:'connected'})")
    expect(side).to_be_enabled()
    expect(side).to_have_accessible_name('Отключить')
    expect(main).to_be_enabled()
    page.evaluate("fixtureSnapshot({selected:'auto',automaticNode:'b',state:'waiting'})")
    expect(side).to_have_accessible_name('Отменить')
    expect(side).to_have_accessible_description('Amsterdam')
    page.evaluate("fixtureSnapshot({selected:'',state:'idle',nodes:[]})")
    expect(side).to_be_disabled()
    expect(tip).to_have_text('Сервер не выбран')


def sidebar_tooltip(page):
    long_name = 'Франкфурт · ' + 'Длинное название сервера ' * 8
    side, tip = page.locator('#sidebarPower'), page.locator('#sidebarServer')
    for width, height in [(960, 640), (800, 600), (640, 540)]:
        page.set_viewport_size({'width': width, 'height': height})
        side.hover()
        expect(tip).to_have_css('opacity', '1')
        assert side.locator('span').is_visible() == (width > 700)
        # Change the selection while the tooltip stays open. It must shrink again
        # after displaying a name that fills the entire button width.
        for name in ['DE', long_name, 'I']:
            page.evaluate('name=>fixtureSnapshot({nodes:[{id:"de",name,protocol:"vless"}],selected:"de"})', name)
            expect(tip).to_have_text(name.strip())
            bubble, button = tip.bounding_box(), side.bounding_box()
            assert abs((bubble['x'] + bubble['width']/2) - (button['x'] + button['width']/2)) < 0.1
            assert bubble['y'] >= 0 and bubble['y'] + bubble['height'] < button['y']
            if name == long_name:
                assert abs(bubble['width'] - button['width']) < 0.1
                assert tip.evaluate('e=>e.scrollWidth>e.clientWidth')
            else:
                assert bubble['width'] < button['width']
                assert tip.evaluate('e=>e.scrollWidth===e.clientWidth')
        expect(tip).to_have_css('text-overflow', 'ellipsis')
        expect(tip).to_have_css('pointer-events', 'none')
        page.mouse.move(width - 15, 20)
        expect(tip).to_be_hidden()
    page.keyboard.press('Tab')
    side.focus()
    expect(tip).to_have_css('opacity', '1')
    expect(side).to_be_focused()


def settings_backdrop(page):
    page.emulate_media(reduced_motion='reduce')
    page.locator('[data-page=settings]').click()
    for button, dialog in [('automationButton', 'automationDialog'), ('advancedButton', 'advancedDialog'),
                           ('routingButton', 'routingDialog'), ('onlineUpdateButton', 'onlineUpdateDialog'),
                           ('aboutButton', 'aboutDialog')]:
        trigger = page.locator('#' + button)
        trigger.click()
        panel = page.locator('#' + dialog)
        panel.locator('h2').click()
        expect(panel).to_be_visible()
        page.mouse.click(2, 60)
        expect(panel).to_be_hidden()
        expect(trigger).to_be_focused()

    page.locator('#routingButton').click()
    for button, dialog in [('routingImport', 'routingImportDialog'), ('geoIpButton', 'geoIpDialog')]:
        page.locator('#' + button).click()
        page.mouse.click(2, 60)
        expect(page.locator('#' + dialog)).to_be_hidden()
        expect(page.locator('#routingDialog')).to_be_visible()
    page.mouse.click(2, 60)
    page.locator('#advancedButton').click()
    page.locator('#backupButton').click()
    page.mouse.click(2, 60)
    expect(page.locator('#backupDialog')).to_be_hidden()

    page.locator('#advancedButton').click()
    page.locator('#bypassDomains').fill('draft.example')
    page.locator('#hwidButton').click()
    page.mouse.click(2, 60)
    expect(page.locator('#deviceDialog')).to_be_hidden()
    expect(page.locator('#hwidButton')).to_be_focused()
    page.locator('#bypassDomains').hover()
    page.mouse.down()
    page.mouse.move(2, 60)
    page.mouse.up()
    expect(page.locator('#discardDialog')).to_be_hidden()
    page.mouse.click(2, 60)
    expect(page.locator('#keepEditing')).to_be_focused()
    page.locator('#keepEditing').click()
    expect(page.locator('#bypassDomains')).to_have_value('draft.example')
    page.mouse.click(2, 60)
    page.locator('#discardChanges').click()
    expect(page.locator('#advancedDialog')).to_be_hidden()
    expect(page.locator('#advancedButton')).to_be_focused()


def settings_navigation(page):
    page.set_viewport_size({'width': 800, 'height': 600})
    page.locator('[data-page=settings]').click()
    assert page.locator('.nav-button[data-page=diagnostics], .nav-button[data-page=logs]').count() == 0
    expect(page.locator('.nav-button[data-page=services]')).to_be_visible()
    assert page.locator('.setting-card').evaluate('e=>e.firstElementChild.id==="onlineUpdateButton"')
    assert abs(page.locator('#onlineUpdateButton').bounding_box()['height'] - page.locator('#statisticsButton').bounding_box()['height']) < 1
    for target in ['logs']:
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
        for test in [servers, journal_focus, quality_address, copy_log_selection, toast_layer, modal_handoff, interrupted_dialog, hidden_motion, interrupted_power, layouts, home_stability, connection_chrome, sidebar_power, sidebar_tooltip, settings_navigation, settings_backdrop, startup_loading]:
            page = browser.new_page(viewport={'width': 960, 'height': 640})
            errors = []
            page.on('pageerror', lambda e: errors.append(str(e)))
            page.add_init_script(MOCK)
            try:
                page.goto((ROOT / 'Windows/ui/index.html').as_uri())
                expect(page.locator('#selectedName')).to_have_text('Добавить подписку')
                expect(page.locator('#startupScreen')).to_be_hidden()
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
