'use strict';
window.KotServices = ({getModel,getPage,rpc,toast,openDialog,closeDialog,requestClose}) => {
  const $=id=>document.getElementById(id);
  let provider='zapret', resultKey='', resultControls=[], strategyPending='', historyKey='', strategyKey='', settingService='', settingsSession=0, savingSettings=false, rotatingSecret=false, starting=new Set();
  const state=()=>getModel().services||{items:[],zapret:{},telegram:{}};
  const item=id=>state().items?.find(s=>s.id===id)||{id,status:'Не установлен'};
  async function perform(action,data={}){try{await rpc(action,data);return true;}catch(e){toast(e.message);return false;}}
  async function operation(service,operation,extra={}){
    if(starting.has(service))return;starting.add(service);render();
    try{return await perform('serviceAction',{service,operation,...extra});}
    finally{starting.delete(service);render();}
  }
  function showProvider(id){document.querySelector('.service-tabs').dataset.selected=id==='telegram'?'1':'0';provider=id;document.querySelectorAll('[data-service-tab]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.serviceTab===id));$('zapretPanel').hidden=id!=='zapret';$('telegramPanel').hidden=id!=='telegram';render();}
  function text(id,value){const el=$(id);if(el.textContent!==String(value))el.textContent=String(value);}
  function render(){
    const s=state();$('serviceAutoUpdates').checked=s.checkUpdates!==false;
    for(const id of ['zapret','telegram']){
      const e=item(id),busy=!!e.busy||starting.has(id)||(id==='zapret'&&!!strategyPending),prefix=id==='zapret'?'zapret':'telegram';
      text(prefix+'Status',e.phase||e.status||'Не установлен');$(prefix+'Status').dataset.running=!!e.running;
      text(prefix+'Version',e.installedVersion?'Установлена '+e.installedVersion:'Пакет ещё не установлен');
      text(prefix+'Update',e.updateAvailable?'Доступна '+e.availableVersion:e.checkedAt?'Проверено '+new Date(e.checkedAt).toLocaleString('ru-RU'): '');
      $(prefix+'Error').hidden=!e.error;text(prefix+'Error',e.error||'');
      const progress=$(prefix+'Progress');progress.hidden=!e.busy&&!starting.has(id);progress.value=Math.min(1,Math.max(0,e.progress||0));
      const install=$(prefix+'Install');install.textContent=e.installedVersion?'Обновить':'Установить';install.hidden=!!e.installedVersion&&!e.updateAvailable;install.disabled=busy||!!s.testing;
      $(prefix+'Start').hidden=!!e.running||!e.installedVersion;$(prefix+'Start').disabled=busy||!!s.testing;
      $(prefix+'Stop').hidden=!e.running;$(prefix+'Stop').disabled=busy||!!s.testing;
      $(prefix+'Restart').hidden=!e.running;$(prefix+'Restart').disabled=busy||!!s.testing;
      $(prefix+'Check').disabled=busy;$(prefix+'Settings').disabled=busy||!!s.testing;
      $(prefix+'Remove').hidden=!e.installedVersion;$(prefix+'Remove').disabled=busy||!!s.testing;
      $(prefix+'Cancel').hidden=!e.busy;$(prefix+'Cancel').disabled=false;
      if(getPage()==='services'){
        const logs=(e.logs||[]).join('\n');if($(prefix+'Log').textContent!==logs)$(prefix+'Log').textContent=logs||'Здесь появится журнал сервиса.';
      }
    }
    const z=item('zapret'),busy=!!z.busy||starting.has('zapret')||!!strategyPending;
    $('zapretTest').disabled=busy||!z.installedVersion||!!s.networkBusy;
    text('zapretTestNote',s.networkBusy?'Для подбора отключите VPN и kill switch. Иначе результат будет относиться к VPN.':'Проверяем прямое соединение, затем стратегии. HTTP-тесты не подтверждают работу видео и голосовых звонков.');
    const strategies=z.strategies||[],key=JSON.stringify(strategies);if(key!==strategyKey){strategyKey=key;$('zapretStrategy').replaceChildren(...strategies.map(name=>{const o=document.createElement('option');o.value=name;o.textContent=name.replace(/\.bat$/i,'');return o;}));}
    $('zapretStrategy').disabled=busy||!strategies.length||!!s.testing;if(s.zapret?.strategy&&document.activeElement!==$('zapretStrategy'))$('zapretStrategy').value=strategyPending||s.zapret.strategy;
    text('zapretAutoStart',s.zapret?.autoStart?'Запускается вместе с kot':'Запуск вручную');text('telegramAutoStart',s.telegram?.autoStart?'Запускается вместе с kot':'Запуск вручную');
    text('telegramAddress','127.0.0.1:'+(s.telegram?.port||1443));
    $('telegramOpen').disabled=!item('telegram').running||!!item('telegram').busy;$('telegramCopy').disabled=!item('telegram').installedVersion;
    $('zapretCopyResults').disabled=!s.test?.baseline?.length;$('zapretExportResults').disabled=!s.test?.baseline?.length;
    const history=s.history||[],historyState=JSON.stringify(history);
    if(historyState!==historyKey){historyKey=historyState;$('zapretHistorySelect').replaceChildren(...[...history].reverse().map(h=>{const o=document.createElement('option');o.value=h.started;o.textContent=new Date(h.started).toLocaleString('ru-RU')+' · '+h.count+' стратегий'+(h.cancelled?' · остановлен':'');return o;}));}
    $('zapretHistorySelect').disabled=!!s.testing||busy||!history.length;
    if(s.test?.started&&document.activeElement!==$('zapretHistorySelect'))$('zapretHistorySelect').value=s.test.started;
    if(getPage()==='services')renderResults(s.test);
  }
  function renderResults(run){
    // Service snapshots and selection must update controls without closing details or losing focus.
    const key=JSON.stringify(run);
    $('zapretResultsSection').hidden=!run;
    if(key!==resultKey){
      resultKey=key;resultControls=[];
      const root=$('zapretResults');root.replaceChildren();
      if(!run)return;
      text('zapretResultSummary',(run.cancelled?'Подбор остановлен. ':'')+'Без zapret: '+(run.baseline||[]).filter(c=>c.success).length+'/'+(run.baseline||[]).length+' доступных адресов. '+(run.recommended?'Рекомендация: '+run.recommended:'Улучшение не подтверждено.'));
      const summary=document.createElement('details');const heading=document.createElement('summary');heading.textContent='Проверки без zapret';summary.append(heading);appendChecks(summary,run.baseline||[]);root.append(summary);
      for(const result of run.results||[]){
        const row=document.createElement('div');row.className='service-result';row.dataset.recommended=run.recommended===result.strategy;
        const detail=document.createElement('details'),title=document.createElement('summary');
        const passed=(result.checks||[]).filter(c=>c.success).length;
        title.textContent=result.strategy.replace(/\.bat$/i,'')+' · '+passed+'/'+(run.baseline||[]).length;
        if(run.recommended===result.strategy){const badge=document.createElement('span');badge.className='strategy-best';badge.textContent='Лучший результат';title.append(badge);}
        detail.append(title);
        if(result.error){const error=document.createElement('p');error.className='error';error.textContent=result.error;detail.append(error);}
        appendChecks(detail,result.checks||[]);row.append(detail);
        const apply=document.createElement('button');apply.className='quiet-button strategy-apply';apply.type='button';
        const mark=document.createElement('span');mark.className='strategy-check';mark.setAttribute('aria-hidden','true');mark.textContent='✓';
        const label=document.createElement('span');apply.append(mark,label);
        apply.addEventListener('click',()=>saveStrategy(result.strategy));row.append(apply);root.append(row);
        resultControls.push({row,apply,label,result,passed});
      }
    }
    if(!run)return;
    const s=state(),z=item('zapret'),busy=!!s.testing||!!z.busy||starting.has('zapret')||!!strategyPending;
    for(const {row,apply,label,result,passed} of resultControls){
      const available=(z.strategies||[]).includes(result.strategy),pending=strategyPending===result.strategy;
      const selected=available&&s.zapret?.strategy===result.strategy&&!pending;
      row.dataset.selected=selected;
      apply.setAttribute('aria-pressed',String(selected));apply.setAttribute('aria-busy',String(pending));
      apply.setAttribute('aria-disabled',String(busy||selected||!available||!!result.error||!passed));
      // Keep the clicked control focusable during its request and after selection.
      apply.disabled=!pending&&((busy&&!selected)||!available||!!result.error||!passed);
      const caption=pending?'Сохранение…':selected?'Выбран':'Выбрать';
      if(label.textContent!==caption)label.textContent=caption;
      apply.setAttribute('aria-label',caption+': '+result.strategy.replace(/\.bat$/i,''));
      apply.title=!available?'Стратегия отсутствует в установленном пакете':selected?'Сохранённая стратегия zapret':run.recommended===result.strategy?'Лучший результат этого подбора':'Выбрать стратегию';
    }
    const history=state().history||[];text('zapretHistory','Сохранено прогонов: '+history.length+' из 10. Версия пакета: '+run.version+'. '+new Date(run.started).toLocaleString('ru-RU'));
  }
  function appendChecks(root,checks){for(const check of checks){const line=document.createElement('p');line.className='service-check';line.textContent=(check.success?'Доступен':'Ошибка')+' · '+check.target+' · '+check.milliseconds+' мс'+(check.status?' · HTTP '+check.status:'')+(check.error?' · '+check.error:'');root.append(line);}}
  async function saveStrategy(strategy){
    const s=state(),z=item('zapret');
    if(strategyPending||s.testing||z.busy||starting.has('zapret')||strategy===s.zapret?.strategy||!(z.strategies||[]).includes(strategy))return;
    strategyPending=strategy;render();
    try{if(await perform('serviceSettings',{service:'zapret',settings:{...s.zapret,strategy},restart:!!z.running}))toast('Стратегия сохранена');}
    finally{strategyPending='';$('zapretStrategy').value=state().zapret?.strategy||'';render();}
  }
  function settings(service){
    settingService=service;settingsSession++;$('serviceSettingsSave').disabled=savingSettings;const s=state(),z=s.zapret||{},t=s.telegram||{};
    text('serviceSettingsTitle',service==='zapret'?'Настройки zapret':'Настройки Telegram');$('zapretSettingsFields').hidden=service!=='zapret';$('telegramSettingsFields').hidden=service!=='telegram';
    $('serviceAutoStart').checked=!!(service==='zapret'?z.autoStart:t.autoStart);
    $('zapretGameFilter').checked=!!z.gameFilter;$('zapretDomains').value=z.domains||'';$('zapretExcludedDomains').value=z.excludedDomains||'';$('zapretTargets').value=z.targets||'https://discord.com\nhttps://www.youtube.com';$('zapretTimeout').value=z.timeoutSeconds||6;
    $('telegramPort').value=t.port||1443;$('telegramPool').value=t.poolSize??4;$('telegramCloudflare').checked=t.cloudflare!==false;$('telegramHttp2').checked=t.http2!==false;$('telegramDcIp').value=t.dcIp||'';$('telegramDomains').value=t.domains||'';$('telegramWorkers').value=t.workerDomains||'';$('telegramSecret').value='';
    text('serviceSettingsError','');text('serviceRestartNote',item(service).running?'Сервис будет перезапущен с новыми настройками.':'Настройки применятся при следующем запуске.');openDialog($('serviceSettingsDialog'));
  }
  document.querySelectorAll('[data-service-tab]').forEach(b=>b.addEventListener('click',()=>showProvider(b.dataset.serviceTab)));
  for(const id of ['zapret','telegram']){
    for(const [suffix,action] of [['Install','install'],['Start','start'],['Stop','stop'],['Restart','restart'],['Check','check']])$(id+suffix).addEventListener('click',()=>operation(id,action));
    $(id+'Cancel').addEventListener('click',()=>perform('serviceCancel',{service:id}));$(id+'Settings').addEventListener('click',()=>settings(id));
    $(id+'Source').addEventListener('click',()=>perform('serviceSource',{service:id}));
    $(id+'Remove').addEventListener('click',()=>{$('serviceRemoveDialog').dataset.service=id;text('serviceRemoveTitle','Удалить '+(id==='zapret'?'zapret':'Telegram-прокси')+'?');openDialog($('serviceRemoveDialog'));});
    $(id+'ClearLog').addEventListener('click',()=>perform('serviceClearLog',{service:id}));
  }
  $('serviceAutoUpdates').addEventListener('change',async()=>{if(!await perform('serviceUpdateSettings',{enabled:$('serviceAutoUpdates').checked}))render();});
  $('zapretStrategy').addEventListener('change',()=>saveStrategy($('zapretStrategy').value));
  $('telegramOpen').addEventListener('click',()=>perform('serviceOpenTelegram'));
  $('telegramCopy').addEventListener('click',async()=>{if(await perform('serviceCopyLink'))toast('Ссылка на локальный прокси скопирована');});
  $('telegramRotate').addEventListener('click',()=>openDialog($('serviceSecretDialog')));
  $('serviceSecretCancel').addEventListener('click',()=>closeDialog($('serviceSecretDialog')));
  $('serviceSecretConfirm').addEventListener('click',async()=>{
    if(rotatingSecret)return;rotatingSecret=true;$('serviceSecretConfirm').disabled=true;
    try{if(await perform('serviceRotateSecret')){closeDialog($('serviceSecretDialog'));toast('Secret обновлён. Подключите Telegram по новой ссылке.');}}
    finally{rotatingSecret=false;$('serviceSecretConfirm').disabled=false;}
  });
  $('serviceSettingsClose').addEventListener('click',()=>requestClose($('serviceSettingsDialog')));
  $('serviceSettingsDialog').addEventListener('close',()=>{settingsSession++;$('telegramSecret').value='';});
  $('serviceSettingsForm').addEventListener('submit',async event=>{
    event.preventDefault();if(savingSettings)return;savingSettings=true;const session=settingsSession,b=$('serviceSettingsSave');b.disabled=true;
    const service=settingService,autoStart=$('serviceAutoStart').checked;
    const settings=service==='zapret'?{...state().zapret,autoStart,gameFilter:$('zapretGameFilter').checked,domains:$('zapretDomains').value,excludedDomains:$('zapretExcludedDomains').value,targets:$('zapretTargets').value,timeoutSeconds:Number($('zapretTimeout').value)}:{autoStart,port:Number($('telegramPort').value),poolSize:Number($('telegramPool').value),cloudflare:$('telegramCloudflare').checked,http2:$('telegramHttp2').checked,dcIp:$('telegramDcIp').value,domains:$('telegramDomains').value,workerDomains:$('telegramWorkers').value,secret:$('telegramSecret').value};
    try{await rpc('serviceSettings',{service,settings,restart:!!item(service).running});if(session===settingsSession)closeDialog($('serviceSettingsDialog'));toast('Настройки сохранены');}catch(e){if(session===settingsSession)text('serviceSettingsError',e.message);else toast(e.message);}finally{savingSettings=false;b.disabled=false;}
  });
  $('serviceRemoveCancel').addEventListener('click',()=>closeDialog($('serviceRemoveDialog')));
  $('serviceRemoveConfirm').addEventListener('click',async()=>{if(await operation($('serviceRemoveDialog').dataset.service,'remove'))closeDialog($('serviceRemoveDialog'));});
  $('zapretTest').addEventListener('click',()=>{
    const root=$('zapretTestStrategies');root.replaceChildren();
    for(const name of item('zapret').strategies||[]){const label=document.createElement('label');label.className='service-checkbox';const input=document.createElement('input');input.type='checkbox';input.value=name;input.checked=true;label.append(input,document.createTextNode(name.replace(/\.bat$/i,'')));root.append(label);}
    text('zapretTestError','');openDialog($('zapretTestDialog'));
  });
  $('zapretTestAll').addEventListener('click',()=>{$('zapretTestStrategies').querySelectorAll('input').forEach(i=>{i.checked=true;});});
  $('zapretTestNone').addEventListener('click',()=>{$('zapretTestStrategies').querySelectorAll('input').forEach(i=>{i.checked=false;});});
  $('zapretTestClose').addEventListener('click',()=>requestClose($('zapretTestDialog')));
  $('zapretTestRun').addEventListener('click',async()=>{const strategies=[...$('zapretTestStrategies').querySelectorAll('input:checked')].map(i=>i.value);if(!strategies.length){text('zapretTestError','Выберите хотя бы одну стратегию.');return;}if(await operation('zapret','test',{strategies}))closeDialog($('zapretTestDialog'));});
  $('zapretHistorySelect').addEventListener('change',()=>perform('serviceSelectTest',{started:$('zapretHistorySelect').value}));
  $('zapretCopyResults').addEventListener('click',async()=>{if(await perform('serviceCopyResults'))toast('Результаты скопированы');});
  $('zapretExportResults').addEventListener('click',()=>perform('serviceExportResults'));
  return {render};
};
