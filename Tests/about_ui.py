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
with sync_playwright() as p:
    browser=p.chromium.launch(args=['--no-sandbox'])
    try:
        page=browser.new_page(viewport={'width':960,'height':640})
        errors=[];calls=[];pool=[track('One.wav'),track('Two.wav'),track('../bad.wav'),dict(track('Huge.wav'),size=999999999)]
        page.on('pageerror',lambda e:errors.append(str(e)))
        page.add_init_script(MOCK)
        page.route('https://api.github.com/**',lambda route:(calls.append(route.request.url),route.fulfill(json=pool,headers={'Access-Control-Allow-Origin':'*'})))
        page.route('https://raw.githubusercontent.com/**',lambda route:route.fulfill(body=WAV,content_type='audio/wav',headers={'Access-Control-Allow-Origin':'*'}))
        page.goto((ROOT/'Windows/ui/index.html').as_uri())
        assert not calls
        page.locator('[data-page=settings]').click()
        expect(page.locator('#page-settings [data-page=diagnostics]')).to_have_count(0)
        page.locator('#aboutButton').click();expect(page.locator('#musicNext')).to_be_enabled()
        assert len(calls)==1
        assert page.locator('#aboutAudio').evaluate('e=>e.paused')
        page.locator('#musicPlay').click();page.wait_for_function('()=>document.getElementById("aboutAudio").currentTime>.25')
        first=page.locator('#aboutAudio').evaluate('e=>e.currentSrc')
        assert '/music/music/' in first and 'bad' not in first and 'Huge' not in first
        page.locator('#musicPlay').click()
        t=page.locator('#aboutAudio').evaluate('e=>e.currentTime')
        page.wait_for_timeout(250)
        assert abs(page.locator('#aboutAudio').evaluate('e=>e.currentTime')-t)<.05
        page.locator('#musicPlay').click();page.wait_for_function('(t)=>document.getElementById("aboutAudio").currentTime>t+.15',arg=t)
        assert page.locator('#aboutAudio').evaluate('e=>e.currentSrc')==first
        page.locator('#musicNext').click();page.wait_for_function('(src)=>document.getElementById("aboutAudio").currentSrc!==src',arg=first)
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
            assert not page.locator('#aboutAudio').get_attribute('src')
            if isinstance(response,dict):
                page.unroute('https://api.github.com/**');page.route('https://api.github.com/**',lambda route:route.fulfill(json=[track('Recovered.wav')]))
                page.route('https://raw.githubusercontent.com/**',lambda route:route.fulfill(body=WAV,content_type='audio/wav'))
                page.locator('#musicPlay').click();page.wait_for_function('()=>document.getElementById("aboutAudio").currentTime>0')
            page.close()
    finally:browser.close()
print('PASS About: real audio, pause/resume identity and position, random next/end, remote updates, close/Escape pause, image, empty/error recovery and unsafe metadata')
