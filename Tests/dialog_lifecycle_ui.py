"""Modal lifecycle regressions in real Chromium with synthetic host data.

The route adds read-only state access for assertions, not alternate lifecycle logic.
Faults are explicit WAAPI injections; they are not claims about WebView2 failures.
"""
import argparse
import importlib.util
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from tempfile import TemporaryDirectory
from threading import Thread
import json
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
fixture = {'__file__': str(ROOT / 'Tests/ui_resilience.py')}
exec((ROOT / 'Tests/ui_resilience.py').read_text().split('OUTPUT =')[0], fixture)
MOCK = fixture['MOCK']
PROBE = r'''() => {
  window.modalEvidence={effects:[],stack:[],native:[],errors:[]};
  window.addEventListener('unhandledrejection',e=>modalEvidence.errors.push(e.reason?.name||'rejection'));
  const animate=Element.prototype.animate;
  Element.prototype.animate=function(frames,options){
    if(!this.matches('dialog'))return animate.call(this,frames,options);
    const closing=!!this.dataset.closing,backdrop=options.pseudoElement==='::backdrop';
    if(closing&&backdrop&&window.modalFault==='throw-backdrop')throw new Error('synthetic animation creation failure');
    const effect=animate.call(this,frames,options),entry={id:this.id,closing,backdrop,effect,options:{...options},frames,finished:'pending'};
    modalEvidence.effects.push(entry);
    effect.finished.then(()=>entry.finished='fulfilled',()=>entry.finished='rejected');
    if(closing&&backdrop&&window.modalFault==='pause-backdrop')effect.pause();
    return effect;
  };
  const factory=window.KotDialogUI;
  window.KotDialogUI=options=>{
    const controller=factory({...options,onStackChange:(dialog,open)=>{
      modalEvidence.stack.push({id:dialog.id,open});options.onStackChange(dialog,open);
    }});
    window.modalController=controller;return controller;
  };
  for(const d of document.querySelectorAll('dialog'))d.addEventListener('close',()=>modalEvidence.native.push(d.id));
}'''


def boot(browser, source, theme='dark', width=960):
    p = browser.new_page(viewport={'width': width, 'height': 640}, reduced_motion='no-preference')
    p.set_default_timeout(4000)
    p.errors=[]
    p.on('pageerror', lambda e:p.errors.append(str(e)))
    p.add_init_script(MOCK)
    text = (source/'Windows/ui/dialog-ui.js').read_text()
    anchor = '  let pendingDiscard='
    assert anchor in text
    text=text.replace(anchor, '  window.modalStates=states;\n'+anchor, 1)
    text+='\n('+PROBE+')();\n'
    p.route('**/dialog-ui.js', lambda r:r.fulfill(body=text, content_type='text/javascript'))
    p.goto((source/'Windows/ui/index.html').as_uri())
    expect(p.locator('#startupScreen')).to_be_hidden()
    p.evaluate("theme=>{fixtureSnapshot({settings:{theme,mode:'tun',accent:'lime',tray:true},nodes:[{id:'node',name:'Synthetic node',protocol:'vless'}],selected:'node'});fixtureNavigate('settings');}",theme)
    p.wait_for_timeout(320)
    return p


OPEN = '''id=>{
 const d=document.getElementById(id);modalController.open(d);
 if(!d.open||!d.matches(':modal'))throw new Error('not actually modal');
 const s=modalStates.get(d);window.modalBefore={state:s,generation:s.generation,closed:modalEvidence.stack.filter(x=>x.id===id&&!x.open).length,native:modalEvidence.native.filter(x=>x===id).length};
}'''
CLOSED = '''id=>{
 const d=document.getElementById(id),s=modalStates.get(d);
 return {open:d.open,closing:d.dataset.closing||null,modal:d.matches(':modal'),finalized:s.finalized,generation:s.generation,
 focusFrame:s.focusFrame==null,effects:s.effects.length,finish:s.finish===null,timer:s.closeTimer==null,
 owned:modalEvidence.effects.filter(x=>x.id===id&&x.effect.playState!=='idle').length,
 closed:modalEvidence.stack.filter(x=>x.id===id&&!x.open).length-modalBefore.closed,
 native:modalEvidence.native.filter(x=>x===id).length-modalBefore.native,
 focus:document.activeElement.id||document.activeElement.tagName};
}'''


