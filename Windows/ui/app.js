(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  function startupTheme(theme){
    const light=theme==='light';
    document.documentElement.classList.toggle('theme-light',light);
    document.body.classList.toggle('theme-light',light);
    const mark=$('startupScreen').querySelector('img'),source=light?'brand-icon-light.svg':'brand-icon.svg';
    if(mark.getAttribute('src')!==source)mark.src=source;
  }
  startupTheme(window.kotStartup?.theme);
  const app=document.querySelector('.app'), sidebarToggle=$('sidebarToggle');
  const narrowWindow=matchMedia('(max-width:700px)');
  const sidebarChoices=new Map();
  function sidebarKey(){return narrowWindow.matches?'kot.sidebar.narrow':'kot.sidebar.wide';}
  function showSidebar(mode){
    const collapsed=mode==='collapsed';
    app.dataset.sidebar=mode;
    sidebarToggle.setAttribute('aria-expanded',String(!collapsed));
    sidebarToggle.title=collapsed?'Развернуть панель':'Свернуть панель';
    sidebarToggle.setAttribute('aria-label',sidebarToggle.title);
    for(const button of $('sidebarNav').querySelectorAll('.nav-button')){
      if(collapsed)button.title=button.getAttribute('aria-label');
      else button.removeAttribute('title');
    }
  }
  function restoreSidebar(){
    const key=sidebarKey();
    let mode=sidebarChoices.get(key);
    if(!mode){try{mode=localStorage.getItem(key);}catch{}}
    showSidebar(mode==='expanded'||mode==='collapsed'?mode:(narrowWindow.matches?'collapsed':'expanded'));
  }
  sidebarToggle.addEventListener('click',()=>{
    const key=sidebarKey(), mode=app.dataset.sidebar==='expanded'?'collapsed':'expanded';
    showSidebar(mode);
    sidebarChoices.set(key,mode);
    try{localStorage.setItem(key,mode);}catch{}
  });
  narrowWindow.addEventListener('change',restoreSidebar);
  restoreSidebar();
  // Bundled flag-icons 7.3.2. Never build asset paths from subscription text.
  const {label,serverPresentation,countryBadge,bytes}=window.KotPresentation;
  const reduce = matchMedia('(prefers-reduced-motion: reduce)'), motions = new Map();
  const motionTokens=getComputedStyle(document.documentElement);
  const ease=motionTokens.getPropertyValue('--ease').trim();
  const enterDuration=parseFloat(motionTokens.getPropertyValue('--motion-enter'));
  function cancelMotion(el){motions.get(el)?.cancel();motions.delete(el);}
  function animate(el, frames, options={}) {
    cancelMotion(el); if(reduce.matches) return null;
    const motion=el.animate(frames,{duration:enterDuration,easing:ease,...options}); motions.set(el,motion);
    motion.finished.then(()=>{if(motions.get(el)===motion)motions.delete(el);}).catch(()=>{}); return motion;
  }
  reduce.addEventListener('change',()=>{if(reduce.matches){motions.forEach(a=>a.cancel());motions.clear();}});
  let model={nodes:[],selected:'',state:'idle',settings:{theme:'dark',mode:'tun'}}, current='home', toastTimer, importBusy=false, refreshBusy=false, updateCheckBusy=false, renderedServers='', initialSnapshot=false;
  const host=window.chrome?.webview,openDialogs=[];
  const bridge=window.KotNativeBridge(host),rpc=bridge.rpc;
  function raiseToast(){const el=$('toast');if(el.hidden)return;if(el.matches(':popover-open'))el.hidePopover();const owner=openDialogs.findLast(dialog=>dialog.open)||document.body;if(el.parentElement!==owner)owner.append(el);el.showPopover();}
  let toastGeneration=0;
  function toast(text){
    const generation=++toastGeneration,el=$('toast');
    let from={opacity:0,transform:'translate(-50%,7px)'};
    if(el.matches(':popover-open')){
      const style=getComputedStyle(el);
      // The new message can have a different width. Keep its own horizontal centre.
      const y=new DOMMatrixReadOnly(style.transform).m42;
      from={opacity:style.opacity,transform:`translate(-50%,${y}px)`};
    }
    clearTimeout(toastTimer);el.textContent=text;el.hidden=false;raiseToast();
    animate(el,[from,{opacity:1,transform:'translate(-50%,0)'}]);
    toastTimer=setTimeout(()=>{
      const finish=()=>{if(generation!==toastGeneration)return;el.hidePopover();el.hidden=true;cancelMotion(el);};
      const motion=animate(el,[{opacity:1,transform:'translate(-50%,0)'},{opacity:0,transform:'translate(-50%,7px)'}],{duration:parseFloat(motionTokens.getPropertyValue('--motion-exit'))});
      if(motion)motion.finished.then(finish).catch(()=>{if(reduce.matches)finish();});else finish();
    },4000);
  }
  let brandClicks=0, meow;
  $('brandButton').addEventListener('click',()=>{
    if(++brandClicks!==443)return;
    brandClicks=0;
    meow??=new Audio('sounds/meow.ogg');meow.volume=.7;meow.currentTime=0;
    meow.play().catch(()=>toast('Не удалось воспроизвести мяу.'));
  });
  async function request(action,data={}) {try{await rpc(action,data);return true;}catch(e){toast(e.message);return false;}}
  async function finishStartup(startupOriginMs){
    const screen=$('startupScreen'),mark=screen.querySelector('.startup-mark'),caption=screen.querySelector('p');
    const main=document.querySelector('.main'),sidebar=document.querySelector('.sidebar'),started=[];
    try{
      await Promise.all([document.fonts.ready,mark.querySelector('img').decode().catch(()=>{})]);
      if(Number.isFinite(startupOriginMs)){
        const origin=performance.now()+(startupOriginMs-Date.now());
        for(const motion of screen.getAnimations({subtree:true}))motion.startTime=origin;
      }
      // Paint the web loading screen before the native one is removed by the host.
      await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
      await rpc('uiReady');
      const markStyle=getComputedStyle(mark),captionStyle=getComputedStyle(caption);
      const markFrom={opacity:markStyle.opacity,filter:markStyle.filter,transform:markStyle.transform};
      const captionFrom={opacity:captionStyle.opacity,transform:captionStyle.transform};
      mark.style.animation='none';caption.style.animation='none';
      document.body.classList.remove('booting');document.body.classList.add('revealing');
      const run=(el,frames,options)=>{const motion=animate(el,frames,options);if(motion)started.push(motion);};
      run(mark,[markFrom,{opacity:0,filter:'blur(7px)',transform:'translateY(-8px) scale(.9)'}],{duration:300,fill:'forwards'});
      run(caption,[captionFrom,{opacity:0,transform:'translateY(-4px)'}],{duration:220,fill:'forwards'});
      run(screen,[{opacity:1},{opacity:0}],{duration:480,easing:'cubic-bezier(.4,0,.2,1)',fill:'forwards'});
      for(const el of [main,sidebar])run(el,[{opacity:0,filter:'blur(10px)',transform:'translateY(5px)'},{opacity:1,filter:'blur(0)',transform:'translateY(0)'}],{duration:520});
      run($('brandButton'),[{opacity:0,transform:'translateY(5px) scale(.96)'},{opacity:1,transform:'translateY(0) scale(1)'}],{duration:460,delay:60,fill:'backwards'});
      // Cancellation (including a changed motion preference) also releases input.
      await Promise.allSettled(started.map(motion=>motion.finished));
    }catch(e){toast(e.message);}
    finally{
      screen.hidden=true;screen.getAnimations({subtree:true}).forEach(motion=>motion.cancel());
      started.forEach(motion=>motion.cancel());
      document.body.classList.remove('booting','revealing');main.inert=false;sidebar.inert=false;
      document.documentElement.classList.remove('startup-spin');
    }
  }
  function moveNav(){const active=document.querySelector('.nav-button[aria-current=page]');const pill=document.querySelector('.nav-indicator');pill.style.transform=`translateY(${active.offsetTop}px)`;pill.style.height=active.offsetHeight+'px';}
  function page(name){
    if(name===current||!$('page-'+name))return;
    const previous=$('page-'+current);for(const el of motions.keys())if(el===previous||previous.contains(el))cancelMotion(el);current=name;
    closeSubscriptions();
    nodeJson.closeMenu({immediate:true});
    document.querySelectorAll('.view').forEach(el=>el.hidden=el.id!=='page-'+name);
    document.querySelectorAll('[data-page]').forEach(el=>{if(el.dataset.page===(el.classList.contains('nav-button')&&['statistics','diagnostics','logs'].includes(name)?'settings':name))el.setAttribute('aria-current','page');else el.removeAttribute('aria-current');});
    const main=document.querySelector('.main');main.dataset.view=name;main.scrollTop=0; moveNav(); request('view',{page:name}); if(name==='logs')journal.render(true); if(name==='servers')renderServers(); if(name==='statistics'||name==='home')statistics.render();clientTools.render();serviceTools.render();
    const view=$('page-'+name);animate(view,[{opacity:0,transform:'translateY(8px)'},{opacity:1,transform:'translateY(0)'}],{duration:260});
    // A single page transition prevents nested delayed elements from flashing.
  }
  let togglePending=false;
  function updatePowerAvailability(){
    const state=model.state;
    const unavailable=togglePending||state==='disconnecting'||(state==='idle'&&!model.nodes.length);
    $('powerButton').disabled=unavailable;
    // Keep the compact control focusable so its server description is available even when idle and empty.
    $('sidebarPower').setAttribute('aria-disabled',String(unavailable||state==='connecting'));
  }
  async function togglePower(event){
    if(togglePending||event.currentTarget.getAttribute('aria-disabled')==='true')return;
    togglePending=true;updatePowerAvailability();
    try{await request('toggle');}
    finally{togglePending=false;updatePowerAvailability();}
  }
  function setState(state, previous){
    $('connection').dataset.state=state;
    document.querySelector('.title-brand').dataset.state=state;
    const texts={idle:['Не подключено','Подключить'],connecting:['Подключение','Отменить'],connected:['Подключено','Отключить'],disconnecting:['Отключение','Отключить'],waiting:['Ожидание сети · повтор подключения','Отменить']}[state]||['Не подключено','Подключить'];
    $('stateLabel').textContent=texts[0];$('powerLabel').textContent=texts[1];$('powerButton').setAttribute('aria-label',texts[1]);
    const transitioning=state==='connecting'||state==='disconnecting';
    const sideLabel=transitioning?texts[0]+'…':texts[1];
    $('sidebarPowerLabel').textContent=sideLabel;
    $('sidebarPower').setAttribute('aria-label',sideLabel);
    $('sidebarPower').setAttribute('aria-busy',String(transitioning));
    updatePowerAvailability();
    const blocked=model.settings?.killSwitchActive&&state==='idle';
    $('connectionError').textContent=model.error||(blocked?'Kill switch блокирует интернет. Подключитесь или отключите защиту в настройках.':'');$('connectionError').hidden=!model.error&&!blocked;$('copyErrorLog').hidden=!model.error;
    $('connectionError').title=$('connectionError').textContent;
    if(state!==previous){
      const icon=$('powerIcon'),from=getComputedStyle(icon).transform;cancelMotion(icon);
      if(current==='home'){
        const status=$('stateLabel'),label=$('powerLabel');
        const style=getComputedStyle(status);
        const fromStatus=motions.has(status)?{opacity:style.opacity,transform:style.transform}:{opacity:0,transform:'translateY(3px)'};
        animate(status,[fromStatus,{opacity:1,transform:'translateY(0)'}],{duration:220});
        animate(label,[{opacity:motions.has(label)?getComputedStyle(label).opacity:0},{opacity:1}],{duration:parseFloat(motionTokens.getPropertyValue('--motion-exit'))});
        if(state==='connected')animate(icon,[{transform:from==='none'?'scale(.82)':from},{transform:'scale(1.12)',offset:.6},{transform:'scale(1)'}],{duration:440});
        else if(from!=='none')animate(icon,[{transform:from},{transform:'scale(1)'}],{duration:300});
      }
    }
  }
  function apply(data,startupOriginMs){
    const old=model.state;model=data;nodeJson.sync();
    startupTheme(data.settings.theme);
    document.querySelectorAll('[data-theme]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.theme===data.settings.theme));document.querySelector('.theme-control').dataset.selected=data.settings.theme==='dark'?'0':'1';
    document.querySelectorAll('[data-mode]').forEach(b=>b.setAttribute('aria-pressed',b.dataset.mode===data.settings.mode));document.querySelector('.mode').dataset.selected=data.settings.mode==='tun'?'0':'1';
    document.querySelectorAll('[data-setting]').forEach(b=>b.setAttribute('aria-checked',!!data.settings[b.dataset.setting]));
    label($('listName'),data.hasSubscription?data.name:'Подписка');
    const selected=data.nodes.find(n=>n.id===data.selected);
    const automatic=data.selected==='auto', live=data.state==='connected'?data.activeNode:null;
    const active=live&&(data.nodes.find(n=>n.id===live.id)||live);
    const next=data.needsSelection?null:data.nodes.find(n=>n.id===(data.nextNode??data.selected));
    const actual=active||data.nodes.find(n=>n.id===data.automaticNode);
    const tooltipNode=active||(!data.needsSelection&&(automatic?actual:next));
    $('sidebarServer').textContent=tooltipNode?serverPresentation(tooltipNode).name:'Сервер не выбран';
    label($('selectedName'),automatic?'Авто':selected?serverPresentation(selected).name:data.nodes.length?'Выбрать сервер':'Добавить подписку');countryBadge($('selectedCountry'),selected,automatic);
    let detail=automatic?(actual?serverPresentation(actual).name:'Быстрый доступный сервер'):selected?.protocol.toUpperCase()||'';
    if(active&&(!selected||active.id!==selected.id)&&!automatic)
      detail=next?.id===active.id?'Сейчас и при следующем подключении: '+serverPresentation(active).name:'Сейчас подключён: '+serverPresentation(active).name;
    else if(next&&next.id!==data.selected)detail='При следующем подключении: '+serverPresentation(next).name;
    if(data.needsSelection)detail=(active?'Сейчас подключён: '+serverPresentation(active).name+'. ':'')+'Для подключения выберите сервер';
    label($('selectedCity'),detail);$('selectedCity').title=detail;
    const autoEligible=data.nodes.some(n=>!n.providerRouting&&n.protocol!=='xray'&&(!data.favorites?.length||data.favorites.includes(n.id)));
    $('autoServer').hidden=!data.nodes.length;$('autoServer').setAttribute('aria-pressed',automatic);$('autoDescription').textContent=!autoEligible?'Нет обычных серверов для Авто':data.favorites?.length?'Обычные серверы из избранного':'Обычные серверы, проверенные через VPN';
    renderSubscriptions();routing.render();
    $('backgroundError').textContent=data.backgroundError||'';$('backgroundError').hidden=!data.backgroundError;
    $('refreshButton').disabled=refreshBusy||!data.hasSubscription||!!data.localSubscription;$('removeSubscription').hidden=!data.hasSubscription;
    if(!$('advancedDialog').open)$('bypassDomains').value=data.settings.bypass||'';
    $('skippedDetails').hidden=!data.warnings?.length;$('skippedNodes').replaceChildren(...(data.warnings||[]).slice(0,50).map(w=>{const p=document.createElement('p');p.textContent=w;return p;}));
    const displayVersion=data.displayVersion||data.version||'';
    $('titleVersion').textContent=displayVersion;$('version').textContent='kot. '+displayVersion+' · Xray experimental'+(data.updated?' · '+data.updated:'');
    const ping=data.ping||{};
    $('pingAll').disabled=!data.nodes.length||!!ping.busy;$('pingAll').dataset.busy=!!ping.busy;
    $('pingProgress').hidden=!ping.busy;$('pingProgress').textContent=(ping.done||0)+' / '+(ping.total||0);$('cancelPing').hidden=!ping.busy;
    $('pingError').hidden=!ping.error;$('pingError').textContent=ping.error||'';
    clientTools.render();serviceTools.render();statistics.renderTraffic(); statistics.render(true); renderUpdate(); if(current==='logs')journal.render(true);
    setState(data.state,old);const renderKey=JSON.stringify([data.nodes,data.selected,active?.id,next?.id,data.favorites,data.ping?.settings?.sort,data.ping?.busy]);if(renderKey!==renderedServers){renderedServers=renderKey;renderServers();}
    if(!initialSnapshot){initialSnapshot=true;finishStartup(startupOriginMs);}
  }
  const pendingPings=new Set();
  const pingKey=id=>JSON.stringify([model.activeSubscription,id]);
  const pingBusy=item=>pendingPings.has(pingKey(item.id))||['queued','checking'].includes(item.ping?.status);
  function renderPing(row,item){
    const ping=row.querySelector('.ping-node'),value=ping.querySelector('span'),result=item.ping;
    ping.disabled=pingBusy(item);
    ping.setAttribute('aria-busy',String(ping.disabled));
    ping.dataset.status=result?.status||'idle';
    const text=result?.status==='ok'?result.ms+' мс':result?.status==='checking'?'Проверка':result?.status==='queued'?'В очереди':result?.status==='error'?'Ошибка':result?.status==='unsupported'?'UDP':result?.status==='cancelled'?'Отмена':'';
    if(value.textContent!==text)value.textContent=text;
    ping.title=result?.status==='ok'?(result.mode||'http').toUpperCase()+': '+result.successes+'/'+result.attempts+' попыток'+(result.error?'; '+result.error:''):result?.error||'Проверить сервер';
    ping.setAttribute('aria-label','Пинг '+serverPresentation(item).name+': '+(text||'не проверен')+'. '+ping.title);
  }
  async function pingNode(id){
    const item=model.nodes.find(n=>n.id===id);
    if(!item||pingBusy(item))return;
    const key=pingKey(id);pendingPings.add(key);renderServers();
    try{await request('pingNode',{id});}
    finally{pendingPings.delete(key);renderServers();}
  }
  function renderServers(){
    const query=$('serverSearch').value.trim().toLocaleLowerCase('ru'),visible=model.nodes.filter(n=>(n.name+' '+n.protocol).toLocaleLowerCase('ru').includes(query));
    const active=document.activeElement, focusId=active?.closest('[data-node]')?.dataset.node, focusPing=active?.classList.contains('ping-node'),focusQr=active?.classList.contains('qr-node');
    const list=$('serverList'),rows=new Map([...list.children].map(row=>[row.dataset.node,row]));
    $('noServers').hidden=!!visible.length;$('noServers').textContent=model.nodes.length?'Ничего не найдено':'Добавьте подписку';$('serverCount').textContent=visible.length;
    const sort=model.ping?.settings?.sort||'none';
    if(sort==='name')visible.sort((a,b)=>serverPresentation(a).name.localeCompare(serverPresentation(b).name,'ru'));
    if(sort==='ping')visible.sort((a,b)=>(a.ping?.ms??Infinity)-(b.ping?.ms??Infinity));
    visible.forEach((item,index)=>{
      // Ping snapshots only patch the existing control. Replacing the hovered row
      // resets Chromium's computed hover colours, focus and in-flight transitions.
      const {ping:_,...details}=item;
      const rowKey=JSON.stringify([details,model.activeSubscription,model.selected,model.activeNode?.id,model.nextNode,model.needsSelection,model.state,model.favorites]);
      let row=rows.get(item.id);rows.delete(item.id);
      if(!row||row.dataset.renderKey!==rowKey){
        const old=row,presentation=serverPresentation(item);
        row=document.createElement('div');row.className='server-row';row.dataset.node=item.id;row.dataset.renderKey=rowKey;
        if(old)old.replaceWith(row);
        const favorite=document.createElement('button');favorite.className='favorite-node';favorite.hidden=true;favorite.textContent=model.favorites?.includes(item.id)?'★':'☆';favorite.setAttribute('aria-label','Избранное: '+presentation.name);favorite.setAttribute('aria-pressed',!!model.favorites?.includes(item.id));favorite.addEventListener('click',()=>request('favorite',{id:item.id}));
        const button=document.createElement('button');button.className='server-card';button.setAttribute('aria-pressed',item.id===model.selected);button.setAttribute('aria-label','Выбрать '+presentation.name);
        const country=document.createElement('span');country.className='country';countryBadge(country,item);
        const copy=document.createElement('span');copy.className='server-copy';const title=document.createElement('strong');title.textContent=presentation.name;title.title=presentation.name;const city=document.createElement('small');
        const connected=model.state==='connected'&&item.id===model.activeNode?.id;
        const queued=!model.needsSelection&&item.id===model.nextNode&&item.id!==model.selected;
        const status=connected?'Подключён':queued?'Следующее подключение':'';
        city.textContent=[item.protocol.toUpperCase(),status].filter(Boolean).join(' · ');
        if(status)button.setAttribute('aria-label','Выбрать '+presentation.name+'. '+status);
        copy.append(title,city);button.append(country,copy);
        if(item.id===model.selected){const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');icon.classList.add('selected-mark');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#check');icon.append(use);button.append(icon);}
        button.addEventListener('click',async()=>{button.disabled=true;try{await rpc('select',{id:item.id,subscriptionId:model.activeSubscription});page('home');}catch(e){toast(e.message);}finally{button.disabled=false;}});
        const ping=document.createElement('button');ping.className='ping-node';
        const icon=document.createElementNS('http://www.w3.org/2000/svg','svg');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#signal');icon.append(use);
        ping.append(document.createElement('span'),icon);ping.addEventListener('click',()=>pingNode(item.id));row.append(button,favorite,ping);
        const qr=document.createElement('button');qr.type='button';qr.className='qr-node';qr.innerHTML='<svg aria-hidden="true"><use href="#ellipsis"/></svg>';qr.setAttribute('aria-label','Действия сервера '+presentation.name);
        qr.setAttribute('aria-expanded',String(nodeJson.menuOpenFor(item.id)));qr.setAttribute('aria-haspopup','menu');qr.setAttribute('aria-controls','nodeMenu');qr.title='Действия сервера';
        qr.addEventListener('click',()=>nodeJson.showMenu(item,qr));
        qr.addEventListener('keydown',event=>{if(event.key==='ArrowDown'||event.key==='ArrowUp'){event.preventDefault();nodeJson.showMenu(item,qr,event.key==='ArrowUp');}});row.append(qr);
      }
      renderPing(row,item);
      // Leave unchanged rows attached, including the popup anchor and focused control.
      if(list.children[index]!==row)list.insertBefore(row,list.children[index]||null);
      if(focusId===item.id&&!row.contains(document.activeElement))row.querySelector(focusQr?'.qr-node':focusPing?'.ping-node':'.server-card').focus({preventScroll:true});
    });
    for(const row of rows.values())row.remove();
    if(focusId&&!visible.some(item=>item.id===focusId))$('serverSearch').focus({preventScroll:true});
  }

  new ResizeObserver(()=>{
    $('updateNotice').closest('main').style.setProperty('--update-notice-height',$('updateNotice').getBoundingClientRect().height+'px');
  }).observe($('updateNotice'));
  let updateDownloadBusy=false, updateDeferBusy=false;
  function renderUpdate(){const u=model.updates||{},busy=updateCheckBusy||updateDownloadBusy||!!u.busy;$('updateNotice').hidden=!u.availableVersion||u.availableVersion===u.dismissedVersion||busy||$('onlineUpdateDialog').open;
    $('updateNoticeTitle').textContent='Доступно обновление '+(u.availableVersion||'');
    $('acceptUpdate').disabled=busy||updateDeferBusy;$('deferUpdate').disabled=busy||updateDeferBusy;
    $('downloadUpdate').disabled=busy;
    $('updateBadge').hidden=!u.availableVersion;$('updateStatus').textContent=(u.status||'Проверка обновлений')+(u.bytes?' · '+bytes(u.bytes):'')+(u.checkedAt&&!u.busy?' · '+u.checkedAt:'');$('updateError').textContent=u.error||'';$('downloadUpdate').hidden=!u.availableVersion;$('retryUpdate').hidden=!!u.availableVersion||!u.error;$('retryUpdate').disabled=busy;$('downloadUpdate').textContent=updateDownloadBusy||u.busy&&u.availableVersion?'Обновление…':u.error?'Повторить':'Обновить до '+(u.availableVersion||'');$('downloadUpdate').title=(u.error?'Повторить обновление до ':'Обновить до ')+(u.availableVersion||'');$('cancelUpdate').hidden=!u.busy;$('onlineUpdateButton').disabled=busy;$('updateProgress').hidden=!busy;if(u.size>0){$('updateProgress').max=u.size;$('updateProgress').value=u.bytes||0;}else $('updateProgress').removeAttribute('value');
    const visual=$('smoothUpdateProgress'),bar=visual.firstElementChild;visual.hidden=!busy;
    const fraction=$('updateProgress').position;
    visual.dataset.indeterminate=fraction<0;
    if(fraction<0){bar.style.removeProperty('transform');return;}
    if(fraction===0){bar.style.transition='none';bar.style.transform='scaleX(0)';void bar.offsetWidth;bar.style.transition='';}
    else bar.style.transform='scaleX('+fraction+')';
  }
  $('onlineUpdateButton').addEventListener('click',async()=>{
    if(updateCheckBusy||model.updates?.busy)return;
    updateCheckBusy=true;model.updates={...model.updates,status:'Проверка обновлений',error:'',availableVersion:null,bytes:0};
    renderUpdate();openDialog($('onlineUpdateDialog'));
    try{await rpc('checkUpdates');}catch(e){model.updates={...model.updates,status:'Не удалось проверить обновления',error:e.message};}
    finally{updateCheckBusy=false;renderUpdate();}
  });
  $('retryUpdate').addEventListener('click',()=>$('onlineUpdateButton').click());
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
  let menuGeneration=0,menuClosing=false,menuExit=null;
  function closeSubscriptions(){
    if(!subscriptionMenu.matches(':popover-open')||menuClosing)return;
    menuClosing=true;const generation=++menuGeneration,style=getComputedStyle(subscriptionMenu);
    const finish=()=>{if(generation!==menuGeneration)return;subscriptionMenu.hidePopover();cancelMotion(subscriptionMenu);menuClosing=false;menuExit=null;};
    menuExit=finish;const motion=animate(subscriptionMenu,[{opacity:style.opacity,transform:style.transform},{opacity:0,transform:'translateY('+(subscriptionMenu.dataset.upwards==='true'?'4':'-4')+'px) scale(.985)'}],{duration:parseFloat(motionTokens.getPropertyValue('--motion-exit'))});
    if(motion)motion.finished.then(finish).catch(()=>{if(reduce.matches)finish();});else finish();
  }
  reduce.addEventListener('change',()=>{if(reduce.matches)menuExit?.();});
  document.addEventListener('pointerdown',e=>{if(!subscriptionMenu.contains(e.target)&&!subscriptionSelect.contains(e.target))closeSubscriptions();});
  document.addEventListener('keydown',e=>{if(e.key==='Escape'&&!e.defaultPrevented&&!menuClosing&&subscriptionMenu.matches(':popover-open')){e.preventDefault();closeSubscriptions();subscriptionSelect.focus({preventScroll:true});}});
  function positionSubscriptions(){
    const r=subscriptionSelect.closest('.profile-tools').getBoundingClientRect(),edge=12,gap=8,width=Math.min(340,innerWidth-edge*2);
    subscriptionMenu.style.width=width+'px';
    subscriptionMenu.style.left=Math.max(edge,Math.min(r.left,innerWidth-width-edge))+'px';
    const below=innerHeight-r.bottom-gap-edge,above=r.top-gap-edge;
    const upwards=below<200&&above>below;
    subscriptionMenu.dataset.upwards=upwards;subscriptionMenu.style.maxHeight=Math.max(0,upwards?above:below)+'px';
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
      const focused=document.activeElement?.closest('.profile-entry')?.querySelector('.profile-option')?.dataset.subscription,control=document.activeElement?.className;
      $('subscriptionOptions').replaceChildren(...subscriptions.map(s=>{
        const option=document.createElement('button');option.type='button';option.className='profile-option';
        option.dataset.subscription=s.id;
        const entry=document.createElement('div');entry.className='profile-entry';entry.setAttribute('role','listitem');
        const icon=document.createElementNS('http://www.w3.org/2000/svg','svg'),use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#globe');icon.append(use);icon.classList.add('profile-icon');icon.setAttribute('aria-hidden','true');
        const copy=document.createElement('span');copy.className='profile-copy';const name=document.createElement('strong');label(name,s.name);
        const count=document.createElement('small'),n=Math.max(0,Math.trunc(Number(s.count)||0));
        count.textContent=n+' '+(n%10===1&&n%100!==11?'сервер':n%10>=2&&n%10<=4&&(n%100<12||n%100>14)?'сервера':'серверов');copy.append(name,count);
        const check=document.createElementNS('http://www.w3.org/2000/svg','svg'),mark=document.createElementNS('http://www.w3.org/2000/svg','use');mark.setAttribute('href','#check');check.append(mark);check.classList.add('profile-check');check.setAttribute('aria-hidden','true');
        option.append(icon,copy,check);option.addEventListener('click',()=>chooseSubscription(s.id));
        const actions=document.createElement('div');actions.className='profile-entry-actions';
        for(const [action,symbol,text] of [['edit','edit','Редактировать'],['delete','trash','Удалить']]){
          const button=document.createElement('button');button.type='button';button.className='profile-'+action;
          button.title=text;button.setAttribute('aria-label',text+' профиль '+s.name);
          button.innerHTML='<svg aria-hidden="true"><use href="#'+symbol+'"/></svg>';
          button.addEventListener('click',()=>{closeSubscriptions();subscriptionSelect.focus({preventScroll:true});action==='edit'?editSubscription(s.id):removeSubscription(s.id);});actions.append(button);
        }
        entry.append(option,actions);return entry;
      }));
      $('subscriptionOptions').dataset.key=key;
      if(focused&&subscriptionMenu.matches(':popover-open'))[...$('subscriptionOptions').querySelectorAll('.profile-option')].find(b=>b.dataset.subscription===focused)?.closest('.profile-entry').querySelector('.'+control)?.focus({preventScroll:true});
    }
    for(const option of $('subscriptionOptions').querySelectorAll('.profile-option')){const active=option.dataset.subscription===model.activeSubscription;option.setAttribute('aria-pressed',active);option.closest('.profile-entry').dataset.active=active;}
    for(const button of $('subscriptionOptions').querySelectorAll('button'))button.disabled=switchingSubscription;
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
  function focusSubscription(button){
    button.focus({preventScroll:true});
    const row=button.closest('.profile-entry')||button,top=row.offsetTop,bottom=top+row.offsetHeight;
    if(top<subscriptionMenu.scrollTop)subscriptionMenu.scrollTop=top;
    else if(bottom>subscriptionMenu.scrollTop+subscriptionMenu.clientHeight)subscriptionMenu.scrollTop=bottom-subscriptionMenu.clientHeight;
  }
  function openSubscriptions(last=false){
    const visible=subscriptionMenu.matches(':popover-open');
    if(!visible||menuClosing){
      const style=getComputedStyle(subscriptionMenu);
      const from=visible?{opacity:style.opacity,transform:style.transform}:null;
      menuGeneration++;menuClosing=false;menuExit=null;
      positionSubscriptions();if(!visible)subscriptionMenu.showPopover();
      const enteredFrom=from||{opacity:0,transform:'translateY('+(subscriptionMenu.dataset.upwards==='true'?'4':'-4')+'px) scale(.985)'};
      animate(subscriptionMenu,[enteredFrom,{opacity:1,transform:'translateY(0) scale(1)'}],{duration:parseFloat(motionTokens.getPropertyValue('--motion-exit'))});
    }
    const options=[...$('subscriptionOptions').querySelectorAll('.profile-option')];
    const target=last?options.at(-1):options.find(b=>b.getAttribute('aria-pressed')==='true')||options[0];
    focusSubscription(target||$('addSubscriptionOption'));
  }
  subscriptionSelect.addEventListener('click',e=>{e.preventDefault();subscriptionMenu.matches(':popover-open')&&!menuClosing?closeSubscriptions():openSubscriptions();});
  subscriptionSelect.addEventListener('keydown',e=>{if(e.key==='ArrowDown'||e.key==='ArrowUp'){e.preventDefault();openSubscriptions(e.key==='ArrowUp');}});
  subscriptionMenu.addEventListener('beforetoggle',e=>{subscriptionSelect.setAttribute('aria-expanded',e.newState==='open');});
  subscriptionMenu.addEventListener('keydown',e=>{
    if(e.key==='Escape'){e.preventDefault();closeSubscriptions();subscriptionSelect.focus({preventScroll:true});return;}
    if(!['ArrowDown','ArrowUp','Home','End'].includes(e.key))return;
    const options=[...$('subscriptionOptions').querySelectorAll('.profile-option')];if(!options.length)return;
    e.preventDefault();const index=options.indexOf(document.activeElement?.closest('.profile-entry')?.querySelector('.profile-option'));
    focusSubscription(options[e.key==='Home'?0:e.key==='End'?options.length-1:e.key==='ArrowDown'?(index+1)%options.length:(index-1+options.length)%options.length]);
  });
  subscriptionMenu.addEventListener('focusout',e=>{if(e.relatedTarget){if(!subscriptionMenu.contains(e.relatedTarget)&&e.relatedTarget!==subscriptionSelect)closeSubscriptions();}else setTimeout(()=>{if(!subscriptionMenu.contains(document.activeElement)&&document.activeElement!==subscriptionSelect)closeSubscriptions();},0);});
  $('addSubscriptionOption').addEventListener('click',()=>{closeSubscriptions();openAdd();});
  addEventListener('resize',()=>{if(subscriptionMenu.matches(':popover-open'))positionSubscriptions();});
  document.querySelector('.main').addEventListener('scroll',closeSubscriptions,{passive:true});
  $('autoServer').addEventListener('click',async()=>{if(await request('select',{id:'auto',subscriptionId:model.activeSubscription}))page('home');});
  document.querySelectorAll('[data-page]').forEach(el=>el.addEventListener('click',()=>page(el.dataset.page)));
  $('homeServer').addEventListener('click',()=>model.nodes.length?page('servers'):openAdd());$('serverSearch').addEventListener('input',renderServers);
  for(const button of [$('powerButton'),$('sidebarPower')])button.addEventListener('click',togglePower);
  document.querySelectorAll('[data-mode]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:'mode',value:el.dataset.mode})));
  document.querySelectorAll('[data-theme]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:'theme',value:el.dataset.theme})));
  document.querySelectorAll('[data-setting]').forEach(el=>el.addEventListener('click',()=>request('settings',{key:el.dataset.setting,value:el.getAttribute('aria-checked')!=='true'})));
  function clearLink(){$('profileLink').value='';$('addError').textContent='';}
  const dialogFocus=new WeakMap();
  function rememberFocus(){
    const el=document.activeElement,node=el?.closest('[data-node]')?.dataset.node;
    const owner=el?.closest('dialog');if(owner?.dataset.closing)return dialogFocus.get(owner)||(()=>null);
    const control=['qr-node','ping-node','favorite-node','server-card'].find(name=>el?.classList.contains(name));
    return ()=>el?.isConnected?el:node?[...$('serverList').children].find(row=>row.dataset.node===node)?.querySelector('.'+control)||$('serverSearch'):null;
  }
  const dialogUI=window.KotDialogUI({rememberFocus,onStackChange:(dialog,opened)=>{
    if(opened){openDialogs.push(dialog);}
    else {const index=openDialogs.indexOf(dialog);if(index!==-1)openDialogs.splice(index,1);dialogFocus.delete(dialog);}
    raiseToast();
  }});
  const openDialog=dialog=>{if(!dialog.open)dialogFocus.set(dialog,rememberFocus());dialogUI.open(dialog);},closeDialog=dialogUI.close,requestClose=dialogUI.requestClose;
  function openAdd(){clearLink();$('replaceNote').hidden=!model.hasSubscription;openDialog($('addDialog'));}
  document.querySelectorAll('[data-open-add]').forEach(el=>el.addEventListener('click',openAdd));$('cancelAdd').addEventListener('click',()=>requestClose($('addDialog')));$('addDialog').addEventListener('close',()=>{if(importBusy)request('cancelImport');clearLink();});
  $('addForm').addEventListener('submit',async event=>{
    event.preventDefault();if($('addDialog').dataset.closing)return;
    const url=$('profileLink').value.trim();if(!url){dialogUI.fieldError($('profileLink'),$('addError'),'Вставьте ссылку или выберите файл / картинку QR.');return;}
    if(/^http:\/\//i.test(url)){dialogUI.fieldError($('profileLink'),$('addError'),'Нужна HTTPS-ссылка на подписку.');return;}
    const remote=/^https:\/\//i.test(url);
    const submit=$('addForm').querySelector('[type=submit]');if(submit.disabled)return;submit.disabled=true;importBusy=true;submit.textContent='Загрузка';$('addError').textContent='';
    try{const result=await rpc(remote?'import':'importText',remote?{url,name:$('profileName').value.trim()}:{text:url,name:$('profileName').value.trim()});closeDialog($('addDialog'));page('servers');toast(result?.selectionNotice||(model.warnings?.length?'Серверы добавлены. Предупреждений: '+model.warnings.length:'Серверы добавлены'));}
    catch(e){$('addError').textContent=e.message;}
    finally{submit.disabled=false;importBusy=false;submit.textContent='Добавить';}
  });
  $('refreshButton').addEventListener('click',async()=>{if(refreshBusy)return;refreshBusy=true;const b=$('refreshButton');b.disabled=true;try{const result=await rpc('refresh',{subscriptionId:model.activeSubscription});toast(result?.selectionNotice||(model.warnings?.length?'Обновлено. Предупреждений: '+model.warnings.length:'Подписка обновлена'));}catch(e){toast(e.message);}finally{refreshBusy=false;b.disabled=!model.hasSubscription||!!model.localSubscription;}});
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
  $('advancedButton').addEventListener('click',()=>{$('bypassDomains').value=model.settings.bypass||'';$('advancedError').textContent='';openDialog($('advancedDialog'));});$('closeAdvanced').addEventListener('click',()=>requestClose($('advancedDialog')));
  $('saveRules').addEventListener('click',async()=>{const b=$('saveRules');b.disabled=true;try{await rpc('settings',{key:'bypass',value:$('bypassDomains').value});closeDialog($('advancedDialog'));toast('Правила сохранены');}catch(e){$('advancedError').textContent=e.message;}finally{b.disabled=false;}});
  ['diagnostics','copyErrorLog'].forEach(id=>$(id).addEventListener('click',async()=>{if(await request('copyLog'))toast('Полный лог скопирован');}));
  $('pingAll').addEventListener('click',()=>request('pingAll'));$('cancelPing').addEventListener('click',()=>request('cancelPing'));
  function pingMethod(){const http=$('pingMode').value==='http';$('pingUrlRow').hidden=!http;$('pingUrl').required=http;$('pingUrl').disabled=!http;$('pingMethodNote').textContent=http?'Проверяет подключение и доступ к сайту.':'Проверяет TCP-порт, без проверки подключения. Для Hysteria2 нужен HTTP.';}
  $('pingMode').addEventListener('change',pingMethod);
  $('pingSettings').addEventListener('click',()=>{
    const p=model.ping?.settings||{mode:'http',url:'https://www.gstatic.com/generate_204',timeoutMs:5000,attempts:2,parallelism:3,sort:'none'};
    $('pingMode').value=p.mode;$('pingUrl').value=p.url;$('pingTimeout').value=p.timeoutMs;$('pingAttempts').value=p.attempts;$('pingParallel').value=p.parallelism;$('pingSort').value=p.sort;$('pingSettingsError').textContent='';pingMethod();openDialog($('pingDialog'));
  });
  $('closePing').addEventListener('click',()=>requestClose($('pingDialog')));
  $('pingForm').addEventListener('submit',async event=>{
    event.preventDefault();const submit=$('pingForm').querySelector('[type=submit]');submit.disabled=true;
    try{await rpc('pingSettings',{mode:$('pingMode').value,url:$('pingUrl').value.trim(),timeoutMs:Number($('pingTimeout').value),attempts:Number($('pingAttempts').value),parallelism:Number($('pingParallel').value),sort:$('pingSort').value});closeDialog($('pingDialog'));toast('Настройки пинга сохранены');}
    catch(e){$('pingSettingsError').textContent=e.message;}finally{submit.disabled=false;}
  });
  let removingSubscription='',removeBusy=false;
  function removeSubscription(id){const saved=(model.subscriptions||[]).find(s=>s.id===id);if(!saved||removeBusy)return;removingSubscription=id;$('removeSubscriptionNote').textContent='Удалить профиль «'+saved.name+'» и его сохранённые серверы? Остальные профили останутся.';openDialog($('removeDialog'));}
  $('removeSubscription').addEventListener('click',()=>removeSubscription(model.activeSubscription));$('cancelRemove').addEventListener('click',()=>closeDialog($('removeDialog')));
  $('confirmRemove').addEventListener('click',async()=>{
    if(removeBusy)return;removeBusy=true;$('confirmRemove').disabled=true;
    try{if(await request('remove',{id:removingSubscription})){closeDialog($('removeDialog'));if($('advancedDialog').open){closeDialog($('advancedDialog'));page('home');}toast('Профиль удалён');}}
    finally{removeBusy=false;$('confirmRemove').disabled=false;}
  });
  let editingSubscription='',editAttempt=0,editBusy=false,editLocal=false;
  async function editSubscription(id){
    if(editBusy)return;
    const attempt=++editAttempt;editingSubscription=id;$('editSubscriptionName').value='';$('editSubscriptionAddress').value='';$('editSubscriptionError').textContent='';
    $('editSubscriptionFields').disabled=true;$('saveSubscription').disabled=true;$('editSubscriptionSource').hidden=true;openDialog($('editSubscriptionDialog'));
    try{
      const saved=await rpc('readSubscription',{id});
      if(attempt!==editAttempt||!$('editSubscriptionDialog').open||$('editSubscriptionDialog').dataset.closing)return;
      editLocal=!!saved.local;$('editSubscriptionName').value=saved.name;$('editSubscriptionAddress').value=saved.address||'';
      $('editSubscriptionSource').hidden=editLocal;$('editSubscriptionAddress').required=!editLocal;
      $('editSubscriptionFields').disabled=false;$('saveSubscription').disabled=false;dialogUI.markClean($('editSubscriptionDialog'));$('editSubscriptionName').focus();
    }catch(e){if(attempt===editAttempt)$('editSubscriptionError').textContent=e.message;}
  }
  $('cancelEditSubscription').addEventListener('click',()=>requestClose($('editSubscriptionDialog')));
  $('editSubscriptionDialog').addEventListener('close',()=>{editAttempt++;editingSubscription='';$('editSubscriptionAddress').value='';});
  $('editSubscriptionForm').addEventListener('submit',async event=>{
    event.preventDefault();if(editBusy||!editingSubscription||$('saveSubscription').disabled||$('editSubscriptionDialog').dataset.closing)return;
    const name=$('editSubscriptionName').value.trim(),address=editLocal?'':$('editSubscriptionAddress').value.trim();
    if(!name){dialogUI.fieldError($('editSubscriptionName'),$('editSubscriptionError'),'Введите название профиля.');return;}
    if(!editLocal){try{const uri=new URL(address);if(uri.protocol!=='https:'||uri.username||uri.password||uri.hash)throw new Error();}catch{dialogUI.fieldError($('editSubscriptionAddress'),$('editSubscriptionError'),'Нужна HTTPS-ссылка без логина, пароля и фрагмента.');return;}}
    const attempt=editAttempt;editBusy=true;$('saveSubscription').disabled=true;$('editSubscriptionFields').disabled=true;$('editSubscriptionError').textContent='';
    try{await rpc('editSubscription',{id:editingSubscription,name,address});if(attempt===editAttempt){closeDialog($('editSubscriptionDialog'));toast('Профиль сохранён');}}
    catch(e){if(attempt===editAttempt)$('editSubscriptionError').textContent=e.message;}
    finally{editBusy=false;if(attempt===editAttempt){$('saveSubscription').disabled=false;$('editSubscriptionFields').disabled=false;}}
  });
  $('automationButton').addEventListener('click',()=>{const a=model.automation||{reconnect:true,refreshHours:6,pingMinutes:0,autoMinutes:3,autoToleranceMs:50};$('reconnectSetting').setAttribute('aria-checked',a.reconnect);$('refreshHours').value=a.refreshHours;$('backgroundPing').value=a.pingMinutes;$('autoMinutes').value=a.autoMinutes;$('autoTolerance').value=a.autoToleranceMs;$('automationError').textContent='';openDialog($('automationDialog'));});
  $('reconnectSetting').addEventListener('click',()=>{$('reconnectSetting').setAttribute('aria-checked',$('reconnectSetting').getAttribute('aria-checked')!=='true');});
  $('closeAutomation').addEventListener('click',()=>requestClose($('automationDialog')));
  $('automationForm').addEventListener('submit',async e=>{e.preventDefault();const b=$('automationForm').querySelector('[type=submit]');b.disabled=true;try{await rpc('automationSettings',{reconnect:$('reconnectSetting').getAttribute('aria-checked')==='true',refreshHours:Number($('refreshHours').value),pingMinutes:Number($('backgroundPing').value),autoMinutes:Number($('autoMinutes').value),autoToleranceMs:Number($('autoTolerance').value)});closeDialog($('automationDialog'));toast('Сохранено');}catch(e){$('automationError').textContent=e.message;}finally{b.disabled=false;}});
  $('backupButton').addEventListener('click',()=>{if(!dialogUI.isDirty($('advancedDialog')))closeDialog($('advancedDialog'));$('backupPassword').value='';$('backupError').textContent='';openDialog($('backupDialog'));});
  $('closeBackup').addEventListener('click',()=>requestClose($('backupDialog')));$('backupDialog').addEventListener('close',()=>{$('backupPassword').value='';});
  for(const [id,action] of [['exportBackup','backupExport'],['restoreBackup','backupImport']])$(id).addEventListener('click',async()=>{const password=$('backupPassword').value;if(password.length<8){dialogUI.fieldError($('backupPassword'),$('backupError'),'Пароль от 8 символов.');return;}for(const id of ['exportBackup','restoreBackup'])$(id).disabled=true;try{await rpc(action,{password});closeDialog($('backupDialog'));}catch(e){$('backupError').textContent=e.message;}finally{for(const id of ['exportBackup','restoreBackup'])$(id).disabled=false;}});
  function dismissDialog(dialog){
    if(dialog===$('advancedDialog')&&!$('deviceDialog').hidden)closeDevice();
    else requestClose(dialog);
  }
  function onBackdrop(event,dialog){
    if(event.target!==dialog)return false;
    const rect=dialog.getBoundingClientRect();
    return event.clientX<rect.left||event.clientX>=rect.right||event.clientY<rect.top||event.clientY>=rect.bottom;
  }
  // Handle Escape before the browser's close watcher so repeated Escape preserves
  // nested navigation, dirty confirmation and the animated close path consistently.
  document.addEventListener('keydown',event=>{
    if(event.key!=='Escape'||event.defaultPrevented)return;
    const dialog=openDialogs.findLast(item=>item.open);if(!dialog)return;
    event.preventDefault();dismissDialog(dialog);
  });
  for(const dialog of document.querySelectorAll('dialog')){
    dialog.addEventListener('cancel',event=>{event.preventDefault();dismissDialog(dialog);});
    if(dialog.id==='discardDialog'||dialog.matches('[data-dirty-form]:not([data-backdrop-close])'))continue;
    let pressedBackdrop=false;
    dialog.addEventListener('pointerdown',event=>{pressedBackdrop=event.button===0&&onBackdrop(event,dialog);});
    dialog.addEventListener('click',event=>{
      if(pressedBackdrop&&onBackdrop(event,dialog))dismissDialog(dialog);
      pressedBackdrop=false;
    });
  }
  document.querySelectorAll('[data-window]').forEach(b=>b.addEventListener('click',()=>request(b.dataset.window)));
  document.querySelector('.titlebar').addEventListener('mousedown',e=>{if(e.button===0&&!e.target.closest('button'))request('drag',{double:e.detail===2});});
  document.querySelectorAll('[data-edge]').forEach(b=>b.addEventListener('mousedown',e=>{if(e.button===0)request('resize',{edge:b.dataset.edge});}));
  $('statisticsBack').addEventListener('click',()=>{page('settings');$('statisticsButton').focus({preventScroll:true});});
  document.querySelectorAll('[data-back-settings]').forEach(button=>button.addEventListener('click',()=>{const from=current;page('settings');document.querySelector('.settings-tools [data-page='+from+']').focus({preventScroll:true});}));
  const serviceTools=window.KotServices({getModel:()=>model,getPage:()=>current,rpc,toast,openDialog,closeDialog,requestClose});
  window.KotAbout({getModel:()=>model,openDialog,closeDialog});
  const clientTools=window.KotClientTools({getModel:()=>model,getPage:()=>current,rpc,toast,openDialog,closeDialog,page});
  const nodeJson=window.KotNodeJson({getModel:()=>model,rpc,toast,openDialog,closeDialog,share:clientTools.share,animate,cancelMotion});
  const statistics=window.KotStatistics({getModel:()=>model,getPage:()=>current,renderDetails:period=>clientTools.renderStatistics(period)});
  const journal=window.KotJournal({getModel:()=>model,getPage:()=>current,request,toast});
  const routing=window.KotRouting({getModel:()=>model,rpc,toast,dialogUI,openDialog,closeDialog,requestClose});
  new ResizeObserver(moveNav).observe(document.querySelector('.nav'));renderServers();moveNav();
  if(host){bridge.start({snapshot:apply,navigate:page,notice:toast});request('ready');}else{document.body.classList.remove('booting');$('startupScreen').hidden=true;document.querySelector('.main').inert=false;document.querySelector('.sidebar').inert=false;toast('Откройте интерфейс через Kot.exe.');}
})();
