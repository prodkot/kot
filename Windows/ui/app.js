(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  // Bundled flag-icons 7.3.2. Never build asset paths from subscription text.
  const flagCodes = new Set('ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bl bm bn bo bq br bs bt bv bw by bz ca cc cd cf cg ch ci ck cl cm cn co cp cr cu cv cw cx cy cz de dg dj dk dm do dz ec ee eg eh er es et eu fi fj fk fm fo fr ga gb gd ge gf gg gh gi gl gm gn gp gq gr gs gt gu gw gy hk hm hn hr ht hu ic id ie il im in io iq ir is it je jm jo jp ke kg kh ki km kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mf mg mh mk ml mm mn mo mp mq mr ms mt mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pc pe pf pg ph pk pl pm pn pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg sh si sj sk sl sm sn so sr ss st sv sx sy sz tc td tf tg th tj tk tl tm tn to tr tt tv tw tz ua ug um un us uy uz va vc ve vg vi vn vu wf ws xk xx ye yt za zm zw'.split(' '));
  const flagPattern = /[\u{1F1E6}-\u{1F1FF}]{2}/gu;
  function flagCode(emoji) {
    return [...emoji].map(c=>String.fromCharCode(c.codePointAt(0)-0x1F1E6+97)).join('');
  }
  function flagImage(code, fallback=()=>document.createTextNode(code.toUpperCase())) {
    const image=document.createElement('img');image.className='country-flag';
    image.src='flags/'+code+'.svg';image.alt=code.toUpperCase();image.draggable=false;
    image.addEventListener('error',()=>image.replaceWith(fallback()),{once:true});
    return image;
  }
  function label(el, value) {
    const text=String(value??'');if(el.dataset.label===text)return;
    const parts=[];let offset=0;
    for(const match of text.matchAll(flagPattern)) {
      const code=flagCode(match[0]);parts.push(document.createTextNode(text.slice(offset,match.index)));
      parts.push(flagCodes.has(code)?flagImage(code):document.createTextNode(code.toUpperCase()));
      offset=match.index+match[0].length;
    }
    parts.push(document.createTextNode(text.slice(offset)));el.replaceChildren(...parts);el.dataset.label=text;
  }
  function serverPresentation(node) {
    let code='';
    const name=String(node?.name??'').replace(/([\u{1F1E6}-\u{1F1FF}]{2})\uFE0F?/gu,(_,emoji)=>{
      const candidate=flagCode(emoji);if(!code&&flagCodes.has(candidate))code=candidate;return '';
    }).replace(/[ \t]{2,}/g,' ').trim();
    // Native node.code is a protocol (TR, SS, VL), not an ISO country code.
    const supplied=String(node?.countryCode||'').toLowerCase();
    return {name:name||'Сервер',code:code||(flagCodes.has(supplied)?supplied:'')};
  }
  function countryFallback() {
    const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');
    icon.classList.add('country-fallback');icon.setAttribute('viewBox','0 0 20 20');
    icon.setAttribute('role','img');icon.setAttribute('aria-label','Без флага');
    const circle=document.createElementNS('http://www.w3.org/2000/svg','circle');
    circle.setAttribute('cx','10');circle.setAttribute('cy','10');circle.setAttribute('r','7');
    circle.setAttribute('fill','#f2f4f1');circle.setAttribute('stroke','#c7cdc8');icon.append(circle);return icon;
  }
  function countryBadge(el, node, automatic=false) {
    const code=serverPresentation(node).code;
    const key=automatic?'auto':code||'circle';
    if(el.dataset.badge===key)return;
    el.replaceChildren(automatic?document.createTextNode('A'):code?flagImage(code,countryFallback):countryFallback());
    el.dataset.badge=key;
  }
  const reduce = matchMedia('(prefers-reduced-motion: reduce)'), motions = new Map();
  const ease = 'cubic-bezier(.22,1,.36,1)';
  function cancelMotion(el){motions.get(el)?.cancel();motions.delete(el);}
  function animate(el, frames, options={}) {
    cancelMotion(el); if(reduce.matches) return null;
    const motion=el.animate(frames,{duration:280,easing:ease,...options}); motions.set(el,motion);
    motion.finished.then(()=>{if(motions.get(el)===motion)motions.delete(el);}).catch(()=>{}); return motion;
  }
  reduce.addEventListener('change',()=>{if(reduce.matches){motions.forEach(a=>a.cancel());motions.clear();}});
  let model={nodes:[],selected:'',state:'idle',settings:{theme:'dark',mode:'tun'}}, current='home', toastTimer, sequence=0, importBusy=false, refreshBusy=false, updateCheckBusy=false, renderedServers='', initialSnapshot=false;
  const pending=new Map(), host=window.chrome?.webview,openDialogs=[];
  function rpc(action,data={}) {
    if(!host) return Promise.reject(new Error('Откройте интерфейс через Kot.exe.'));
    return new Promise((resolve,reject)=>{
      const id=++sequence, timer=setTimeout(()=>{pending.delete(id);reject(new Error('Операция заняла слишком много времени.'));},action==='downloadUpdate'?720000:120000);
      pending.set(id,{resolve,reject,timer}); host.postMessage({id,action,data});
    });
  }
  function raiseToast(){const el=$('toast');if(el.hidden)return;if(el.matches(':popover-open'))el.hidePopover();const owner=openDialogs.findLast(dialog=>dialog.open)||document.body;if(el.parentElement!==owner)owner.append(el);el.showPopover();}
  function toast(text){clearTimeout(toastTimer);$('toast').textContent=text;$('toast').hidden=false;raiseToast();animate($('toast'),[{opacity:0,transform:'translate(-50%,7px)'},{opacity:1,transform:'translate(-50%,0)'}]);toastTimer=setTimeout(()=>{$('toast').hidePopover();$('toast').hidden=true;cancelMotion($('toast'));},4000);}
  let brandClicks=0, meow;
  $('brandButton').addEventListener('click',()=>{
    if(++brandClicks!==443)return;
    brandClicks=0;
    meow??=new Audio('sounds/meow.ogg');meow.volume=.7;meow.currentTime=0;
    meow.play().catch(()=>toast('Не удалось воспроизвести мяу.'));
  });
  async function request(action,data={}) {try{await rpc(action,data);return true;}catch(e){toast(e.message);return false;}}
  function moveNav(){const active=document.querySelector('.nav-button[aria-current=page]');const pill=document.querySelector('.nav-indicator');pill.style.transform=`translateY(${active.offsetTop}px)`;pill.style.height=active.offsetHeight+'px';}
  function page(name){
    if(name===current||!$('page-'+name))return;
    const previous=$('page-'+current);for(const el of motions.keys())if(el===previous||previous.contains(el))cancelMotion(el);current=name;
    closeSubscriptions();
    document.querySelectorAll('.view').forEach(el=>el.hidden=el.id!=='page-'+name);
    document.querySelectorAll('[data-page]').forEach(el=>{if(el.dataset.page===(el.classList.contains('nav-button')&&['statistics','diagnostics','logs'].includes(name)?'settings':name))el.setAttribute('aria-current','page');else el.removeAttribute('aria-current');});
    const main=document.querySelector('.main');main.dataset.view=name;main.scrollTop=0; moveNav(); request('view',{page:name}); if(name==='logs')renderLogs(); if(name==='servers')renderServers(); if(name==='statistics'||name==='home')renderStatistics();clientTools.render();serviceTools.render();
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
    $('connectionError').title=$('connectionError').textContent;
    if(state!==previous){cancelMotion($('powerIcon'));if(current==='home'){animate($('stateLabel'),[{opacity:0,transform:'translateY(3px)'},{opacity:1,transform:'translateY(0)'}],{duration:220});animate($('powerLabel'),[{opacity:0},{opacity:1}],{duration:200});if(state==='connected')animate($('powerIcon'),[{transform:'scale(.82)'},{transform:'scale(1.12)',offset:.6},{transform:'scale(1)'}],{duration:440});}}
  }
  function apply(data){
    const old=model.state;model=data;
    document.body.classList.toggle('theme-light',data.settings.theme==='light');
    document.querySelectorAll('[data-theme]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.theme===data.settings.theme));document.querySelector('.theme-control').dataset.selected=data.settings.theme==='dark'?'0':'1';
    document.querySelectorAll('[data-mode]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.mode===data.settings.mode));document.querySelector('.mode').dataset.selected=data.settings.mode==='tun'?'0':'1';
    document.querySelectorAll('[data-setting]').forEach(b=>b.setAttribute('aria-checked',!!data.settings[b.dataset.setting]));
    label($('subscriptionName'),data.hasSubscription?data.name:'Подписка');label($('listName'),data.hasSubscription?data.name:'Подписка');
    const selected=data.nodes.find(n=>n.id===data.selected);
    const automatic=data.selected==='auto', actual=data.nodes.find(n=>n.id===data.automaticNode);
    label($('selectedName'),automatic?'Авто':selected?serverPresentation(selected).name:'Добавить подписку');countryBadge($('selectedCountry'),selected,automatic);label($('selectedCity'),automatic?actual?serverPresentation(actual).name:'Быстрый доступный сервер':selected?.protocol.toUpperCase()||'');
    $('autoServer').hidden=!data.nodes.length;$('autoServer').setAttribute('aria-pressed',automatic);$('autoDescription').textContent=data.favorites?.length?'Из избранного':'Быстрый доступный сервер';
    renderSubscriptions();renderRouting();
    $('backgroundError').textContent=data.backgroundError||'';$('backgroundError').hidden=!data.backgroundError;
    $('refreshButton').disabled=refreshBusy||!data.hasSubscription||!!data.localSubscription;$('removeSubscription').hidden=!data.hasSubscription;
    if(!$('advancedDialog').open)$('bypassDomains').value=data.settings.bypass||'';
    $('skippedDetails').hidden=!data.warnings?.length;$('skippedNodes').replaceChildren(...(data.warnings||[]).slice(0,50).map(w=>{const p=document.createElement('p');p.textContent=w;return p;}));
    $('titleVersion').textContent=data.version||'';$('version').textContent='kot. '+data.version+' · sing-box 1.14.2'+(data.updated?' · '+data.updated:'');
    const ping=data.ping||{};
    $('pingAll').disabled=!data.nodes.length||!!ping.busy;$('pingAll').dataset.busy=!!ping.busy;
    $('pingProgress').hidden=!ping.busy;$('pingProgress').textContent=(ping.done||0)+' / '+(ping.total||0);$('cancelPing').hidden=!ping.busy;
    $('pingError').hidden=!ping.error;$('pingError').textContent=ping.error||'';
    clientTools.render();serviceTools.render();renderTraffic(); renderStatistics(true); renderUpdate(); if(current==='logs'&&!logsPaused){logSnapshot={telemetry:data.telemetry,journal:data.journal,state:data.state};renderLogs();}
    setState(data.state,old);const renderKey=JSON.stringify([data.nodes,data.selected,data.favorites,data.ping?.settings?.sort,data.ping?.busy]);if(renderKey!==renderedServers){renderedServers=renderKey;renderServers();}
    if(!initialSnapshot){initialSnapshot=true;document.fonts.ready.then(()=>requestAnimationFrame(()=>{getComputedStyle(document.body).backgroundColor;$('startupScreen').hidden=true;document.querySelector('.main').inert=false;document.querySelector('.sidebar').inert=false;document.body.classList.remove('booting');request('uiReady');}));}
  }
  function renderServers(){
    const query=$('serverSearch').value.trim().toLocaleLowerCase('ru'),visible=model.nodes.filter(n=>(n.name+' '+n.protocol).toLocaleLowerCase('ru').includes(query));
    const active=document.activeElement, focusId=active?.closest('[data-node]')?.dataset.node, focusPing=active?.classList.contains('ping-node'), focusFavorite=active?.classList.contains('favorite-node'),focusQr=active?.classList.contains('qr-node');
    $('serverList').replaceChildren();$('noServers').hidden=!!visible.length;$('noServers').textContent=model.nodes.length?'Ничего не найдено':'Добавьте подписку';$('serverCount').textContent=visible.length;
    const sort=model.ping?.settings?.sort||'none';
    if(sort==='name')visible.sort((a,b)=>serverPresentation(a).name.localeCompare(serverPresentation(b).name,'ru'));
    if(sort==='ping')visible.sort((a,b)=>(a.ping?.ms??Infinity)-(b.ping?.ms??Infinity));
    visible.forEach(item=>{
      const presentation=serverPresentation(item);
      const row=document.createElement('div');row.className='server-row';row.dataset.node=item.id;
      const favorite=document.createElement('button');favorite.className='favorite-node';favorite.textContent=model.favorites?.includes(item.id)?'★':'☆';favorite.setAttribute('aria-label','Избранное: '+presentation.name);favorite.setAttribute('aria-pressed',!!model.favorites?.includes(item.id));favorite.addEventListener('click',()=>request('favorite',{id:item.id}));
      const button=document.createElement('button');button.className='server-card';button.setAttribute('aria-pressed',item.id===model.selected);button.setAttribute('aria-label','Выбрать '+presentation.name);
      const country=document.createElement('span');country.className='country';countryBadge(country,item);
      const copy=document.createElement('span');copy.className='server-copy';const title=document.createElement('strong');title.textContent=presentation.name;const city=document.createElement('small');city.textContent=item.protocol.toUpperCase();copy.append(title,city);button.append(country,copy);
      if(item.id===model.selected){const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');icon.classList.add('selected-mark');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#check');icon.append(use);button.append(icon);}
      button.addEventListener('click',async()=>{button.disabled=true;try{await rpc('select',{id:item.id});page('home');}catch(e){toast(e.message);}finally{button.disabled=false;}});
      const ping=document.createElement('button');ping.className='ping-node';ping.disabled=!!model.ping?.busy;ping.setAttribute('aria-label','Пинг '+presentation.name);
      const result=item.ping;ping.dataset.status=result?.status||'idle';
      const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#pulse');icon.append(use);
      const value=document.createElement('span');value.textContent=result?.status==='ok'?result.ms+' мс':result?.status==='checking'?'…':result?.status==='queued'?'В очереди':result?.status==='error'?'Ошибка':result?.status==='unsupported'?'UDP':result?.status==='cancelled'?'Отмена':'Пинг';
      ping.title=result?.status==='ok'?result.mode.toUpperCase()+': '+result.successes+'/'+result.attempts+' попыток'+(result.error?'; '+result.error:''):result?.error||'Проверить сервер';
      ping.append(icon,value);ping.addEventListener('click',()=>request('pingNode',{id:item.id}));row.append(button,favorite,ping);$('serverList').append(row);
      const qr=document.createElement('button');qr.type='button';qr.className='qr-node';qr.textContent='QR';qr.setAttribute('aria-label','QR '+presentation.name);qr.addEventListener('click',()=>clientTools.share(item.id));row.append(qr);
      if(focusId===item.id)(focusQr?qr:focusPing?ping:focusFavorite?favorite:button).focus({preventScroll:true});
    });
    if(focusId&&!visible.some(item=>item.id===focusId))$('serverSearch').focus({preventScroll:true});
  }

  let logTab='connections',logsPaused=false,logSnapshot=null,connectionsKey='';
  function bytes(n,rate=false){if(n===null||n===undefined||!Number.isFinite(n))return 'Нет данных';const units=['Б','КБ','МБ','ГБ','ТБ'];let i=0;while(n>=1024&&i<units.length-1){n/=1024;i++;}return new Intl.NumberFormat('ru-RU',{maximumFractionDigits:i===0?0:1}).format(n)+' '+units[i]+(rate?'/с':'');}
  function renderTraffic(){const t=model.telemetry||{},on=model.state==='connected';$('downloadRate').textContent=on?bytes(t.available?t.downloadRate:null,true):'0 Б/с';$('uploadRate').textContent=on?bytes(t.available?t.uploadRate:null,true):'0 Б/с';$('downloadRate').title='За сеанс: '+bytes(t.downloadTotal||0);$('uploadRate').title='За сеанс: '+bytes(t.uploadTotal||0);}
  let statisticsPeriod='session',statisticsAnchor=0,statisticsElapsed=0;
  function duration(seconds){
    if(!Number.isFinite(seconds)||seconds<0)return 'Нет данных';
    const value=Math.floor(seconds),days=Math.floor(value/86400),pad=n=>String(n).padStart(2,'0');
    return (days?days+' д ':'')+pad(Math.floor(value/3600)%24)+':'+pad(Math.floor(value/60)%60)+':'+pad(value%60);
  }
  function renderSessionClock(snapshot=false){
    const s=model.statistics,running=!!s?.running&&model.state==='connected';
    if(snapshot){statisticsAnchor=performance.now();statisticsElapsed=Number.isFinite(s?.elapsedSeconds)?Math.max(0,s.elapsedSeconds):0;}
    // Only a confirmed host connection runs the clock. Stop extrapolating if the bridge stalls.
    const extra=running?Math.min(5,Math.max(0,(performance.now()-statisticsAnchor)/1000)):0;
    $('connectionTime').hidden=!s?.started;
    const elapsed=duration(statisticsElapsed+extra);
    $('connectionTime').textContent=elapsed;
    $('homeSessionTime').textContent=elapsed;$('homeSession').dataset.running=String(running&&!!s?.started);
    $('connectionTime').title=running?'Время текущего подключения':'Длительность последнего сеанса';
    $('connectionTime').setAttribute('aria-label',$('connectionTime').title);
    return {s,running,extra};
  }
  function renderStatistics(snapshot=false){
    const {s,running,extra}=renderSessionClock(snapshot),t=s?.[statisticsPeriod];
    $('statisticsSession').dataset.running=String(running);
    $('statisticsConnectionLabel').textContent=running?'Текущее подключение':s?.started?'Последний сеанс':'Ещё не подключались';
    const sessionDate=s?.started?new Date(s.started).toLocaleString('ru-RU'):'';
    $('statisticsConnectionInfo').textContent=sessionDate?'Начало: '+sessionDate:'Таймер появится после подключения';
    $('statisticsDownload').textContent=t?bytes(t.download):'Нет данных';
    $('statisticsUpload').textContent=t?bytes(t.upload):'Нет данных';
    $('statisticsDuration').textContent=t?duration(t.connectedSeconds+(running?extra:0)):'Нет данных';
    const total=t&&Number.isFinite(t.download)&&Number.isFinite(t.upload)?bytes(t.download+t.upload):'Нет данных';
    $('statisticsSummary').textContent='Всего: '+total+' · Сеансов: '+(Number.isFinite(t?.sessions)?new Intl.NumberFormat('ru-RU').format(t.sessions):'Нет данных')+(statisticsPeriod==='session'&&!running&&s?.started?' · Последний сеанс':'');
    $('statistics').title=s?.firstRecorded?'Учёт с '+new Date(s.firstRecorded).toLocaleString('ru-RU'):'';
    $('statisticsError').textContent=s?.error||s?.warning||'';$('statisticsError').hidden=!$('statisticsError').textContent;
    clientTools.renderStatistics(statisticsPeriod);
    document.querySelectorAll('[data-statistics-period]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.statisticsPeriod===statisticsPeriod));
  }
  const statisticsButtons=[...document.querySelectorAll('[data-statistics-period]')];
  statisticsButtons.forEach((button,index)=>{
    button.addEventListener('click',()=>{statisticsPeriod=button.dataset.statisticsPeriod;renderStatistics();});
    button.addEventListener('keydown',event=>{
      const key=event.key;if(!['ArrowLeft','ArrowRight','Home','End'].includes(key))return;
      event.preventDefault();const next=key==='Home'?0:key==='End'?statisticsButtons.length-1:(index+(key==='ArrowRight'?1:-1)+statisticsButtons.length)%statisticsButtons.length;
      statisticsButtons[next].focus();statisticsButtons[next].click();
    });
  });
  setInterval(()=>{if(current==='statistics')renderStatistics();else if(current==='home')renderSessionClock();},250);
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
      const key=JSON.stringify(rows);if(key!==connectionsKey){const focused=document.activeElement?.closest('#connectionList details')?.dataset.connection,opened=new Set([...$('connectionList').querySelectorAll('details[open]')].map(e=>e.dataset.connection));$('connectionList').replaceChildren(...rows.map(c=>{
        const card=document.createElement('details');card.className='connection-card';card.dataset.connection=c.id;card.open=opened.has(c.id);const summary=document.createElement('summary'),top=document.createElement('div');top.className='connection-top';
        const destination=document.createElement('span');destination.className='connection-dest';destination.textContent=c.destination||'Адрес неизвестен';const network=document.createElement('span');network.className='connection-network';network.textContent=(c.network||'').toUpperCase();top.append(destination,network);
        const meta=document.createElement('div');meta.className='connection-meta';const route=document.createElement('span');route.className='connection-route';label(route,(c.process?c.process+' · ':'')+(c.chains?.join(' → ')||'Маршрут неизвестен'));const total=document.createElement('span');total.className='connection-bytes';total.textContent='↓ '+bytes(c.download)+'  ↑ '+bytes(c.upload);meta.append(route,total);summary.append(top,meta);
        const detail=document.createElement('div');detail.className='connection-detail';detail.textContent='Вход: '+(c.inbound||'Не определён')+'\nПравило: '+(c.rule||'final')+(c.start?'\nНачало: '+new Date(c.start).toLocaleTimeString('ru-RU'):'');detail.style.whiteSpace='pre-line';card.append(summary,detail);return card;
      }));connectionsKey=key;if(focused){const card=[...$('connectionList').children].find(e=>e.dataset.connection===focused);(card?.querySelector('summary')||$('logSearch')).focus({preventScroll:true});}}
      $('logStatus').textContent=logsPaused?'На паузе':t.error||((t.count||0)+' активных'+((t.count||0)>300?' · показаны первые 300':''));$('logEmpty').hidden=!!rows.length;$('logEmpty').textContent=query?'Ничего не найдено':snap.state==='connected'?'Нет активных соединений':'Подключитесь к серверу';
    }
  }
  new ResizeObserver(()=>{
    $('updateNotice').closest('main').style.setProperty('--update-notice-height',$('updateNotice').getBoundingClientRect().height+'px');
  }).observe($('updateNotice'));
  let updateDownloadBusy=false, updateDeferBusy=false;
  function renderUpdate(){const u=model.updates||{},busy=updateCheckBusy||updateDownloadBusy||!!u.busy;$('updateNotice').hidden=!u.availableVersion||u.availableVersion===u.dismissedVersion||busy||$('onlineUpdateDialog').open;
    $('updateNoticeTitle').textContent='Доступно обновление '+(u.availableVersion||'');
    $('acceptUpdate').disabled=busy||updateDeferBusy;$('deferUpdate').disabled=busy||updateDeferBusy;
    $('downloadUpdate').disabled=busy;
    $('updateBadge').hidden=!u.availableVersion;$('updateStatus').textContent=(u.status||'Проверка обновлений')+(u.bytes?' · '+bytes(u.bytes):'')+(u.checkedAt&&!u.busy?' · '+u.checkedAt:'');$('updateError').textContent=u.error||'';$('downloadUpdate').hidden=!u.availableVersion||busy;$('downloadUpdate').textContent='Обновить до '+(u.availableVersion||'');$('cancelUpdate').hidden=!u.busy;$('onlineUpdateButton').disabled=busy;$('updateProgress').hidden=!busy;if(u.bytes&&u.size){$('updateProgress').max=u.size;$('updateProgress').value=u.bytes;}else $('updateProgress').removeAttribute('value');}
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
  $('onlineUpdateDialog').addEventListener('close',renderUpdate);
  async function acceptUpdate(){
    const version=model.updates?.availableVersion;
    if(!version||updateCheckBusy||updateDownloadBusy||model.updates?.busy||updateDeferBusy)return;
    updateDownloadBusy=true;openDialog($('onlineUpdateDialog'));renderUpdate();
    try{await rpc('downloadUpdate',{version});}catch(e){toast(e.message);}
    finally{updateDownloadBusy=false;renderUpdate();}
  }
  $('downloadUpdate').addEventListener('click',acceptUpdate);
  $('acceptUpdate').addEventListener('click',acceptUpdate);
  $('deferUpdate').addEventListener('click',async()=>{
    const version=model.updates?.availableVersion;
    if(!version||updateDeferBusy||updateDownloadBusy||model.updates?.busy)return;
    updateDeferBusy=true;renderUpdate();
    try{await rpc('deferUpdate',{version});}catch(e){toast(e.message);}
    finally{updateDeferBusy=false;renderUpdate();}
  });

  const subscriptionMenu=$('subscriptionMenu'), subscriptionSelect=$('subscriptionSelect');
  let switchingSubscription=false;
  function closeSubscriptions(){if(subscriptionMenu.matches(':popover-open'))subscriptionMenu.hidePopover();}
  function positionSubscriptions(){
    const r=subscriptionSelect.getBoundingClientRect(),edge=12,gap=8,width=Math.min(340,innerWidth-edge*2);
    subscriptionMenu.style.width=width+'px';
    subscriptionMenu.style.left=Math.max(edge,Math.min(r.left,innerWidth-width-edge))+'px';
    const below=innerHeight-r.bottom-gap-edge,above=r.top-gap-edge;
    const upwards=below<200&&above>below;
    subscriptionMenu.style.maxHeight=Math.max(0,upwards?above:below)+'px';
    subscriptionMenu.style.top=upwards?'auto':r.bottom+gap+'px';
    subscriptionMenu.style.bottom=upwards?innerHeight-r.top+gap+'px':'auto';
  }
  function renderSubscriptions(){
    const subscriptions=model.subscriptions||[],active=subscriptions.find(s=>s.id===model.activeSubscription);
    label($('subscriptionLabel'),active?.name||'Добавить подписку');
    subscriptionSelect.dataset.subscription=active?.id||'';
    subscriptionSelect.title=active?.name||'Добавить подписку';
    subscriptionSelect.disabled=switchingSubscription;
    const key=JSON.stringify(subscriptions.map(s=>[s.id,s.name,s.count]));
    if($('subscriptionOptions').dataset.key!==key){
      const focused=document.activeElement?.dataset.subscription;
      $('subscriptionOptions').replaceChildren(...subscriptions.map(s=>{
        const option=document.createElement('button');option.type='button';option.className='profile-option';
        option.setAttribute('role','option');option.tabIndex=-1;option.dataset.subscription=s.id;
        const icon=document.createElementNS('http://www.w3.org/2000/svg','svg'),use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#globe');icon.append(use);icon.classList.add('profile-icon');icon.setAttribute('aria-hidden','true');
        const copy=document.createElement('span');copy.className='profile-copy';const name=document.createElement('strong');label(name,s.name);
        const count=document.createElement('small'),n=Math.max(0,Math.trunc(Number(s.count)||0));
        count.textContent=n+' '+(n%10===1&&n%100!==11?'сервер':n%10>=2&&n%10<=4&&(n%100<12||n%100>14)?'сервера':'серверов');copy.append(name,count);
        const check=document.createElementNS('http://www.w3.org/2000/svg','svg'),mark=document.createElementNS('http://www.w3.org/2000/svg','use');mark.setAttribute('href','#check');check.append(mark);check.classList.add('profile-check');check.setAttribute('aria-hidden','true');
        option.append(icon,copy,check);option.addEventListener('click',()=>chooseSubscription(s.id));return option;
      }));
      $('subscriptionOptions').dataset.key=key;
      if(focused&&subscriptionMenu.matches(':popover-open'))[...$('subscriptionOptions').children].find(b=>b.dataset.subscription===focused)?.focus({preventScroll:true});
    }
    for(const option of $('subscriptionOptions').children){option.setAttribute('aria-selected',option.dataset.subscription===model.activeSubscription);option.disabled=switchingSubscription;}
    $('subscriptionEmpty').hidden=!!subscriptions.length;
    if(subscriptionMenu.matches(':popover-open'))positionSubscriptions();
  }
  async function chooseSubscription(id){
    if(switchingSubscription)return;
    closeSubscriptions();subscriptionSelect.focus({preventScroll:true});
    if(id===model.activeSubscription)return;
    switchingSubscription=true;renderSubscriptions();
    try{await request('switchSubscription',{id});}finally{switchingSubscription=false;renderSubscriptions();if(current==='servers'&&document.activeElement===document.body)subscriptionSelect.focus({preventScroll:true});}
  }
  function openSubscriptions(last=false){
    positionSubscriptions();subscriptionMenu.showPopover();
    const options=[...$('subscriptionOptions').children];
    const target=last?options.at(-1):options.find(b=>b.getAttribute('aria-selected')==='true')||options[0];
    (target||$('addSubscriptionOption')).focus({preventScroll:true});
  }
  subscriptionSelect.addEventListener('click',e=>{e.preventDefault();subscriptionMenu.matches(':popover-open')?closeSubscriptions():openSubscriptions();});
  subscriptionSelect.addEventListener('keydown',e=>{if(e.key==='ArrowDown'||e.key==='ArrowUp'){e.preventDefault();openSubscriptions(e.key==='ArrowUp');}});
  subscriptionMenu.addEventListener('beforetoggle',e=>{subscriptionSelect.setAttribute('aria-expanded',e.newState==='open');});
  subscriptionMenu.addEventListener('toggle',e=>{if(e.newState==='open')animate(subscriptionMenu,[{opacity:0,transform:'translateY(-4px)'},{opacity:1,transform:'translateY(0)'}],{duration:160});});
  subscriptionMenu.addEventListener('keydown',e=>{
    if(e.key==='Escape'){e.preventDefault();closeSubscriptions();subscriptionSelect.focus({preventScroll:true});return;}
    if(!['ArrowDown','ArrowUp','Home','End'].includes(e.key))return;
    const options=[...$('subscriptionOptions').children];if(!options.length)return;
    e.preventDefault();const index=options.indexOf(document.activeElement);
    options[e.key==='Home'?0:e.key==='End'?options.length-1:e.key==='ArrowDown'?(index+1)%options.length:(index-1+options.length)%options.length].focus({preventScroll:true});
  });
  subscriptionMenu.addEventListener('focusout',e=>{if(e.relatedTarget){if(!subscriptionMenu.contains(e.relatedTarget)&&e.relatedTarget!==subscriptionSelect)closeSubscriptions();}else setTimeout(()=>{if(!subscriptionMenu.contains(document.activeElement)&&document.activeElement!==subscriptionSelect)closeSubscriptions();},0);});
  $('addSubscriptionOption').addEventListener('click',()=>{closeSubscriptions();openAdd();});
  addEventListener('resize',()=>{if(subscriptionMenu.matches(':popover-open'))positionSubscriptions();});
  document.querySelector('.main').addEventListener('scroll',closeSubscriptions,{passive:true});
  $('autoServer').addEventListener('click',async()=>{if(await request('select',{id:'auto'}))page('home');});
  document.querySelectorAll('[data-page]').forEach(el=>el.addEventListener('click',()=>page(el.dataset.page)));
  $('homeServer').addEventListener('click',()=>model.nodes.length?page('servers'):openAdd());$('serverSearch').addEventListener('input',renderServers);
  $('powerButton').addEventListener('click',()=>request('toggle'));
  document.querySelectorAll('[data-mode]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:'mode',value:el.dataset.mode})));
  document.querySelectorAll('[data-theme]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:'theme',value:el.dataset.theme})));
  document.querySelectorAll('[data-setting]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:el.dataset.setting,value:el.getAttribute('aria-checked')!=='true'})));
  function clearLink(){$('profileLink').value='';$('addError').textContent='';}
  const dialogFocus=new WeakMap();
  function rememberFocus(){
    const el=document.activeElement,node=el?.closest('[data-node]')?.dataset.node,connection=el?.closest('[data-connection]')?.dataset.connection;
    const owner=el?.closest('dialog');if(owner?.dataset.closing)return dialogFocus.get(owner)||(()=>null);
    const control=['qr-node','ping-node','favorite-node','server-card'].find(name=>el?.classList.contains(name));
    return ()=>el?.isConnected?el:node?[...$('serverList').children].find(row=>row.dataset.node===node)?.querySelector('.'+control)||$('serverSearch'):connection?[...$('connectionList').children].find(row=>row.dataset.connection===connection)?.querySelector('summary')||$('logSearch'):null;
  }
  function openDialog(dialog){if(dialog.open)return;dialogFocus.set(dialog,rememberFocus());dialog.showModal();openDialogs.push(dialog);raiseToast();animate(dialog,[{opacity:0,transform:'translateY(12px) scale(.97)'},{opacity:1,transform:'translateY(0) scale(1)'}],{duration:280});}
  function closeDialog(dialog){
    if(!dialog.open||dialog.dataset.closing)return;dialog.dataset.closing='true';if(dialog===$('advancedDialog'))clearHwid();if(dialog===$('addDialog')){if(importBusy)request('cancelImport');clearLink();}
    const style=getComputedStyle(dialog),motion=animate(dialog,[{opacity:style.opacity,transform:style.transform},{opacity:0,transform:'translateY(6px) scale(.985)'}],{duration:130});
    const finish=()=>{dialog.close();openDialogs.splice(openDialogs.indexOf(dialog),1);delete dialog.dataset.closing;if(!document.querySelector('dialog:modal'))dialogFocus.get(dialog)?.()?.focus({preventScroll:true});dialogFocus.delete(dialog);raiseToast();};if(motion)motion.finished.then(finish).catch(finish);else finish();
  }
  function openAdd(){clearLink();$('replaceNote').hidden=!model.hasSubscription;openDialog($('addDialog'));}
  document.querySelectorAll('[data-open-add]').forEach(el=>el.addEventListener('click',openAdd));$('cancelAdd').addEventListener('click',()=>closeDialog($('addDialog')));$('addDialog').addEventListener('close',clearLink);
  $('addForm').addEventListener('submit',async event=>{
    event.preventDefault();if($('addDialog').dataset.closing)return;
    const url=$('profileLink').value.trim();if(!url){$('addError').textContent='Вставьте ссылку или выберите файл / картинку QR.';return;}
    if(/^http:\/\//i.test(url)){$('addError').textContent='Нужна HTTPS-ссылка на подписку.';return;}
    const remote=/^https:\/\//i.test(url);
    const submit=$('addForm').querySelector('[type=submit]');if(submit.disabled)return;submit.disabled=true;importBusy=true;submit.textContent='Загрузка';$('addError').textContent='';
    try{await rpc(remote?'import':'importText',remote?{url,name:$('profileName').value.trim()}:{text:url,name:$('profileName').value.trim()});closeDialog($('addDialog'));page('servers');toast(model.warnings?.length?'Серверы добавлены. Пропущено: '+model.warnings.length:'Серверы добавлены');}
    catch(e){$('addError').textContent=e.message;}
    finally{submit.disabled=false;importBusy=false;submit.textContent='Добавить';}
  });
  $('refreshButton').addEventListener('click',async()=>{if(refreshBusy)return;refreshBusy=true;const b=$('refreshButton');b.disabled=true;try{await rpc('refresh');toast(model.warnings?.length?'Обновлено. Пропущено: '+model.warnings.length:'Подписка обновлена');}catch(e){toast(e.message);}finally{refreshBusy=false;b.disabled=!model.hasSubscription||!!model.localSubscription;}});
  let deviceAttempt=0;
  function clearHwid(){deviceAttempt++;$('hwidValue').value='';$('deviceName').value='';$('copyHwid').disabled=true;}
  function closeDevice(){clearHwid();$('deviceDialog').hidden=true;$('advancedContent').hidden=false;$('advancedTitle').textContent='Дополнительно';$('advancedDialog').scrollTop=0;$('hwidButton').focus({preventScroll:true});}
  $('hwidButton').addEventListener('click',async()=>{
    const panel=$('deviceDialog'),dialog=$('advancedDialog');if(!dialog.open||dialog.dataset.closing||!panel.hidden)return;
    clearHwid();const attempt=deviceAttempt;$('hwidError').textContent='';$('advancedContent').hidden=true;panel.hidden=false;$('advancedTitle').textContent='HWID';dialog.scrollTop=0;$('closeDevice').focus({preventScroll:true});
    try{const device=await rpc('readHwid');if(attempt!==deviceAttempt||panel.hidden||!dialog.open||dialog.dataset.closing)return;
      $('hwidValue').value=device.hwid;$('deviceName').value=device.name;$('copyHwid').disabled=false;
    }catch(e){if(attempt===deviceAttempt&&!panel.hidden&&dialog.open&&!dialog.dataset.closing)$('hwidError').textContent=e.message;}
  });
  $('closeDevice').addEventListener('click',closeDevice);$('advancedDialog').addEventListener('close',()=>{clearHwid();$('deviceDialog').hidden=true;$('advancedContent').hidden=false;$('advancedTitle').textContent='Дополнительно';});
  $('copyHwid').addEventListener('click',async()=>{
    const b=$('copyHwid'),attempt=deviceAttempt;b.disabled=true;$('hwidError').textContent='';
    try{await rpc('copyHwid');toast('HWID скопирован');}catch(e){if(attempt===deviceAttempt)$('hwidError').textContent=e.message;}
    finally{if(attempt===deviceAttempt&&!$('deviceDialog').hidden&&$('advancedDialog').open&&!$('advancedDialog').dataset.closing)b.disabled=false;}
  });
  $('advancedButton').addEventListener('click',()=>{$('bypassDomains').value=model.settings.bypass||'';$('advancedError').textContent='';openDialog($('advancedDialog'));});$('closeAdvanced').addEventListener('click',()=>closeDialog($('advancedDialog')));
  let routingDraftId=null,routingBusy=false,routingDirty=false,routingRemovingId='';
  let geoIpBusy=false,geoIpDirty=false,geoIpRequestError='';
  const routingLists=['directDomains','proxyDomains','blockDomains','directIps','proxyIps','blockIps','directApps','proxyApps','blockApps','directCountries','proxyCountries','blockCountries'];
  const routingField=key=>$('routing'+key[0].toUpperCase()+key.slice(1));
  function routingSaved(){return (model.routing?.profiles||[]).find(p=>p.id===routingDraftId);}
  function routingTab(name,focus=false){
    for(const button of document.querySelectorAll('[data-routing-tab]')){const selected=button.dataset.routingTab===name;button.setAttribute('aria-selected',selected);button.tabIndex=selected?0:-1;if(selected&&focus)button.focus();}
    for(const panel of document.querySelectorAll('[data-routing-panel]'))panel.hidden=panel.dataset.routingPanel!==name;
  }
  function renderRouting(){
    const routing=model.routing||{active:'',profiles:[]},saved=routingSaved(),active=routing.profiles.find(p=>p.id===routing.active);
    $('routingSummary').textContent='Маршрут: '+(active?.rules.name||'по умолчанию');
    $('routingActive').replaceChildren(new Option('По умолчанию',''),...routing.profiles.map(p=>new Option((p.sourceUrl?'Обновляемый · ':'')+p.rules.name,p.id)));
    $('routingActive').value=routing.active||'';$('routingActive').disabled=routingBusy||routingDirty;
    $('routingFields').disabled=routingBusy;
    for(const id of ['routingNew','routingImport'])$(id).disabled=routingBusy||routingDirty;
    $('routingExport').disabled=routingBusy||routingDirty||!saved;$('routingRefresh').disabled=routingBusy||routingDirty||!saved?.sourceUrl;
    $('routingRemove').hidden=!saved;$('routingRemove').disabled=routingBusy||routingDirty;
    $('geoIpButton').disabled=routingBusy;
    $('routingForm').querySelector('[type=submit]').disabled=routingBusy;
    $('routingReset').disabled=routingBusy;
    const status=saved?.updated?'Обновлено: '+new Date(saved.updated).toLocaleString('ru-RU'):'';
    $('routingStatus').textContent=[routingDirty?'Есть несохранённые изменения':status,routing.errors?.[saved?.id]].filter(Boolean).join('\n');
    renderGeoIp();
  }
  function loadRouting(id){
    routingDraftId=id;routingDirty=false;$('routingError').textContent='';$('routingGeneralError').textContent='';
    $('routingForm').hidden=id===null;$('routingEmpty').hidden=id!==null;
    if(id!==null){
      const saved=routingSaved(),rules=saved?.rules||{};
      $('routingName').value=rules.name||'Мой профиль';$('routingDefault').value=rules.defaultAction||'proxy';
      for(const key of routingLists)routingField(key).value=(rules[key]||[]).join('\n');
      $('routingDirectDns').value=rules.directDns||'cloudflare';$('routingRemoteDns').value=rules.remoteDns||'cloudflare';
      $('routingSource').value=saved?.sourceUrl||'';$('routingHours').value=saved?.refreshHours??24;$('routingSourceDetails').open=!!saved?.sourceUrl;
    }
    routingTab('domains');renderRouting();
  }
  function openRouting(){loadRouting(model.routing?.active||null);openDialog($('routingDialog'));}
  for(const id of ['routingButton','homeRoutingButton'])$(id).addEventListener('click',openRouting);
  $('closeRouting').addEventListener('click',()=>closeDialog($('routingDialog')));
  $('routingNew').addEventListener('click',()=>{loadRouting('');$('routingName').focus();});
  $('routingReset').addEventListener('click',()=>loadRouting(model.routing?.active||null));
  $('routingForm').addEventListener('input',()=>{routingDirty=true;renderRouting();});
  $('routingForm').addEventListener('change',()=>{routingDirty=true;renderRouting();});
  document.querySelectorAll('[data-routing-tab]').forEach(button=>{
    button.addEventListener('click',()=>routingTab(button.dataset.routingTab));
    button.addEventListener('keydown',event=>{const names=['domains','ips','apps','countries'],index=names.indexOf(button.dataset.routingTab);let target;if(event.key==='ArrowRight')target=(index+1)%names.length;else if(event.key==='ArrowLeft')target=(index+names.length-1)%names.length;else if(event.key==='Home')target=0;else if(event.key==='End')target=names.length-1;else return;event.preventDefault();routingTab(names[target],true);});
  });
  $('routingActive').addEventListener('change',async()=>{
    if(routingBusy)return;const id=$('routingActive').value;routingBusy=true;renderRouting();
    try{await rpc('routingSelect',{id});loadRouting(id||null);}catch(e){$('routingGeneralError').textContent=e.message;}
    finally{routingBusy=false;renderRouting();}
  });
  function renderGeoIp(){
    const data=model.geoIp||{countries:[],refreshHours:24},busy=geoIpBusy||data.busy;
    if(!geoIpDirty&&!geoIpBusy)$('geoIpHours').value=data.refreshHours??24;
    $('geoIpHours').disabled=!!busy;
    $('geoIpForm').querySelector('[type=submit]').disabled=!!busy;
    $('geoIpRefresh').disabled=!!busy||!data.countries.length;
    $('geoIpRefresh').textContent=busy?'Обновление…':'Обновить сейчас';
    $('geoIpCountries').replaceChildren(...data.countries.map(country=>{
      const row=document.createElement('div');row.className='geoip-country';
      const name=document.createElement('strong');name.textContent=country.code.toUpperCase();
      const status=document.createElement('span');status.textContent=country.ready?'Проверено: '+new Date(country.updated).toLocaleString('ru-RU'):'База ещё не загружена';
      row.append(name,status);return row;
    }));
    if(!data.countries.length)$('geoIpCountries').textContent='Добавьте страны во вкладке «Страны» и сохраните профиль.';
    $('geoIpError').textContent=geoIpRequestError||data.error||'';
  }
  $('geoIpButton').addEventListener('click',()=>{geoIpDirty=false;geoIpRequestError='';renderGeoIp();openDialog($('geoIpDialog'));});
  $('closeGeoIp').addEventListener('click',()=>closeDialog($('geoIpDialog')));
  $('geoIpHours').addEventListener('input',()=>geoIpDirty=true);
  $('geoIpForm').addEventListener('submit',async event=>{
    event.preventDefault();if(geoIpBusy||model.geoIp?.busy)return;
    const refreshHours=Number($('geoIpHours').value);geoIpBusy=true;geoIpRequestError='';renderGeoIp();
    try{await rpc('geoIpSettings',{refreshHours});geoIpDirty=false;toast('Интервал обновления GeoIP сохранён');}
    catch(e){geoIpRequestError=e.message;}
    finally{geoIpBusy=false;renderGeoIp();}
  });
  $('geoIpRefresh').addEventListener('click',async()=>{
    if(geoIpBusy||model.geoIp?.busy)return;geoIpBusy=true;geoIpRequestError='';renderGeoIp();
    try{await rpc('geoIpRefresh');geoIpRequestError='';toast('Базы GeoIP обновлены');}
    catch(e){geoIpRequestError=model.geoIp?.error?'':e.message;}
    finally{geoIpBusy=false;renderGeoIp();}
  });
  $('routingForm').addEventListener('submit',async event=>{
    event.preventDefault();if(routingBusy)return;
    const rules={version:1,name:$('routingName').value.trim(),defaultAction:$('routingDefault').value,directDns:$('routingDirectDns').value,remoteDns:$('routingRemoteDns').value};
    for(const key of routingLists)rules[key]=routingField(key).value.split(/[\r\n,;]+/).map(v=>v.trim()).filter(Boolean);
    routingBusy=true;$('routingError').textContent='';renderRouting();
    try{await rpc('routingSave',{id:routingDraftId||'',rules,sourceUrl:$('routingSource').value.trim(),refreshHours:Number($('routingHours').value)});loadRouting(model.routing?.active||null);toast('Маршрутизация сохранена и применена');}
    catch(e){$('routingError').textContent=e.message;}
    finally{routingBusy=false;renderRouting();}
  });
  $('routingRefresh').addEventListener('click',async()=>{
    if(routingBusy)return;const id=routingDraftId;routingBusy=true;$('routingError').textContent='';renderRouting();
    try{await rpc('routingRefresh',{id});loadRouting(id);toast('Профиль маршрутизации обновлён');}
    catch(e){$('routingError').textContent=e.message;}
    finally{routingBusy=false;renderRouting();}
  });
  $('routingExport').addEventListener('click',async()=>{
    if(routingBusy||!routingSaved())return;routingBusy=true;renderRouting();
    try{await rpc('routingExport',{id:routingDraftId});}catch(e){$('routingError').textContent=e.message;}
    finally{routingBusy=false;renderRouting();}
  });
  $('routingImport').addEventListener('click',()=>{$('routingImportContent').value='';$('routingImportError').textContent='';openDialog($('routingImportDialog'));});
  $('cancelRoutingImport').addEventListener('click',()=>closeDialog($('routingImportDialog')));
  $('routingImportForm').addEventListener('submit',async event=>{
    event.preventDefault();if(routingBusy)return;routingBusy=true;renderRouting();
    const submit=$('routingImportForm').querySelector('[type=submit]');submit.disabled=true;$('routingImportError').textContent='';
    try{await rpc('routingImport',{content:$('routingImportContent').value.trim()});loadRouting(model.routing?.active||null);closeDialog($('routingImportDialog'));toast('Профиль маршрутизации добавлен');}
    catch(e){$('routingImportError').textContent=e.message;}
    finally{routingBusy=false;submit.disabled=false;renderRouting();}
  });
  $('routingRemove').addEventListener('click',()=>{
    const saved=routingSaved();if(!saved)return;routingRemovingId=saved.id;$('routingRemoveError').textContent='';
    $('routingRemoveNote').textContent='Будет удалён профиль «'+saved.rules.name+'». Активный профиль заменится правилами по умолчанию.';openDialog($('routingRemoveDialog'));
  });
  $('cancelRoutingRemove').addEventListener('click',()=>closeDialog($('routingRemoveDialog')));
  $('confirmRoutingRemove').addEventListener('click',async()=>{
    if(routingBusy)return;routingBusy=true;const b=$('confirmRoutingRemove');b.disabled=true;renderRouting();
    try{await rpc('routingRemove',{id:routingRemovingId});loadRouting(model.routing?.active||null);closeDialog($('routingRemoveDialog'));toast('Профиль маршрутизации удалён');}
    catch(e){$('routingRemoveError').textContent=e.message;}
    finally{routingBusy=false;b.disabled=false;renderRouting();}
  });
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
  document.querySelectorAll('dialog').forEach(dialog=>{dialog.addEventListener('cancel',event=>{event.preventDefault();if(dialog===$('advancedDialog')&&!$('deviceDialog').hidden)closeDevice();else closeDialog(dialog);});dialog.addEventListener('click',event=>{if(event.target!==dialog)return;const r=dialog.getBoundingClientRect();if(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom)closeDialog(dialog);});});
  document.querySelectorAll('[data-window]').forEach(b=>b.addEventListener('click',()=>request(b.dataset.window)));
  document.querySelector('.titlebar').addEventListener('mousedown',e=>{if(e.button===0&&!e.target.closest('button'))request('drag',{double:e.detail===2});});
  document.querySelectorAll('[data-edge]').forEach(b=>b.addEventListener('mousedown',e=>{if(e.button===0)request('resize',{edge:b.dataset.edge});}));
  $('statisticsBack').addEventListener('click',()=>{page('settings');$('statisticsButton').focus({preventScroll:true});});
  document.querySelectorAll('[data-back-settings]').forEach(button=>button.addEventListener('click',()=>{const from=current;page('settings');document.querySelector('.settings-tools [data-page='+from+']').focus({preventScroll:true});}));
  const serviceTools=window.KotServices({getModel:()=>model,getPage:()=>current,rpc,toast,openDialog,closeDialog});
  const clientTools=window.KotClientTools({getModel:()=>model,getPage:()=>current,getPeriod:()=>statisticsPeriod,rpc,toast,openDialog,closeDialog,page});
  new ResizeObserver(moveNav).observe(document.querySelector('.nav'));renderServers();moveNav();
  if(host){host.addEventListener('message',({data})=>{if(data.kind==='navigate'&&['home','servers','statistics','diagnostics','logs','services','settings'].includes(data.page))page(data.page);else if(data.kind==='snapshot')apply(data.data);else if(data.kind==='reply'){const p=pending.get(data.id);if(p){pending.delete(data.id);clearTimeout(p.timer);data.ok?p.resolve(data.data):p.reject(new Error(data.error));}}});request('ready');}else{document.body.classList.remove('booting');$('startupScreen').hidden=true;document.querySelector('.main').inert=false;document.querySelector('.sidebar').inert=false;toast('Откройте интерфейс через Kot.exe.');}
})();