def assert_closed(p, id='advancedDialog', generation=None):
    expect(p.locator('#'+id)).not_to_have_attribute('open','')
    p.wait_for_function("id=>modalEvidence.native.filter(x=>x===id).length-modalBefore.native===1",arg=id)
    result=p.evaluate(CLOSED,id)
    assert not result['open'] and not result['modal'] and result['closing'] is None,result
    assert result['finalized'] and result['effects']==0 and result['owned']==0,result
    assert result['focusFrame'] and result['finish'] and result['timer'] and result['closed']==1 and result['native']==1,result
    if generation is not None:assert result['generation']==generation,result
    assert not p.errors,p.errors
    assert not p.evaluate('modalEvidence.errors'),p.evaluate('modalEvidence.errors')
    return result


def mechanics(browser, source, rows, cycles, modes=None):
    for mode in modes or ['normal','long-backdrop','pause-backdrop','throw-backdrop','cancel-surface','cancel-backdrop','cancel-both','reopen','reopen-paused','native','native-reopen','native-reopen-closing','reduced','pending-render']:
        p=boot(browser,source)
        try:
            for i in range(cycles):
                if mode=='long-backdrop':p.evaluate("document.documentElement.style.setProperty('--motion-backdrop','360ms')")
                p.evaluate("advancedButton.focus()")
                p.evaluate(OPEN,'advancedDialog');p.wait_for_timeout(310)
                p.evaluate('''mode=>{
                  modalFault=mode==='reopen-paused'||mode==='native-reopen-closing'?'pause-backdrop':mode;const d=advancedDialog,s=modalStates.get(d);window.started=performance.now();
                  if(mode==='native-reopen-closing'){modalController.close(d);d.close();}
                  else if(mode.startsWith('native'))d.close();else modalController.close(d);
                  if(mode.startsWith('cancel-'))for(const x of modalEvidence.effects.filter(x=>x.id===d.id&&x.closing&&x.effect.playState!=='idle')){
                    if(mode==='cancel-both'||x.backdrop===(mode==='cancel-backdrop'))x.effect.cancel();
                  }
                  if(mode==='pending-render'){const end=performance.now()+600;while(performance.now()<end){}}
                  if(mode.startsWith('reopen')||mode.startsWith('native-reopen'))modalController.open(d);
                }''',mode)
                if mode=='reduced':p.emulate_media(reduced_motion='reduce')
                if mode.startswith(('reopen','native-reopen')):
                    p.wait_for_timeout(700)
                    v=p.evaluate('''()=>({open:advancedDialog.open,modal:advancedDialog.matches(':modal'),closing:advancedDialog.dataset.closing||null,finalized:modalStates.get(advancedDialog).finalized,generation:modalStates.get(advancedDialog).generation,focus:document.activeElement.closest('dialog')?.id})''')
                    assert v['open'] and v['modal'] and not v['closing'] and not v['finalized'],v
                    assert v['generation']==(2 if mode.startswith('reopen') else 0),v
                    assert v['focus']=='advancedDialog',v
                    # Each earlier close session must be retired, but never this one.
                    assert p.evaluate("modalEvidence.stack.filter(x=>x.id==='advancedDialog'&&!x.open).length-modalBefore.closed")==(1 if mode.startswith('native-reopen') else 0)
                    p.evaluate(OPEN,'advancedDialog')
                    p.evaluate("modalFault='';modalController.close(advancedDialog)")
                    expected=4 if mode.startswith('reopen') else 2
                else:expected=1 if mode=='native' else 2
                result=assert_closed(p,generation=expected)
                assert result['focus']=='advancedButton',result
                if mode in ('normal','long-backdrop'):
                    effects=p.evaluate("modalEvidence.effects.filter(x=>x.closing).slice(-2).map(x=>({finished:x.finished,duration:x.options.duration,easing:x.options.easing}))")
                    assert effects==[{'finished':'fulfilled','duration':200,'easing':'ease-in'}, {'finished':'fulfilled','duration':360 if mode=='long-backdrop' else 200,'easing':'ease-in'}],effects
                rows.append({'test':mode,'iteration':i,**result})
                p.emulate_media(reduced_motion='no-preference')
        finally:p.close()
        print('PASS modal mechanics',mode,cycles,flush=True)


