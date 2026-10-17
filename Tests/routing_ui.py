"""Routing editor behavior against the production HTML and WebView message bridge."""
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
MOCK = r'''(() => {
let callback, sequence=0;
const model={name:'Подписка',hasSubscription:false,nodes:[],selected:'',state:'idle',error:'',
 settings:{theme:'dark',accent:'lime',mode:'tun',startup:false,autoConnect:false,tray:true,sendHwid:true,killSwitch:false,bypass:''},
 ping:{busy:false,done:0,total:0,error:'',settings:{mode:'http',url:'https://www.gstatic.com/generate_204',timeoutMs:5000,attempts:2,parallelism:3,sort:'none'}},
 version:'0.5.9',updates:{busy:false,status:'',error:''},favorites:[],subscriptions:[],activeSubscription:'',
 automation:{reconnect:true,refreshHours:6,pingMinutes:0,autoMinutes:3,autoToleranceMs:50},
 routing:{active:'',profiles:[],errors:{}},geoIp:{busy:false,error:'',refreshHours:24,countries:[]}};
let geoIpDate='2026-10-11T14:00:00Z';
const snapshot=()=>{
 const codes=[...new Set(model.routing.profiles.flatMap(p=>['directCountries','proxyCountries','blockCountries'].flatMap(k=>p.rules[k]||[])).map(c=>c.toLowerCase()))].sort();
 model.geoIp.countries=codes.map(code=>({code,ready:true,updated:geoIpDate}));
 callback({data:{kind:'snapshot',data:structuredClone(model)}});
};
window.routingRequests=[];
window.fixtureSnapshot=()=>snapshot();
window.fixtureProvider=()=>{model.nodes=[{id:'provider',name:'Provider',protocol:'vless',providerRouting:true}];model.selected='provider';model.routing.active='';snapshot();};
window.fixtureProfiles=()=>structuredClone(model.routing);
window.fixtureGeoIp=()=>structuredClone(model.geoIp);
window.chrome={webview:{addEventListener:(_,cb)=>callback=cb,postMessage:msg=>setTimeout(()=>{
 const reply=(ok=true,error='')=>callback({data:{kind:'reply',id:msg.id,ok,error}});
 const find=()=>model.routing.profiles.find(p=>p.id===msg.data.id);
 if(msg.action.startsWith('routing')||msg.action.startsWith('geoIp'))window.routingRequests.push(structuredClone(msg));
 switch(msg.action){
 case 'routingSave':
   if(window.fixtureSaveError){reply(false,'Введите имена приложений без пути.');return;}
   const saved={id:msg.data.id||'route'+(++sequence),rules:msg.data.rules,sourceUrl:msg.data.sourceUrl,refreshHours:msg.data.refreshHours,updated:null};
   const index=model.routing.profiles.findIndex(p=>p.id===saved.id);
   if(index<0)model.routing.profiles.push(saved);else model.routing.profiles[index]=saved;
   model.routing.active=saved.id;break;
 case 'routingSelect':model.routing.active=msg.data.id;break;
 case 'routingExport':window.fixtureExport=find();break;
 case 'routingImport':
   if(window.fixtureImportError){reply(false,'Неверный JSON-профиль маршрутизации kot.');return;}
   const remote=msg.data.content.startsWith('https://');
   const imported={id:'route'+(++sequence),rules:remote?{version:1,name:'Обновляемый профиль',defaultAction:'proxy',proxyDomains:['youtube.com'],directDns:'cloudflare',remoteDns:'adguard'}:JSON.parse(msg.data.content),sourceUrl:remote?msg.data.content:'',refreshHours:24,updated:'2026-10-11T12:00:00Z'};
   model.routing.profiles.push(imported);model.routing.active=imported.id;break;
 case 'routingRefresh':
   if(window.fixtureRefreshError){model.routing.errors[msg.data.id]='Последние рабочие правила сохранены';snapshot();reply(false,'Источник профиля вернул HTTP 503.');return;}
   delete model.routing.errors[msg.data.id];find().rules.proxyDomains=['updated.example'];find().updated='2026-10-12T12:00:00Z';break;
 case 'routingRemove':model.routing.profiles=model.routing.profiles.filter(p=>p.id!==msg.data.id);if(model.routing.active===msg.data.id)model.routing.active='';break;
 case 'geoIpSettings':model.geoIp.refreshHours=msg.data.refreshHours;break;
 case 'geoIpRefresh':
   if(window.fixtureGeoIpError){model.geoIp.error='Не удалось обновить GeoIP. Рабочая база сохранена.';snapshot();reply(false,'Источник GeoIP вернул HTTP 503.');return;}
   model.geoIp.error='';geoIpDate='2026-10-12T14:00:00Z';break;
 }
 snapshot();reply();
},window.fixtureDelay||10)}};
})();'''

