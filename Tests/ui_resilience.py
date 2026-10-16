"""All production screens/dialogs at supported sizes, long data and scaled rendering."""
import ast
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
def literal(path, name):
    return next(ast.literal_eval(n.value) for n in ast.parse((ROOT / path).read_text()).body
                if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == name for t in n.targets))
MOCK = literal('Tests/statistics_ui.py', 'MOCK')
EXTRA = literal('Tests/services_ui.py', 'EXTRA')
MOCK = MOCK.replace('window.chrome=', EXTRA + '''
window.fixtureRequests=[];
window.fixtureSnapshot=window.fixtureStatistics;
model.services.items.forEach(e=>Object.assign(e,{installedVersion:'1.0.0',strategies:e.id==='zapret'?['general.bat','general (ALT).bat']:[]}));
window.chrome=''')
MOCK = MOCK.replace("window.statisticsRequests.push(msg);setTimeout(()=>{window.fixtureStatistics();listener({data:{kind:'reply',id:msg.id,ok:true}});},10);", '''
window.fixtureRequests.push(msg);
setTimeout(()=>{
const data=msg.action==='readHwid'?{hwid:'A'.repeat(64),name:'kot. windows'}:msg.action==='readNodeQr'?{name:'Frankfurt',link:'vless://fixture@example.org:443',image:''}:undefined;
window.fixtureStatistics();listener({data:{kind:'reply',id:msg.id,ok:msg.action!==window.fixtureReject,error:'Не удалось сохранить настройки.',data}});
},window.fixtureDelay||10);
''')
OUTPUT = os.environ.get('KOT_UI_PREVIEW')
if OUTPUT:
    Path(OUTPUT).mkdir(parents=True, exist_ok=True)
LONG = 'ОченьДлинноеНазваниеБезПробелов' * 16
NODES = [dict(id='node'+str(i), name=(LONG if i == 0 else 'Сервер ' + str(i)), protocol='vless', code='VL') for i in range(120)]
ROUTING = {'active': 'rules', 'errors': {}, 'profiles': [{'id': 'rules', 'sourceUrl': '', 'refreshHours': 24,
            'rules': {'version': 1, 'name': 'Профиль маршрутизации', 'defaultRoute': 'vpn', 'direct': {'domains': [], 'ips': [], 'apps': [], 'countries': []},
                      'vpn': {'domains': [], 'ips': [], 'apps': [], 'countries': []}, 'block': {'domains': [], 'ips': [], 'apps': [], 'countries': []},
                      'dns': {'direct': 'system', 'vpn': 'cloudflare', 'blockAds': False}}}]}
PAGES = ['home', 'servers', 'statistics', 'diagnostics', 'logs', 'services', 'settings']
DIALOGS = [
 ('addDialog', 'home', ['.sidebar [data-open-add]']),
 ('advancedDialog', 'settings', ['#advancedButton']),
 ('deviceDialog', 'settings', ['#advancedButton', '#hwidButton']),
 ('pingDialog', 'servers', ['#pingSettings']),
 ('automationDialog', 'settings', ['#automationButton']),
 ('backupDialog', 'settings', ['#advancedButton', '#backupButton']),
 ('onlineUpdateDialog', 'settings', ['#onlineUpdateButton']),
 ('removeDialog', 'settings', ['#advancedButton', '#removeSubscription']),
 ('routingDialog', 'settings', ['#routingButton']),
 ('routingImportDialog', 'settings', ['#routingButton', '#routingImport']),
 ('routingRemoveDialog', 'settings', ['#routingButton', '#routingRemove']),
 ('geoIpDialog', 'settings', ['#routingButton', '#geoIpButton']),
 ('qualityDialog', 'servers', ['#qualityButton']),
 ('shareQrDialog', 'servers', ['[data-node=node0] .qr-node']),
 ('serviceSettingsDialog-zapret', 'services', ['[data-service-tab=zapret]', '#zapretSettings']),
 ('serviceSettingsDialog-telegram', 'services', ['[data-service-tab=telegram]', '#telegramSettings']),
 ('zapretTestDialog', 'services', ['[data-service-tab=zapret]', '#zapretTest']),
 ('serviceRemoveDialog', 'services', ['[data-service-tab=telegram]', '#telegramRemove']),
 ('serviceSecretDialog', 'services', ['[data-service-tab=telegram]', '#telegramSettings', '#telegramRotate']),
]

