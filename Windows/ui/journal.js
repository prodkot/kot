/* Reader-owned pause, selection and follow state. */
'use strict';
window.KotJournal = ({getModel,getPage,request,toast}) => {
  const $=id=>document.getElementById(id);
  let logsPaused=false,logsFollowing=true,logSnapshot=null;
  function latestJournal(){return typeof getModel().journal==='string'?getModel().journal:logSnapshot||'';}
  function journalAtBottom(){const box=$('journalText');return box.scrollHeight-box.scrollTop-box.clientHeight<=2;}
  function journalSelected(){
    const selection=window.getSelection();
    return selection&&!selection.isCollapsed&&selection.containsNode($('journalText'),true);
  }
  function renderLogs(refresh=false){
    const box=$('journalText');
    // Keep the actual text node while reading, including when the host's bounded
    // tail rotates. Retaining scrollTop alone moves the text under the reader.
    if(refresh){
      if(logSnapshot!==null&&(!journalAtBottom()||journalSelected()))logsFollowing=false;
      if(logSnapshot===null||(!logsPaused&&logsFollowing))logSnapshot=latestJournal();
    }
    const query=$('logSearch').value.trim().toLocaleLowerCase('ru'),level=$('logLevel').value;
    const text=(logSnapshot||'').split('\n').filter(line=>
      (!query||line.toLocaleLowerCase('ru').includes(query))&&
      (level==='all'||(level==='error'?/error|fatal|exception|ошибк/i:/warn|предупреж/i).test(line))).join('\n');
    if(box.textContent!==text){box.textContent=text;if(logsFollowing&&!logsPaused)box.scrollTop=box.scrollHeight;}
    $('logStatus').textContent=logsPaused?'На паузе':logsFollowing?'Журнал текущего запуска':'Чтение журнала';
    $('latestLogs').hidden=logsFollowing&&!logsPaused;
    $('latestLogs').textContent=latestJournal()!==(logSnapshot||'')?'К новым записям':'В конец журнала';
    $('pauseLogs').setAttribute('aria-pressed',logsPaused);$('pauseLogs').textContent=logsPaused?'Продолжить':'Пауза';
    $('logEmpty').hidden=!!text.trim();$('logEmpty').textContent=query||level!=='all'?'Ничего не найдено':'Нет событий';
  }
  function followJournal(){
    logsPaused=false;logsFollowing=true;logSnapshot=latestJournal();
    if(journalSelected())window.getSelection().removeAllRanges();
    renderLogs();$('journalText').scrollTop=$('journalText').scrollHeight;
  }
  function holdJournal(){if(getPage()==='logs'&&logsFollowing){logsFollowing=false;renderLogs();}}
  $('journalText').addEventListener('scroll',()=>{if(!journalAtBottom())holdJournal();});
  document.addEventListener('selectionchange',()=>{if(journalSelected())holdJournal();});
  $('latestLogs').addEventListener('click',()=>{followJournal();$('journalText').focus({preventScroll:true});});
  $('logSearch').addEventListener('input',()=>renderLogs());$('logLevel').addEventListener('change',()=>renderLogs());
  $('pauseLogs').addEventListener('click',()=>{if(logsPaused)followJournal();else{logsPaused=true;renderLogs();}});
  $('copyViewLog').addEventListener('click',async()=>{if(await request('copyLog'))toast('Полный лог скопирован');});
  return {render:renderLogs};
};
