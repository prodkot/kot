"""P5.1 contracts against the production UI and real RPC-shaped snapshots, not the mockup."""
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
ROOT=Path(__file__).resolve().parents[1]
fixture={'__file__':str(ROOT/'Tests/ui_resilience.py')}
exec((ROOT/'Tests/ui_resilience.py').read_text().split('OUTPUT =')[0],fixture)
MOCK=fixture['MOCK']

def boot(browser,motion='reduce'):
    p=browser.new_page(viewport={'width':960,'height':640},reduced_motion=motion)
    p.set_default_timeout(3000)
    p.add_init_script(MOCK);p.route('https://api.github.com/**',lambda r:r.fulfill(json=[]))
    p.errors=[];p.on('pageerror',lambda e:p.errors.append(str(e)))
    p.goto((ROOT/'Windows/ui/index.html').as_uri());expect(p.locator('#startupScreen')).to_be_hidden()
    p.evaluate("fixtureSnapshot({name:'Моя подписка',hasSubscription:true,subscriptions:[{id:'s',name:'Моя подписка',count:1}],activeSubscription:'s',state:'connected',selected:'node',nodes:[{id:'node',name:'Frankfurt',protocol:'vless'}],favorites:['node']})")
    return p

def done(p):
    assert not p.errors,p.errors
    p.close()

def dismiss(p):
    for _ in range(8):
        if not p.locator('dialog[open]').count():return
        if p.locator('#discardDialog').is_visible():p.locator('#discardChanges').click()
        else:p.keyboard.press('Escape')
        p.wait_for_timeout(250)
    assert not p.locator('dialog[open]').count()

def forms(browser):
    p=boot(browser)
    cases=[('home',['.sidebar [data-open-add]'],'addDialog','#profileLink','https://example.org/draft'),
      ('settings',['#advancedButton'],'advancedDialog','#bypassDomains','draft.example'),
      ('servers',['#pingSettings'],'pingDialog','#pingUrl','https://example.org/ping'),
      ('settings',['#automationButton'],'automationDialog','#refreshHours','12'),
      ('settings',['#advancedButton','#backupButton'],'backupDialog','#backupPassword','not-a-real-password'),
      ('settings',['#routingButton','#routingNew'],'routingDialog','#routingName','Draft'),
      ('settings',['#routingButton','#routingImport'],'routingImportDialog','#routingImportContent','{}'),
      ('settings',['#routingButton','#geoIpButton'],'geoIpDialog','#geoIpHours','12'),
      ('servers',['#subscriptionSelect','.profile-edit'],'editSubscriptionDialog','#editSubscriptionName','Draft'),
      ('services',['[data-service-tab=telegram]','#telegramSettings'],'serviceSettingsDialog','#telegramPort','2443'),
      ('services',['[data-service-tab=zapret]','#zapretSettings'],'serviceSettingsDialog','#zapretDomains','draft.example')]
    for section,clicks,id,field,value in cases:
        dismiss(p);p.evaluate('s=>fixtureNavigate(s)',section)
        for sel in clicks:p.locator(sel).click()
        if id=='editSubscriptionDialog':expect(p.locator(field)).to_have_value('Моя подписка')
        p.locator(field).fill(value)
        p.locator('#'+id+' .cross').click();expect(p.locator('#discardDialog')).to_be_visible();expect(p.locator('#keepEditing')).to_be_focused()
        # Escape affects only the confirmation and keeps the exact draft.
        p.keyboard.press('Escape');expect(p.locator('#discardDialog')).not_to_be_visible();expect(p.locator(field)).to_have_value(value)
        p.keyboard.press('Escape');expect(p.locator('#discardDialog')).to_be_visible();p.locator('#keepEditing').click()
        expect(p.locator(field)).to_have_value(value)
        p.locator('#'+id+' .cross').click();p.locator('#discardChanges').click();expect(p.locator('#'+id)).not_to_be_visible()
    dismiss(p)
    p.evaluate("fixtureNavigate('services')");p.locator('[data-service-tab=zapret]').click();p.locator('#zapretTest').click()
    p.locator('#zapretTestNone').click();p.keyboard.press('Escape');expect(p.locator('#discardDialog')).to_be_visible();dismiss(p)
    # Save failure retains the draft, retry waits for real success and does not ask to discard.
    p.locator('#zapretSettings').click();p.locator('#zapretDomains').fill('keep.example');p.evaluate("fixtureReject='serviceSettings'")
    p.locator('#serviceSettingsSave').click();expect(p.locator('#serviceSettingsError')).to_contain_text('Не удалось')
    expect(p.locator('#zapretDomains')).to_have_value('keep.example');p.evaluate("fixtureReject=''")
    p.locator('#serviceSettingsSave').click();expect(p.locator('#serviceSettingsDialog')).not_to_be_visible();expect(p.locator('#discardDialog')).not_to_be_visible()
    done(p);print('PASS P5.1: all pending-input forms, safe discard, failed/successful native replies',flush=True)