def close_all(page):
    for _ in range(6):
        if page.locator('dialog[open]').count() == 0:
            return
        page.keyboard.press('Escape')
        page.wait_for_timeout(160)
    assert not page.locator('dialog[open]').count(), 'dialog stack cannot be dismissed'

def geometry(page, selector):
    return page.locator(selector).evaluate('''e=>{
        const r=e.getBoundingClientRect();
        return {horizontal:e.scrollWidth<=e.clientWidth+2,left:r.left,right:r.right,top:r.top,bottom:r.bottom,width:innerWidth,height:innerHeight};
    }''')

def snapshot(page, name, enabled):
    if OUTPUT and enabled:
        page.screenshot(path=str(Path(OUTPUT)/(name+'.png')), animations='disabled')

def delayed_service_actions(browser):
    page=browser.new_page(viewport={'width':960,'height':640},reduced_motion='reduce')
    page.add_init_script(MOCK)
    try:
        page.goto((ROOT/'Windows/ui/index.html').as_uri())
        expect(page.locator('#startupScreen')).to_be_hidden()
        page.evaluate("fixtureNavigate('services');window.fixtureDelay=700")
        page.locator('[data-service-tab=telegram]').click();page.locator('#telegramSettings').click();page.locator('#telegramRotate').click()
        page.locator('#serviceSecretConfirm').evaluate('e=>{e.click();e.click();e.click()}')
        expect(page.locator('#serviceSecretConfirm')).to_be_disabled()
        page.wait_for_timeout(800)
        assert page.evaluate("fixtureRequests.filter(r=>r.action==='serviceRotateSecret').length") == 1, 'repeated clicks rotate the secret more than once'
        close_all(page)
        for reject in [False,True]:
            page.evaluate('reject=>window.fixtureReject=reject?"serviceSettings":""',reject)
            page.locator('[data-service-tab=telegram]').click();page.locator('#telegramSettings').click()
            page.locator('#serviceSettingsSave').click()
            page.keyboard.press('Escape');page.wait_for_timeout(160)
            page.locator('[data-service-tab=zapret]').click();page.locator('#zapretSettings').click()
            page.locator('#zapretDomains').fill('draft.example.org')
            page.wait_for_timeout(800)
            expect(page.locator('#serviceSettingsDialog')).to_be_visible()
            expect(page.locator('#zapretDomains')).to_have_value('draft.example.org')
            expect(page.locator('#serviceSettingsError')).to_have_text('')
            close_all(page)
        print('PASS delayed replies preserve a newly opened form; secret rotation executes once',flush=True)
    finally:
        page.close()