with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        page = browser.new_page(viewport={'width': 960, 'height': 640})
        errors = []
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.add_init_script(MOCK)
        page.goto((ROOT / 'Windows/ui/index.html').as_uri())
        page.locator('#homeRoutingButton').click()
        expect(page.locator('#routingEmpty')).to_be_visible()
        expect(page.locator('#routingForm')).not_to_be_visible()
        page.locator('#routingNew').click()
        page.locator('#routingName').fill('Работа')
        page.locator('#routingDefault').select_option('direct')
        page.locator('#routingDirectDomains').fill('.ru\n.рф\nпример.рф\n \n')
        page.locator('#routingProxyDomains').fill('youtube.com; wikipedia.org')
        page.locator('#routingBlockDomains').fill('ads.example')
        page.locator('#routingTabDomains').press('ArrowRight')
        expect(page.locator('#routingTabIps')).to_be_focused()
        expect(page.locator('#routingPanelDomains')).not_to_be_visible()
        page.locator('#routingProxyIps').fill('203.0.113.0/24')
        page.locator('#routingTabIps').press('ArrowRight')
        page.locator('#routingProxyApps').fill('chrome.exe,telegram.exe')
        page.locator('#routingDirectApps').fill('game.exe')
        page.locator('#routingTabApps').press('End')
        expect(page.locator('#routingTabCountries')).to_be_focused()
        page.locator('#routingDirectCountries').fill('RU')
        page.locator('#routingProxyCountries').fill('US')
        page.locator('#routingBlockCountries').fill('CN')
        page.locator('#routingTabCountries').press('ArrowLeft')
        page.locator('#routingRemoteDns').select_option('adguard')
        # Repeated snapshots preserve unsaved rules and the selected tab.
        page.evaluate('fixtureSnapshot();fixtureSnapshot()')
        expect(page.locator('#routingProxyApps')).to_have_value('chrome.exe,telegram.exe')
        expect(page.locator('#routingActive')).to_be_disabled()
        expect(page.locator('#routingImport')).to_be_disabled()
        page.evaluate('window.fixtureDelay=250')
        submit = page.locator('#routingForm [type=submit]')
        submit.click()
        page.evaluate('fixtureSnapshot()')
        expect(submit).to_be_disabled()
        submit.evaluate('(button)=>button.click()')
        expect(page.locator('#routingName')).to_have_value('Работа')
        expect(page.locator('#routingActive')).to_have_value('route1')
        requests = page.evaluate("routingRequests.filter(r=>r.action==='routingSave')")
        assert len(requests) == 1
        assert requests[0]['data']['rules']['directDomains'] == ['.ru', '.рф', 'пример.рф']
        assert requests[0]['data']['rules']['proxyApps'] == ['chrome.exe', 'telegram.exe']
        assert requests[0]['data']['rules']['proxyDomains'] == ['youtube.com', 'wikipedia.org']
        assert requests[0]['data']['rules']['proxyIps'] == ['203.0.113.0/24']
        assert requests[0]['data']['rules']['remoteDns'] == 'adguard'
        assert requests[0]['data']['rules']['directCountries'] == ['RU']
        # GeoIP updates are independent of the routing draft; snapshots preserve interval edits.
        page.locator('#geoIpButton').click()
        expect(page.locator('#geoIpCountries')).to_contain_text('RU')
        expect(page.locator('#geoIpCountries')).to_contain_text('Проверено:')
        page.locator('#geoIpHours').fill('0')
        page.evaluate('fixtureSnapshot();fixtureSnapshot()')
        expect(page.locator('#geoIpHours')).to_have_value('0')
        page.locator('#geoIpForm [type=submit]').click()
        expect(page.locator('#geoIpRefresh')).to_be_enabled()
        assert page.evaluate('fixtureGeoIp().refreshHours') == 0
        page.evaluate('window.fixtureGeoIpError=true;window.fixtureDelay=250')
        previous_date = page.evaluate('fixtureGeoIp().countries[0].updated')
        page.locator('#geoIpRefresh').click()
        expect(page.locator('#geoIpRefresh')).to_have_text('Обновление…')
        page.locator('#geoIpRefresh').evaluate('(button)=>button.click()')
        expect(page.locator('#geoIpError')).to_contain_text('Рабочая база сохранена')
        assert page.evaluate('fixtureGeoIp().countries[0].updated') == previous_date
        assert page.evaluate("routingRequests.filter(r=>r.action==='geoIpRefresh').length") == 1
        page.evaluate('window.fixtureGeoIpError=false;window.fixtureDelay=0')
        page.locator('#geoIpRefresh').click()
        expect(page.locator('#geoIpError')).to_be_empty()
        assert page.evaluate('fixtureGeoIp().countries[0].updated') != previous_date
        page.locator('#geoIpHours').fill('24')
        page.locator('#geoIpForm [type=submit]').click()
        expect(page.locator('#geoIpRefresh')).to_be_enabled()
        assert page.evaluate('fixtureGeoIp().refreshHours') == 24
        for theme in ['dark', 'light']:
            page.evaluate('(light)=>document.body.classList.toggle("theme-light",light)', theme == 'light')
            for width, height in [(800,600),(960,640),(700,660)]:
                page.set_viewport_size({'width':width,'height':height})
                box=page.locator('#geoIpDialog').bounding_box()
                assert box['y']>=0 and box['y']+box['height']<=height
                assert page.locator('#geoIpDialog').evaluate('(e)=>e.scrollWidth===e.clientWidth')
            page.set_viewport_size({'width':960,'height':640})
            page.screenshot(path=str(ROOT/f'Tests/ui-geoip-{theme}.png'),animations='disabled')
        page.evaluate('document.body.classList.remove("theme-light")')
        page.locator('#closeGeoIp').click()
        page.locator('#routingTabCountries').click()
        expect(page.locator('#routingDirectCountries')).to_have_value('RU')
        page.screenshot(path=str(ROOT/'Tests/ui-countries-dark.png'),animations='disabled')
        page.evaluate('window.fixtureDelay=0')
        # Failed validation leaves the editable draft intact.
        page.locator('#routingTabApps').click()
        page.locator('#routingProxyApps').fill('C:\\bad.exe')
        page.evaluate('window.fixtureSaveError=true')
        submit.click()
        expect(page.locator('#routingError')).to_have_text('Введите имена приложений без пути.')
        expect(page.locator('#routingProxyApps')).to_have_value('C:\\bad.exe')
        page.locator('#routingReset').click()
        page.evaluate('window.fixtureSaveError=false')
        page.locator('#routingTabApps').click()
        expect(page.locator('#routingProxyApps')).to_have_value('chrome.exe\ntelegram.exe')
        page.locator('#routingTabDomains').click()
        for theme in ['dark', 'light']:
            page.evaluate('(light)=>document.body.classList.toggle("theme-light",light)', theme == 'light')
            for width, height in [(800, 600), (960, 640), (700, 660)]:
                page.set_viewport_size({'width': width, 'height': height})
                box = page.locator('#routingDialog').bounding_box()
                assert box['y'] >= 0 and box['y'] + box['height'] <= height
                assert page.locator('#routingDialog').evaluate('(e)=>e.scrollWidth===e.clientWidth')
                submit.scroll_into_view_if_needed()
                expect(submit).to_be_in_viewport()
            page.set_viewport_size({'width': 960, 'height': 640})
            page.locator('#routingDialog').evaluate('(e)=>e.scrollTop=0')
            page.screenshot(path=str(ROOT / f'Tests/ui-routing-{theme}.png'), animations='disabled')
        page.evaluate('document.body.classList.remove("theme-light")')
        page.locator('#routingExport').click()
        page.wait_for_function('()=>window.fixtureExport?.id==="route1"')
        page.locator('#routingImport').click()
        page.locator('#routingImportContent').fill('{broken json}')
        page.evaluate('window.fixtureImportError=true')
        page.locator('#routingImportForm [type=submit]').click()
        expect(page.locator('#routingImportError')).to_contain_text('Неверный JSON')
        expect(page.locator('#routingImportContent')).to_have_value('{broken json}')
        page.evaluate('window.fixtureImportError=false')
        page.locator('#routingImportContent').fill('https://rules.example/routing.json')
        page.locator('#routingImportForm [type=submit]').click()
        expect(page.locator('#routingImportDialog')).not_to_be_visible()
        expect(page.locator('#routingSource')).to_have_value('https://rules.example/routing.json')
        expect(page.locator('#routingHours')).to_have_value('24')
        expect(page.locator('#routingRefresh')).to_be_enabled()
        page.evaluate('window.fixtureRefreshError=true')
        page.locator('#routingRefresh').click()
        expect(page.locator('#routingError')).to_contain_text('HTTP 503')
        expect(page.locator('#routingProxyDomains')).to_have_value('youtube.com')
        expect(page.locator('#routingStatus')).to_contain_text('Последние рабочие правила сохранены')
        page.evaluate('window.fixtureRefreshError=false')
        page.locator('#routingRefresh').click()
        expect(page.locator('#routingProxyDomains')).to_have_value('updated.example')
        # Detaching a source and disabling auto updates are saved explicitly.
        page.locator('#routingSource').fill('')
        page.locator('#routingHours').fill('0')
        submit.click()
        expect(page.locator('#routingActive')).to_be_enabled()
        expect(page.locator('#routingRefresh')).to_be_disabled()
        assert page.evaluate('fixtureProfiles().profiles[1].refreshHours') == 0
        # A shared JSON name is rendered as text, including in removal confirmation.
        page.locator('#routingImport').click()
        page.locator('#routingImportContent').fill('{"version":1,"name":"<b>Личный</b>"}')
        page.locator('#routingImportForm [type=submit]').click()
        expect(page.locator('#routingImportDialog')).not_to_be_visible()
        expect(page.locator('#routingName')).to_have_value('<b>Личный</b>')
        page.locator('#routingRemove').click()
        expect(page.locator('#routingRemoveNote')).to_contain_text('<b>Личный</b>')
        assert page.locator('#routingRemoveNote b').count() == 0
        page.locator('#confirmRoutingRemove').click()
        expect(page.locator('#routingRemoveDialog')).not_to_be_visible()
        expect(page.locator('#routingActive')).to_have_value('')
        expect(page.locator('#routingEmpty')).to_be_visible()
        page.locator('#routingActive').select_option('route1')
        expect(page.locator('#routingName')).to_have_value('Работа')
        page.locator('#closeRouting').click()
        expect(page.locator('#routingSummary')).to_have_text('Маршрут: Работа')
        page.locator('[data-page=settings]').click()
        page.locator('#routingButton').click()
        expect(page.locator('#routingName')).to_have_value('Работа')
        page.locator('#closeRouting').click()
        page.evaluate('fixtureProvider()')
        expect(page.locator('#routingSummary')).to_have_text('Маршрут: правила подписки')
        page.locator('#routingButton').click()
        expect(page.locator('#routingActive option').first).to_have_text('Правила подписки')
        page.locator('#closeRouting').click()
        page.locator('[data-page=servers]').click()
        expect(page.locator('.qr-node')).to_be_disabled()
        assert not errors, errors
        print('PASS routing UI: profiles, country rules, GeoIP updates/rollback/intervals, keyboard tabs, snapshots, errors, import/export, removal and responsive themes')
    finally:
        browser.close()
