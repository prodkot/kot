/* Confirmed-host session clock and statistics controls. */
'use strict';
window.KotStatistics = ({getModel,getPage,renderDetails}) => {
  const $=id=>document.getElementById(id),{bytes}=window.KotPresentation;
  function renderTraffic(){const t=getModel().telemetry||{},on=getModel().state==='connected';$('downloadRate').textContent=on?bytes(t.available?t.downloadRate:null,true):'0 Б/с';$('uploadRate').textContent=on?bytes(t.available?t.uploadRate:null,true):'0 Б/с';$('downloadRate').title='За сеанс: '+bytes(t.downloadTotal||0);$('uploadRate').title='За сеанс: '+bytes(t.uploadTotal||0);}
  let statisticsPeriod='session',statisticsAnchor=0,statisticsElapsed=0;
  function duration(seconds){
    if(!Number.isFinite(seconds)||seconds<0)return 'Нет данных';
    const value=Math.floor(seconds),days=Math.floor(value/86400),pad=n=>String(n).padStart(2,'0');
    return (days?days+' д ':'')+pad(Math.floor(value/3600)%24)+':'+pad(Math.floor(value/60)%60)+':'+pad(value%60);
  }
  function renderSessionClock(snapshot=false){
    const s=getModel().statistics,running=!!s?.running&&getModel().state==='connected';
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
    renderDetails(statisticsPeriod);
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
  setInterval(()=>{if(getPage()==='statistics')renderStatistics();else if(getPage()==='home')renderSessionClock();},250);
  return {render:renderStatistics,renderTraffic};
};
