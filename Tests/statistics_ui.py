"""Check the production statistics UI with a host fixture and a controlled clock."""
from datetime import datetime, timezone
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
MOCK = r'''(() => {
let listener;
const model={version:'0.7.0',name:'Личная подписка',hasSubscription:true,
 nodes:[{id:'nl',name:'Нидерланды',countryCode:'NL',protocol:'vless',code:'VL'}],selected:'nl',state:'connected',error:'',warnings:[],favorites:[],
 settings:{theme:'dark',accent:'lime',mode:'tun',startup:false,autoConnect:false,tray:true,sendHwid:false,killSwitch:false,bypass:''},
 subscriptions:[{id:'s',name:'Личная подписка',count:1}],activeSubscription:'s',
 ping:{busy:false,done:0,total:0,error:'',settings:{mode:'http',url:'https://example.org',timeoutMs:5000,attempts:2,parallelism:3,sort:'none'}},
 automation:{reconnect:true,refreshHours:6,pingMinutes:0,autoMinutes:3,autoToleranceMs:50},updates:{busy:false,status:'',error:''},
 routing:{active:'',profiles:[],errors:{}},geoIp:{busy:false,error:'',refreshHours:24,countries:[]},
 telemetry:{available:true,downloadRate:2516582,uploadRate:398459,downloadTotal:536870912,uploadTotal:67108864,count:0,connections:[]},
 statistics:{running:true,elapsedSeconds:1174,started:'2026-10-11T14:40:26Z',ended:null,firstRecorded:'2026-09-01T10:00:00Z',error:'',warning:'',
 session:{download:536870912,upload:67108864,connectedSeconds:1174,sessions:1},
 week:{download:12884901888,upload:1610612736,connectedSeconds:176400,sessions:12},
 month:{download:52613349376,upload:6442450944,connectedSeconds:820800,sessions:48},
 allTime:{download:214748364800,upload:32212254720,connectedSeconds:2163600,sessions:156}}};
window.statisticsRequests=[];
window.fixtureStatistics=(changes={})=>{Object.assign(model,changes);listener({data:{kind:'snapshot',data:structuredClone(model)}});};
window.fixtureStatisticsData=()=>structuredClone(model.statistics);
window.fixtureNavigate=page=>listener({data:{kind:'navigate',page}});
window.chrome={webview:{addEventListener:(_,cb)=>listener=cb,postMessage:msg=>{
 window.statisticsRequests.push(msg);setTimeout(()=>{window.fixtureStatistics();listener({data:{kind:'reply',id:msg.id,ok:true}});},10);
}}};
})();'''

