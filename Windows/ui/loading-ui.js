(() => {
  'use strict';
  const data=window.KotLoadingMotion, motions=data.motions, rest=[0,0,0,0];
  const glyph='M138 0 140 1440H416V560L782 1080H1118L728 540L1144 0H790L416 520V0Z';
  function sequence(compact=false,random=Math.random){
    const used=new Set(), result=['quiet'];
    for(let i=0;i<63;i++){
      let group='calm',r=random();
      if(!compact){for(const [key,weight] of Object.entries(data.weights)){group=key;if(r<weight)break;r-=weight;}}
      let pool=Object.keys(motions).filter(id=>motions[id].group===group&&!used.has(id));
      if(!pool.length)pool=['quiet','breathe','peek'];
      const id=pool[Math.min(pool.length-1,Math.floor(random()*pool.length))];result.push(id);
      if(['joke','rare'].includes(motions[id].group))used.add(id);
    }
    return result;
  }
  function sample(id,time){
    const m=motions[id]||motions.quiet,p=Math.max(0,Math.min(m.frames.length-1,time*data.fps/1000));
    const i=Math.floor(p),a=m.frames[i],b=m.frames[Math.min(i+1,m.frames.length-1)];
    return a.map((v,j)=>v+(b[j]-v)*(p-i));
  }
  function locate(list,time){
    for(const id of list){if(time<motions[id].duration)return {id,time};time-=motions[id].duration;}
    // A very long wait never repeats the same joke. The tail stays quiet.
    return {id:'breathe',time:time%motions.breathe.duration};
  }
  function create(host,{compact=false,variant=null,list=null,once=false,render=null,onfinish=()=>{}}={}){
    const media=matchMedia('(prefers-reduced-motion: reduce)');
    let svg,point,letter;
    if(!render){
      host.classList.add('kot-loader');host.setAttribute('aria-hidden','true');
      host.innerHTML=`<svg viewBox="0 0 1560 1800" focusable="false" aria-hidden="true"><g class="loader-letter"><path d="${glyph}" transform="translate(0 1600) scale(1 -1)"/></g><g class="loader-point"><path d="M1141.1764705882354 1328h272v272h-272z"/></g></svg>`;
      svg=host.firstElementChild;point=svg.querySelector('.loader-point');letter=svg.querySelector('.loader-letter');
      render=p=>{point.setAttribute('transform',`translate(${p[0]*272} ${p[1]*272}) rotate(${p[2]} 1277.1764705882354 1464)`);letter.setAttribute('transform',`translate(${p[3]*272} 0)`);};
    }
    let schedule=list||sequence(compact), current=variant,queued=null,pendingAt=null, elapsed=0,last=null,raf=0,active=false,disposed=false,intersecting=true,hostVisible=true,rate=1;
    const allowed=()=>active&&!disposed&&host.isConnected&&!document.hidden&&hostVisible&&intersecting&&!media.matches;
    const duration=()=>current?motions[current].duration:Infinity;
    function draw(){const loc=current?{id:current,time:Math.min(elapsed,duration())}:locate(schedule,elapsed);render(media.matches?rest:sample(loc.id,loc.time));host.dataset.motion=loc.id;}
    function tick(now){
      raf=0;if(!allowed()){sync();return;}
      if(last!==null)elapsed+=(now-last)*rate;last=now;
      if(!current&&queued&&elapsed>=pendingAt){elapsed-=pendingAt;current=queued;queued=null;pendingAt=null;}
      if(current&&elapsed>=duration()){
        if(once){render(rest);active=false;onfinish();return;}
        elapsed-=duration();if(queued){current=queued;queued=null;}elapsed%=duration();
      }
      draw();raf=requestAnimationFrame(tick);
    }
    function sync(){
      cancelAnimationFrame(raf);raf=0;last=null;
      if(media.matches&&(!once||active)){render(rest);if(once&&active){active=false;onfinish();}}
      if(allowed())raf=requestAnimationFrame(tick);
    }
    function preference(){sync();}
    const observer=new IntersectionObserver(entries=>{const next=entries.at(-1).isIntersecting;if(next!==intersecting){intersecting=next;sync();}});observer.observe(host);
    const visibility=()=>sync();document.addEventListener('visibilitychange',visibility);media.addEventListener('change',preference);
    // WebView2 can remain document-visible when its WinForms host is minimized or in the tray.
    const nativeVisibility=e=>{const next=e.detail!==false;if(next!==hostVisible){hostVisible=next;sync();}};window.addEventListener('kot-window-visibility',nativeVisibility);
    render(rest);
    return {
      start(){if(disposed||active)return;active=true;elapsed=0;queued=null;pendingAt=null;if(!list&&!current)schedule=sequence(compact);draw();preference();},
      stop(){active=false;sync();render(rest);},
      syncOrigin(origin){if(Number.isFinite(origin)){elapsed=Math.max(0,Date.now()-origin);last=null;draw();}},
      select(id){if(!motions[id])return;if(!active){current=id;elapsed=0;draw();}else if(id!==current){queued=id;if(!current){const loc=locate(schedule,elapsed);pendingAt=elapsed-loc.time+motions[loc.id].duration;}}},
      speed(value){rate=Math.max(.1,Math.min(2,value));},
      seek(time){elapsed=Math.max(0,time);last=null;draw();},
      get state(){return {active,scheduled:!!raf,elapsed,variant:current,queued};},
      dispose(){if(disposed)return;active=false;sync();disposed=true;observer.disconnect();document.removeEventListener('visibilitychange',visibility);media.removeEventListener('change',preference);window.removeEventListener('kot-window-visibility',nativeVisibility);render(rest);}
    };
  }
  window.KotLoading={create,sequence,sample,locate};
})();
