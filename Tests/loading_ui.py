"""Real production loader: lifecycle, physics, probabilities, startup and easter-egg regression."""
from pathlib import Path
import ast,json,math
from playwright.sync_api import sync_playwright,expect
ROOT=Path(__file__).resolve().parents[1];UI=ROOT/'Windows/ui'
mock=next(ast.literal_eval(n.value) for n in ast.parse((ROOT/'Tests/ui.py').read_text()).body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='mock' for t in n.targets))
data=json.loads((UI/'loading-motion.json').read_text());counter=0
# Generated web and native inputs must be identical.
assert json.loads((UI/'loading-motion.js').read_text().split('window.KotLoadingMotion=',1)[1].rstrip(';\n'))==data
for id,m in data['motions'].items():
    frames=m['frames'];assert frames[0]==[0]*4 and frames[-1]==[0]*4,id
    for x,y,a,k in frames:
        angle=math.radians(a);bottom=y-.5+.5*(abs(math.cos(angle))+abs(math.sin(angle)))
        assert bottom<.00001,(id,'ground penetration',bottom)
    # All boundary derivatives approach zero. Impulses inside jumps/contact are intentional.
    assert max(abs(a-b)/(90 if j==2 else 1) for j,(a,b) in enumerate(zip(frames[0],frames[1])))<.001,id
    assert max(abs(a-b)/(90 if j==2 else 1) for j,(a,b) in enumerate(zip(frames[-1],frames[-2])))<.001,id
    counter+=1
# Ballistic acceleration constant away from impact, no artificial apex hold.
y=[p[1] for p in data['motions']['hop']['frames']]
for i in range(40,98):
    assert abs((y[i+1]-2*y[i]+y[i-1])*120**2-24)<.05
# Corner is fixed throughout the rollover, not a rotating sprite floating above the floor.
for x,y,a,k in data['motions']['roll']['frames']:
    r=math.radians(a)
    assert abs(x+.5*math.cos(r)-.5*math.sin(r)-.5)<.00001
    assert abs(y+.5*math.sin(r)+.5*math.cos(r)-.5)<.00001
