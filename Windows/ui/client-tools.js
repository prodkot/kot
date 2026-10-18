/* Optional pages share the same host bridge as the connection screen. */
'use strict';
window.KotClientTools = ({getModel,getPage,getPeriod,rpc,toast,openDialog,closeDialog,page}) => {
  const $=id=>document.getElementById(id);
  const formatBytes=n=>{n=Math.max(0,Number(n)||0);const units=['Б','КиБ','МиБ','ГиБ','ТиБ'];let i=0;while(n>=1024&&i<4){n/=1024;i++;}return new Intl.NumberFormat('ru-RU',{maximumFractionDigits:i?1:0}).format(n)+' '+units[i];};
  const routeName=r=>({vpn:'Через VPN',direct:'Напрямую',unknown:'Маршрут не определён'}[r]||'Не определено');
  let qrNode='',qrAttempt=0,importing=false,toolsStarting=false,sharing=false,lastStatisticsKey='',lastNodeKey='',lastResultKey='';
  async function perform(action,data={}) {try{return await rpc(action,data);}catch(e){toast(e.message);return null;}}
  function nodeOptions(){
    const model=getModel(),nodes=model.nodes||[],key=JSON.stringify(nodes.map(n=>[n.id,n.name]));
    if(key===lastNodeKey)return;lastNodeKey=key;
    for(const id of ['toolsNode']){
      const el=$(id),selected=el.value;el.replaceChildren(...nodes.map(n=>{const option=document.createElement('option');option.value=n.id;option.textContent=n.name;return option;}));
      el.value=nodes.some(n=>n.id===selected)?selected:nodes.some(n=>n.id===model.selected)?model.selected:nodes[0]?.id||'';
    }
  }
  function renderSubscription(){
    const model=getModel(),u=model.subscriptionUsage;$('subscriptionInfo').hidden=!model.hasSubscription;
    $('subscriptionChecked').textContent=u?.checkedAt?'Обновлено '+new Date(u.checkedAt).toLocaleDateString('ru-RU'):'';
    $('subscriptionTraffic').textContent=model.localSubscription?'Локальные серверы: данные провайдера отсутствуют.':!u?'Провайдер не передал лимит трафика и срок подписки.':(u.used!=null?'Использовано '+formatBytes(u.used):'Использованный трафик не указан')+(u.total>0?' из '+formatBytes(u.total):' · лимит не указан')+(u.remaining!=null?' · осталось '+formatBytes(u.remaining):'');
    const meter=$('subscriptionMeter');meter.hidden=!(u?.total>0&&u.used!=null);if(!meter.hidden){meter.max=u.total;meter.value=Math.min(u.total,u.used);}
    $('subscriptionExpires').textContent=u?.expires?'Действует до '+new Date(u.expires).toLocaleString('ru-RU')+(u.daysLeft>0?' · '+u.daysLeft+' дн.':''):model.localSubscription?'':'Срок действия не указан';
    $('subscriptionWarning').hidden=u?.daysLeft==null||u.daysLeft>3;$('subscriptionWarning').textContent=u?.daysLeft<=0?'Срок подписки закончился.':u?.daysLeft!=null?'Подписка заканчивается через '+u.daysLeft+' дн.':'';
  }
  function render(){
    nodeOptions();renderSubscription();const model=getModel(),t=model.tools||{},busy=!!t.busy||toolsStarting,quality=t.kind==='connection';
    $('qualityButton').disabled=model.state!=='connected';$('qualityButton').title=model.state==='connected'?'Проверить адрес через подключённый VPN':'Сначала подключите VPN';
    $('toolsNode').disabled=busy||model.state==='connected';$('toolsWebsite').disabled=busy;
    $('toolsConnectionNote').textContent=model.state==='connected'?'Проверяется текущее VPN-соединение.':'При отключённом VPN проверяется выбранный сервер без изменения подключения.';
    $('runDiagnostics').disabled=busy||!model.nodes?.length;$('cancelTools').hidden=!t.busy;$('copyDiagnostics').disabled=!t.steps?.length||busy;
    $('toolsStatus').textContent=t.status?(t.server?t.server+' · ':'')+t.status:'Запустите проверку, чтобы найти причину проблем с соединением.';
    $('toolsError').textContent=t.error||'';$('toolsError').hidden=!t.error;
    const key=JSON.stringify(t.steps||[]);if(key!==lastResultKey){lastResultKey=key;$('toolsResults').replaceChildren(...(t.steps||[]).map(s=>{const row=document.createElement('div');row.className='tool-result';row.dataset.status=s.status;const name=document.createElement('strong');name.textContent=s.name+(s.status==='ok'?' · OK':s.status==='error'?' · Ошибка':s.status==='checking'?' · Проверка':s.status==='cancelled'?' · Отменено':'');const detail=document.createElement('p');detail.textContent=s.detail;row.append(name,detail);return row;}));}
    $('testQuality').disabled=busy||model.state!=='connected';$('cancelQuality').hidden=!t.busy||!quality;
    const q=quality?t.quality:null,settings=model.ping?.settings||{};
    $('qualityAddress').textContent=quality&&t.address?t.address:settings.url||'https://www.gstatic.com/generate_204';
    $('qualityNote').textContent=model.state==='connected'?'HTTP-проверка через подключённый VPN. Адрес, число попыток и таймаут берутся из настроек пинга.':'Подключите VPN, чтобы проверить адрес.';
    $('qualityMedian').textContent=q?.medianMs!=null?q.medianMs+' мс':'Нет данных';
    $('qualitySuccess').textContent=q?q.successes+' / '+q.attempts+' ('+Math.round(q.successes/q.attempts*100)+'%)':'Нет данных';
    $('qualitySamples').replaceChildren(...(quality?t.samples||[]:[]).map((ms,i)=>{const el=document.createElement('span');el.textContent=ms==null?'Таймаут / ошибка':ms+' мс';el.dataset.error=ms==null;el.title='Запрос '+(i+1);return el;}));
    $('qualityStatus').textContent=quality?(t.server?t.server+' · ':'')+(t.status||''):'';
    $('qualityError').textContent=quality?t.error||'':'';$('qualityError').hidden=!$('qualityError').textContent;
  }
  $('qualityButton').addEventListener('click',()=>{render();openDialog($('qualityDialog'));startCheck();});
  $('closeQuality').addEventListener('click',()=>closeDialog($('qualityDialog')));
  $('qualityDialog').addEventListener('close',()=>{if(getModel().tools?.busy&&getModel().tools.kind==='connection')perform('cancelTools');});
  for(const id of ['cancelTools','cancelQuality'])$(id).addEventListener('click',()=>perform('cancelTools'));
  $('copyDiagnostics').addEventListener('click',async()=>{try{await rpc('copyDiagnostics');toast('Результат скопирован');}catch(e){toast(e.message);}});
  async function start(action,id){if(toolsStarting||getModel().tools?.busy)return;const text=$('toolsWebsite').value.trim();try{const url=new URL(text);if(text.length>2048||url.protocol!=='https:'||url.username||url.password||url.hash)throw new Error();}catch{toast('Нужен HTTPS-адрес без логина, пароля и фрагмента');return;}toolsStarting=true;render();try{await rpc(action,{id,url:text});}catch(e){toast(e.message);}finally{toolsStarting=false;render();}}
  $('toolsForm').addEventListener('submit',e=>{e.preventDefault();start('runDiagnostics',$('toolsNode').value);});
  async function startCheck(){
    if(toolsStarting||getModel().tools?.busy||getModel().state!=='connected')return;
    toolsStarting=true;render();
    try{await rpc('testConnection');}catch(e){toast(e.message);}finally{toolsStarting=false;render();}
  }
  $('testQuality').addEventListener('click',startCheck);
  async function share(id){
    if(sharing)return;sharing=true;qrNode=id;const attempt=++qrAttempt;$('shareQrImage').hidden=true;$('shareQrImage').removeAttribute('src');$('shareQrLink').value='';$('shareQrError').textContent='';$('shareQrTitle').textContent='QR сервера';openDialog($('shareQrDialog'));
    try{const result=await rpc('readNodeQr',{id});if(attempt!==qrAttempt||!$('shareQrDialog').open)return;$('shareQrTitle').textContent=result.name;$('shareQrLink').value=result.link;$('shareQrImage').src=result.image;$('shareQrImage').hidden=false;}
    catch(e){if(attempt===qrAttempt&&$('shareQrDialog').open)$('shareQrError').textContent=e.message;}finally{sharing=false;}
  }
  $('closeShareQr').addEventListener('click',()=>closeDialog($('shareQrDialog')));$('shareQrDialog').addEventListener('close',()=>{qrAttempt++;qrNode='';$('shareQrImage').removeAttribute('src');$('shareQrLink').value='';});
  for(const action of ['copyNodeLink','copyNodeQr','saveNodeQr'])$(action).addEventListener('click',async()=>{if(!qrNode)return;try{await rpc(action,{id:qrNode});if(action!=='saveNodeQr')toast('Скопировано');}catch(e){$('shareQrError').textContent=e.message;}});
  function setImportBusy(value){importing=value;for(const id of ['importServersFile','readQrFile','readQrClipboard'])$(id).disabled=value;$('addForm').querySelector('[type=submit]').disabled=value;}
  for(const action of ['readQrFile','readQrClipboard'])$(action).addEventListener('click',async()=>{if(importing)return;setImportBusy(true);$('addError').textContent='';try{const result=await rpc(action);if(result?.text&&$('addDialog').open&&!$('addDialog').dataset.closing){$('profileLink').value=result.text;$('profileLink').focus();}}catch(e){if($('addDialog').open)$('addError').textContent=e.message;}finally{setImportBusy(false);}});
  $('importServersFile').addEventListener('click',async()=>{if(importing)return;setImportBusy(true);$('addError').textContent='';const before=getModel().activeSubscription;try{await rpc('importServersFile',{name:$('profileName').value.trim()});if(getModel().activeSubscription!==before){closeDialog($('addDialog'));page('servers');toast(getModel().warnings?.length?'Добавлено. Пропущено: '+getModel().warnings.length:'Серверы добавлены');}}catch(e){$('addError').textContent=e.message;}finally{setImportBusy(false);}});
  function paint(points,bars,labels){
    const canvas=$('trafficChart'),rect=canvas.getBoundingClientRect();if(rect.width<1)return;const scale=Math.min(2,devicePixelRatio||1),width=Math.round(rect.width),height=170;canvas.width=width*scale;canvas.height=height*scale;const ctx=canvas.getContext('2d');ctx.scale(scale,scale);
    const style=getComputedStyle(document.body),muted=style.getPropertyValue('--muted').trim(),down=style.getPropertyValue('--detail').trim();const up='#7398dc',top=15,bottom=height-25,left=45,right=width-8;
    const values=points.flatMap(p=>[p.download,p.upload]).filter(Number.isFinite),max=Math.max(1,...values);
    ctx.font='10px Manrope, sans-serif';ctx.fillStyle=muted;ctx.fillText(formatBytes(max)+(bars?'':'/с'),0,10);ctx.fillText('0',19,bottom+4);
    ctx.strokeStyle=style.getPropertyValue('--line').trim();ctx.beginPath();ctx.moveTo(left,bottom);ctx.lineTo(right,bottom);ctx.stroke();
    if(points.length){
      const step=(right-left)/Math.max(1,bars?points.length:points.length-1);
      for(const [key,color] of [['download',down],['upload',up]]){
        ctx.strokeStyle=color;ctx.fillStyle=color;ctx.lineWidth=2;
        if(bars){points.forEach((p,i)=>{if(!Number.isFinite(p[key]))return;const h=(bottom-top)*p[key]/max,w=Math.max(1,step*.3),x=left+i*step+(key==='upload'?step*.5:step*.14);ctx.fillRect(x,bottom-h,w,h);});}
        else{ctx.beginPath();let joined=false;points.forEach((p,i)=>{if(!Number.isFinite(p[key])){joined=false;return;}const x=left+i*step,y=bottom-(bottom-top)*p[key]/max;if(joined)ctx.lineTo(x,y);else ctx.moveTo(x,y);joined=true;});ctx.stroke();points.forEach((p,i)=>{if(!Number.isFinite(p[key]))return;ctx.beginPath();ctx.arc(left+i*step,bottom-(bottom-top)*p[key]/max,2,0,Math.PI*2);ctx.fill();});}
      }
      ctx.fillStyle=muted;ctx.fillText(labels[0]||'',left,height-5);const end=labels[1]||'';ctx.fillText(end,right-ctx.measureText(end).width,height-5);
    }
    canvas.setAttribute('aria-label',(bars?'Трафик по дням':'Скорость')+'. Скачано и отправлено. Максимум '+formatBytes(max)+(bars?'':' в секунду'));
  }
  function renderStatistics(period){
    if(getPage()!=='statistics')return;const model=getModel(),detail=model.statistics?.detail;
    const today=new Date(),dayKey=d=>d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0'),lastDay=dayKey(today),first=new Date(today);first.setDate(first.getDate()-(period==='week'?6:29));const firstDay=dayKey(first);
    const key=JSON.stringify([period,detail,lastDay,getModel().settings?.theme,$('trafficChart').clientWidth]);if(key===lastStatisticsKey)return;lastStatisticsKey=key;
    const entries=detail?.[period]||[],routeTotals=new Map(),apps=new Map();for(const e of entries){routeTotals.set(e.route,(routeTotals.get(e.route)||0)+e.download+e.upload);const old=apps.get(e.app)||{download:0,upload:0,routes:new Set()};old.download+=e.download;old.upload+=e.upload;old.routes.add(routeName(e.route));apps.set(e.app,old);}
    $('trafficRouteTotals').replaceChildren(...['vpn','direct','unknown'].map(route=>{const el=document.createElement('span');el.textContent=routeName(route)+': '+formatBytes(routeTotals.get(route)||0);return el;}));
    const ordered=[...apps.entries()].sort((a,b)=>(b[1].download+b[1].upload)-(a[1].download+a[1].upload));$('trafficApps').replaceChildren(...ordered.map(([app,e])=>{const row=document.createElement('div');row.className='traffic-app';const name=document.createElement('strong');name.textContent=app;name.title=[...e.routes].join(', ');const values=document.createElement('span');values.textContent='↓ '+formatBytes(e.download)+' · ↑ '+formatBytes(e.upload);row.append(name,values);return row;}));
    if(!ordered.length){const p=document.createElement('p');p.textContent='За этот период подробных данных пока нет.';$('trafficApps').append(p);}
    $('trafficAttribution').textContent=(detail?.firstRecorded?'Подробный учёт с '+new Date(detail.firstRecorded).toLocaleString('ru-RU')+'. ':'')+'Учтён трафик, прошедший через ядро. Короткие соединения и неизвестные приложения попадают в «Не определено». В режиме системного прокси часть процессов не определяется.';
    $('trafficDetailsError').textContent=detail?.error||'';$('trafficDetailsError').hidden=!detail?.error;
    const days=(detail?.days||[]).filter(d=>d.date>=firstDay&&d.date<=lastDay).sort((a,b)=>a.date.localeCompare(b.date));$('trafficDays').replaceChildren(...days.slice().reverse().map(d=>{const row=document.createElement('tr');for(const value of [new Date(d.date+'T12:00:00').toLocaleDateString('ru-RU'),formatBytes(d.download),formatBytes(d.upload),formatBytes(d.download+d.upload)]){const cell=document.createElement('td');cell.textContent=value;row.append(cell);}return row;}));$('trafficDaysEmpty').hidden=!!days.length;
    const session=period==='session',points=session?detail?.speeds||[]:days;$('trafficChartTitle').textContent=session?'Скорость за последние 3 минуты':'Трафик по дням'+(period==='allTime'?' · последние 30 дней':'');
    $('trafficChartCaption').textContent=points.length?'Скачано: цвет акцента · отправлено: синий. '+(session?'Пробелы означают отсутствие измерений.':'Суммарный трафик ядра; итоги выше включают выбранный период.'):'График появится после накопления данных.';
    const labels=session?(points.length?[new Date(points[0].time).toLocaleTimeString('ru-RU'),new Date(points[points.length-1].time).toLocaleTimeString('ru-RU')]:[]):days.length?[new Date(days[0].date+'T12:00:00').toLocaleDateString('ru-RU'),new Date(days[days.length-1].date+'T12:00:00').toLocaleDateString('ru-RU')]:[];paint(points,!session,labels);
  }
  new ResizeObserver(()=>{lastStatisticsKey='';if(getPage()==='statistics')renderStatistics(getPeriod());}).observe($('trafficChart'));
  return {render,renderStatistics,share};
};