def menu_and_ping(browser):
    p=boot(browser,'no-preference');p.evaluate("fixtureNavigate('servers')");p.wait_for_timeout(350)
    menu=p.locator('#subscriptionMenu');trigger=p.locator('#subscriptionSelect')
    for hold in [0,180,400]:
        trigger.hover();p.mouse.down();p.wait_for_timeout(hold);p.mouse.up()
        for delay in [0,35,80,160]:
            p.wait_for_timeout(delay)
            delta=p.evaluate("document.getElementById('subscriptionMenu').getBoundingClientRect().left-document.querySelector('.profile-tools').getBoundingClientRect().left")
            assert abs(delta)<=.02,delta
        p.mouse.move(10,10);expect(menu).to_be_visible()
        p.keyboard.press('Escape');p.wait_for_timeout(35)
        delta=p.evaluate("document.getElementById('subscriptionMenu').getBoundingClientRect().left-document.querySelector('.profile-tools').getBoundingClientRect().left")
        assert abs(delta)<=.02,delta
        expect(menu).not_to_be_visible()
    # Reopening during exit must cancel the old completion.
    p.evaluate("subscriptionSelect.click();subscriptionSelect.click();subscriptionSelect.click()")
    p.wait_for_timeout(350);expect(menu).to_be_visible();p.keyboard.press('Escape');expect(menu).not_to_be_visible()
    positions=[]
    for status,ms in [('idle',None),('queued',None),('checking',None),('ok',42),('ok',194),('ok',9999),('error',None),('unsupported',None),('cancelled',None)]:
        p.evaluate('(ping)=>fixtureSnapshot({nodes:[{id:"node",name:"Frankfurt",protocol:"vless",ping}]})',{'status':status,'ms':ms,'mode':'http','successes':2,'attempts':2})
        positions.append(p.locator('[data-node=node] .qr-node').bounding_box()['x'])
        assert p.locator('.ping-node').bounding_box()['width']==116
        expect(p.locator('.favorite-node')).to_be_hidden()
    assert max(positions)-min(positions)<.02,positions
    p.locator('.qr-node').focus();p.evaluate('fixtureSnapshot({nodes:[{id:"node",name:"Frankfurt changed",protocol:"vless"}]})');expect(p.locator('.qr-node')).to_be_focused()
    p.locator('#autoServer').click();p.wait_for_timeout(50)
    assert p.evaluate("fixtureRequests.filter(r=>r.action==='select').at(-1).data.id")=='auto'
    assert not p.evaluate("fixtureRequests.some(r=>r.action==='favorite')")
    done(p);print('PASS P5.1: menu press/hold/enter/exit edge, interruption, ping columns, favorite data contract',flush=True)

