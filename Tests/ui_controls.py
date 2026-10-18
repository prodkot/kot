"""Live menu frames, disabled cursors, uniform journal controls and arrowless scrollbars."""
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
from flags_ui import MOCK, ROOT

with sync_playwright() as p:
    browser=p.chromium.launch(args=['--no-sandbox'])
    try:
        for motion in ('no-preference','reduce'):
            page=browser.new_page(viewport={'width':960,'height':640},reduced_motion=motion)
            errors=[];page.on('pageerror',lambda e:errors.append(str(e)));page.add_init_script(MOCK)
            page.goto((ROOT/'Windows/ui/index.html').as_uri())
            page.locator('[data-page=servers]').click();page.wait_for_timeout(350)
            expect(page.locator('#qualityButton')).to_be_disabled()
            assert page.locator('#qualityButton').evaluate('e=>getComputedStyle(e).cursor')=='not-allowed'
            expect(page.locator('#pingAll use')).to_have_attribute('href','#signal')
            expect(page.locator('.ping-node use').first).to_have_attribute('href','#signal')
            expect(page.locator('.qr-node use').first).to_have_attribute('href','#ellipsis')
            assert page.locator('.ping-node').first.inner_text()==''
            # The first visible frame is already transparent. No deferred toggle flash.
            initial=page.evaluate('''()=>{
              document.getElementById('subscriptionSelect').click();const e=document.getElementById('subscriptionMenu');
              window.menuMotion=e.getAnimations()[0];return {opacity:+getComputedStyle(e).opacity,count:e.getAnimations().length};
            }''')
            assert initial['count']==(1 if motion=='no-preference' else 0),initial
            if motion=='no-preference':
                assert initial['opacity']<.05,initial
                assert page.evaluate('''()=>{for(let i=0;i<10;i++)fixtureSnapshot({});return document.getElementById('subscriptionMenu').getAnimations()[0]===window.menuMotion}''')
            menu=page.locator('#subscriptionMenu');page.wait_for_timeout(260)
            assert menu.evaluate('e=>getComputedStyle(e).opacity')=='1'
            page.keyboard.press('Escape');expect(menu).not_to_be_visible()
            page.evaluate("for(let i=0;i<6;i++){document.getElementById('subscriptionSelect').click();document.getElementById('subscriptionSelect').click();}")
            expect(menu).not_to_be_visible()
            # Every scroll surface uses the same arrowless scrollbar, including inputs.
            for selector in ('.main','#journalText','#subscriptionMenu','#addDialog','#profileLink','#serviceLog'):
                if page.locator(selector).count():
                    assert page.locator(selector).evaluate("e=>getComputedStyle(e,'::-webkit-scrollbar-button').display")=='none',selector
                    assert page.locator(selector).evaluate('e=>getComputedStyle(e).scrollbarColor')=='auto',selector
            page.locator('[data-page=settings]').click();page.locator('[data-page=logs]').click();page.wait_for_timeout(350)
            boxes=[page.locator(selector).bounding_box() for selector in ('#copyViewLog','#pauseLogs','#page-logs [data-back-settings]')]
            assert len({round(b['height']) for b in boxes})==1,boxes
            assert len({round(b['y']) for b in boxes})==1,boxes
            journal=page.locator('#journalText')
            page.evaluate("fixtureSnapshot({journal:'event\\n'.repeat(400)})");journal.evaluate('e=>e.scrollTop=0')
            journal.hover();page.mouse.wheel(0,200);page.wait_for_timeout(150)
            assert journal.evaluate('e=>e.scrollTop')>0
            if motion=='no-preference':page.screenshot(path=str(ROOT/'Tests/ui-controls-journal.png'),animations='disabled')
            assert not errors,errors
            page.close()
    finally:browser.close()
print('Controls: real opening frames, reduced motion, rapid toggles, stable snapshots, signal/ellipsis icons, disabled cursors, equal log buttons and scrollbars passed.')
