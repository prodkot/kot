"""Real media playback: pause identity, next/end, remote refresh, errors and dialogs."""
import ast, io, wave
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
ROOT=Path(__file__).resolve().parents[1]
MOCK=next(ast.literal_eval(n.value) for n in ast.parse((ROOT/'Tests/statistics_ui.py').read_text()).body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='MOCK' for t in n.targets))
stream=io.BytesIO()
with wave.open(stream,'wb') as f:
    f.setnchannels(1);f.setsampwidth(2);f.setframerate(8000);f.writeframes(b'\0\0'*8000*8)
WAV=stream.getvalue()
def track(name):return dict(type='file',name=name,path='music/'+name,size=len(WAV),sha='a'*40)

def serve_audio(route):
    headers={'Access-Control-Allow-Origin':'*','Accept-Ranges':'bytes'}
    requested=route.request.headers.get('range')
    if not requested:
        route.fulfill(body=WAV,content_type='audio/wav',headers=headers)
        return
    start,end=requested.removeprefix('bytes=').split('-')
    first=int(start)
    last=min(int(end),len(WAV)-1) if end else len(WAV)-1
    headers['Content-Range']=f'bytes {first}-{last}/{len(WAV)}'
    route.fulfill(status=206,body=WAV[first:last+1],content_type='audio/wav',headers=headers)

