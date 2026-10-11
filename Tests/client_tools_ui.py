"""User flows for tools, provider allowance, QR/YAML import and detailed history."""
import ast
from datetime import datetime, timezone
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
tree = ast.parse((ROOT / 'Tests/statistics_ui.py').read_text())
BASE = next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign)
            and any(isinstance(t, ast.Name) and t.id == 'MOCK' for t in n.targets))
SETUP = r'''
model.version='0.6.0';model.hasSubscription=true;model.localSubscription=false;
model.subscriptionUsage={upload:1073741824,download:4294967296,used:5368709120,total:10737418240,remaining:5368709120,expires:'2026-10-13T15:00:00Z',checkedAt:'2026-10-11T15:00:00Z',daysLeft:2};
model.tools={busy:false,steps:[],samples:[]};
model.statistics.detail={firstRecorded:'2026-10-11T12:00:00Z',error:'',
 session:[{app:'browser.exe',route:'vpn',download:1048576,upload:1024},{app:'game.exe',route:'direct',download:2048,upload:100},{app:'Не определено',route:'unknown',download:100,upload:5}],
 week:[{app:'browser.exe',route:'vpn',download:2097152,upload:2048}],month:[{app:'game.exe',route:'direct',download:4194304,upload:8192}],allTime:[{app:'<img src=x onerror=alert(1)>',route:'vpn',download:8388608,upload:4096}],
 days:[{date:'2026-09-01',download:1,upload:1},{date:'2026-09-30',download:4000,upload:20},{date:'2026-10-04',download:5000,upload:25},{date:'2026-10-05',download:6000,upload:30},{date:'2026-10-11',download:7000,upload:35},{date:'2026-10-12',download:8000,upload:40}],
 speeds:[{time:'2026-10-11T14:59:54Z',download:2048,upload:128},{time:'2026-10-11T14:59:56Z',download:null,upload:null},{time:'2026-10-11T14:59:58Z',download:4096,upload:256}]};
window.fixtureModel=()=>structuredClone(model);window.cancelFile=false;window.cancelQr=false;
'''
HANDLER = r'''
 window.statisticsRequests.push(msg);setTimeout(()=>{
 let data;
 if(msg.action==='runDiagnostics')model.tools={busy:true,kind:'diagnostics',server:'DE',status:'DNS Windows для сервера',steps:[{name:'DNS Windows для сервера',status:'checking',detail:'Проверка'}],samples:[]};
 if(msg.action==='testQuality')model.tools={busy:true,kind:'quality',server:'DE',status:'HTTP-проверка 1 из 10',steps:[],samples:[]};
 if(msg.action==='cancelTools'){model.tools.busy=false;model.tools.status='Проверка отменена';}
 if(msg.action==='readNodeQr')data={name:'DE',link:'vless://11111111-1111-4111-8111-111111111111@example.com:443?security=tls#DE',image:'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=='};
 if(msg.action==='readQrFile'||msg.action==='readQrClipboard')data={text:window.cancelQr?null:'https://provider.example/qr-subscription'};
 if(msg.action==='importText'||msg.action==='importServersFile'&&!window.cancelFile){model.activeSubscription='local';model.localSubscription=true;model.subscriptionUsage=null;model.name='Файл';}
 window.fixtureStatistics();listener({data:{kind:'reply',id:msg.id,ok:true,data}});
 },10);
'''
MOCK = BASE.replace('window.statisticsRequests=[];', SETUP + '\nwindow.statisticsRequests=[];')
MOCK = MOCK.replace(" window.statisticsRequests.push(msg);setTimeout(()=>{window.fixtureStatistics();listener({data:{kind:'reply',id:msg.id,ok:true}});},10);", HANDLER)