def modal_motion(browser):
    p=boot(browser,'no-preference');p.evaluate("fixtureNavigate('settings')")
    for _ in range(3):
        p.locator('#advancedButton').click();p.locator('#closeAdvanced').click();expect(p.locator('#advancedDialog')).not_to_be_visible();expect(p.locator('#advancedButton')).to_be_focused()
    result=p.evaluate('''async()=>{let count=0;advancedDialog.addEventListener('close',()=>count++);
      advancedButton.click();await new Promise(r=>setTimeout(r,35));closeAdvanced.click();await new Promise(r=>setTimeout(r,35));advancedButton.click();
      await new Promise(r=>setTimeout(r,350));return {open:advancedDialog.open,opacity:getComputedStyle(advancedDialog).opacity,count};}''')
    assert result=={'open':True,'opacity':'1','count':0},result
    p.locator('#advancedDialog .help-button').click();expect(p.locator('#advancedDialogHelp')).to_be_visible()
    p.keyboard.press('Escape');expect(p.locator('#advancedDialogHelp')).not_to_be_visible();expect(p.locator('#advancedDialog')).to_be_visible()
    expect(p.locator('#advancedDialog .help-button')).to_be_focused()
    p.locator('#closeAdvanced').evaluate('e=>e.click()');p.emulate_media(reduced_motion='reduce');expect(p.locator('#advancedDialog')).not_to_be_visible()
    p.locator('#advancedButton').click();expect(p.locator('#advancedDialog')).to_be_visible();assert p.locator('#advancedDialog').evaluate('e=>e.getAnimations().length')==0
    p.locator('#advancedDialog').evaluate('e=>e.close()');p.wait_for_timeout(40)
    p.locator('#advancedButton').click();p.locator('#hwidButton').click();expect(p.locator('#deviceDialog')).to_be_visible()
    assert p.locator('#advancedDialog').get_attribute('data-closing') is None
    dismiss(p);done(p);print('PASS P5.1: modal reentry, no stale close, focus, help Escape, reduced motion mid-close',flush=True)

def menu_edges_and_toast(browser):
    p=boot(browser,'no-preference');p.evaluate("fixtureNavigate('servers')");p.wait_for_timeout(320)
    menu=p.locator('#subscriptionMenu');trigger=p.locator('#subscriptionSelect')
    p.evaluate("fixtureSnapshot({subscriptions:[{id:'s',name:'Очень длинное название подписки '.repeat(10),count:1}],activeSubscription:'s'})")
    for hold in [0,180,400]:
        trigger.hover();p.mouse.down();p.wait_for_timeout(hold);p.mouse.up();expect(menu).to_be_visible()
        p.wait_for_timeout(230)
        assert abs(menu.bounding_box()['x']-p.locator('.profile-tools').bounding_box()['x'])<=.02
        # Pressing the trigger then releasing outside does not accidentally dismiss.
        trigger.hover();p.mouse.down();p.mouse.move(5,5);p.mouse.up();expect(menu).to_be_visible()
        p.keyboard.press('Escape');expect(menu).not_to_be_visible()
    trigger.click();expect(menu).to_have_attribute('data-upwards','false')
    p.set_viewport_size({'width':800,'height':480});p.wait_for_timeout(300)
    expect(menu).to_have_attribute('data-upwards','true')
    box=menu.bounding_box();anchor=p.locator('.profile-tools').bounding_box()
    assert box['y']>=12 and box['y']+box['height']<=anchor['y']-7
    assert abs(box['x']-anchor['x'])<=.02
    p.set_viewport_size({'width':960,'height':640});p.wait_for_timeout(300)
    expect(menu).to_have_attribute('data-upwards','false')
    p.mouse.click(10,10);expect(menu).not_to_be_visible()
    p.evaluate("fixtureSnapshot({nodes:Array.from({length:40},(_,i)=>({id:'n'+i,name:'Сервер '+i,protocol:'vless'}))})")
    trigger.click();p.locator('.main').evaluate('e=>e.scrollTop=100');expect(menu).not_to_be_visible()
    p.evaluate("fixtureNavigate('settings')");p.locator('#advancedButton').click();p.locator('#hwidButton').click()
    expect(p.locator('#copyHwid')).to_be_enabled();p.locator('#copyHwid').click();expect(p.locator('#toast')).to_be_visible()
    p.wait_for_function("()=>toast.getAnimations().some(a=>a.playState==='running'&&a.effect.getTiming().duration===200)",timeout=6000)
    # A second successful copy during exit keeps the new notification alive.
    p.locator('#copyHwid').click();p.wait_for_timeout(350);expect(p.locator('#toast')).to_be_visible()
    p.wait_for_function("()=>toast.getAnimations().some(a=>a.playState==='running'&&a.effect.getTiming().duration===200)",timeout=6000)
    p.emulate_media(reduced_motion='reduce');expect(p.locator('#toast')).to_be_hidden()
    done(p);print('PASS P5.1: long menu press/release, up/down, resize, outside/scroll dismissal, toast reentry and reduced exit',flush=True)

