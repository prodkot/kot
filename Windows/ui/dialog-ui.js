/* Shared modal lifecycle. Native operations remain in their owning modules. */
'use strict';
window.KotDialogUI = ({rememberFocus,onStackChange}) => {
  const $=id=>document.getElementById(id), reduced=matchMedia('(prefers-reduced-motion: reduce)');
  const tokens=getComputedStyle(document.documentElement);
  const motion=name=>parseFloat(tokens.getPropertyValue('--motion-'+name));
  const states=new WeakMap(), active=new Set(), baselines=new WeakMap(), fieldErrors=new WeakMap();
  let pendingDiscard=null, help=null, helpButton=null, helpMotion=null, helpGeneration=0;
  function fields(dialog){return [...dialog.querySelectorAll('input:not([readonly]),textarea:not([readonly]),select,[role=switch]:not([data-setting])')];}
  function values(dialog){return JSON.stringify(fields(dialog).map(e=>[e.id,e.type==='checkbox'?e.checked:e.matches('[role=switch]')?e.getAttribute('aria-checked'):e.value]));}
  function markClean(dialog){baselines.set(dialog,values(dialog));}
  function isDirty(dialog){return dialog.hasAttribute('data-dirty-form')&&baselines.has(dialog)&&baselines.get(dialog)!==values(dialog);}
  function cancelEffects(state){for(const effect of state.effects)effect.cancel();state.effects=[];}
  function cancelClose(state){clearTimeout(state.closeTimer);state.closeTimer=null;state.finish=null;}
  function cancelFocus(state){cancelAnimationFrame(state.focusFrame);state.focusFrame=null;}
  function restoreFocus(state){
    const top=[...active].filter(dialog=>dialog.open).at(-1),target=state.focus?.();
    const available=el=>el?.isConnected&&(!top||top.contains(el))&&!el.closest('[inert],[hidden],dialog[data-closing]')&&!el.matches(':disabled,[aria-disabled=true]')&&el.getClientRects().length&&getComputedStyle(el).visibility==='visible';
    const focus=el=>{if(!available(el))return false;el.focus({preventScroll:true});return document.activeElement===el;};
    // Native close may already have chosen a valid control in the current layer.
    // Preserve newer user focus; RPC completion does not own focus restoration.
    if(document.activeElement!==document.body&&available(document.activeElement))return true;
    if(focus(target))return true;
    const scope=top||target?.closest('section')||document.querySelector('main .view:not([hidden])')||document;
    for(const el of scope.querySelectorAll('button,a[href],input,textarea,select,[tabindex]'))if(el.tabIndex>=0&&focus(el))return true;
    if(top)return focus(top);
    else if(scope!==document)for(const el of document.querySelectorAll('button,a[href],input,textarea,select,[tabindex]'))if(el.tabIndex>=0&&focus(el))return true;
    return false;
  }
  function finalize(dialog,state){
    if(!state||states.get(dialog)!==state||state.finalized)return;
    state.finalized=true;state.generation++;cancelClose(state);cancelFocus(state);cancelEffects(state);
    if(help?.closest('dialog')===dialog)hideHelp(false,true);
    if(pendingDiscard===dialog){pendingDiscard=null;close($('discardDialog'));}
    delete dialog.dataset.closing;active.delete(dialog);baselines.delete(dialog);onStackChange(dialog,false);
    if(!restoreFocus(state)){
      // Hidden rendering may reject every focus attempt. Retry once when frames
      // resume, only if this document still owns otherwise unassigned focus.
      const generation=state.generation;
      state.focusFrame=requestAnimationFrame(()=>{
        state.focusFrame=null;
        if(states.get(dialog)===state&&state.finalized&&state.generation===generation&&document.hasFocus()&&document.activeElement===document.body)restoreFocus(state);
      });
    }
  }
  // Native close requests can bypass our animation (for example platform dismissal).
  // Retire the same session once; the owning module still receives its close event.
  for(const dialog of document.querySelectorAll('dialog'))dialog.addEventListener('close',()=>{
    if(!dialog.open){if(dialog===$('discardDialog'))pendingDiscard=null;finalize(dialog,states.get(dialog));}
  });
  function effects(dialog,frames,opening,backdrop){
    if(reduced.matches)return [];
    const options={duration:motion(opening?'enter':'exit'),easing:opening?tokens.getPropertyValue('--ease').trim():'ease-in',fill:'both'};
    const result=[];
    try{
      result.push(dialog.animate(frames,options));
      result.push(dialog.animate(backdrop,{...options,duration:motion('backdrop'),easing:opening?'ease-out':'ease-in',pseudoElement:'::backdrop'}));
      return result;
    }catch{
      // Own each effect immediately, including a surface created before backdrop fails.
      // Observe rejection before cancellation; fall back to the static target state.
      for(const effect of result){effect.finished.catch(()=>{});effect.cancel();}
      return [];
    }
  }
  function guardClose(dialog,state,generation,finish){
    const current=()=>states.get(dialog)===state&&!state.finalized&&state.generation===generation&&state.finish===finish;
    const span=Math.max(...state.effects.map(effect=>effect.effect.getComputedTiming().endTime));
    // These are finite, one-shot effects. Allow one effect span for rendering to
    // start, plus one for playback, independently of rendering/finished delivery.
    // A late ready notification can grant its actual remaining playback once.
    // No polling or unbounded renewal; frozen JS runs this only after resuming.
    let deadline=performance.now()+2*span;
    const arm=()=>{clearTimeout(state.closeTimer);state.closeTimer=setTimeout(()=>{if(current())finish();},Math.max(0,deadline-performance.now()));};
    for(const effect of state.effects)effect.ready.then(()=>{
      if(!current())return;
      const remaining=Math.max(0,effect.effect.getComputedTiming().endTime-(effect.currentTime||0));
      deadline=Math.max(deadline,performance.now()+remaining);arm();
    },()=>{});
    arm();
  }
  function visibleFrame(element){
    const style=getComputedStyle(element);
    return {opacity:style.opacity,transform:style.transform};
  }
  function open(dialog){
    let state=states.get(dialog);if(state)cancelFocus(state);
    if(dialog.open&&!dialog.dataset.closing)return;
    const from=dialog.open?visibleFrame(dialog):{opacity:0,transform:'translateY(12px) scale(.97)'};
    const backdropOpacity=dialog.open?getComputedStyle(dialog,'::backdrop').opacity:0;
    if(dialog.open){state.generation++;cancelClose(state);cancelFocus(state);cancelEffects(state);delete dialog.dataset.closing;}
    else{finalize(dialog,state);dialog.querySelectorAll('input,textarea,select').forEach(clearFieldError);state={generation:0,effects:[],focus:rememberFocus(),finish:null,finalized:false};states.set(dialog,state);dialog.showModal();active.add(dialog);markClean(dialog);onStackChange(dialog,true);}
    state.effects=effects(dialog,[from,{opacity:1,transform:'none'}],true,[{opacity:backdropOpacity},{opacity:1}]);
    const generation=state.generation;
    // A nested modal may cancel the browser's backdrop animation independently.
    // Always retire the remaining filled effect, including that cancellation path.
    Promise.allSettled(state.effects.map(e=>e.finished)).then(()=>{if(states.get(dialog)===state&&!state.finalized&&state.generation===generation&&!state.finish)cancelEffects(state);});
  }
  function close(dialog){
    if(!dialog.open||dialog.dataset.closing)return;
    if(dialog===$('discardDialog'))pendingDiscard=null;
    if(help?.closest('dialog')===dialog)hideHelp(false,true);
    const state=states.get(dialog),generation=++state.generation,from=visibleFrame(dialog);
    const backdropOpacity=getComputedStyle(dialog,'::backdrop').opacity;
    cancelEffects(state);dialog.dataset.closing='true';
    const finish=()=>{
      if(states.get(dialog)!==state||state.finalized||generation!==state.generation)return;
      if(dialog.open)dialog.close();finalize(dialog,state);
    };
    state.finish=finish;state.effects=effects(dialog,[from,{opacity:0,transform:'translateY(6px) scale(.985)'}],false,[{opacity:backdropOpacity},{opacity:0}]);
    if(!state.effects.length)finish();else{
      Promise.allSettled(state.effects.map(e=>e.finished)).then(finish);
      guardClose(dialog,state,generation,finish);
    }
  }
  function requestClose(dialog){
    if(!dialog.open||dialog.dataset.closing)return;
    if(!isDirty(dialog)){close(dialog);return;}
    if(pendingDiscard)return;
    pendingDiscard=dialog;open($('discardDialog'));$('keepEditing').focus({preventScroll:true});
  }
  function dismissDiscard(){pendingDiscard=null;close($('discardDialog'));}
  $('closeDiscard').addEventListener('click',dismissDiscard);$('keepEditing').addEventListener('click',dismissDiscard);
  $('discardChanges').addEventListener('click',()=>{const target=pendingDiscard;pendingDiscard=null;
    // Both close animations run together; either finalizer must use the same outside return target.
    if(target){const parentState=states.get(target),discardState=states.get($('discardDialog'));if(parentState&&discardState)discardState.focus=parentState.focus;}
    close($('discardDialog'));if(target)close(target);
  });
  function positionHelp(){
    if(!help||!helpButton)return;
    const r=helpButton.getBoundingClientRect(),edge=12,width=Math.min(420,innerWidth-24);
    help.style.width=width+'px';help.style.left=Math.max(edge,Math.min(r.right-width,innerWidth-width-edge))+'px';
    const height=help.getBoundingClientRect().height;help.style.top=Math.max(edge,Math.min(r.bottom+8,innerHeight-height-edge))+'px';
  }
  function hideHelp(focus=true,immediate=false){
    if(!help||(!immediate&&helpButton.getAttribute('aria-expanded')==='false'))return;
    const panel=help,button=helpButton,generation=++helpGeneration,from=visibleFrame(panel);
    helpMotion?.cancel();button.setAttribute('aria-expanded','false');
    const finish=()=>{if(generation!==helpGeneration)return;panel.hidePopover();helpMotion?.cancel();helpMotion=null;help=null;helpButton=null;if(focus)button.focus({preventScroll:true});};
    if(immediate||reduced.matches){finish();return;}
    helpMotion=panel.animate([from,{opacity:0,transform:'translateY(-4px)'}],{duration:motion('help'),fill:'both'});helpMotion.finished.then(finish).catch(()=>{});
  }
  for(const button of document.querySelectorAll('[data-help]'))button.addEventListener('click',()=>{
    const same=button===helpButton;
    if(same&&button.getAttribute('aria-expanded')==='true'){hideHelp();return;}
    const from=same?visibleFrame(help):{opacity:0,transform:'translateY(-4px)'};
    if(same)helpMotion?.cancel();else hideHelp(false,true);
    helpGeneration++;helpButton=button;help=$(button.dataset.help);
    if(!same){help.showPopover();positionHelp();}
    button.setAttribute('aria-expanded','true');help.tabIndex=-1;help.focus({preventScroll:true});
    if(!reduced.matches){helpMotion=help.animate([from,{opacity:1,transform:'none'}],{duration:motion('help')});}
  });
  document.addEventListener('keydown',e=>{if(e.key==='Escape'&&help){e.preventDefault();e.stopImmediatePropagation();hideHelp();}},true);
  document.addEventListener('pointerdown',e=>{if(help&&!help.contains(e.target)&&!helpButton.contains(e.target))hideHelp(false);});
  addEventListener('resize',positionHelp);
  document.addEventListener('scroll',e=>{
    const scroller=e.target===document?document.documentElement:e.target;
    if(help&&!help.contains(scroller)&&scroller.contains?.(helpButton))hideHelp(false,true);
  },true);
  reduced.addEventListener('change',()=>{if(!reduced.matches)return;for(const dialog of active){const state=states.get(dialog);cancelEffects(state);state.finish?.();}hideHelp(false,true);});
  // HTML validity remains authoritative; expose it to assistive technology too.
  function clearFieldError(field){
    const entry=fieldErrors.get(field);if(!entry)return;
    field.removeAttribute('aria-invalid');
    if(entry.note.textContent===entry.message)entry.note.textContent='';
    if(entry.addedDescription){
      const ids=(field.getAttribute('aria-describedby')||'').split(/\s+/).filter(id=>id&&id!==entry.note.id);
      if(ids.length)field.setAttribute('aria-describedby',ids.join(' '));else field.removeAttribute('aria-describedby');
    }
    fieldErrors.delete(field);
  }
  function showFieldError(field,note,message){
    clearFieldError(field);note.textContent=message;note.hidden=false;field.setAttribute('aria-invalid','true');
    const ids=(field.getAttribute('aria-describedby')||'').split(/\s+/).filter(Boolean),addedDescription=!ids.includes(note.id);
    if(addedDescription)ids.push(note.id);field.setAttribute('aria-describedby',ids.join(' '));
    fieldErrors.set(field,{note,message,addedDescription});
  }
  document.addEventListener('invalid',e=>{
    const field=e.target;
    if(!field.id)return;
    const id=field.id+'Validity';let note=$(id);
    if(!note){note=document.createElement('span');note.id=id;note.className='error field-validity';field.after(note);}
    showFieldError(field,note,field.validationMessage);
  },true);
  for(const event of ['input','change'])document.addEventListener(event,e=>clearFieldError(e.target));
  function fieldError(field,error,message){showFieldError(field,error,message);field.focus({preventScroll:true});}
  return {open,close,requestClose,markClean,isDirty,fieldError};
};