with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        page = browser.new_page(viewport={'width': 1080, 'height': 770})
        errors = []; page.on('pageerror', lambda e: errors.append(str(e)))
        page.add_init_script(MOCK); page.clock.install(time=datetime(2026, 10, 11, 15, tzinfo=timezone.utc))
        page.goto((ROOT / 'Windows/ui/index.html').as_uri())
        expect(page.locator('#stateLabel')).to_have_text('Подключено')
        assert page.locator('#page-home #statisticsDetails, #page-home #toolsForm').count() == 0
        page.locator('[data-page=servers]').click()
        expect(page.locator('#subscriptionInfo')).to_be_visible()
        expect(page.locator('#subscriptionTraffic')).to_contain_text('Использовано 5 ГиБ из 10 ГиБ')
        expect(page.locator('#subscriptionWarning')).to_contain_text('2 дн.')
        assert page.locator('#subscriptionMeter').evaluate('(e)=>e.value/e.max') == .5
        page.evaluate('fixtureStatistics({subscriptionUsage:null})')
        expect(page.locator('#subscriptionTraffic')).to_contain_text('Провайдер не передал')
        expect(page.locator('#subscriptionMeter')).not_to_be_visible()
        expect(page.locator('#subscriptionWarning')).not_to_be_visible()
        page.evaluate('fixtureStatistics({localSubscription:true})')
        expect(page.locator('#refreshButton')).to_be_disabled()
        expect(page.locator('#subscriptionTraffic')).to_contain_text('Локальные серверы')
        page.evaluate('fixtureStatistics({localSubscription:false})')
        expect(page.locator('#refreshButton')).to_be_enabled()

        page.locator('[data-page=diagnostics]').click()
        expect(page.locator('#toolsNode')).to_be_disabled()
        expect(page.locator('#toolsConnectionNote')).to_contain_text('текущее VPN-соединение')
        page.locator('#runDiagnostics').click()
        expect(page.locator('#runDiagnostics')).to_be_disabled()
        expect(page.locator('#toolsWebsite')).to_be_disabled()
        expect(page.locator('#cancelTools')).to_be_visible()
        expect(page.locator('#toolsResults')).to_contain_text('Проверка')
        page.locator('#cancelTools').click()
        expect(page.locator('#toolsStatus')).to_contain_text('отменена')
        page.evaluate("fixtureStatistics({tools:{busy:false,kind:'diagnostics',status:'Все проверки прошли',server:'DE',steps:[{name:'Внешний IP через VPN',status:'ok',detail:'203.0.113.1'},{name:'Сайт',status:'error',detail:'<img src=x onerror=alert(1)>'}]}})")
        expect(page.locator('#toolsResults')).to_contain_text('203.0.113.1')
        expect(page.locator('#toolsResults')).to_contain_text('<img src=x onerror=alert(1)>')
        assert page.locator('#toolsResults img, #toolsResults [onerror]').count() == 0
        page.locator('#copyDiagnostics').click()
        expect(page.locator('#toast')).to_have_text('Результат скопирован')
        page.evaluate("fixtureStatistics({state:'idle'})")
        expect(page.locator('#toolsNode')).to_be_enabled()
        expect(page.locator('#toolsConnectionNote')).to_contain_text('без изменения подключения')
        page.locator('#toolsWebsite').fill('http://example.com')
        before = page.evaluate("statisticsRequests.filter(r=>r.action==='runDiagnostics').length")
        page.locator('#runDiagnostics').click()
        assert page.evaluate("statisticsRequests.filter(r=>r.action==='runDiagnostics').length") == before
        page.locator('#toolsWebsite').fill('https://example.com/check')
        page.evaluate("fixtureStatistics({tools:{busy:false,kind:'diagnostics',status:'Все проверки прошли',server:'Нидерланды',steps:[{name:'DNS Windows для сервера',status:'ok',detail:'203.0.113.10'},{name:'TCP-порт сервера',status:'ok',detail:'Порт доступен · 42 мс'},{name:'DNS через VPN (Google DoH)',status:'ok',detail:'93.184.215.14'},{name:'Внешний IP через VPN (ipify)',status:'ok',detail:'203.0.113.1'},{name:'Сайт через VPN',status:'ok',detail:'example.com доступен · 98 мс'}]}})")
        page.clock.run_for(5000)
        page.screenshot(path=str(ROOT / 'Tests/ui-diagnostics-dark.png'), animations='disabled')

        page.locator('[data-page=servers]').click(); page.locator('#qualityButton').click()
        expect(page.locator('#qualityDialog')).to_be_visible()
        expect(page.locator('#qualityDialog')).to_contain_text('1 МиБ')
        page.locator('#testQuality').click()
        expect(page.locator('#testQuality')).to_be_disabled()
        expect(page.locator('#qualityStatus')).to_contain_text('HTTP-проверка 1 из 10')
        page.evaluate("fixtureStatistics({tools:{busy:false,kind:'quality',status:'Проверка качества завершена',server:'DE',steps:[],samples:[90,100,null,110,90,100,95,105,100,110],quality:{attempts:10,successes:9,medianMs:100,minMs:90,maxMs:110,jitterMs:12.5},download:{bytes:1048576,megabitsPerSecond:8.4},speedError:''}})")
        expect(page.locator('#qualitySuccess')).to_have_text('9 / 10 (90%)')
        expect(page.locator('#qualityMedian')).to_have_text('100 мс')
        expect(page.locator('#qualitySpeed')).to_have_text('8,4 Мбит/с')
        assert page.locator('#qualitySamples span').count() == 10
        expect(page.locator('#qualitySamples [data-error=true]')).to_have_text('Таймаут / ошибка')
        page.locator('#testQuality').click(); expect(page.locator('#cancelQuality')).to_be_visible()
        page.locator('#closeQuality').click(); expect(page.locator('#qualityDialog')).not_to_be_visible()
        expect(page.locator('#toolsStatus')).to_contain_text('отменена')

        page.locator('.qr-node').first.click(); expect(page.locator('#shareQrImage')).to_be_visible()
        expect(page.locator('#shareQrLink')).to_have_value('vless://11111111-1111-4111-8111-111111111111@example.com:443?security=tls#DE')
        page.locator('#copyNodeLink').click(); expect(page.locator('#toast')).to_have_text('Скопировано')
        page.locator('#copyNodeQr').click(); page.locator('#saveNodeQr').click(); page.locator('#closeShareQr').click()
        expect(page.locator('#shareQrDialog')).not_to_be_visible()
        assert page.locator('#shareQrImage').get_attribute('src') is None
        assert page.locator('#shareQrLink').input_value() == ''
        page.locator('#subscriptionSelect').click(); page.locator('#addSubscriptionOption').click()
        expect(page.locator('#profileLink')).to_be_visible()
        page.locator('#readQrFile').click()
        expect(page.locator('#profileLink')).to_have_value('https://provider.example/qr-subscription')
        page.evaluate('window.cancelQr=true'); page.locator('#profileLink').fill('existing');page.locator('#readQrClipboard').click()
        expect(page.locator('#readQrClipboard')).to_be_enabled()
        expect(page.locator('#profileLink')).to_have_value('existing')
        page.evaluate('window.cancelFile=true'); page.locator('#importServersFile').click()
        expect(page.locator('#importServersFile')).to_be_enabled()
        expect(page.locator('#addDialog')).to_be_visible()
        page.locator('#profileLink').fill('proxies:\n  - {name: Test, type: trojan, server: example.com, port: 443, password: secret}')
        page.locator('#addForm [type=submit]').click()
        expect(page.locator('#addDialog')).not_to_be_visible()
        assert page.evaluate("statisticsRequests.some(r=>r.action==='importText'&&r.data.text.startsWith('proxies:'))")
        expect(page.locator('#refreshButton')).to_be_disabled()

        page.locator('.nav-button[data-page=settings]').click()
        page.locator('#statisticsButton').click()
        expect(page.locator('#trafficApps')).to_contain_text('browser.exe')
        expect(page.locator('#trafficRouteTotals')).to_contain_text('Через VPN: 1 МиБ')
        expect(page.locator('#trafficRouteTotals')).to_contain_text('Напрямую:')
        expect(page.locator('#trafficAttribution')).to_contain_text('Короткие соединения')
        expect(page.locator('#trafficChartTitle')).to_have_text('Скорость за последние 3 минуты')
        assert page.locator('#trafficChart').evaluate('(c)=>Array.from(c.getContext("2d").getImageData(0,0,c.width,c.height).data).some((v,i)=>i%4===3&&v>0)')
        assert page.locator('#trafficChart').evaluate('(c)=>{const d=c.getContext("2d").getImageData(0,0,c.width,c.height).data;let dots=0;for(let i=0;i<d.length;i+=4)if(d[i+2]>180&&d[i+2]>d[i]&&d[i+3]>200)dots++;return dots>8;}'), 'isolated speed samples remain visible across missing measurements'
        page.locator('[data-statistics-period=week]').click()
        expect(page.locator('#trafficRouteTotals')).to_contain_text('2 МиБ')
        assert page.locator('#trafficDays tr').count() == 2
        expect(page.locator('#trafficDays')).not_to_contain_text('04.10.2026')
        expect(page.locator('#trafficDays')).not_to_contain_text('12.10.2026')
        page.locator('[data-statistics-period=month]').click()
        expect(page.locator('#trafficApps')).to_contain_text('game.exe')
        assert page.locator('#trafficDays tr').count() == 4
        page.locator('[data-statistics-period=allTime]').click()
        expect(page.locator('#trafficApps')).to_contain_text('<img src=x onerror=alert(1)>')
        assert page.locator('#trafficApps img, #trafficApps [onerror]').count() == 0
        expect(page.locator('#trafficChartTitle')).to_contain_text('последние 30 дней')
        page.locator('[data-statistics-period=session]').click()
        page.clock.run_for(5000); page.set_viewport_size({'width':1080,'height':1080})
        page.locator('.main').evaluate('(e)=>e.scrollTop=0')
        for theme in ('dark', 'light'):
            page.evaluate('(theme)=>fixtureStatistics({settings:{...fixtureModel().settings,theme}})', theme)
            page.wait_for_timeout(250); page.screenshot(path=str(ROOT / f'Tests/ui-client-statistics-{theme}.png'), animations='disabled')
        page.set_viewport_size({'width':800,'height':600})
        for section in ('statistics', 'diagnostics', 'servers'):
            if section == 'statistics': page.locator('.nav-button[data-page=settings]').click()
            page.locator(f'[data-page={section}]').click()
            assert page.locator('#page-' + section).evaluate('(e)=>e.scrollWidth<=e.clientWidth'), section
        page.set_viewport_size({'width':1080,'height':770})
        page.locator('[data-page=home]').click(); page.wait_for_timeout(300)
        assert page.locator('#page-home .power-zone').bounding_box()['width'] == 208
        assert not errors, errors
    finally:
        browser.close()
print('PASS client tools: diagnostics/cancellation, provider metadata, quality metrics, QR/YAML flows, safe text, calendar app statistics, graphs, themes and minimum window size')
