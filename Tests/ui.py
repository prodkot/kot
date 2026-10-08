from pathlib import Path
from playwright.sync_api import sync_playwright, expect
import json
root=Path(__file__).resolve().parents[1]
errors=[]
mock=r'''(()=>{
let callback;
const model={name:'Подписка',hasSubscription:false,nodes:[],selected:'',state:'idle',error:'',settings:{theme:'dark',accent:'lime',mode:'tun',startup:false,autoConnect:false,tray:true,sendHwid:true,killSwitch:false,killSwitchActive:false,bypass:''},updated:null,warnings:[],ping:{busy:false,done:0,total:0,error:'',settings:{mode:'http',url:'https://www.gstatic.com/generate_204',timeoutMs:5000,attempts:2,parallelism:3,sort:'none'}},version:'0.5.8',updates:{busy:false,status:'',error:'',availableVersion:null},favorites:[],subscriptions:[],activeSubscription:'',automation:{reconnect:true,refreshHours:6,pingMinutes:0,autoMinutes:3,autoToleranceMs:50}};
window.chrome={webview:{addEventListener:(event,cb)=>callback=cb,postMessage:msg=>setTimeout(()=>{
 const snapshot=()=>callback({data:{kind:'snapshot',data:structuredClone(model)}});
 const reply=(ok=true,error='',data)=>callback({data:{kind:'reply',id:msg.id,ok,error,data}});
 switch(msg.action){
  case 'ready':break;
  case 'import':model.hasSubscription=true;model.name=msg.data.name||'Подписка';model.nodes=[{id:'a',name:'<img src=x onerror=alert(1)>',protocol:'vless',code:'VL'},{id:'b',name:'Мой второй сервер',protocol:'trojan',code:'TR'}];model.selected='a';model.subscriptions=[{id:'sub1',name:model.name,count:2},{id:'sub2',name:'Вторая',count:1}];model.activeSubscription='sub1';break;
  case 'refresh':window.refreshCount=(window.refreshCount||0)+1;break;
  case 'switchSubscription':model.activeSubscription=msg.data.id;break;
  case 'favorite':if(model.favorites.includes(msg.data.id))model.favorites=model.favorites.filter(id=>id!==msg.data.id);else model.favorites.push(msg.data.id);break;
  case 'automationSettings':model.automation=msg.data;window.automationSaved=msg.data;break;
  case 'backupExport':window.backupExported=msg.data.password;break;
  case 'backupImport':window.backupImported=msg.data.password;break;
  case 'view':window.activeView=msg.data.page;break;
  case 'checkUpdates':window.updateCheckCount=(window.updateCheckCount||0)+1;if(window.fixtureUpdateError){reply(false,'Не удалось связаться с GitHub.');return;}model.updates.status='Доступна версия 0.5.9';model.updates.availableVersion='0.5.9';break;
  case 'downloadUpdate':window.onlineUpdateRequested=true;window.updateDownloadCount=(window.updateDownloadCount||0)+1;window.confirmedVersion=msg.data.version;break;
  case 'deferUpdate':model.updates.dismissedVersion=msg.data.version;window.deferredVersion=msg.data.version;break;
  case 'readHwid':if(window.fixtureDeviceError){reply(false,'Не удалось прочитать HWID.');return;}reply(true,'',{hwid:'A'.repeat(64),name:'kot. windows (0.5.8)'});return;
  case 'copyHwid':if(window.fixtureClipboardError){reply(false,'Буфер обмена занят.');return;}window.copiedHwid='A'.repeat(64);break;
  case 'copyLog':window.copiedFullLog=true;break;
  case 'pingSettings':model.ping.settings=msg.data;break;
  case 'pingAll':model.ping.busy=true;model.ping.done=0;model.ping.total=model.nodes.length;for(const n of model.nodes)n.ping={status:'checking',mode:model.ping.settings.mode};break;
  case 'pingNode':model.ping.busy=true;model.ping.done=0;model.ping.total=1;model.nodes.find(n=>n.id===msg.data.id).ping={status:'checking',mode:model.ping.settings.mode};break;
  case 'cancelPing':model.ping.busy=false;break;
  case 'select':model.selected=msg.data.id;break;
  case 'toggle':model.state=model.state==='idle'?'connecting':'idle';break;
  case 'settings':if(msg.data.key==='bypass'&&msg.data.value.includes('https://')){reply(false,'Введите домены без https://');return;}model.settings[msg.data.key]=msg.data.value;break;
  case 'remove':model.nodes=[];model.hasSubscription=false;model.selected='';model.state='idle';break;
 }
 snapshot();reply();
},msg.action==='downloadUpdate'?(window.fixtureDownloadDelay||10):(msg.action==='readHwid'?(window.fixtureDeviceDelay||10):(['refresh','checkUpdates'].includes(msg.action)?(window.fixtureMutationDelay||10):10)))}};
window.fixtureNavigate=(page)=>callback({data:{kind:"navigate",page}});
window.fixtureSnapshot=(patch)=>{Object.assign(model,patch);callback({data:{kind:'snapshot',data:structuredClone(model)}});};
})();'''
with sync_playwright() as p:
    browser=p.chromium.launch(args=['--no-sandbox'])
    page=browser.new_page(viewport={'width':960,'height':640})
    page.add_init_script(mock);page.on('pageerror',lambda e:errors.append(str(e)))
    page.goto((root/'Windows/ui/index.html').as_uri());expect(page.locator('#powerButton')).to_be_disabled()
    expect(page.locator('#selectedName')).to_have_text('Добавить подписку')
    page.locator('#homeServer').click();page.locator('#profileLink').fill('http://example.com/sub');page.locator('#addForm [type=submit]').click();expect(page.locator('#addError')).to_have_text('Нужна HTTPS-ссылка на подписку.')
    page.locator('#profileName').fill('<b>Личная</b>');page.locator('#profileLink').fill('https://example.invalid/test');page.locator('#addForm [type=submit]').click();expect(page.locator('#addDialog')).not_to_be_visible()
    expect(page.locator('#profileLink')).to_have_value('');expect(page.locator('#serverList .server-card')).to_have_count(2);assert not page.locator('#serverList img').count();assert not page.locator('#subscriptionName b').count()
    page.evaluate('window.fixtureMutationDelay=350');page.locator('#refreshButton').click();page.evaluate('fixtureSnapshot({})');expect(page.locator('#refreshButton')).to_be_disabled();page.locator('#refreshButton').evaluate('(e)=>e.click()');page.wait_for_timeout(400);assert page.evaluate('window.refreshCount')==1;page.evaluate('window.fixtureMutationDelay=0')
    page.locator('#serverSearch').fill('второй');expect(page.locator('#serverList .server-card')).to_have_count(1);page.locator('#serverList .server-card').click();expect(page.locator('#selectedName')).to_have_text('Мой второй сервер')
    page.locator('#powerButton').click();expect(page.locator('#connection')).to_have_attribute('data-state','connecting');page.wait_for_timeout(1900);expect(page.locator('#connection')).to_have_attribute('data-state','connecting')
    page.locator('#powerButton').click();expect(page.locator('#connection')).to_have_attribute('data-state','idle')
    page.evaluate("fixtureSnapshot({state:'connected'})");expect(page.locator('#powerLabel')).to_have_text('Отключить');page.evaluate("fixtureSnapshot({state:'idle',error:'Сервер не ответил'})");expect(page.locator('#connectionError')).to_have_text('Сервер не ответил')
    page.locator('#copyErrorLog').click();expect(page.locator('#toast')).to_have_text('Полный лог скопирован');assert page.evaluate('window.copiedFullLog')
    page.locator('[data-page=servers]').click();page.locator('#serverSearch').fill('');page.locator('#pingAll').click();expect(page.locator('#pingProgress')).to_have_text('0 / 2');expect(page.locator('#pingAll')).to_be_disabled();expect(page.locator('.ping-node').first).to_be_disabled()
    page.locator('#cancelPing').click();expect(page.locator('#pingAll')).to_be_enabled();expect(page.locator('#pingProgress')).not_to_be_visible()
    page.locator('#pingSettings').click();expect(page.locator('#pingUrlRow')).to_be_visible();page.locator('#pingMode').select_option('tcp');expect(page.locator('#pingUrlRow')).not_to_be_visible();page.locator('#pingTimeout').fill('2500');page.locator('#pingAttempts').fill('3');page.locator('#pingParallel').fill('2');page.locator('#pingSort').select_option('ping');page.locator('#pingForm [type=submit]').click();expect(page.locator('#pingDialog')).not_to_be_visible()
    page.locator('#pingSettings').click();expect(page.locator('#pingTimeout')).to_have_value('2500');expect(page.locator('#pingMode')).to_have_value('tcp');page.locator('#closePing').click();expect(page.locator('#pingDialog')).not_to_be_visible()
    page.locator('.ping-node').first.click();expect(page.locator('#pingProgress')).to_have_text('0 / 1');page.locator('#cancelPing').click()
    page.evaluate("fixtureSnapshot({nodes:[{id:'a',name:'Slow',protocol:'vless',code:'VL',ping:{status:'ok',ms:300,mode:'tcp',successes:3,attempts:3}},{id:'b',name:'Fast',protocol:'trojan',code:'TR',ping:{status:'ok',ms:40,mode:'tcp',successes:3,attempts:3}}]})");expect(page.locator('#serverList .server-card').first).to_have_attribute('aria-label','Выбрать Fast');expect(page.locator('.ping-node').first).to_have_text('40 мс')
    for width,height in [(800,600),(960,640)]:
        page.set_viewport_size({'width':width,'height':height});page.locator('#pingSettings').click();box=page.locator('#pingDialog').bounding_box();assert box['y']>=0 and box['y']+box['height']<=height;page.locator('#closePing').click();expect(page.locator('#pingDialog')).not_to_be_visible()
    page.set_viewport_size({'width':960,'height':640});page.wait_for_timeout(300);page.screenshot(path=str(root/'Tests/ui-ping.png'),animations='disabled')
    page.evaluate('''()=>{const original=Element.prototype.animate;window.pageMotion=[];Element.prototype.animate=function(...a){window.pageMotion.push(this.id||this.className);return original.apply(this,a);};fixtureNavigate('settings');fixtureSnapshot({});fixtureSnapshot({});fixtureNavigate('settings');}''')
    assert page.evaluate('window.pageMotion')==['page-settings'],page.evaluate('window.pageMotion')
    page.locator('[data-setting=killSwitch]').click();expect(page.locator('[data-setting=killSwitch]')).to_have_attribute('aria-checked','true')
    page.evaluate("fixtureSnapshot({settings:{theme:'dark',accent:'lime',mode:'tun',killSwitch:true,killSwitchActive:true},state:'idle',error:''})")
    expect(page.locator('#connectionError')).to_contain_text('Kill switch блокирует интернет')
    page.evaluate("fixtureSnapshot({settings:{theme:'dark',accent:'lime',mode:'tun',startup:false,autoConnect:false,tray:true,sendHwid:true,killSwitch:false,killSwitchActive:false,bypass:''},error:''})")
    for theme in ('light','dark'):
        page.locator(f'[data-theme={theme}]').click()
        for accent in ('gray','lime','green','purple'):
            page.locator(f'.accent-choice:has(input[value={accent}])').click();expect(page.locator('body')).to_have_attribute('data-accent',accent)
            square=page.locator('.brand .brand-dot').evaluate('(e)=>({w:e.offsetWidth,h:e.offsetHeight,r:getComputedStyle(e).borderRadius,c:getComputedStyle(e).backgroundColor})')
            assert square['w']==square['h']==7 and square['r']=='0px',square
    page.locator('[data-setting=startup]').click();expect(page.locator('[data-setting=startup]')).to_have_attribute('aria-checked','true')
    page.locator('#advancedButton').click();expect(page.locator('[data-setting=sendHwid]')).to_have_attribute('aria-checked','true');page.locator('[data-setting=sendHwid]').click();expect(page.locator('[data-setting=sendHwid]')).to_have_attribute('aria-checked','false');page.locator('[data-setting=sendHwid]').click();expect(page.locator('[data-setting=sendHwid]')).to_have_attribute('aria-checked','true');page.locator('#bypassDomains').fill('https://example.com');page.locator('#saveRules').click();expect(page.locator('#advancedError')).to_have_text('Введите домены без https://');page.locator('#bypassDomains').fill('example.com');page.locator('#saveRules').click();expect(page.locator('#advancedDialog')).not_to_be_visible()
    # Only explicit HWID read exposes the ID; display/copy works with sending disabled.
    page.evaluate("fixtureSnapshot({settings:{theme:'dark',accent:'lime',startup:true,autoConnect:false,tray:true,sendHwid:false,bypass:'example.com'}})")
    for width,height in [(800,600),(960,640)]:
        page.set_viewport_size({'width':width,'height':height});page.locator('#hwidButton').click()
        expect(page.locator('#hwidValue')).to_have_value('A'*64);expect(page.locator('#deviceName')).to_have_value('kot. windows (0.5.8)')
        expect(page.locator('#hwidValue')).to_have_attribute('readonly','');expect(page.locator('#copyHwid')).to_be_enabled()
        box=page.locator('#deviceDialog').bounding_box();assert box['y']>=0 and box['y']+box['height']<=height
        page.locator('#copyHwid').click();expect(page.locator('#toast')).to_have_text('HWID скопирован');assert page.evaluate('window.copiedHwid')=='A'*64
        if width==960:page.screenshot(path=str(root/'Tests/ui-hwid-031.png'),animations='disabled')
        page.locator('#closeDevice').click();expect(page.locator('#deviceDialog')).not_to_be_visible();expect(page.locator('#hwidValue')).to_have_value('')
    page.evaluate('window.fixtureDeviceError=true');page.locator('#hwidButton').click();expect(page.locator('#hwidError')).to_have_text('Не удалось прочитать HWID.');expect(page.locator('#copyHwid')).to_be_disabled();page.locator('#closeDevice').click();expect(page.locator('#deviceDialog')).not_to_be_visible()
    page.evaluate('window.fixtureDeviceError=false;window.fixtureClipboardError=true');page.locator('#hwidButton').click();expect(page.locator('#copyHwid')).to_be_enabled();page.locator('#copyHwid').click();expect(page.locator('#hwidError')).to_have_text('Буфер обмена занят.');expect(page.locator('#copyHwid')).to_be_enabled();page.keyboard.press('Escape');expect(page.locator('#deviceDialog')).not_to_be_visible();expect(page.locator('#hwidValue')).to_have_value('')
    page.evaluate('window.fixtureClipboardError=false;window.fixtureDeviceDelay=400');page.locator('#hwidButton').click();page.locator('#closeDevice').click();expect(page.locator('#deviceDialog')).not_to_be_visible();page.wait_for_timeout(450);expect(page.locator('#hwidValue')).to_have_value('');page.evaluate('window.fixtureDeviceDelay=0')
    for width,height in [(800,600),(960,640),(1200,800)]:
        page.set_viewport_size({'width':width,'height':height});assert page.evaluate('document.documentElement.scrollWidth===innerWidth');expect(page.locator('#titleVersion')).to_have_text('0.5.8');assert page.locator('#titleVersion').bounding_box()['x']>page.locator('.title-brand .wordmark').bounding_box()['x']
        assert page.locator('#advancedButton').bounding_box()['y']<height
    for target in ["logs", "servers", "settings", "home"]:
        page.evaluate("fixtureNavigate", target);expect(page.locator("#page-"+target)).to_be_visible()
    page.evaluate("fixtureNavigate", "untrusted");expect(page.locator("#page-home")).to_be_visible()
    page.evaluate("fixtureNavigate", "settings")
    page.set_viewport_size({'width':960,'height':640});page.locator('[data-theme=dark]').click();page.locator('.accent-choice:has(input[value=lime])').click();page.locator('[data-page=home]').click();page.evaluate("fixtureSnapshot({error:''})");page.wait_for_timeout(400);page.screenshot(path=str(root/'Tests/ui-home.png'),animations='disabled')
    page.locator('[data-page=servers]').click();page.locator('#subscriptionSelect').click();page.locator('#subscriptionOptions [data-subscription=sub2]').click();expect(page.locator('#subscriptionSelect')).to_have_attribute('data-subscription','sub2')
    page.locator('.favorite-node').first.click();expect(page.locator('.favorite-node').first).to_have_attribute('aria-pressed','true');expect(page.locator('#autoDescription')).to_have_text('Из избранного')
    page.locator('#autoServer').click();expect(page.locator('#selectedName')).to_have_text('Авто')
    page.evaluate("fixtureSnapshot({state:'waiting'})");expect(page.locator('#powerLabel')).to_have_text('Отменить');expect(page.locator('#powerButton')).to_be_enabled();page.evaluate("fixtureSnapshot({state:'idle'})")
    for width,height in [(800,600),(960,640)]:
        page.set_viewport_size({'width':width,'height':height});page.locator('[data-page=settings]').click();page.locator('#automationButton').click();box=page.locator('#automationDialog').bounding_box();assert box['y']>=0 and box['y']+box['height']<=height
        page.locator('#backgroundPing').fill('15');page.locator('#autoTolerance').fill('100');page.locator('#automationForm [type=submit]').click();expect(page.locator('#automationDialog')).not_to_be_visible();assert page.evaluate('window.automationSaved.pingMinutes')==15
        page.locator('#advancedButton').click();page.locator('#backupButton').click();expect(page.locator('#advancedDialog')).not_to_be_visible();page.locator('#backupPassword').fill('tiny');page.locator('#exportBackup').click();expect(page.locator('#backupError')).to_have_text('Пароль от 8 символов.')
        page.locator('#backupPassword').fill('test-password');page.locator('#exportBackup').click();expect(page.locator('#backupDialog')).not_to_be_visible();expect(page.locator('#backupPassword')).to_have_value('');assert page.evaluate('window.backupExported')=='test-password'
    page.set_viewport_size({'width':960,'height':640});page.locator('#advancedButton').click();page.locator('#closeAdvanced').click();expect(page.locator('#advancedDialog')).not_to_be_visible()
    # Actual bundled font must load under production CSP, including Cyrillic.
    page.evaluate("document.fonts.ready");assert page.evaluate("document.fonts.check('600 14px Manrope', 'Соединение')")
    assert page.locator('body').evaluate("e=>getComputedStyle(e).fontFamily").startswith('Manrope')
    page.locator('[data-page=home]').click()
    telemetry={'available':True,'downloadRate':2097152,'uploadRate':524288,'downloadTotal':52428800,'uploadTotal':1048576,'count':2,'connections':[
        {'id':'one','destination':'cdn.example.org:443','network':'tcp','inbound':'tun/tun-in','process':'browser.exe','chains':['Нидерланды','Авто'],'rule':'final','download':52428800,'upload':65536,'start':'2026-10-07T12:00:00Z'},
        {'id':'two','destination':'<img src=x onerror=alert(1)>','network':'udp','inbound':'tun/tun-in','process':'game.exe','chains':['Напрямую'],'rule':'ip_is_private => direct','download':4096,'upload':1024,'start':'2026-10-07T12:00:00Z'}]}
    page.evaluate('(t)=>fixtureSnapshot({state:"connected",telemetry:t,error:"",journal:"[12:00] [app] ready\\n[12:01] [tunnel-core] ERROR connection failed\\n<script>alert(1)</script>"})',telemetry)
    expect(page.locator('#downloadRate')).to_have_text('2 МБ/с');expect(page.locator('#uploadRate')).to_have_text('512 КБ/с')
    for width,height in [(800,600),(960,640),(1200,800)]:
        page.set_viewport_size({'width':width,'height':height});box=page.locator('#homeServer').bounding_box();assert box['y']+box['height']<=height
        assert page.evaluate('document.documentElement.scrollWidth===innerWidth');expect(page.locator('#titleVersion')).to_have_text('0.5.8');assert page.locator('#titleVersion').bounding_box()['x']>page.locator('.title-brand .wordmark').bounding_box()['x']
    page.set_viewport_size({'width':960,'height':640});page.wait_for_timeout(300);page.screenshot(path=str(root/'Tests/ui-030-home.png'),animations='disabled')
    page.locator('[data-page=logs]').click();expect(page.locator('#connectionList details')).to_have_count(2);assert not page.locator('#connectionList img').count();page.wait_for_timeout(30);assert page.evaluate('window.activeView')=='logs'
    page.locator('#connectionList details').first.locator('summary').click();expect(page.locator('#connectionList details').first).to_have_attribute('open','')
    page.locator('#logSearch').fill('browser');expect(page.locator('#connectionList details')).to_have_count(1);page.locator('#logSearch').fill('');expect(page.locator('#connectionList details')).to_have_count(2)
    page.locator('#pauseLogs').click();page.evaluate('fixtureSnapshot({telemetry:{available:true,count:0,connections:[]}})');expect(page.locator('#connectionList details')).to_have_count(2);page.locator('#pauseLogs').click();expect(page.locator('#connectionList details')).to_have_count(0)
    page.evaluate('(t)=>fixtureSnapshot({telemetry:t})',telemetry)
    page.locator('#logSearch').fill('cdn');page.wait_for_timeout(300);page.screenshot(path=str(root/'Tests/ui-030-connections.png'),animations='disabled');page.locator('#logSearch').fill('')
    page.locator('[data-log-tab=journal]').click();expect(page.locator('#journalText')).to_contain_text('<script>');assert not page.locator('#journalText script').count();page.locator('#logLevel').select_option('error');expect(page.locator('#journalText')).to_contain_text('ERROR');expect(page.locator('#journalText')).not_to_contain_text('ready');page.locator('#logLevel').select_option('all');page.locator('#copyViewLog').click();expect(page.locator('#toast')).to_have_text('Полный лог скопирован')
    page.locator('#logSearch').fill('missing');expect(page.locator('#logEmpty')).to_have_text('Ничего не найдено');page.locator('#logSearch').fill('')
    for width,height in [(800,600),(960,640),(1200,800)]:
        page.set_viewport_size({'width':width,'height':height});assert page.evaluate('document.documentElement.scrollWidth===innerWidth');expect(page.locator('#titleVersion')).to_have_text('0.5.8');assert page.locator('#titleVersion').bounding_box()['x']>page.locator('.title-brand .wordmark').bounding_box()['x']
    page.set_viewport_size({'width':960,'height':640});page.locator('[data-page=home]').click();page.evaluate('fixtureSnapshot({telemetry:{available:false}})');expect(page.locator('#downloadRate')).to_have_text('Нет данных');page.evaluate('fixtureSnapshot({state:"idle"})');expect(page.locator('#downloadRate')).to_have_text('0 Б/с')
    # A background offer never sends a download request or interrupts the active tunnel.
    page.locator('[data-page=home]').click()
    page.evaluate("fixtureSnapshot({state:'connected',updates:{busy:false,status:'Доступна версия 0.5.9',availableVersion:'0.5.9',dismissedVersion:''}})")
    expect(page.locator('#updateNotice')).to_be_visible();expect(page.locator('#updateNoticeTitle')).to_have_text('Доступно обновление 0.5.9')
    expect(page.locator('#onlineUpdateDialog')).not_to_be_visible();expect(page.locator('#connection')).to_have_attribute('data-state','connected')
    page.wait_for_timeout(100);assert not page.evaluate('window.onlineUpdateRequested||false')
    for theme in ['dark','light']:
        page.evaluate("document.body.classList.toggle('theme-light',"+str(theme=='light').lower()+")")
        for width,height in [(800,600),(960,640),(700,660)]:
            page.set_viewport_size({'width':width,'height':height})
            notice=page.locator('#updateNotice').bounding_box();buttons=page.locator('#updateNotice .update-notice-actions').bounding_box()
            assert 0<=notice['x'] and notice['x']+notice['width']<=width
            assert notice['y']<=buttons['y'] and buttons['y']+buttons['height']<=notice['y']+notice['height']
            assert buttons['x']+buttons['width']<=notice['x']+notice['width']
            assert page.evaluate('document.documentElement.scrollWidth===innerWidth')
            expect(page.locator('#page-home .segmented')).to_be_in_viewport()
    page.set_viewport_size({'width':960,'height':640});page.evaluate("document.body.classList.remove('theme-light')")
    page.screenshot(path=str(root/'Tests/ui-update-consent.png'),animations='disabled')
    page.locator('#deferUpdate').click();expect(page.locator('#updateNotice')).not_to_be_visible();assert page.evaluate('window.deferredVersion')=='0.5.9'
    page.evaluate('fixtureSnapshot({})');expect(page.locator('#updateNotice')).not_to_be_visible()
    page.locator('[data-page=servers]').click();expect(page.locator('#updateNotice')).not_to_be_visible()
    page.evaluate("fixtureSnapshot({updates:{busy:false,status:'Доступна версия 0.5.10',availableVersion:'0.5.10',dismissedVersion:'0.5.9'}})")
    expect(page.locator('#updateNotice')).to_be_visible();page.evaluate('window.fixtureDownloadDelay=350')
    page.locator('#acceptUpdate').click();expect(page.locator('#onlineUpdateDialog')).to_be_visible();expect(page.locator('#updateNotice')).not_to_be_visible()
    page.locator('#acceptUpdate').evaluate('(e)=>e.click()');page.locator('#downloadUpdate').evaluate('(e)=>e.click()')
    page.wait_for_timeout(400);assert page.evaluate('window.updateDownloadCount')==1;assert page.evaluate('window.confirmedVersion')=='0.5.10'
    page.locator('#closeOnlineUpdate').click();page.evaluate('window.fixtureDownloadDelay=0;window.onlineUpdateRequested=false')
    # An imported persisted deferral still allows explicitly checking and installing in Settings.
    page.evaluate("fixtureSnapshot({state:'idle',updates:{busy:false,status:'',availableVersion:null,dismissedVersion:'0.5.9'}})")
    page.locator('[data-page=settings]').click();expect(page.locator('#onlineUpdateButton')).to_have_text('Проверить обновления');page.locator('#onlineUpdateButton').click()
    expect(page.locator('#onlineUpdateDialog')).to_be_visible();expect(page.locator('#downloadUpdate')).to_have_text('Обновить до 0.5.9');assert page.locator('#updateSource,#updateAddress,#autoUpdate,#updateSourceForm,#updateButton').count()==0
    assert page.evaluate('window.updateCheckCount')==1;page.locator('#downloadUpdate').click();page.wait_for_timeout(30);assert page.evaluate('window.onlineUpdateRequested');page.locator('#closeOnlineUpdate').click()
    page.evaluate('window.fixtureMutationDelay=350');page.locator('#onlineUpdateButton').click();page.evaluate('fixtureSnapshot({})');expect(page.locator('#onlineUpdateButton')).to_be_disabled();expect(page.locator('#downloadUpdate')).not_to_be_visible();expect(page.locator('#updateProgress')).to_be_visible();page.wait_for_timeout(400);expect(page.locator('#onlineUpdateButton')).to_be_enabled();assert page.evaluate('window.updateCheckCount')==2;page.locator('#closeOnlineUpdate').click()
    page.evaluate('window.fixtureMutationDelay=0;window.fixtureUpdateError=true');page.locator('#onlineUpdateButton').click();expect(page.locator('#updateError')).to_have_text('Не удалось связаться с GitHub.');expect(page.locator('#downloadUpdate')).not_to_be_visible();expect(page.locator('#onlineUpdateButton')).to_be_enabled();page.locator('#closeOnlineUpdate').click()
    page.evaluate('window.fixtureUpdateError=false');page.locator('#onlineUpdateButton').click();expect(page.locator('#downloadUpdate')).to_have_text('Обновить до 0.5.9');expect(page.locator('#updateError')).to_be_empty()

    for width,height in [(800,600),(960,640)]:
        page.set_viewport_size({'width':width,'height':height});box=page.locator('#onlineUpdateDialog').bounding_box();assert box['y']>=0 and box['y']+box['height']<=height
    page.locator('#closeOnlineUpdate').click();expect(page.locator('#onlineUpdateDialog')).not_to_be_visible();page.set_viewport_size({'width':960,'height':640})

    page.locator('[data-page=home]').click()
    expect(page.locator('[data-mode=tun]')).to_have_text('Режим TUN')
    expect(page.locator('[data-mode=proxy]')).to_have_text('Системный прокси')
    page.locator('[data-mode=proxy]').click();expect(page.locator('[data-mode=proxy]')).to_have_attribute('aria-pressed','true')
    page.locator('[data-mode=tun]').click();expect(page.locator('[data-mode=tun]')).to_have_attribute('aria-pressed','true')

    # Clean demonstration captures. These are fixture values, never presented as live traffic measurement.
    page.evaluate("fixtureSnapshot({name:'Личная подписка',nodes:[{id:'a',name:'🇳🇱 Нидерланды',code:'NL',protocol:'vless'}],selected:'a',state:'connected',automaticNode:'',error:'',telemetry:{available:true,downloadRate:2097152,uploadRate:524288,downloadTotal:52428800,uploadTotal:1048576,count:2,connections:[{id:'demo-one',destination:'cdn.example.org:443',network:'tcp',inbound:'tun/tun-in',process:'browser.exe',chains:['Нидерланды'],rule:'final',download:52428800,upload:65536,start:'2026-10-07T12:00:00Z'},{id:'demo-two',destination:'192.168.1.1:80',network:'tcp',inbound:'tun/tun-in',process:'browser.exe',chains:['Напрямую'],rule:'ip_is_private => direct',download:4096,upload:1024,start:'2026-10-07T12:00:00Z'}]}})")
    page.locator('[data-page=home]').click();page.wait_for_timeout(450);page.evaluate("document.getElementById('toast').hidden=true;document.activeElement.blur()");page.screenshot(path=str(root/'Tests/ui-030-home.png'),animations='disabled')
    page.locator('[data-page=logs]').click();page.locator('[data-log-tab=connections]').click();page.wait_for_timeout(450);page.evaluate("document.activeElement.blur()");page.screenshot(path=str(root/'Tests/ui-030-connections.png'),animations='disabled')

    page.emulate_media(reduced_motion='reduce');page.locator('[data-page=settings]').click();page.locator('[data-page=home]').click();assert not page.evaluate('document.getAnimations().some(a=>a.playState==="running")')
    page.locator('[data-page=settings]').click();page.locator('#advancedButton').click();page.locator('#removeSubscription').click();page.locator('#confirmRemove').click();expect(page.locator('#powerButton')).to_be_disabled()
    assert not errors,errors
    browser.close()
print(json.dumps({'ui':'passed','themes_and_accents':8,'unsafe_labels':'text only','status':'host driven, no fake success timer','ping':'all, individual, cancel, settings, sorting','full_log':'copy from error','errors':errors},ensure_ascii=False))