failures=[]
with sync_playwright() as pw:
    browser=pw.chromium.launch(args=['--no-sandbox'])
    try:
        delayed_service_actions(browser)
        for width,height,scale in [(800,600,1),(960,640,1.25),(960,640,1.5),(1280,800,2)]:
            for theme in ['dark','light']:
                page=browser.new_page(viewport={'width':width,'height':height},device_scale_factor=scale,reduced_motion='reduce')
                page.set_default_timeout(4000)
                errors=[]
                page.on('pageerror',lambda e:errors.append(str(e)))
                page.add_init_script(MOCK)
                try:
                    page.goto((ROOT/'Windows/ui/index.html').as_uri())
                    expect(page.locator('#startupScreen')).to_be_hidden()
                    page.evaluate('p=>fixtureSnapshot(p)',{'settings':{'theme':theme,'mode':'tun','tray':True,'bypass':''},'nodes':NODES,'selected':'node0','name':LONG[:60],
                                  'subscriptions':[{'id':'s','name':LONG[:60],'count':len(NODES)}],'activeSubscription':'s','routing':ROUTING})
                    for section in PAGES:
                        page.evaluate('p=>fixtureNavigate(p)',section)
                        page.wait_for_timeout(30)
                        box=geometry(page,'.main')
                        assert box['horizontal'], (section,box)
                        assert page.evaluate('document.documentElement.scrollWidth<=innerWidth'),section
                        snapshot(page,f'{theme}-{section}-long',scale==1)
                    for label,section,clicks in DIALOGS:
                        close_all(page)
                        page.evaluate('p=>fixtureNavigate(p)',section)
                        for selector in clicks:
                            page.locator(selector).click()
                        id=label.split('-')[0]
                        expect(page.locator('#'+id)).to_be_visible()
                        dialog=page.locator('#'+id).evaluate('e=>e.closest("dialog").id')
                        box=geometry(page,'#'+dialog)
                        assert box['horizontal'] and box['left']>=-1 and box['right']<=width+1 and box['top']>=-1 and box['bottom']<=height+1,(label,box)
                        overflow = page.locator('#'+id).evaluate('''(e,b)=>[...e.querySelectorAll('button')].filter(x=>x.getClientRects().length).filter(x=>{const r=x.getBoundingClientRect();return r.left<b.left-1||r.right>b.right+1}).map(x=>x.id)''',box)
                        assert not overflow,(label,overflow)
                        actions=page.locator('#'+id+' .dialog-actions button:visible')
                        if actions.count():
                            actions.last.scroll_into_view_if_needed()
                            b=actions.last.bounding_box()
                            assert b['y']>=0 and b['y']+b['height']<=height+1,(label,'unreachable action',b)
                        page.locator('#'+dialog).evaluate('e=>{e.scrollTop=0;e.querySelectorAll("form").forEach(f=>f.scrollTop=0)}')
                        snapshot(page,f'{theme}-{label}',scale==1)
                    close_all(page)
                    # Long unbroken errors must not push the connection button or page sideways.
                    page.evaluate('text=>fixtureSnapshot({state:"waiting",error:text,backgroundError:text})',LONG)
                    page.evaluate("fixtureNavigate('home')")
                    assert geometry(page,'.main')['horizontal']
                    snapshot(page,f'{theme}-home-offline',scale==1)
                    page.evaluate("fixtureNavigate('services');fixtureServiceItem('telegram',{busy:true,phase:'Загрузка пакета',error:'',progress:.4})")
                    page.locator('[data-service-tab=telegram]').click()
                    expect(page.locator('#telegramCancel')).to_be_enabled()
                    expect(page.locator('#telegramSettings')).to_be_disabled()
                    snapshot(page,f'{theme}-service-busy',scale==1)
                    page.evaluate('text=>fixtureServiceItem("telegram",{busy:false,error:text,phase:""})',LONG)
                    assert geometry(page,'.main')['horizontal'], 'long service error expands the page'
                    snapshot(page,f'{theme}-service-error',scale==1)
                    page.evaluate('fixtureSnapshot({nodes:[],selected:"",hasSubscription:false,subscriptions:[],activeSubscription:"",state:"idle",error:""})')
                    for section in ['home','servers','diagnostics']:
                        page.evaluate('p=>fixtureNavigate(p)',section)
                        snapshot(page,f'{theme}-{section}-empty',scale==1)
                    assert not errors,errors
                    print(f'PASS {theme} {width}x{height} scale={scale}: all pages and {len(DIALOGS)} dialogs',flush=True)
                except Exception as error:
                    failures.append(f'{theme} {width}x{height} scale={scale}: {error}')
                    snapshot(page,f'FAIL-{theme}-{width}-{scale}',True)
                    print('FAIL '+failures[-1],flush=True)
                finally:
                    page.close()
    finally:
        browser.close()
assert not failures,'\n'.join(failures)