def applications(browser, source, rows, cycles):
    p=boot(browser,source)
    try:
        for i in range(cycles):
            print('RUN application',i+1,'pending',flush=True)
            # Genuine pending RPC, real button handlers, then synchronous fast close.
            p.evaluate("fixtureDelay=1200;onlineUpdateButton.focus();onlineUpdateButton.click()")
            expect(p.locator('#onlineUpdateDialog')).to_have_attribute('open','')
            assert p.locator('#onlineUpdateButton').is_disabled()
            p.evaluate(OPEN,'onlineUpdateDialog')
            p.evaluate('closeOnlineUpdate.click()')
            result=assert_closed(p,'onlineUpdateDialog',2)
            assert result['focus']!='BODY',result
            assert p.evaluate("document.activeElement.matches(':enabled')&&!document.activeElement.closest('dialog')")
            # Completion must not steal the focus the user subsequently chose.
            p.locator('#advancedButton').focus();expect(p.locator('#onlineUpdateButton')).to_be_enabled()
            expect(p.locator('#advancedButton')).to_be_focused()
            rows.append({'test':'pending-rpc-focus','iteration':i,**result})
            p.evaluate('fixtureDelay=10')
            print('RUN application',i+1,'hwid',flush=True)
            # Native close queued before new advanced/HWID session.
            p.evaluate("advancedButton.click();advancedDialog.close();advancedButton.click();hwidButton.click()")
            expect(p.locator('#hwidValue')).to_have_value('A'*64)
            p.wait_for_timeout(400)
            expect(p.locator('#deviceDialog')).to_be_visible();expect(p.locator('#copyHwid')).to_be_enabled()
            expect(p.locator('#closeDevice')).to_be_focused()
            p.evaluate(OPEN,'advancedDialog');p.evaluate('modalController.close(advancedDialog)');assert_closed(p,generation=2)
            expect(p.locator('#hwidValue')).to_have_value('');expect(p.locator('#deviceDialog')).to_be_hidden()
            rows.append({'test':'hwid-old-close','iteration':i})
            print('RUN application',i+1,'lower',flush=True)
            # Closing a lower layer must not focus beneath the still-open top one.
            p.evaluate("advancedButton.click();modalController.open(onlineUpdateDialog);closeOnlineUpdate.focus();modalController.close(advancedDialog)")
            expect(p.locator('#advancedDialog')).not_to_be_visible()
            expect(p.locator('#onlineUpdateDialog')).to_be_visible();expect(p.locator('#closeOnlineUpdate')).to_be_focused()
            p.evaluate("modalController.close(onlineUpdateDialog)")
            expect(p.locator('#onlineUpdateDialog')).not_to_be_visible()
            rows.append({'test':'close-lower-layer','iteration':i})
            print('RUN application',i+1,'focus-choice',flush=True)
            # A queued native close event must not undo a newer user focus choice.
            p.evaluate("advancedButton.click();advancedDialog.close();onlineUpdateButton.focus()")
            p.wait_for_timeout(100)
            expect(p.locator('#onlineUpdateButton')).to_be_focused()
            rows.append({'test':'native-user-focus','iteration':i})
            print('RUN application',i+1,'dirty',flush=True)
            # Discard cancellation preserves both the draft and the modal layer.
            p.locator('#advancedButton').click();p.locator('#bypassDomains').fill('draft.example')
            p.locator('#closeAdvanced').click();expect(p.locator('#keepEditing')).to_be_focused()
            p.keyboard.press('Escape');expect(p.locator('#discardDialog')).not_to_be_visible()
            expect(p.locator('#bypassDomains')).to_have_value('draft.example')
            assert p.evaluate("document.activeElement.closest('dialog')===advancedDialog")
            p.locator('#backupButton').click();expect(p.locator('#backupDialog')).to_be_visible()
            p.keyboard.press('Escape');expect(p.locator('#backupDialog')).not_to_be_visible();expect(p.locator('#backupButton')).to_be_focused()
            p.locator('#closeAdvanced').click();p.locator('#discardChanges').click()
            expect(p.locator('dialog[open]')).to_have_count(0)
            expect(p.locator('#advancedButton')).to_be_focused()
            rows.append({'test':'dirty-nested','iteration':i})
            print('RUN application',i+1,'json',flush=True)
            # JSON's existing menu action and Escape return to its logical anchor.
            p.evaluate("fixtureNavigate('servers')")
            p.locator('.qr-node').click();p.locator('#nodeMenuJson').click()
            expect(p.locator('#nodeJsonDialog')).to_be_visible();p.keyboard.press('Escape')
            expect(p.locator('#nodeJsonDialog')).not_to_be_visible();expect(p.locator('.qr-node')).to_be_focused()
            rows.append({'test':'json-escape','iteration':i})
            p.evaluate("fixtureNavigate('settings')")
            assert p.evaluate('''()=>[...document.querySelectorAll('dialog')].every(d=>{
              const s=modalStates.get(d);return !d.open&&!d.matches(':modal')&&!d.dataset.closing&&
                (!s||(s.finalized&&s.effects.length===0&&s.finish===null&&s.closeTimer==null&&s.focusFrame==null));
            })&&modalEvidence.effects.every(x=>x.effect.playState==='idle')''')
            print('PASS application cycle',i+1,flush=True)
        assert not p.errors,p.errors
        assert not p.evaluate('modalEvidence.errors')
    finally:p.close()
    print('PASS pending RPC, HWID, dirty/nested, JSON',cycles,flush=True)


