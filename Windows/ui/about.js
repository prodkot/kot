/* The optional music library is independent of app releases. No tokens or host RPC. */
'use strict';
window.KotAbout = ({getModel,openDialog,closeDialog}) => {
  const $=id=>document.getElementById(id),audio=$('aboutAudio');
  const endpoint='https://api.github.com/repos/prodkot/kot/contents/music?ref=music';
  const root='https://raw.githubusercontent.com/prodkot/kot/music/music/';
  let tracks=[],current=null,loadedAt=0,loading=null,controller=null,wantsPlay=false,attempt=0;
  audio.volume=.25;
  const demo=Object.hasOwn(document.body.dataset,'demoVersion');
  function note(text=''){$('musicStatus').textContent=text;$('musicStatus').hidden=!text;}
  function render(){
    const playing=wantsPlay;
    $('musicPlay').setAttribute('aria-label',playing?'Пауза':'Воспроизвести');
    $('musicPlay').title=playing?'Пауза':'Воспроизвести';
    $('musicPlayIcon').setAttribute('d',playing?'M9 5v14M15 5v14':'m9 5 11 7-11 7Z');
    $('musicNext').disabled=!tracks.length||!!loading;
    $('musicTitle').textContent=current?.title||'Немного музыки';
    $('musicTitle').title=current?.title||'';
  }
  async function library(){
    if(loading)return loading;
    if(loadedAt&&Date.now()-loadedAt<300000)return;
    controller=new AbortController();const timer=setTimeout(()=>controller?.abort(),10000);
    loading=(async()=>{
      try{
        const response=await fetch(endpoint,{cache:'no-cache',credentials:'omit',referrerPolicy:'no-referrer',redirect:'error',signal:controller.signal,headers:{Accept:'application/vnd.github+json'}});
        if(!response.ok)throw Error();
        // Keep response memory bounded even for a misconfigured remote directory.
        const reader=response.body.getReader(),parts=[];let length=0;
        try{for(;;){const {done,value}=await reader.read();if(done)break;length+=value.length;if(length>524288){await reader.cancel();throw Error();}parts.push(value);}}finally{reader.releaseLock();}
        const buffer=new Uint8Array(length);let offset=0;for(const part of parts){buffer.set(part,offset);offset+=part.length;}
        const data=JSON.parse(new TextDecoder().decode(buffer));if(!Array.isArray(data))throw Error();
        tracks=data.filter(f=>f.type==='file'&&typeof f.name==='string'&&f.name.length<=160&&!/[\\/\u0000-\u001f]/.test(f.name)&&/\.(mp3|ogg|m4a|wav|flac|webm)$/i.test(f.name)&&Number.isSafeInteger(f.size)&&f.size>0&&f.size<=20*1024*1024&&/^[a-f0-9]{40}$/.test(f.sha)&&f.path==='music/'+f.name).slice(0,100).map(f=>({id:f.name,title:f.name.replace(/\.[^.]+$/,''),url:root+encodeURIComponent(f.name)+'?v='+f.sha}));
        loadedAt=Date.now();note(tracks.length?'':'Музыка скоро появится.');
      }catch(e){if(e.name!=='AbortError')note(tracks.length?'Не удалось обновить список. Доступны прежние треки.':'Музыка пока недоступна. Нажми воспроизведение, чтобы повторить.');}
      finally{clearTimeout(timer);controller=null;loading=null;render();}
    })();render();return loading;
  }
  function choose(){
    const pool=tracks.filter(t=>t.id!==current?.id),choices=pool.length?pool:tracks;
    if(!choices.length)return false;
    current=choices[Math.floor(Math.random()*choices.length)];audio.src=current.url;render();return true;
  }
  async function play(next=false){
    const token=++attempt;wantsPlay=true;render();
    // A paused track retains its source and position, even if the online pool changed.
    if(next||!current){await library();if(token!==attempt||!wantsPlay)return;if(!choose()){wantsPlay=false;render();return;}}
    try{await audio.play();if(!wantsPlay)audio.pause();}
    catch{if(token===attempt){wantsPlay=false;note('Не удалось включить трек. Попробуй ещё раз или выбери другой.');render();}}
  }
  function pause(){attempt++;wantsPlay=false;audio.pause();render();}
  $('aboutButton').addEventListener('click',()=>{
    $('aboutVersion').textContent='Версия '+(getModel().version||'');openDialog($('aboutDialog'));
    if(demo){note('Музыка доступна в приложении.');$('musicPlay').disabled=true;return;}
    library();
  });
  $('musicPlay').addEventListener('click',()=>wantsPlay?pause():play());
  $('musicNext').addEventListener('click',()=>{pause();play(true);});
  audio.addEventListener('ended',()=>{if(wantsPlay&&$('aboutDialog').open&&!$('aboutDialog').dataset.closing){wantsPlay=false;play(true);}});
  audio.addEventListener('playing',()=>{if(!wantsPlay){audio.pause();return;}note();render();});
  audio.addEventListener('error',()=>{if(!current)return;pause();note('Трек недоступен. Попробуй ещё раз или выбери другой.');});
  audio.addEventListener('timeupdate',()=>{
    const t=Number.isFinite(audio.currentTime)?audio.currentTime:0;
    $('musicTime').textContent=Math.floor(t/60)+':'+String(Math.floor(t%60)).padStart(2,'0');
    $('musicProgress').value=Number.isFinite(audio.duration)&&audio.duration>0?t/audio.duration:0;
  });
  $('closeAbout').addEventListener('click',()=>closeDialog($('aboutDialog')));
  $('aboutDialog').addEventListener('close',()=>{pause();controller?.abort();});
  $('astraCredit').addEventListener('click',()=>openDialog($('astraDialog')));
  $('closeAstra').addEventListener('click',()=>closeDialog($('astraDialog')));
  window.addEventListener('pagehide',()=>{pause();controller?.abort();});
  render();
};