def update_quality(browser):
    p=boot(browser);p.evaluate("fixtureNavigate('settings')");p.locator('#onlineUpdateButton').click()
    p.evaluate("fixtureSnapshot({updates:{busy:false,availableVersion:'9.9.9',status:'Доступна версия 9.9.9'}})")
    before=p.locator('#downloadUpdate').bounding_box()
    p.locator('#downloadUpdate').click();p.wait_for_timeout(50)
    assert p.evaluate("fixtureRequests.filter(r=>r.action==='downloadUpdate').at(-1).data.version")=='9.9.9'
    for amount in [0,100,750,1000]:
        p.evaluate('bytes=>fixtureSnapshot({updates:{busy:true,availableVersion:"9.9.9",size:1000,bytes,status:"Загрузка"}})',amount)
        expect(p.locator('#updateProgress')).to_have_attribute('value',str(amount))
        assert p.locator('#smoothUpdateProgress').get_attribute('data-indeterminate')=='false'
        now=p.locator('#downloadUpdate').bounding_box();assert now==before,(now,before)
    p.evaluate('fixtureSnapshot({updates:{busy:true,availableVersion:"9.9.9",size:0,bytes:100,status:"Загрузка"}})')
    assert p.locator('#updateProgress').get_attribute('value') is None
    p.locator('#closeOnlineUpdate').click();expect(p.locator('#onlineUpdateDialog')).not_to_be_visible()
    assert not p.evaluate("fixtureRequests.some(r=>r.action==='cancelUpdate')")
    p.evaluate("fixtureNavigate('servers')");p.locator('#qualityButton').click();before=p.locator('#testQuality').bounding_box()
    for state in [dict(busy=True,kind='connection',status='Проверка'),dict(busy=False,kind='connection',quality={'medianMs':42,'successes':2,'attempts':2},status='Готово'),dict(busy=False,kind='connection',error='Ошибка',status='Ошибка')]:
        p.evaluate('tools=>fixtureSnapshot({tools})',state);assert p.locator('#testQuality').bounding_box()==before
    p.evaluate("fixtureSnapshot({tools:{busy:true,kind:'connection',status:'Проверка'}})");p.locator('#closeQuality').click();p.wait_for_timeout(40)
    assert p.evaluate("fixtureRequests.some(r=>r.action==='cancelTools')")
    done(p);print('PASS P5.1: real bytes/progress and stable actions; update continues on X, quality cancels',flush=True)

def all_dialog_cycles(browser):
    p=boot(browser,'no-preference')
    cases=fixture['literal']('Tests/ui_resilience.py','DIALOGS')
    routing=fixture['literal']('Tests/ui_resilience.py','ROUTING')
    p.evaluate('routing=>fixtureSnapshot({routing})',routing)
    for label,section,clicks in cases:
        for cycle in range(3):
            dismiss(p);p.evaluate('s=>fixtureNavigate(s)',section)
            for selector in clicks:p.locator(selector.replace('node0','node')).click()
            target=p.locator('#'+label.split('-')[0]);expect(target).to_be_visible()
            owner=target.evaluate('e=>e.closest("dialog").id')
            # The finished promise is delivered on a subsequent rendering tick.
            # Wait for cleanup rather than sampling just after the 280 ms boundary.
            p.wait_for_function("id=>{const e=document.getElementById(id);return getComputedStyle(e).opacity==='1'&&!e.getAnimations().length}",arg=owner)
            state=p.locator('#'+owner).evaluate("e=>({opacity:getComputedStyle(e).opacity,motions:e.getAnimations().map(a=>({name:a.constructor.name,state:a.playState,time:a.currentTime,duration:a.effect.getTiming().duration}))})")
            assert state['opacity']=='1' and not state['motions'],(label,cycle,state)
            # Keyboard navigation stays within the native modal, including long forms.
            for key in ['Tab','Tab','Shift+Tab']:
                p.keyboard.press(key)
                assert p.locator('#'+owner).evaluate('e=>document.activeElement===document.body||e.contains(document.activeElement)'),label
            dismiss(p)
        print('PASS P5.1 cycles: '+label,flush=True)
    done(p)