with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        page = browser.new_page(viewport={'width': 960, 'height': 640})
        errors = []
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.add_init_script(MOCK)
        page.clock.install(time=datetime(2026, 10, 11, 15, tzinfo=timezone.utc))
        page.goto((ROOT / 'Windows/ui/index.html').as_uri())
        expect(page.locator('#stateLabel')).to_have_text('Подключено')
        page.clock.pause_at(datetime(2026, 10, 11, 15, 0, 5, tzinfo=timezone.utc))
        page.evaluate('fixtureStatistics()')
        # Home shares the confirmed session clock, while history stays on Statistics.
        expect(page.locator('#page-home')).to_be_visible()
        expect(page.locator('#statistics')).not_to_be_visible()
        expect(page.locator('#connectionTime')).not_to_be_visible()
        assert page.locator('#page-home #statistics, #page-home #connectionTime').count() == 0
        expect(page.locator('#homeSessionTime')).to_have_text('00:19:34')
        assert page.locator('#page-home .power-zone').bounding_box()['width'] == 208
        page.locator('.nav-button[data-page=settings]').click()
        page.locator('#statisticsButton').click()
        page.clock.run_for(20)
        page.evaluate('fixtureStatistics()')
        expect(page.locator('#page-statistics')).to_be_visible()
        expect(page.locator('[data-page=statistics]')).to_have_attribute('aria-current', 'page')
        expect(page.locator('#statisticsConnectionLabel')).to_have_text('Текущее подключение')
        assert page.evaluate("statisticsRequests.some(r=>r.action==='view'&&r.data.page==='statistics')")
        expect(page.locator('#connectionTime')).to_have_text('00:19:34')
        expect(page.locator('#statisticsDownload')).to_have_text('512 МБ')
        expect(page.locator('#statisticsUpload')).to_have_text('64 МБ')
        page.clock.run_for(2250)
        expect(page.locator('#connectionTime')).to_have_text('00:19:36')
        page.locator('[data-statistics-period=week]').click()
        expect(page.locator('#statisticsDownload')).to_have_text('12 ГБ')
        expect(page.locator('#statisticsDuration')).to_have_text('2 д 01:00:02')
        page.locator('[data-statistics-period=week]').press('ArrowRight')
        expect(page.locator('[data-statistics-period=month]')).to_be_focused()
        expect(page.locator('#statisticsDownload')).to_have_text('49 ГБ')
        page.locator('[data-statistics-period=month]').press('End')
        expect(page.locator('#statisticsUpload')).to_have_text('30 ГБ')
        expect(page.locator('#statisticsSummary')).to_contain_text('Сеансов: 156')
        page.evaluate('fixtureStatistics()')
        expect(page.locator('[data-statistics-period=allTime]')).to_have_attribute('aria-pressed', 'true')
        page.clock.run_for(20000)
        expect(page.locator('#connectionTime')).to_have_text('00:19:39')
        expect(page.locator('#stateLabel')).to_have_text('Подключено')

        # Host samples still arrive on another page. Tray navigation uses the same page path.
        page.locator('[data-page=home]').click()
        page.clock.run_for(20)
        page.evaluate("const s=fixtureStatisticsData();s.elapsedSeconds=1180;fixtureStatistics({statistics:s})")
        page.clock.run_for(1500)
        expect(page.locator('#homeSessionTime')).to_have_text('00:19:41')
        expect(page.locator('#connectionTime')).not_to_be_visible()
        page.evaluate("fixtureNavigate('statistics')")
        expect(page.locator('#connectionTime')).to_have_text('00:19:41')
        expect(page.locator('[data-statistics-period=allTime]')).to_have_attribute('aria-pressed', 'true')
        page.clock.run_for(20)
        page.evaluate("const s=fixtureStatisticsData();s.elapsedSeconds=1174;fixtureStatistics({statistics:s})")

        # Missing API data does not erase accumulated statistics or stop the confirmed clock.
        page.evaluate("const s=fixtureStatisticsData();s.warning='Статистика временно недоступна.';fixtureStatistics({statistics:s,telemetry:{available:false}})")
        expect(page.locator('#downloadRate')).to_have_text('Нет данных')
        expect(page.locator('#statisticsDownload')).to_have_text('200 ГБ')
        expect(page.locator('#statisticsError')).to_have_text('Статистика временно недоступна.')
        page.clock.run_for(1250)
        expect(page.locator('#connectionTime')).to_have_text('00:19:35')
        page.evaluate("const s=fixtureStatisticsData();s.running=false;s.ended='2026-10-11T15:00:00Z';s.warning='';fixtureStatistics({state:'idle',statistics:s})")
        page.clock.run_for(12000)
        expect(page.locator('#connectionTime')).to_have_text('00:19:34')
        expect(page.locator('#statisticsConnectionLabel')).to_have_text('Последний сеанс')
        page.locator('[data-statistics-period=allTime]').press('Home')
        expect(page.locator('#statisticsSummary')).to_contain_text('Последний сеанс')
        expect(page.locator('#statisticsDownload')).to_have_text('512 МБ')
        page.evaluate("const s=fixtureStatisticsData();s.running=false;fixtureStatistics({state:'connecting',statistics:s})")
        page.clock.run_for(10000)
        expect(page.locator('#stateLabel')).to_have_text('Подключение')
        expect(page.locator('#connectionTime')).to_have_text('00:19:34')
        page.evaluate("const s=fixtureStatisticsData();s.running=true;s.elapsedSeconds=0;s.started='2026-10-11T15:00:00Z';s.session={download:0,upload:0,connectedSeconds:0,sessions:1};fixtureStatistics({state:'connected',statistics:s})")
        expect(page.locator('#connectionTime')).to_have_text('00:00:00')
        expect(page.locator('#statisticsDownload')).to_have_text('0 Б')
        page.locator('[data-statistics-period=allTime]').click()
        expect(page.locator('#statisticsDownload')).to_have_text('200 ГБ')
        page.evaluate("const s=fixtureStatisticsData();s.error='<img src=x onerror=alert(1)>';fixtureStatistics({statistics:s})")
        expect(page.locator('#statisticsError')).to_have_text('<img src=x onerror=alert(1)>')
        assert page.locator('#statisticsError img').count() == 0

        for theme in ['dark', 'light']:
            page.evaluate("theme=>{const s=fixtureStatisticsData();s.error='';s.elapsedSeconds=1174;s.session={download:536870912,upload:67108864,connectedSeconds:1174,sessions:1};fixtureStatistics({statistics:s,telemetry:{available:true,downloadRate:2516582,uploadRate:398459,downloadTotal:536870912,uploadTotal:67108864,count:0,connections:[]},settings:{theme,accent:'lime',mode:'tun'}})}", theme)
            page.locator('[data-statistics-period=week]').click()
            for width, height in [(800, 600), (960, 640), (700, 660)]:
                page.set_viewport_size({'width': width, 'height': height})
                page.evaluate("document.querySelector('.main').scrollTop=0")
                assert page.evaluate('document.documentElement.scrollWidth===innerWidth')
                for selector in ['#statisticsBack', '#page-statistics h1', '#statisticsSession', '#connectionTime', '#statistics', '#statisticsSummary']:
                    box = page.locator(selector).bounding_box()
                    assert box['y'] >= 38 and box['y'] + box['height'] <= height, (theme, width, height, selector, box)
                back = page.locator('#statisticsBack').bounding_box()
                title = page.locator('#page-statistics h1').bounding_box()
                assert title['x'] + title['width'] < back['x'], (back, title)
                assert abs(back['y'] + back['height']/2 - title['y'] - title['height']/2) < 1, (back, title)
                summary = page.locator('#statistics').bounding_box()
                details = page.locator('#statisticsDetails').bounding_box()
                assert abs(details['y'] - summary['y'] - summary['height'] - 20) < 1, (summary, details)
                expect(page.locator('#statisticsBack')).to_have_text('Назад')
                expect(page.locator('#powerButton')).not_to_be_visible()
            for key in ['Enter', 'Space']:
                page.locator('#statisticsBack').focus()
                page.locator('#statisticsBack').press(key)
                expect(page.locator('#page-settings')).to_be_visible()
                page.locator('#statisticsButton').click()
                expect(page.locator('#page-statistics')).to_be_visible()
            page.set_viewport_size({'width': 960, 'height': 640})
            page.evaluate("document.activeElement.blur();document.querySelector('.main').scrollTop=0")
            page.evaluate('document.fonts.ready')
            page.screenshot(path=str(ROOT / f'docs/images/statistics-{theme}.png'), animations='disabled')
        page.locator('[data-page=home]').click()
        page.clock.run_for(20)
        page.evaluate("fixtureStatistics({settings:{theme:'dark',accent:'lime',mode:'tun'}});document.activeElement.blur();document.querySelector('.main').scrollTop=0")
        page.screenshot(path=str(ROOT / 'Tests/ui-statistics-home.png'), animations='disabled')
        page.evaluate("fixtureStatistics({updates:{busy:false,status:'',availableVersion:'0.6.0'}})")
        for width,height in [(800,600),(960,640),(700,660)]:
            page.set_viewport_size({'width':width,'height':height})
            page.evaluate("document.querySelector('.main').scrollTop=0")
            expect(page.locator('#updateNotice')).to_be_visible()
            expect(page.locator('#statistics')).not_to_be_visible()
            expect(page.locator('#connectionTime')).not_to_be_visible()
            for selector in ['#powerButton','#homeServer','#page-home .mode']:
                box=page.locator(selector).bounding_box()
                assert box['y']>=38 and box['y']+box['height']<=height,(width,height,selector,box)
        page.locator('.nav-button[data-page=settings]').click()
        page.locator('#statisticsButton').click()
        page.clock.run_for(20)
        page.evaluate('fixtureStatistics({statistics:null})')
        expect(page.locator('#connectionTime')).not_to_be_visible()
        expect(page.locator('#statisticsConnectionLabel')).to_have_text('Ещё не подключались')
        expect(page.locator('#statisticsDownload')).to_have_text('Нет данных')
        assert not errors, errors
        print('PASS statistics page: restored home, sidebar and tray navigation, background clock, reconnect, four periods, errors, keyboard, themes and minimum window size')
    finally:
        browser.close()
