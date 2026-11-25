/* Routing and GeoIP forms. Validation/application remain native operations. */
'use strict';
window.KotRouting = ({getModel,rpc,toast,dialogUI,openDialog,closeDialog,requestClose}) => {
  const $=id=>document.getElementById(id);
  let routingDraftId=null,routingBusy=false,routingDirty=false,routingRemovingId='';
  let geoIpBusy=false,geoIpDirty=false,geoIpRequestError='';
  const routingLists=['directDomains','proxyDomains','blockDomains','directIps','proxyIps','blockIps','directApps','proxyApps','blockApps','directCountries','proxyCountries','blockCountries'];
  const routingField=key=>$('routing'+key[0].toUpperCase()+key.slice(1));
  function routingSaved(){return (getModel().routing?.profiles||[]).find(p=>p.id===routingDraftId);}
  function routingTab(name,focus=false){
    for(const button of document.querySelectorAll('[data-routing-tab]')){const selected=button.dataset.routingTab===name;button.setAttribute('aria-selected',selected);button.tabIndex=selected?0:-1;if(selected&&focus)button.focus();}
    for(const panel of document.querySelectorAll('[data-routing-panel]'))panel.hidden=panel.dataset.routingPanel!==name;
  }
  function renderRouting(){
    const routing=getModel().routing||{active:'',profiles:[]},saved=routingSaved(),active=routing.profiles.find(p=>p.id===routing.active);
    const provider=getModel().selected!=='auto'&&getModel().nodes.some(n=>n.providerRouting&&n.id===getModel().selected),defaultLabel=provider?'Правила подписки':'По умолчанию';
    $('routingSummary').textContent='Маршрут: '+(active?.rules.name||(provider?'правила подписки':'по умолчанию'));
    $('routingActive').replaceChildren(new Option(defaultLabel,''),...routing.profiles.map(p=>new Option((p.sourceUrl?'Обновляемый · ':'')+p.rules.name,p.id)));
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
    routingTab('domains');renderRouting();dialogUI.markClean($('routingDialog'));
  }
  function openRouting(){loadRouting(getModel().routing?.active||null);openDialog($('routingDialog'));}
  for(const id of ['routingButton','homeRoutingButton'])$(id).addEventListener('click',openRouting);
  $('closeRouting').addEventListener('click',()=>requestClose($('routingDialog')));
  $('routingNew').addEventListener('click',()=>{loadRouting('');$('routingName').focus();});
  $('routingReset').addEventListener('click',()=>loadRouting(getModel().routing?.active||null));
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
    const data=getModel().geoIp||{countries:[],refreshHours:24},busy=geoIpBusy||data.busy;
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
  $('closeGeoIp').addEventListener('click',()=>requestClose($('geoIpDialog')));
  $('geoIpHours').addEventListener('input',()=>geoIpDirty=true);
  $('geoIpForm').addEventListener('submit',async event=>{
    event.preventDefault();if(geoIpBusy||getModel().geoIp?.busy)return;
    const refreshHours=Number($('geoIpHours').value);geoIpBusy=true;geoIpRequestError='';renderGeoIp();
    try{await rpc('geoIpSettings',{refreshHours});geoIpDirty=false;dialogUI.markClean($('geoIpDialog'));toast('Интервал обновления GeoIP сохранён');}
    catch(e){geoIpRequestError=e.message;}
    finally{geoIpBusy=false;renderGeoIp();}
  });
  $('geoIpRefresh').addEventListener('click',async()=>{
    if(geoIpBusy||getModel().geoIp?.busy)return;geoIpBusy=true;geoIpRequestError='';renderGeoIp();
    try{await rpc('geoIpRefresh');geoIpRequestError='';toast('Базы GeoIP обновлены');}
    catch(e){geoIpRequestError=getModel().geoIp?.error?'':e.message;}
    finally{geoIpBusy=false;renderGeoIp();}
  });
  $('routingForm').addEventListener('submit',async event=>{
    event.preventDefault();if(routingBusy)return;
    const rules={version:1,name:$('routingName').value.trim(),defaultAction:$('routingDefault').value,directDns:$('routingDirectDns').value,remoteDns:$('routingRemoteDns').value};
    for(const key of routingLists)rules[key]=routingField(key).value.split(/[\r\n,;]+/).map(v=>v.trim()).filter(Boolean);
    routingBusy=true;$('routingError').textContent='';renderRouting();
    try{await rpc('routingSave',{id:routingDraftId||'',rules,sourceUrl:$('routingSource').value.trim(),refreshHours:Number($('routingHours').value)});loadRouting(getModel().routing?.active||null);toast('Маршрутизация сохранена и применена');}
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
  $('cancelRoutingImport').addEventListener('click',()=>requestClose($('routingImportDialog')));
  $('routingImportForm').addEventListener('submit',async event=>{
    event.preventDefault();if(routingBusy)return;routingBusy=true;renderRouting();
    const submit=$('routingImportForm').querySelector('[type=submit]');submit.disabled=true;$('routingImportError').textContent='';
    try{await rpc('routingImport',{content:$('routingImportContent').value.trim()});loadRouting(getModel().routing?.active||null);closeDialog($('routingImportDialog'));toast('Профиль маршрутизации добавлен');}
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
    try{await rpc('routingRemove',{id:routingRemovingId});loadRouting(getModel().routing?.active||null);closeDialog($('routingRemoveDialog'));toast('Профиль маршрутизации удалён');}
    catch(e){$('routingRemoveError').textContent=e.message;}
    finally{routingBusy=false;b.disabled=false;renderRouting();}
  });
  return {render:renderRouting};
};