with sync_playwright() as p:
    browser=p.chromium.launch(args=['--no-sandbox'])
    try:
        page=browser.new_page(viewport={'width':960,'height':640})
        errors=[];calls=[];pool=[track('One.wav'),track('Two.wav'),track('../bad.wav'),dict(track('Huge.wav'),size=999999999)]
        page.on('pageerror',lambda e:errors.append(str(e)))
        page.add_init_script(MOCK)
        page.route('https://api.github.com/**',lambda route:(calls.append(route.request.url),route.fulfill(json=pool,headers={'Access-Control-Allow-Origin':'*'})))
        page.route('https://raw.githubusercontent.com/**',serve_audio)
        page.goto((ROOT/'Windows/ui/index.html').as_uri())
        assert not calls
        page.locator('[data-page=settings]').click()
        expect(page.locator('#page-settings [data-page=diagnostics]')).to_have_count(0)
        page.locator('#aboutButton').click();expect(page.locator('#musicNext')).to_be_enabled()
        assert len(calls)==1
        seek=page.get_by_role('slider',name='Позиция воспроизведения')
        expect(seek).to_be_disabled()
        assert page.locator('#aboutAudio').evaluate('e=>e.paused')
        speaker=page.get_by_role('button',name='Громкость',exact=True)
        volume=page.get_by_role('slider',name='Громкость музыки')
        player=page.locator('.micro-player')
        layout='e=>[e.offsetLeft,e.offsetTop,e.offsetWidth,e.offsetHeight]'
        player_bounds=player.evaluate(layout)
        expect(volume).to_be_hidden()
        speaker.click()
        expect(volume).to_be_focused()
        expect(volume).to_have_value('25')
        assert player_bounds==player.evaluate(layout)
        volume.press('Home')
        expect(page.locator('#musicVolumeValue')).to_have_text('0%')
        assert page.locator('#aboutAudio').evaluate('e=>e.volume===0 && e.paused')
        volume.press('ArrowRight')
        expect(volume).to_have_attribute('aria-valuetext','1%')
        volume.press('Escape')
        expect(volume).to_be_hidden()
        expect(speaker).to_be_focused()
        expect(page.locator('#aboutDialog')).to_be_visible()
        page.locator('#musicPlay').click();page.wait_for_function('()=>document.getElementById("aboutAudio").currentTime>.25')
        speaker.click()
        volume.click()
        level=int(volume.input_value())
        assert 45<=level<=55
        assert page.locator('#aboutAudio').evaluate('e=>!e.paused && e.volume')==level/100
        page.locator('#musicTitle').click()
        expect(volume).to_be_hidden()
        first=page.locator('#aboutAudio').evaluate('e=>e.currentSrc')
        assert '/music/music/' in first and 'bad' not in first and 'Huge' not in first
        page.locator('#musicPlay').click()
        t=page.locator('#aboutAudio').evaluate('e=>e.currentTime')
        page.wait_for_timeout(250)
        assert abs(page.locator('#aboutAudio').evaluate('e=>e.currentTime')-t)<.05
        expect(seek).to_be_enabled()
        expect(page.locator('#musicDuration')).to_have_text('0:08')
        bounds=seek.bounding_box()
        assert bounds['height']>=24 and page.locator('#musicProgress').bounding_box()['height']==6
        x,y,width=bounds['x'],bounds['y']+bounds['height']/2,bounds['width']
        page.mouse.click(x+width/2,y)
        page.wait_for_function('()=>aboutAudio.paused && !aboutAudio.seeking && Math.abs(aboutAudio.currentTime-4)<.1')
        page.mouse.move(x+width/2,y);page.mouse.down()
        page.mouse.move(x+width*.8,y,steps=6);page.mouse.up()
        page.wait_for_function('()=>aboutAudio.paused && !aboutAudio.seeking && Math.abs(aboutAudio.currentTime-6.4)<.1')
        expect(page.locator('#musicTime')).to_have_text('0:06')
        expect(seek).to_have_attribute('aria-valuetext','0:06 из 0:08')
        assert abs(page.locator('#musicProgress').evaluate('e=>e.position')-.8)<.02
        seek.press('Home')
        page.wait_for_function('()=>aboutAudio.currentTime===0')
        seek.press('ArrowRight')
        page.wait_for_function('()=>aboutAudio.paused && aboutAudio.currentTime>0')
        t=page.locator('#aboutAudio').evaluate('e=>e.currentTime')
        page.locator('#musicPlay').click();page.wait_for_function('(t)=>document.getElementById("aboutAudio").currentTime>t+.15',arg=t)
        assert page.locator('#aboutAudio').evaluate('e=>e.currentSrc')==first
        page.mouse.click(x+width*.65,y)
        page.wait_for_function('()=>!aboutAudio.paused && !aboutAudio.seeking && aboutAudio.currentTime>5 && musicProgress.value>5')
        page.locator('#musicNext').click();page.wait_for_function('(src)=>document.getElementById("aboutAudio").currentSrc!==src',arg=first)
        expect(page.locator('#musicTime')).to_have_text('0:00')
        assert page.locator('#aboutAudio').evaluate('e=>e.volume')==level/100
        second=page.locator('#aboutAudio').evaluate('e=>e.currentSrc')
        page.locator('#aboutAudio').evaluate('e=>e.currentTime=e.duration-.1')
        page.wait_for_function('(src)=>document.getElementById("aboutAudio").currentSrc!==src',arg=second)
        page.locator('#closeAbout').click();expect(page.locator('#aboutDialog')).not_to_be_visible()
        assert page.locator('#aboutAudio').evaluate('e=>e.paused')
        retained=page.locator('#aboutAudio').evaluate('e=>({src:e.currentSrc,t:e.currentTime})')
        pool[:]=[track('New.wav')]
        page.evaluate('(()=>{const now=Date.now;Date.now=()=>now()+300001})()')
        page.locator('#aboutButton').click();page.wait_for_function('()=>document.getElementById("musicNext").disabled===false')
        assert len(calls)==2
        expect(speaker).to_have_attribute('aria-expanded','false')
        speaker.press('Enter')
        expect(volume).to_have_value(str(level))
        volume.press('Tab')
        expect(volume).to_be_hidden()
        expect(page.locator('#musicNext')).to_be_focused()
        page.locator('#musicPlay').click();page.wait_for_timeout(150)
        assert page.locator('#aboutAudio').evaluate('e=>e.currentSrc')==retained['src']
        page.locator('#musicNext').click();page.wait_for_function('()=>document.getElementById("aboutAudio").currentSrc.includes("New.wav")')
        page.locator('#astraCredit').click();expect(page.locator('#astraDialog')).to_be_visible()
        assert page.locator('#astraDialog img').evaluate('async i=>{await i.decode();return i.naturalWidth>0}')
        page.keyboard.press('Escape');expect(page.locator('#astraDialog')).not_to_be_visible();expect(page.locator('#aboutDialog')).to_be_visible()
        page.screenshot(path=str(ROOT/'Tests/ui-about.png'),animations='disabled')
        page.keyboard.press('Escape');expect(page.locator('#aboutDialog')).not_to_be_visible();assert page.locator('#aboutAudio').evaluate('e=>e.paused')
        assert not errors,errors
        page.close()
        # Empty and unavailable pools remain recoverable; unsafe metadata creates no media URL.
        for response in ([],[track('../bad.wav')],{'message':'unavailable'}):
            page=browser.new_page(viewport={'width':800,'height':600},reduced_motion='reduce')
            page.add_init_script(MOCK)
            page.route('https://api.github.com/**',lambda route:route.fulfill(json=response,status=503 if isinstance(response,dict) else 200))
            page.goto((ROOT/'Windows/ui/index.html').as_uri());page.locator('[data-page=settings]').click();page.locator('#aboutButton').click()
            expect(page.locator('#musicStatus')).to_be_visible();page.locator('#musicPlay').click()
            expect(page.locator('#musicPlay')).to_have_attribute('aria-label','Воспроизвести')
            expect(page.locator('#musicSeek')).to_be_disabled()
            assert not page.locator('#aboutAudio').get_attribute('src')
            if isinstance(response,dict):
                page.unroute('https://api.github.com/**');page.route('https://api.github.com/**',lambda route:route.fulfill(json=[track('Recovered.wav')]))
                page.route('https://raw.githubusercontent.com/**',lambda route:route.fulfill(body=WAV,content_type='audio/wav'))
                page.locator('#musicPlay').click();page.wait_for_function('()=>document.getElementById("aboutAudio").currentTime>0')
            page.close()
    finally:browser.close()
print('PASS About: real audio, pause/resume identity and position, random next/end, remote updates, close/Escape pause, image, empty/error recovery and unsafe metadata')