def stress(browser,source,rows,count):
    p=boot(browser,source)
    try:
        for start in range(0,count,25):
            values=p.evaluate('''async count=>{
              const results=[];
              for(let i=0;i<count;i++){
                advancedButton.focus();modalController.open(advancedDialog);
                if(!advancedDialog.open||!advancedDialog.matches(':modal'))throw new Error('not opened');
                const state=modalStates.get(advancedDialog),generation=state.generation,
                  cleanups=modalEvidence.stack.filter(x=>x.id==='advancedDialog'&&!x.open).length;
                const start=performance.now();
                const closed=new Promise((resolve,reject)=>{
                  const deadline=setTimeout(()=>reject(new Error('close event deadline')),4000);
                  advancedDialog.addEventListener('close',()=>{clearTimeout(deadline);resolve();},{once:true});
                });
                modalController.close(advancedDialog);
                modalController.requestClose(advancedDialog);modalController.close(advancedDialog);
                await closed;
                if(advancedDialog.open||advancedDialog.matches(':modal')||advancedDialog.dataset.closing||!state.finalized||state.generation!==generation+2||state.effects.length||state.closeTimer!==null||state.finish!==null||state.focusFrame!=null||document.activeElement!==advancedButton)throw new Error('invalid final state');
                if(modalEvidence.stack.filter(x=>x.id==='advancedDialog'&&!x.open).length!==cleanups+1)throw new Error('cleanup count');
                if(modalEvidence.effects.some(x=>x.effect.playState!=='idle'))throw new Error('owned effects');
                results.push({elapsed:performance.now()-start,generation:state.generation,focus:document.activeElement.id});
                modalEvidence.effects=[];
              }return results;
            }''',min(25,count-start))
            rows.extend({'test':'stress','iteration':start+i,**value} for i,value in enumerate(values))
            print('PASS strict cycles',start+len(values),flush=True)
        assert not p.errors,p.errors
        assert not p.evaluate('modalEvidence.errors')
    finally:p.close()


