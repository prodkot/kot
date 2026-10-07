(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const reduce = matchMedia('(prefers-reduced-motion: reduce)'), motions = new Map();
  const ease = 'cubic-bezier(.22,1,.36,1)';
  function animate(el, frames, options={}) {
    motions.get(el)?.cancel(); if(reduce.matches) return null;
    const motion=el.animate(frames,{duration:280,easing:ease,...options}); motions.set(el,motion);
    motion.finished.then(()=>{if(motions.get(el)===motion)motions.delete(el);}).catch(()=>{}); return motion;
  }
  reduce.addEventListener('change',()=>{if(reduce.matches){motions.forEach(a=>a.cancel());motions.clear();}});
  let model={nodes:[],selected:'',state:'idle',settings:{theme:'dark',accent:'lime',mode:'all'}}, current='home', toastTimer, sequence=0, importBusy=false, refreshBusy=false, updateCheckBusy=false, renderedServers='';
  const pending=new Map(), host=window.chrome?.webview;
  function rpc(action,data={}) {
    if(!host) return Promise.reject(new Error('Откройте интерфейс через Kot.exe.'));
    return new Promise((resolve,reject)=>{
      const id=++sequence, timer=setTimeout(()=>{pending.delete(id);reject(new Error('Операция заняла слишком много времени.'));},action==='downloadUpdate'?720000:120000);
      pending.set(id,{resolve,reject,timer}); host.postMessage({id,action,data});
    });
  }
  function toast(text){clearTimeout(toastTimer);$('toast').textContent=text;$('toast').hidden=false;animate($('toast'),[{opacity:0,transform:'translate(-50%,7px)'},{opacity:1,transform:'translate(-50%,0)'}]);toastTimer=setTimeout(()=>{$('toast').hidden=true;},4000);}
  async function request(action,data={}) {try{await rpc(action,data);return true;}catch(e){toast(e.message);return false;}}
  function moveNav(){const active=document.querySelector('[data-page][aria-current=page]');const pill=document.querySelector('.nav-indicator');pill.style.transform=`translateY(${active.offsetTop}px)`;pill.style.height=active.offsetHeight+'px';}
  function page(name){
    if(name===current)return; current=name;
    document.querySelectorAll('.view').forEach(el=>el.hidden=el.id!=='page-'+name);
    document.querySelectorAll('[data-page]').forEach(el=>{if(el.dataset.page===name)el.setAttribute('aria-current','page');else el.removeAttribute('aria-current');});
    document.querySelector('.main').scrollTop=0; moveNav(); request('view',{page:name}); if(name==='logs')renderLogs(); if(name==='servers')renderServers();
    const view=$('page-'+name);animate(view,[{opacity:0,transform:'translateY(8px)'},{opacity:1,transform:'translateY(0)'}],{duration:260});
    // A single page transition prevents nested delayed elements from flashing.
  }
  function setState(state, previous){
    $('connection').dataset.state=state;
    const texts={idle:['Не подключено','Подключить'],connecting:['Подключение','Отменить'],connected:['Подключено','Отключить'],disconnecting:['Отключение','Отключить'],waiting:['Ожидание сети · повтор подключения','Отменить']}[state]||['Не подключено','Подключить'];
    $('stateLabel').textContent=texts[0];$('powerLabel').textContent=texts[1];$('powerButton').setAttribute('aria-label',texts[1]);
    $('powerButton').disabled=(!model.nodes.length && state==='idle')||state==='disconnecting';
    const blocked=model.settings?.killSwitchActive&&state==='idle';
    $('connectionError').textContent=model.error||(blocked?'Kill switch блокирует интернет. Подключитесь или отключите защиту в настройках.':'');$('connectionError').hidden=!model.error&&!blocked;$('copyErrorLog').hidden=!model.error;
    if(state!==previous){animate($('stateLabel'),[{opacity:0,transform:'translateY(3px)'},{opacity:1,transform:'translateY(0)'}],{duration:220});animate($('powerLabel'),[{opacity:0},{opacity:1}],{duration:200});if(state==='connected')animate($('powerIcon'),[{transform:'scale(.82)'},{transform:'scale(1.12)',offset:.6},{transform:'scale(1)'}],{duration:440});}
  }
  function apply(data){
    const old=model.state;model=data;
    document.body.classList.toggle('theme-light',data.settings.theme==='light');document.body.dataset.accent=data.settings.accent;
    document.querySelectorAll('[data-theme]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.theme===data.settings.theme));document.querySelector('.theme-control').dataset.selected=data.settings.theme==='dark'?'0':'1';
    document.querySelectorAll('input[name=accent]').forEach(b=>b.checked=b.value===data.settings.accent);
    document.querySelectorAll('[data-mode]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.mode===data.settings.mode));document.querySelector('.mode').dataset.selected=data.settings.mode==='all'?'0':'1';
    document.querySelectorAll('[data-setting]').forEach(b=>b.setAttribute('aria-checked',!!data.settings[b.dataset.setting]));
    $('subscriptionName').textContent=data.hasSubscription?data.name:'Подписка';$('listName').textContent=data.hasSubscription?data.name:'Подписка';
    const selected=data.nodes.find(n=>n.id===data.selected);
    const automatic=data.selected==='auto', actual=data.nodes.find(n=>n.id===data.automaticNode);
    $('selectedName').textContent=automatic?'Авто':selected?.name||'Добавить подписку';$('selectedCountry').textContent=automatic?'A':selected?.code||'+';$('selectedCity').textContent=automatic?actual?.name||'Быстрый доступный сервер':selected?.protocol.toUpperCase()||'';
    $('autoServer').hidden=!data.nodes.length;$('autoServer').setAttribute('aria-pressed',automatic);$('autoDescription').textContent=data.favorites?.length?'Из избранного':'Быстрый доступный сервер';
    const subscriptions=data.subscriptions||[];const select=$('subscriptionSelect');
    const optionsKey=JSON.stringify(subscriptions.map(s=>[s.id,s.name,s.count]));
    if(select.dataset.key!==optionsKey){select.replaceChildren(...subscriptions.map(s=>{const o=document.createElement('option');o.value=s.id;o.textContent=s.name+' · '+s.count;return o;}));select.dataset.key=optionsKey;}
    select.value=data.activeSubscription||'';select.disabled=subscriptions.length<2;
    $('backgroundError').textContent=data.backgroundError||'';$('backgroundError').hidden=!data.backgroundError;
    $('refreshButton').disabled=refreshBusy||!data.hasSubscription;$('removeSubscription').hidden=!data.hasSubscription;
    if(!$('advancedDialog').open)$('bypassDomains').value=data.settings.bypass||'';
    $('skippedDetails').hidden=!data.warnings?.length;$('skippedNodes').replaceChildren(...(data.warnings||[]).slice(0,50).map(w=>{const p=document.createElement('p');p.textContent=w;return p;}));
    $('titleVersion').textContent=data.version||'';$('version').textContent='kot. '+data.version+' · sing-box 1.14.2'+(data.updated?' · '+data.updated:'');
    const ping=data.ping||{};
    $('pingAll').disabled=!data.nodes.length||!!ping.busy;$('pingAll').dataset.busy=!!ping.busy;
    $('pingProgress').hidden=!ping.busy;$('pingProgress').textContent=(ping.done||0)+' / '+(ping.total||0);$('cancelPing').hidden=!ping.busy;
    $('pingError').hidden=!ping.error;$('pingError').textContent=ping.error||'';
    renderTraffic(); renderUpdate(); if(current==='logs'&&!logsPaused){logSnapshot={telemetry:data.telemetry,journal:data.journal,state:data.state};renderLogs();}
    setState(data.state,old);const renderKey=JSON.stringify([data.nodes,data.selected,data.favorites,data.ping?.settings?.sort,data.ping?.busy]);if(renderKey!==renderedServers){renderedServers=renderKey;renderServers();}
  }
  function renderServers(){
    const query=$('serverSearch').value.trim().toLocaleLowerCase('ru'),visible=model.nodes.filter(n=>(n.name+' '+n.protocol).toLocaleLowerCase('ru').includes(query));
    const active=document.activeElement, focusId=active?.closest('[data-node]')?.dataset.node, focusPing=active?.classList.contains('ping-node'), focusFavorite=active?.classList.contains('favorite-node');
    $('serverList').replaceChildren();$('noServers').hidden=!!visible.length;$('noServers').textContent=model.nodes.length?'Ничего не найдено':'Добавьте подписку';$('serverCount').textContent=visible.length;
    const sort=model.ping?.settings?.sort||'none';
    if(sort==='name')visible.sort((a,b)=>a.name.localeCompare(b.name,'ru'));
    if(sort==='ping')visible.sort((a,b)=>(a.ping?.ms??Infinity)-(b.ping?.ms??Infinity));
    visible.forEach(item=>{
      const row=document.createElement('div');row.className='server-row';row.dataset.node=item.id;
      const favorite=document.createElement('button');favorite.className='favorite-node';favorite.textContent=model.favorites?.includes(item.id)?'★':'☆';favorite.setAttribute('aria-label','Избранное: '+item.name);favorite.setAttribute('aria-pressed',!!model.favorites?.includes(item.id));favorite.addEventListener('click',()=>request('favorite',{id:item.id}));
      const button=document.createElement('button');button.className='server-card';button.setAttribute('aria-pressed',item.id===model.selected);button.setAttribute('aria-label','Выбрать '+item.name);
      const country=document.createElement('span');country.className='country';country.textContent=item.code;
      const copy=document.createElement('span');copy.className='server-copy';const title=document.createElement('strong');title.textContent=item.name;const city=document.createElement('small');city.textContent=item.protocol.toUpperCase();copy.append(title,city);button.append(country,copy);
      if(item.id===model.selected){const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');icon.classList.add('selected-mark');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#check');icon.append(use);button.append(icon);}
      button.addEventListener('click',async()=>{button.disabled=true;try{await rpc('select',{id:item.id});page('home');}catch(e){toast(e.message);}finally{button.disabled=false;}});
      const ping=document.createElement('button');ping.className='ping-node';ping.disabled=!!model.ping?.busy;ping.setAttribute('aria-label','Пинг '+item.name);
      const result=item.ping;ping.dataset.status=result?.status||'idle';
      const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#pulse');icon.append(use);
      const value=document.createElement('span');value.textContent=result?.status==='ok'?result.ms+' мс':result?.status==='checking'?'…':result?.status==='queued'?'В очереди':result?.status==='error'?'Ошибка':result?.status==='unsupported'?'UDP':result?.status==='cancelled'?'Отмена':'Пинг';
      ping.title=result?.status==='ok'?result.mode.toUpperCase()+': '+result.successes+'/'+result.attempts+' попыток'+(result.error?'; '+result.error:''):result?.error||'Проверить сервер';
      ping.append(icon,value);ping.addEventListener('click',()=>request('pingNode',{id:item.id}));row.append(button,favorite,ping);$('serverList').append(row);
      if(focusId===item.id)(focusPing?ping:focusFavorite?favorite:button).focus({preventScroll:true});
    });
  }

  let logTab='connections',logsPaused=false,logSnapshot=null,connectionsKey='';
  function bytes(n,rate=false){if(n===null||n===undefined||!Number.isFinite(n))return 'Нет данных';const units=['Б','КБ','МБ','ГБ','ТБ'];let i=0;while(n>=1024&&i<units.length-1){n/=1024;i++;}return new Intl.NumberFormat('ru-RU',{maximumFractionDigits:i===0?0:1}).format(n)+' '+units[i]+(rate?'/с':'');}
  function renderTraffic(){const t=model.telemetry||{},on=model.state==='connected';$('downloadRate').textContent=on?bytes(t.available?t.downloadRate:null,true):'0 Б/с';$('uploadRate').textContent=on?bytes(t.available?t.uploadRate:null,true):'0 Б/с';$('downloadRate').title='За сеанс: '+bytes(t.downloadTotal||0);$('uploadRate').title='За сеанс: '+bytes(t.uploadTotal||0);}
  function renderLogs(){
    const snap=logSnapshot||{telemetry:model.telemetry,journal:model.journal,state:model.state},t=snap.telemetry||{},query=$('logSearch').value.trim().toLocaleLowerCase('ru');
    $('logLevel').hidden=logTab!=='journal';$('journalText').hidden=logTab!=='journal';$('connectionList').hidden=logTab!=='connections';
    document.querySelector('.log-tabs').dataset.selected=logTab==='connections'?'0':'1';document.querySelectorAll('[data-log-tab]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.logTab===logTab));
    if(logTab==='journal'){
      let lines=(snap.journal||'').split('\n');const level=$('logLevel').value;lines=lines.filter(line=>(!query||line.toLocaleLowerCase('ru').includes(query))&&(level==='all'||(level==='error'?/error|fatal|exception|ошибк/i:/warn|предупреж/i).test(line)));
      const box=$('journalText'),bottom=box.scrollHeight-box.scrollTop-box.clientHeight<35,text=lines.join('\n');if(box.textContent!==text){box.textContent=text;if(bottom&&!logsPaused)box.scrollTop=box.scrollHeight;}
      $('logStatus').textContent=logsPaused?'На паузе':'Журнал текущего запуска';$('logEmpty').hidden=!!text.trim();$('logEmpty').textContent=query?'Ничего не найдено':'Нет событий';
    }else{
      const rows=(t.connections||[]).filter(c=>[c.destination,c.process,c.network,...(c.chains||[])].join(' ').toLocaleLowerCase('ru').includes(query));
      const key=JSON.stringify(rows);if(key!==connectionsKey){const opened=new Set([...$('connectionList').querySelectorAll('details[open]')].map(e=>e.dataset.connection));$('connectionList').replaceChildren(...rows.map(c=>{
        const card=document.createElement('details');card.className='connection-card';card.dataset.connection=c.id;card.open=opened.has(c.id);const summary=document.createElement('summary'),top=document.createElement('div');top.className='connection-top';
        const destination=document.createElement('span');destination.className='connection-dest';destination.textContent=c.destination||'Адрес неизвестен';const network=document.createElement('span');network.className='connection-network';network.textContent=(c.network||'').toUpperCase();top.append(destination,network);
        const meta=document.createElement('div');meta.className='connection-meta';const route=document.createElement('span');route.className='connection-route';route.textContent=(c.process?c.process+' · ':'')+(c.chains?.join(' → ')||'Маршрут неизвестен');const total=document.createElement('span');total.className='connection-bytes';total.textContent='↓ '+bytes(c.download)+'  ↑ '+bytes(c.upload);meta.append(route,total);summary.append(top,meta);
        const detail=document.createElement('div');detail.className='connection-detail';detail.textContent='Вход: '+(c.inbound||'Не определён')+'\nПравило: '+(c.rule||'final')+(c.start?'\nНачало: '+new Date(c.start).toLocaleTimeString('ru-RU'):'');detail.style.whiteSpace='pre-line';card.append(summary,detail);return card;
      }));connectionsKey=key;}
      $('logStatus').textContent=logsPaused?'На паузе':t.error||((t.count||0)+' активных'+((t.count||0)>300?' · показаны первые 300':''));$('logEmpty').hidden=!!rows.length;$('logEmpty').textContent=query?'Ничего не найдено':snap.state==='connected'?'Нет активных соединений':'Подключитесь к серверу';
    }
  }
  function renderUpdate(){const u=model.updates||{},busy=updateCheckBusy||!!u.busy;$('updateBadge').hidden=!u.availableVersion;$('updateStatus').textContent=(u.status||'Проверка обновлений')+(u.bytes?' · '+bytes(u.bytes):'')+(u.checkedAt&&!u.busy?' · '+u.checkedAt:'');$('updateError').textContent=u.error||'';$('downloadUpdate').hidden=!u.availableVersion||busy;$('downloadUpdate').textContent='Обновить до '+(u.availableVersion||'');$('cancelUpdate').hidden=!u.busy;$('onlineUpdateButton').disabled=busy;$('updateProgress').hidden=!busy;if(u.bytes&&u.size){$('updateProgress').max=u.size;$('updateProgress').value=u.bytes;}else $('updateProgress').removeAttribute('value');}
  document.querySelectorAll('[data-log-tab]').forEach(b=>b.addEventListener('click',()=>{logTab=b.dataset.logTab;renderLogs();}));$('logSearch').addEventListener('input',renderLogs);$('logLevel').addEventListener('change',renderLogs);
  $('pauseLogs').addEventListener('click',()=>{logsPaused=!logsPaused;$('pauseLogs').setAttribute('aria-pressed',logsPaused);$('pauseLogs').textContent=logsPaused?'Продолжить':'Пауза';if(!logsPaused)logSnapshot={telemetry:model.telemetry,journal:model.journal,state:model.state};renderLogs();});$('copyViewLog').addEventListener('click',async()=>{if(await request('copyLog'))toast('Полный лог скопирован');});
  $('onlineUpdateButton').addEventListener('click',async()=>{
    if(updateCheckBusy||model.updates?.busy)return;
    updateCheckBusy=true;model.updates={...model.updates,status:'Проверка обновлений',error:'',availableVersion:null,bytes:0};
    renderUpdate();openDialog($('onlineUpdateDialog'));
    try{await rpc('checkUpdates');}catch(e){model.updates={...model.updates,status:'Не удалось проверить обновления',error:e.message};}
    finally{updateCheckBusy=false;renderUpdate();}
  });
  $('closeOnlineUpdate').addEventListener('click',()=>closeDialog($('onlineUpdateDialog')));$('cancelUpdate').addEventListener('click',()=>request('cancelUpdate'));
  $('downloadUpdate').addEventListener('click',()=>request('downloadUpdate'));

  $('subscriptionSelect').addEventListener('change',()=>request('switchSubscription',{id:$('subscriptionSelect').value}));
  $('autoServer').addEventListener('click',async()=>{if(await request('select',{id:'auto'}))page('home');});
  document.querySelectorAll('[data-page]').forEach(el=>el.addEventListener('click',()=>page(el.dataset.page)));
  $('homeServer').addEventListener('click',()=>model.nodes.length?page('servers'):openAdd());$('serverSearch').addEventListener('input',renderServers);
  $('powerButton').addEventListener('click',()=>request('toggle'));
  document.querySelectorAll('[data-mode]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:'mode',value:el.dataset.mode})));
  document.querySelectorAll('[data-theme]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:'theme',value:el.dataset.theme})));
  document.querySelectorAll('input[name=accent]').forEach(input=>input.addEventListener('change',()=>{if(input.checked)request('settings',{key:'accent',value:input.value});}));
  document.querySelectorAll('[data-setting]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:el.dataset.setting,value:el.getAttribute('aria-checked')!=='true'})));
  function clearLink(){$('profileLink').value='';$('addError').textContent='';}
  function openDialog(dialog){if(dialog.open)return;dialog.showModal();animate(dialog,[{opacity:0,transform:'translateY(12px) scale(.97)'},{opacity:1,transform:'translateY(0) scale(1)'}],{duration:280});}
  function closeDialog(dialog){
    if(dialog.dataset.closing)return;dialog.dataset.closing='true';if(dialog===$('deviceDialog'))clearHwid();if(dialog===$('addDialog')){if(importBusy)request('cancelImport');clearLink();}
    const motion=animate(dialog,[{opacity:1,transform:'scale(1)'},{opacity:0,transform:'translateY(6px) scale(.985)'}],{duration:130});
    const finish=()=>{dialog.close();delete dialog.dataset.closing;};if(motion)motion.finished.then(finish).catch(finish);else finish();
  }
  function openAdd(){clearLink();$('replaceNote').hidden=!model.hasSubscription;openDialog($('addDialog'));}
  document.querySelectorAll('[data-open-add]').forEach(el=>el.addEventListener('click',openAdd));$('cancelAdd').addEventListener('click',()=>closeDialog($('addDialog')));$('addDialog').addEventListener('close',clearLink);
  $('addForm').addEventListener('submit',async event=>{
    event.preventDefault();if($('addDialog').dataset.closing)return;
    const url=$('profileLink').value.trim();try{if(new URL(url).protocol!=='https:')throw 0;}catch{$('addError').textContent='Нужна HTTPS-ссылка на подписку.';return;}
    const submit=$('addForm').querySelector('[type=submit]');if(submit.disabled)return;submit.disabled=true;importBusy=true;submit.textContent='Загрузка';$('addError').textContent='';
    try{await rpc('import',{url,name:$('profileName').value.trim()});closeDialog($('addDialog'));page('servers');toast(model.warnings?.length?'Серверы добавлены. Пропущено: '+model.warnings.length:'Серверы добавлены');}
    catch(e){$('addError').textContent=e.message;}
    finally{submit.disabled=false;importBusy=false;submit.textContent='Добавить';}
  });
  $('refreshButton').addEventListener('click',async()=>{if(refreshBusy)return;refreshBusy=true;const b=$('refreshButton');b.disabled=true;try{await rpc('refresh');toast(model.warnings?.length?'Обновлено. Пропущено: '+model.warnings.length:'Подписка обновлена');}catch(e){toast(e.message);}finally{refreshBusy=false;b.disabled=!model.hasSubscription;}});
  let deviceAttempt=0;
  function clearHwid(){deviceAttempt++;$('hwidValue').value='';$('deviceName').value='';$('copyHwid').disabled=true;}
  $('hwidButton').addEventListener('click',async()=>{
    const dialog=$('deviceDialog');if(dialog.open)return;
    clearHwid();const attempt=deviceAttempt;$('hwidError').textContent='';openDialog(dialog);
    try{const device=await rpc('readHwid');if(attempt!==deviceAttempt||!dialog.open||dialog.dataset.closing)return;
      $('hwidValue').value=device.hwid;$('deviceName').value=device.name;$('copyHwid').disabled=false;
    }catch(e){if(attempt===deviceAttempt&&dialog.open)$('hwidError').textContent=e.message;}
  });
  $('closeDevice').addEventListener('click',()=>closeDialog($('deviceDialog')));$('deviceDialog').addEventListener('close',clearHwid);
  $('copyHwid').addEventListener('click',async()=>{
    const b=$('copyHwid'),attempt=deviceAttempt;b.disabled=true;$('hwidError').textContent='';
    try{await rpc('copyHwid');toast('HWID скопирован');}catch(e){if(attempt===deviceAttempt)$('hwidError').textContent=e.message;}
    finally{if(attempt===deviceAttempt&&$('deviceDialog').open&&!$('deviceDialog').dataset.closing)b.disabled=false;}
  });
  $('advancedButton').addEventListener('click',()=>{$('bypassDomains').value=model.settings.bypass||'';$('advancedError').textContent='';openDialog($('advancedDialog'));});$('closeAdvanced').addEventListener('click',()=>closeDialog($('advancedDialog')));
  $('saveRules').addEventListener('click',async()=>{const b=$('saveRules');b.disabled=true;try{await rpc('settings',{key:'bypass',value:$('bypassDomains').value});closeDialog($('advancedDialog'));toast('Правила сохранены');}catch(e){$('advancedError').textContent=e.message;}finally{b.disabled=false;}});
  ['diagnostics','copyErrorLog'].forEach(id=>$(id).addEventListener('click',async()=>{if(await request('copyLog'))toast('Полный лог скопирован');}));
  $('pingAll').addEventListener('click',()=>request('pingAll'));$('cancelPing').addEventListener('click',()=>request('cancelPing'));
  function pingMethod(){const http=$('pingMode').value==='http';$('pingUrlRow').hidden=!http;$('pingUrl').required=http;$('pingUrl').disabled=!http;$('pingMethodNote').textContent=http?'Проверяет подключение и доступ к сайту.':'Проверяет TCP-порт, без проверки подключения. Для Hysteria2 нужен HTTP.';}
  $('pingMode').addEventListener('change',pingMethod);
  $('pingSettings').addEventListener('click',()=>{
    const p=model.ping?.settings||{mode:'http',url:'https://www.gstatic.com/generate_204',timeoutMs:5000,attempts:2,parallelism:3,sort:'none'};
    $('pingMode').value=p.mode;$('pingUrl').value=p.url;$('pingTimeout').value=p.timeoutMs;$('pingAttempts').value=p.attempts;$('pingParallel').value=p.parallelism;$('pingSort').value=p.sort;$('pingSettingsError').textContent='';pingMethod();openDialog($('pingDialog'));
  });
  $('closePing').addEventListener('click',()=>closeDialog($('pingDialog')));
  $('pingForm').addEventListener('submit',async event=>{
    event.preventDefault();const submit=$('pingForm').querySelector('[type=submit]');submit.disabled=true;
    try{await rpc('pingSettings',{mode:$('pingMode').value,url:$('pingUrl').value.trim(),timeoutMs:Number($('pingTimeout').value),attempts:Number($('pingAttempts').value),parallelism:Number($('pingParallel').value),sort:$('pingSort').value});closeDialog($('pingDialog'));toast('Настройки пинга сохранены');}
    catch(e){$('pingSettingsError').textContent=e.message;}finally{submit.disabled=false;}
  });
  $('removeSubscription').addEventListener('click',()=>openDialog($('removeDialog')));$('cancelRemove').addEventListener('click',()=>closeDialog($('removeDialog')));
  $('confirmRemove').addEventListener('click',async()=>{if(await request('remove')){closeDialog($('removeDialog'));closeDialog($('advancedDialog'));page('home');toast('Подписка удалена');}});
  $('automationButton').addEventListener('click',()=>{const a=model.automation||{reconnect:true,refreshHours:6,pingMinutes:0,autoMinutes:3,autoToleranceMs:50};$('reconnectSetting').setAttribute('aria-checked',a.reconnect);$('refreshHours').value=a.refreshHours;$('backgroundPing').value=a.pingMinutes;$('autoMinutes').value=a.autoMinutes;$('autoTolerance').value=a.autoToleranceMs;$('automationError').textContent='';openDialog($('automationDialog'));});
  $('reconnectSetting').addEventListener('click',()=>{$('reconnectSetting').setAttribute('aria-checked',$('reconnectSetting').getAttribute('aria-checked')!=='true');});
  $('closeAutomation').addEventListener('click',()=>closeDialog($('automationDialog')));
  $('automationForm').addEventListener('submit',async e=>{e.preventDefault();const b=$('automationForm').querySelector('[type=submit]');b.disabled=true;try{await rpc('automationSettings',{reconnect:$('reconnectSetting').getAttribute('aria-checked')==='true',refreshHours:Number($('refreshHours').value),pingMinutes:Number($('backgroundPing').value),autoMinutes:Number($('autoMinutes').value),autoToleranceMs:Number($('autoTolerance').value)});closeDialog($('automationDialog'));toast('Сохранено');}catch(e){$('automationError').textContent=e.message;}finally{b.disabled=false;}});
  $('backupButton').addEventListener('click',()=>{closeDialog($('advancedDialog'));$('backupPassword').value='';$('backupError').textContent='';openDialog($('backupDialog'));});
  $('closeBackup').addEventListener('click',()=>closeDialog($('backupDialog')));$('backupDialog').addEventListener('close',()=>{$('backupPassword').value='';});
  for(const [id,action] of [['exportBackup','backupExport'],['restoreBackup','backupImport']])$(id).addEventListener('click',async()=>{const password=$('backupPassword').value;if(password.length<8){$('backupError').textContent='Пароль от 8 символов.';return;}for(const id of ['exportBackup','restoreBackup'])$(id).disabled=true;try{await rpc(action,{password});closeDialog($('backupDialog'));}catch(e){$('backupError').textContent=e.message;}finally{for(const id of ['exportBackup','restoreBackup'])$(id).disabled=false;}});
  document.querySelectorAll('dialog').forEach(dialog=>{dialog.addEventListener('cancel',event=>{event.preventDefault();closeDialog(dialog);});dialog.addEventListener('click',event=>{if(event.target!==dialog)return;const r=dialog.getBoundingClientRect();if(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom)closeDialog(dialog);});});
  document.querySelectorAll('[data-window]').forEach(b=>b.addEventListener('click',()=>request(b.dataset.window)));
  document.querySelector('.titlebar').addEventListener('mousedown',e=>{if(e.button===0&&!e.target.closest('button'))request('drag',{double:e.detail===2});});
  document.querySelectorAll('[data-edge]').forEach(b=>b.addEventListener('mousedown',e=>{if(e.button===0)request('resize',{edge:b.dataset.edge});}));
  new ResizeObserver(moveNav).observe(document.querySelector('.nav'));renderServers();moveNav();
  if(host){host.addEventListener('message',({data})=>{if(data.kind==='navigate'&&['home','servers','logs','settings'].includes(data.page))page(data.page);else if(data.kind==='snapshot')apply(data.data);else if(data.kind==='reply'){const p=pending.get(data.id);if(p){pending.delete(data.id);clearTimeout(p.timer);data.ok?p.resolve(data.data):p.reject(new Error(data.error));}}});request('ready');}else toast('Откройте интерфейс через Kot.exe.');
})();
