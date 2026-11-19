/* Shared modal lifecycle. Native operations remain in their owning modules. */
'use strict';
window.KotDialogUI = ({rememberFocus,onStackChange}) => {
  const $=id=>document.getElementById(id), reduced=matchMedia('(prefers-reduced-motion: reduce)');
  const states=new WeakMap(), active=new Set(), baselines=new WeakMap(), fieldErrors=new WeakMap();
  let pendingDiscard=null, help=null, helpButton=null, helpMotion=null, helpGeneration=0;
  function fields(dialog){return [...dialog.querySelectorAll('input:not([readonly]),textarea:not([readonly]),select,[role=switch]:not([data-setting])')];}
  function values(dialog){return JSON.stringify(fields(dialog).map(e=>[e.id,e.type==='checkbox'?e.checked:e.matches('[role=switch]')?e.getAttribute('aria-checked'):e.value]));}
  function markClean(dialog){baselines.set(dialog,values(dialog));}
  function isDirty(dialog){return dialog.hasAttribute('data-dirty-form')&&baselines.has(dialog)&&baselines.get(dialog)!==values(dialog);}
  function cancelEffects(state){for(const effect of state.effects)effect.cancel();state.effects=[];}
  function finalize(dialog,state){
    if(!state||state.finalized)return;
    if(help?.closest('dialog')===dialog)hideHelp(false,true);
    if(pendingDiscard===dialog){pendingDiscard=null;close($('discardDialog'));}
    state.finalized=true;state.generation++;state.finish=null;cancelEffects(state);
    delete dialog.dataset.closing;active.delete(dialog);baselines.delete(dialog);onStackChange(dialog,false);
    const target=state.focus?.(),top=[...active].at(-1);
    if(target?.isConnected&&(!top||top.contains(target)))target.focus({preventScroll:true});
  }
  // Native close requests can bypass our animation (for example platform dismissal).
  // Retire the same session once; the owning module still receives its close event.
  for(const dialog of document.querySelectorAll('dialog'))dialog.addEventListener('close',()=>{
    if(!dialog.open){if(dialog===$('discardDialog'))pendingDiscard=null;finalize(dialog,states.get(dialog));}
  });
  function effects(dialog,frames,duration,backdrop){
    if(reduced.matches)return [];
    const options={duration,easing:duration===280?'cubic-bezier(.22,1,.36,1)':'ease-in',fill:'both'};
    const result=[dialog.animate(frames,options)];
    result.push(dialog.animate(backdrop,{...options,duration:200,easing:duration===280?'ease-out':'ease-in',pseudoElement:'::backdrop'}));return result;
  }
  function open(dialog){
    let state=states.get(dialog);
    if(dialog.open&&!dialog.dataset.closing)return;
    if(dialog.open){state.generation++;cancelEffects(state);delete dialog.dataset.closing;state.finish=null;}
    else{finalize(dialog,state);dialog.querySelectorAll('input,textarea,select').forEach(clearFieldError);state={generation:0,effects:[],focus:rememberFocus(),finish:null,finalized:false};states.set(dialog,state);dialog.showModal();active.add(dialog);markClean(dialog);onStackChange(dialog,true);}
    state.effects=effects(dialog,[{opacity:0,transform:'translateY(12px) scale(.97)'},{opacity:1,transform:'none'}],280,[{opacity:0},{opacity:1}]);
    const generation=state.generation;
    // A nested modal may cancel the browser's backdrop animation independently.
    // Always retire the remaining filled effect, including that cancellation path.
    Promise.allSettled(state.effects.map(e=>e.finished)).then(()=>{if(state.generation===generation&&!state.finish)cancelEffects(state);});
  }
  function close(dialog){
    if(!dialog.open||dialog.dataset.closing)return;
    if(dialog===$('discardDialog'))pendingDiscard=null;
    if(help?.closest('dialog')===dialog)hideHelp(false,true);
    const state=states.get(dialog),generation=++state.generation,style=getComputedStyle(dialog),from={opacity:style.opacity,transform:style.transform};
    const backdropOpacity=getComputedStyle(dialog,'::backdrop').opacity;
    cancelEffects(state);dialog.dataset.closing='true';
    const finish=()=>{
      if(generation!==state.generation)return;
      if(dialog.open)dialog.close();finalize(dialog,state);
    };
    state.finish=finish;state.effects=effects(dialog,[from,{opacity:0,transform:'translateY(6px) scale(.985)'}],200,[{opacity:backdropOpacity},{opacity:0}]);
    if(!state.effects.length)finish();else Promise.allSettled(state.effects.map(e=>e.finished)).then(finish);
  }
  function requestClose(dialog){
    if(!dialog.open||dialog.dataset.closing)return;
    if(!isDirty(dialog)){close(dialog);return;}
    if(pendingDiscard)return;
    pendingDiscard=dialog;open($('discardDialog'));$('keepEditing').focus({preventScroll:true});
  }
  function dismissDiscard(){pendingDiscard=null;close($('discardDialog'));}
  $('closeDiscard').addEventListener('click',dismissDiscard);$('keepEditing').addEventListener('click',dismissDiscard);
  $('discardChanges').addEventListener('click',()=>{const target=pendingDiscard;pendingDiscard=null;close($('discardDialog'));if(target)close(target);});
  function positionHelp(){
    if(!help||!helpButton)return;
    const r=helpButton.getBoundingClientRect(),edge=12,width=Math.min(420,innerWidth-24);
    help.style.width=width+'px';help.style.left=Math.max(edge,Math.min(r.right-width,innerWidth-width-edge))+'px';
    const height=help.getBoundingClientRect().height;help.style.top=Math.max(edge,Math.min(r.bottom+8,innerHeight-height-edge))+'px';
  }
  function hideHelp(focus=true,immediate=false){
    if(!help)return;const panel=help,button=helpButton,generation=++helpGeneration;
    helpMotion?.cancel();button.setAttribute('aria-expanded','false');
    const finish=()=>{if(generation!==helpGeneration)return;panel.hidePopover();helpMotion?.cancel();helpMotion=null;help=null;helpButton=null;if(focus)button.focus({preventScroll:true});};
    if(immediate||reduced.matches){finish();return;}
    helpMotion=panel.animate([{opacity:getComputedStyle(panel).opacity,transform:getComputedStyle(panel).transform},{opacity:0,transform:'translateY(-4px)'}],{duration:180,fill:'both'});helpMotion.finished.then(finish).catch(()=>{});
  }
  for(const button of document.querySelectorAll('[data-help]'))button.addEventListener('click',()=>{
    if(button===helpButton){hideHelp();return;}hideHelp(false,true);helpGeneration++;
    helpButton=button;help=$(button.dataset.help);help.showPopover();button.setAttribute('aria-expanded','true');positionHelp();help.tabIndex=-1;help.focus({preventScroll:true});
    if(!reduced.matches){helpMotion=help.animate([{opacity:0,transform:'translateY(-4px)'},{opacity:1,transform:'none'}],{duration:180});}
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