assert all(p[3]==0 for p in data['motions']['nudge']['frames'][:121])
with sync_playwright() as p:
    browser=p.chromium.launch(args=['--no-sandbox']);print('Chromium',browser.version,flush=True)
    for theme in ('dark','light'):
      for zoom in (1,1.25,1.5):
        page=browser.new_page(viewport={'width':630,'height':600});errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
        page.add_init_script(mock.replace("theme:'dark'",f"theme:'{theme}'",1))
        page.goto((UI/'index.html').as_uri());expect(page.locator('#startupScreen')).to_be_hidden()
        page.evaluate('(z)=>document.body.style.zoom=z',zoom)
        # The real mark handles the 47th click only, without taking focus or changing geometry.
        brand=page.locator('#brandButton');brand.focus();before=brand.bounding_box()
        brand.evaluate('(e)=>{for(let i=0;i<46;i++)e.click()}');assert page.locator('.brand-egg-point').count()==0
        brand.click();assert page.locator('.brand-egg-point').count()==1
        page.wait_for_timeout(400);assert page.locator('.brand-egg-point').evaluate('(e)=>getComputedStyle(e).transform')!='none'
        after=brand.bounding_box();assert all(abs(before[k]-after[k])<.1 for k in before)
        assert brand.evaluate('(e)=>document.activeElement===e')
        # Native minimized/tray signal stops callbacks without resetting the pose.
        page.evaluate("window.dispatchEvent(new CustomEvent('kot-window-visibility',{detail:false}))")
        pose=page.locator('.brand-egg-point').get_attribute('style');page.wait_for_timeout(90)
        assert page.locator('.brand-egg-point').get_attribute('style')==pose
        page.evaluate("window.dispatchEvent(new CustomEvent('kot-window-visibility',{detail:true}))")
        brand.evaluate('(e)=>{for(let i=0;i<395;i++)e.click()}');assert page.evaluate('fixtureMeows')==0
        brand.click();assert page.evaluate('fixtureMeows')==1
        if theme=='dark' and zoom==1:
            assert brand.evaluate('''e=>{const point=e.lastElementChild,pose=point.style.transform;
                for(let i=0;i<47;i++)e.click();return point.style.transform===pose;}''')
            page.wait_for_timeout(3400) # Next scene has started at the resting boundary.
            assert 'translate(' in brand.locator('span').get_attribute('style')
            brand.evaluate('(e)=>{for(let i=0;i<396;i++)e.click()}');assert page.evaluate('fixtureMeows')==2
            page.wait_for_timeout(3600)
        else:page.wait_for_timeout(3300)
        assert brand.locator('span').get_attribute('style') in ('',None)
        brand.evaluate('(e)=>{for(let i=0;i<47;i++)e.click()}');page.wait_for_timeout(400)
        assert 'translate(' in brand.locator('span').get_attribute('style')
        page.emulate_media(reduced_motion='reduce');page.wait_for_function("()=>!document.querySelector('.brand-egg-point').style.transform")
        assert not errors,errors;page.close();counter+=1
    page=browser.new_page();page.goto((ROOT/'artifacts/loading-demo.html').as_uri())
    # Large samples + native compact slot: no layout motion, smooth queued switches.
    page.wait_for_timeout(100)
    box=page.locator('article').first.bounding_box();page.wait_for_timeout(450);assert page.locator('article').first.bounding_box()==box
    result=page.evaluate('''()=>{
      let seed=1;const rng=()=>{seed=(1664525*seed+1013904223)>>>0;return seed/4294967296};
      const counts={calm:0,lively:0,joke:0,rare:0};
      for(let n=0;n<10000;n++){
        const list=KotLoading.sequence(false,rng);counts[KotLoadingMotion.motions[list[1]].group]++;
        const jokes=list.filter(id=>['joke','rare'].includes(KotLoadingMotion.motions[id].group));
        if(new Set(jokes).size!==jokes.length||list.includes('protest'))throw Error('repeat/egg');
        if(KotLoading.sequence(true,rng).some(id=>KotLoadingMotion.motions[id].group!=='calm'))throw Error('compact');
      }
      return counts;
    }''');print('10000 first weighted choices',result,flush=True)
    assert 7300<result['calm']<7700 and 1800<result['lively']<2200 and 350<result['joke']<550 and 20<result['rare']<90
    page.evaluate("demoPlayers.hop.select('roll')");assert page.evaluate('demoPlayers.hop.state.queued')=='roll'
    page.wait_for_function("()=>demoPlayers.hop.state.variant==='roll'")
    page.evaluate("window.dispatchEvent(new CustomEvent('kot-window-visibility',{detail:false}))")
    assert page.evaluate('players.every(p=>!p.state.scheduled)')
    t=page.evaluate('demoPlayers.hop.state.elapsed');page.wait_for_timeout(100);assert page.evaluate('demoPlayers.hop.state.elapsed')==t
    page.emulate_media(reduced_motion='reduce');assert page.evaluate('players.every(p=>!p.state.scheduled)')
    assert page.locator('.loader-point').first.get_attribute('transform')=='translate(0 0) rotate(0 1277.1764705882354 1464)'
    page.emulate_media(reduced_motion='no-preference');page.evaluate("window.dispatchEvent(new CustomEvent('kot-window-visibility',{detail:true}))")
    assert page.evaluate('players.some(p=>p.state.scheduled)')
    # CSS-hidden iframe: no loader keeps requesting frames; restore resumes the same clock.
    page.evaluate("window.testFrame=document.createElement('iframe');testFrame.src=location.href;testFrame.style='width:800px;height:650px';document.body.append(testFrame);testFrame.scrollIntoView()")
    frame=page.frames[-1];frame.wait_for_function('()=>window.players&&players.some(p=>p.state.scheduled)')
    page.evaluate("testFrame.style.display='none'");frame.wait_for_function('()=>players.every(p=>!p.state.scheduled)',polling=50)
    times=frame.evaluate('players.map(p=>p.state.elapsed)');page.wait_for_timeout(80);assert frame.evaluate('players.map(p=>p.state.elapsed)')==times
    page.evaluate("testFrame.style.display='block';testFrame.scrollIntoView()");frame.wait_for_function('()=>players.some(p=>p.state.scheduled)')
    page.evaluate('testFrame.remove()')
    # Production geometry at multiple sizes: fixed slot, square remains rigid, original path.
    for size in (16,20,24,48,72,96,144):
        page.locator('.big').first.evaluate('(e,s)=>e.style.fontSize=s+"px"',size)
        bounds=page.locator('.big').first.bounding_box();page.wait_for_timeout(80)
        assert page.locator('.big').first.bounding_box()==bounds
        assert abs(bounds['width']-size)<.01 and abs(bounds['height']-size)<.01
    # Selection never moves the current pose at the moment of input.
    assert page.evaluate("()=>{const e=document.querySelectorAll('.big')[3].querySelector('.loader-point'),before=e.getAttribute('transform');demoPlayers.roll.select('escape');return e.getAttribute('transform')===before}")
    # A loading operation may end at any pose. Restart clears queued work and old frame requests.
    page.evaluate('''()=>{
      const slot=document.createElement('span');document.body.prepend(slot);slot.style.fontSize='48px';
      window.repeatOwner=KotLoading.create(slot);window.repeatSlot=slot;
      for(let i=0;i<100;i++){
        repeatOwner.start();repeatOwner.select('roll');repeatOwner.stop();
        if(repeatOwner.state.active||repeatOwner.state.scheduled)throw Error('stopped owner retains callback');
      }
      repeatOwner.start();if(repeatOwner.state.queued!==null)throw Error('stale selection');
      repeatOwner.select('roll');repeatOwner.seek(2090);
    }''')
    page.evaluate('window.scrollTo(0,0)')
    page.wait_for_function("()=>repeatOwner.state.variant==='roll'")
    page.evaluate('repeatOwner.dispose();repeatSlot.remove()')
    # Controlled refresh clocks: identical elapsed time must produce the same visible pose.
    poses=page.evaluate('''()=>{
      const request=window.requestAnimationFrame,cancel=window.cancelAnimationFrame,poses=[];
      try{
        for(const hz of [30,60,120,144]){
          const frames=new Map();let key=0,pose;
          window.requestAnimationFrame=fn=>{frames.set(++key,fn);return key};
          window.cancelAnimationFrame=id=>frames.delete(id);
          const slot=document.createElement('span');document.body.prepend(slot);
          const player=KotLoading.create(slot,{variant:'hop',render:p=>pose=p});player.start();
          for(let n=0;n<=hz/2;n++){
            const callbacks=[...frames.values()];frames.clear();callbacks.forEach(fn=>fn(n*1000/hz));
          }
          poses.push({elapsed:player.state.elapsed,pose});player.dispose();slot.remove();
          if(frames.size)throw Error('disposed fake refresh clock');
        }
      }finally{window.requestAnimationFrame=request;window.cancelAnimationFrame=cancel;}
      return poses;
    }''')
    for value in poses:
        assert abs(value['elapsed']-500)<.000001
        assert max(abs(a-b) for a,b in zip(value['pose'],poses[0]['pose']))<.000001
    page.evaluate('players.forEach(p=>p.dispose())');assert page.evaluate('players.every(p=>!p.state.active&&!p.state.scheduled)')
    page.close();browser.close()
print('PASS',counter,'physics/theme/zoom cases; sequence, interruption, hidden/reduced, lifecycle and 47/443 checks')
