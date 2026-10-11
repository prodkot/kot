"""Exercise the production service controls, settings, history and navigation."""
import ast
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
tree = ast.parse((ROOT / 'Tests/statistics_ui.py').read_text())
MOCK = next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'MOCK' for t in n.targets))
EXTRA = r'''
model.state='idle';model.statistics.running=false;
model.services={checkUpdates:true,testing:false,networkBusy:false,items:[
 {id:'zapret',status:'Не установлен',busy:false,running:false,strategies:[],logs:[]},
 {id:'telegram',status:'Не установлен',busy:false,running:false,logs:[]}],
 zapret:{autoStart:false,strategy:'general.bat',gameFilter:false,domains:'',excludedDomains:'',targets:'https://discord.com\nhttps://www.youtube.com',timeoutSeconds:6},
 telegram:{autoStart:false,port:1443,cloudflare:true,http2:true,poolSize:4,dcIp:'2:149.154.167.220\n4:149.154.167.220',domains:'',workerDomains:''},history:[]};
window.serviceRequests=[];
window.fixtureServices=(patch={})=>{Object.assign(model.services,patch);window.fixtureStatistics();};
window.fixtureServiceItem=(id,patch)=>{Object.assign(model.services.items.find(e=>e.id===id),patch);window.fixtureStatistics();};
window.fixtureServiceModel=()=>structuredClone(model.services);
'''
MOCK = MOCK.replace('window.chrome=', EXTRA + 'window.chrome=')
MOCK = MOCK.replace("window.statisticsRequests.push(msg);setTimeout(()=>{window.fixtureStatistics();listener({data:{kind:'reply',id:msg.id,ok:true}});},10);", r'''
window.statisticsRequests.push(msg);window.serviceRequests.push(msg);
setTimeout(()=>{
 const data=msg.data||{},s=model.services,e=s.items.find(e=>e.id===data.service);let error='';
 if(msg.action==='serviceAction'){
  if(data.operation==='install')Object.assign(e,{installedVersion:e.id==='zapret'?'1.10.3':'v1.11.2',status:'Остановлен',strategies:e.id==='zapret'?['general.bat','general (ALT).bat','general (ALT2).bat']:undefined});
  if(data.operation==='start'||data.operation==='restart')Object.assign(e,{running:true,status:'Работает',error:''});
  if(data.operation==='stop')Object.assign(e,{running:false,status:'Остановлен'});
  if(data.operation==='check')Object.assign(e,{checkedAt:'2026-10-11T22:00:00Z',availableVersion:e.id==='zapret'?'1.10.4':'v1.11.3',updateAvailable:true});
  if(data.operation==='test')Object.assign(s,{testing:true}),Object.assign(e,{busy:true,phase:'Проверка без zapret'});
  if(data.operation==='remove')Object.assign(e,{installedVersion:null,running:false,status:'Не установлен',strategies:[]});
 }
 if(msg.action==='serviceSettings'){
  if(data.service==='telegram'&&data.settings.port===1024)error='Локальный порт Telegram занят.';
  else s[data.service]=structuredClone(data.settings);
 }
 if(msg.action==='serviceUpdateSettings')s.checkUpdates=data.enabled;
 if(msg.action==='serviceCancel')Object.assign(s,{testing:false}),Object.assign(e,{busy:false,phase:'',error:'Операция отменена.'});
 if(msg.action==='serviceSelectTest')s.test=window.fixtureRuns.find(r=>r.started===data.started);
 if(msg.action==='serviceClearLog')e.logs=[];
 window.fixtureStatistics();listener({data:{kind:'reply',id:msg.id,ok:!error,error}});
},10);
''')
PREVIEW = os.environ.get('KOT_SERVICE_PREVIEW')
with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        page = browser.new_page(viewport={'width':1080,'height':770})
        page.set_default_timeout(6000)
        errors = []
        page.on('pageerror',lambda error:errors.append(str(error)))
        page.add_init_script(MOCK)
        page.goto((ROOT / 'Windows/ui/index.html').as_uri())
        expect(page.locator('#stateLabel')).to_have_text('Не подключено')
        assert page.locator('.nav-button[data-page=statistics]').count()==0
        page.locator('.nav-button[data-page=settings]').click()
        expect(page.locator('#statisticsButton')).to_be_visible()
        for width,height in [(800,600),(960,640)]:
            page.set_viewport_size({'width':width,'height':height})
            page.locator('.main').evaluate('(e)=>e.scrollTop=0')
            for button in page.locator('#page-settings .settings-tools button').all():
                box=button.bounding_box()
                assert box['y']>=0 and box['y']+box['height']<=height
        page.set_viewport_size({'width':1080,'height':770})
        if PREVIEW:
            Path(PREVIEW).mkdir(parents=True,exist_ok=True)
            page.screenshot(path=str(Path(PREVIEW)/'settings-statistics.png'),animations='disabled')
        page.locator('#statisticsButton').click()
        expect(page.locator('#page-statistics')).to_be_visible()
        expect(page.locator('.nav-button[data-page=settings]')).to_have_attribute('aria-current','page')
        page.locator('#statisticsBack').click()
        expect(page.locator('#page-settings')).to_be_visible()
        page.locator('.nav-button[data-page=services]').click()
        expect(page.locator('#page-services')).to_be_visible()
        expect(page.locator('#zapretTest')).to_be_disabled()
        page.locator('#zapretInstall').click()
        expect(page.locator('#zapretVersion')).to_have_text('Установлена 1.10.3')
        expect(page.locator('#zapretStrategy option')).to_have_count(3)
        page.locator('#zapretCheck').click()
        expect(page.locator('#zapretUpdate')).to_have_text('Доступна 1.10.4')
        expect(page.locator('#zapretInstall')).to_have_text('Обновить')
        page.locator('#zapretStart').click()
        expect(page.locator('#zapretStatus')).to_have_text('Работает')
        page.locator('#zapretStrategy').select_option('general (ALT).bat')
        page.wait_for_timeout(100)
        assert page.evaluate("serviceRequests.filter(r=>r.action==='serviceSettings').at(-1).data.restart")
        page.locator('#zapretSettings').click()
        page.locator('#zapretDomains').fill('example.com\nexample.org')
        page.locator('#zapretDomains').focus()
        page.evaluate("fixtureServiceItem('zapret',{logs:['worker snapshot']})")
        expect(page.locator('#zapretDomains')).to_have_value('example.com\nexample.org')
        expect(page.locator('#zapretDomains')).to_be_focused()
        page.locator('#serviceAutoStart').check()
        page.locator('#serviceSettingsSave').click()
        expect(page.locator('#serviceSettingsDialog')).not_to_be_visible()
        expect(page.locator('#zapretAutoStart')).to_have_text('Запускается вместе с kot')
        page.evaluate('fixtureServices({networkBusy:true})')
        expect(page.locator('#zapretTest')).to_be_disabled()
        page.evaluate('fixtureServices({networkBusy:false})')
        page.locator('#zapretTest').click()
        page.locator('#zapretTestNone').click()
        page.locator('#zapretTestRun').click()
        expect(page.locator('#zapretTestError')).to_have_text('Выберите хотя бы одну стратегию.')
        page.locator('#zapretTestStrategies input').first.check()
        page.locator('#zapretTestRun').click()
        expect(page.locator('#zapretTestDialog')).not_to_be_visible()
        expect(page.locator('#zapretCancel')).to_be_visible()
        assert page.evaluate("serviceRequests.filter(r=>r.action==='serviceAction'&&r.data.operation==='test').at(-1).data.strategies") == ['general.bat']
        checks=[{'target':'https://discord.com/','success':True,'milliseconds':73,'status':200,'error':''}]
        run={'started':'2026-10-11T22:02:00Z','version':'1.10.3','cancelled':False,'baseline':[dict(checks[0],success=False,error='timeout')],'results':[{'strategy':'general.bat','checks':checks,'error':''}],'recommended':'general.bat'}
        old=dict(run,started='2026-10-11T21:20:00Z',cancelled=True)
        page.evaluate('(runs)=>{window.fixtureRuns=runs;fixtureServices({test:runs[1],history:runs.map(r=>({...r,count:r.results.length}))});}',[old,run])
        expect(page.locator('#zapretResults button')).to_be_disabled()
        page.locator('#zapretCancel').click()
        expect(page.locator('#zapretCancel')).not_to_be_visible()
        expect(page.locator('#zapretResults button')).to_be_enabled()
        page.locator('#zapretHistorySelect').select_option(old['started'])
        expect(page.locator('#zapretResultSummary')).to_contain_text('Подбор остановлен')
        page.locator('#zapretHistorySelect').select_option(run['started'])
        expect(page.locator('#zapretResultSummary')).to_contain_text('Рекомендация: general.bat')
        page.locator('#zapretResults button').click()
        expect(page.locator('#zapretStrategy')).to_have_value('general.bat')
        page.evaluate("fixtureServiceItem('zapret',{logs:['<img src=x onerror=alert(1)>'],error:'<script>bad()</script>'})")
        page.locator('#zapretLog').locator('..').locator('summary').click()
        expect(page.locator('#zapretLog')).to_contain_text('<img src=x onerror=alert(1)>')
        assert page.locator('#zapretLog img,#zapretError script').count()==0
        page.locator('#zapretClearLog').click()
        expect(page.locator('#zapretLog')).to_contain_text('Здесь появится журнал')
        page.evaluate("fixtureServiceItem('zapret',{error:'',logs:['23:01:08 Стратегия general.bat запущена','23:01:11 Подбор завершён: 1 из 1 адресов доступен']})")
        if PREVIEW:
            page.set_viewport_size({'width':1080,'height':1050})
            page.locator('.main').evaluate('(e)=>e.scrollTop=0')
            expect(page.locator('#toast')).not_to_be_visible(timeout=5000)
            page.screenshot(path=str(Path(PREVIEW)/'services-zapret.png'),animations='disabled')
            page.set_viewport_size({'width':1080,'height':770})
        page.locator('[data-service-tab=telegram]').click()
        expect(page.locator('#telegramPanel')).to_be_visible()
        page.locator('#telegramInstall').click()
        expect(page.locator('#telegramVersion')).to_have_text('Установлена v1.11.2')
        page.locator('#telegramSettings').click()
        expect(page.locator('#telegramSecret')).to_have_value('')
        page.locator('#telegramPort').fill('1024')
        page.locator('#serviceSettingsSave').click()
        expect(page.locator('#serviceSettingsError')).to_have_text('Локальный порт Telegram занят.')
        expect(page.locator('#serviceSettingsDialog')).to_be_visible()
        page.locator('#telegramPort').fill('2443')
        page.locator('#telegramPool').fill('0')
        page.locator('#telegramSecret').fill('abcdef0123456789abcdef0123456789')
        page.locator('#serviceSettingsSave').click()
        expect(page.locator('#serviceSettingsDialog')).not_to_be_visible()
        expect(page.locator('#telegramSecret')).to_have_value('')
        expect(page.locator('#telegramAddress')).to_have_text('127.0.0.1:2443')
        assert page.evaluate('fixtureServiceModel().telegram.poolSize')==0
        page.locator('#telegramStart').click()
        expect(page.locator('#telegramOpen')).to_be_enabled()
        page.locator('#telegramCopy').click()
        page.locator('#telegramOpen').click()
        expect(page.locator('#toast')).to_have_text('Ссылка на локальный прокси скопирована')
        page.locator('#telegramSettings').click()
        page.locator('#telegramRotate').click()
        expect(page.locator('#serviceSecretDialog')).to_be_visible()
        page.locator('#serviceSecretCancel').click()
        expect(page.locator('#serviceSettingsDialog')).to_be_visible()
        page.locator('#serviceSettingsClose').click()
        if PREVIEW:
            page.locator('.main').evaluate('(e)=>e.scrollTop=0')
            expect(page.locator('#toast')).not_to_be_visible(timeout=5000)
            page.screenshot(path=str(Path(PREVIEW)/'services-telegram.png'),animations='disabled')
        page.locator('#telegramStop').click()
        expect(page.locator('#telegramOpen')).to_be_disabled()
        page.locator('#serviceAutoUpdates').uncheck()
        page.wait_for_timeout(100)
        assert not page.evaluate('fixtureServiceModel().checkUpdates')
        page.locator('#telegramRemove').click()
        page.locator('#serviceRemoveCancel').click()
        expect(page.locator('#telegramVersion')).to_have_text('Установлена v1.11.2')
        page.locator('#telegramRemove').click()
        page.locator('#serviceRemoveConfirm').click()
        expect(page.locator('#telegramInstall')).to_be_visible()
        page.locator('[data-service-tab=zapret]').click()
        for theme in ('light','dark'):
            page.evaluate('(theme)=>fixtureStatistics({settings:{theme,accent:"lime"}})',theme)
            for size in ({'width':800,'height':600},{'width':1080,'height':770}):
                page.set_viewport_size(size)
                assert page.locator('#page-services').evaluate('(e)=>e.scrollWidth<=e.clientWidth'),(theme,size)
                page.locator('#zapretSettings').click()
                assert page.locator('#serviceSettingsDialog').evaluate('(e)=>e.scrollWidth<=e.clientWidth')
                page.locator('#serviceSettingsClose').click()
        page.emulate_media(reduced_motion='reduce')
        page.locator('.nav-button[data-page=settings]').click()
        page.locator('.nav-button[data-page=services]').click()
        expect(page.locator('#page-services')).to_be_visible()
        assert not errors,errors
        print('PASS services UI: navigation, installation, state, settings, cancellation, test history, secret handling, themes and minimum size')
    finally: browser.close()