def validation_and_progress(browser):
    p=boot(browser)
    p.evaluate("fixtureNavigate('servers')");p.locator('#pingSettings').click()
    p.locator('#pingTimeout').fill('1');p.locator('#pingForm [type=submit]').click()
    expect(p.locator('#pingTimeout')).to_be_focused();expect(p.locator('#pingTimeout')).to_have_attribute('aria-invalid','true')
    expect(p.locator('#pingTimeoutValidity')).to_be_visible();assert 'pingTimeoutValidity' in p.locator('#pingTimeout').get_attribute('aria-describedby')
    assert not p.evaluate("fixtureRequests.some(r=>r.action==='pingSettings')")
    p.locator('#pingTimeout').fill('500');expect(p.locator('#pingTimeoutValidity')).to_be_hidden();dismiss(p)
    p.evaluate("fixtureNavigate('settings')");p.locator('#onlineUpdateButton').click()
    p.emulate_media(reduced_motion='no-preference')
    p.evaluate("fixtureSnapshot({updates:{busy:true,size:1000,bytes:0,availableVersion:'9.9.9',status:'Загрузка'}})")
    p.wait_for_timeout(30)
    p.evaluate('fixtureSnapshot({updates:{busy:true,size:1000,bytes:1000,availableVersion:"9.9.9",status:"Загрузка"}})')
    p.wait_for_timeout(150)
    scale=p.locator('#smoothUpdateProgress>span').evaluate('e=>new DOMMatrixReadOnly(getComputedStyle(e).transform).a')
    assert 0<scale<1,scale
    p.evaluate('fixtureSnapshot({updates:{busy:false,size:1000,bytes:500,availableVersion:"9.9.9",error:"Ошибка загрузки",status:"Остановлено"}})')
    expect(p.locator('#downloadUpdate')).to_have_text('Повторить');before=p.locator('#downloadUpdate').bounding_box()
    p.evaluate('fixtureSnapshot({updates:{busy:true,size:1000,bytes:0,availableVersion:"9.9.9",status:"Загрузка"}})')
    assert p.locator('#downloadUpdate').bounding_box()==before
    scale=p.locator('#smoothUpdateProgress>span').evaluate('e=>new DOMMatrixReadOnly(getComputedStyle(e).transform).a');assert scale==0,scale
    p.emulate_media(reduced_motion='reduce');dismiss(p)
    # Failed clipboard calls cannot claim success.
    p.locator('#advancedButton').click();p.locator('#hwidButton').click();p.evaluate("fixtureReject='copyHwid'")
    p.locator('#copyHwid').click();expect(p.locator('#hwidError')).to_contain_text('Не удалось')
    assert p.locator('#toast').is_hidden() or p.locator('#toast').inner_text()!='HWID скопирован'
    done(p);print('PASS P5.1: native invalid fields, animated real progress/reset, copy failure',flush=True)

with sync_playwright() as pw:
    browser=pw.chromium.launch(args=['--no-sandbox'])
    try:
        for test in [forms,menu_and_ping,modal_motion,menu_edges_and_toast,update_quality,validation_and_progress,all_dialog_cycles]:test(browser)
    finally:browser.close()