def hidden_frame(browser,source,rows,cycles):
    # Use the shipped sandboxed demo, without relaxing CSP or iframe isolation.
    spec=importlib.util.spec_from_file_location('modal_demo_build',source/'site/build.py')
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    class Handler(SimpleHTTPRequestHandler):
        def log_message(self,*args):pass
    with TemporaryDirectory() as directory:
        module.build(Path(directory),json.loads((source/'site/release.json').read_text()))
        server=ThreadingHTTPServer(('127.0.0.1',0),partial(Handler,directory=directory))
        worker=Thread(target=server.serve_forever,daemon=True);worker.start()
        p=browser.new_page(viewport={'width':1440,'height':1000},reduced_motion='no-preference')
        try:
            p.goto(f'http://127.0.0.1:{server.server_port}/')
            p.locator('[data-explorer-demo]').click()
            expect(p.frame_locator('#client-demo').locator('#startupScreen')).to_be_hidden()
            f=p.frame(url=lambda url:'/demo/' in url);assert f is not None
            f.locator('[data-page=settings]').click()
            for i in range(cycles):
                f.locator('#advancedButton').click();expect(f.locator('#advancedDialog')).to_be_visible()
                f.locator('#closeAdvanced').evaluate('e=>e.click()')
                p.locator('#client-demo').evaluate("e=>e.style.display='none'")
                p.wait_for_timeout(1400)
                result=f.evaluate("()=>{const d=advancedDialog;return {open:d.open,closing:d.dataset.closing||null,modal:d.matches(':modal'),effects:d.getAnimations().length};}")
                assert result=={'open':False,'closing':None,'modal':False,'effects':0},result
                # A return to rendering may restore BODY, but must never steal
                # a newer control or the parent document's keyboard focus.
                if i%4==1:p.locator('[data-close-demo]').focus()
                p.locator('#client-demo').evaluate("e=>e.style.display=''")
                if i%4==2:f.evaluate("onlineUpdateButton.focus()")
                if i%4==0:expect(f.locator('#advancedButton')).to_be_focused()
                elif i%4==1:
                    p.wait_for_timeout(100);expect(p.locator('[data-close-demo]')).to_be_focused()
                elif i%4==2:
                    p.wait_for_timeout(100);expect(f.locator('#onlineUpdateButton')).to_be_focused()
                else:
                    f.evaluate("advancedButton.click();hwidButton.click()")
                    p.wait_for_timeout(100);expect(f.locator('#closeDevice')).to_be_focused()
                    expect(f.locator('#advancedDialog')).to_be_visible()
                    f.locator('#closeDevice').click();f.locator('#closeAdvanced').click()
                    expect(f.locator('#advancedDialog')).not_to_be_visible()
                f.locator('#advancedButton').click();expect(f.locator('#advancedDialog')).to_be_visible()
                f.locator('#closeAdvanced').click();expect(f.locator('#advancedDialog')).not_to_be_visible()
                rows.append({'test':'hidden-iframe','iteration':i,**result})
                print('PASS hidden iframe cycle',i+1,flush=True)
        finally:
            p.close();server.shutdown();server.server_close();worker.join(3)
    print('PASS hidden iframe',cycles,flush=True)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--source-root',type=Path,default=ROOT)
    parser.add_argument('--output',type=Path);parser.add_argument('--cycles',type=int,default=10)
    parser.add_argument('--group',choices=['all','mechanics','applications','hidden','stress'],default='all')
    args=parser.parse_args();rows=[]
    with sync_playwright() as pw:
        browser=pw.chromium.launch(args=['--no-sandbox'])
        try:
            if args.group in ('all','mechanics'):mechanics(browser,args.source_root,rows,args.cycles)
            if args.group in ('all','applications'):applications(browser,args.source_root,rows,args.cycles)
            if args.group in ('all','hidden'):hidden_frame(browser,args.source_root,rows,args.cycles)
            if args.group=='stress':stress(browser,args.source_root,rows,args.cycles)
        finally:
            if args.output:
                args.output.parent.mkdir(parents=True,exist_ok=True)
                args.output.write_text(json.dumps({'browser':browser.version,'completed':len(rows),'results':rows},ensure_ascii=False,indent=2))
            browser.close()
    print('PASS modal lifecycle scenarios:',len(rows),flush=True)

if __name__=='__main__':main()
