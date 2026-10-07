"""Subscription chooser and complete server-row highlighting regressions."""
import json
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
from flags_ui import MODEL, ROOT

fixture = dict(MODEL)
fixture['subscriptions'] = [
    {'id': 'sub1', 'name': 'Личная подписка', 'count': 31},
    {'id': 'sub2', 'name': 'kot.', 'count': 4},
    {'id': 'long', 'name': 'https://example.org/' + 'subscription-' * 7, 'count': 112},
    {'id': 'bad', 'name': '🇩🇪 <img src=x onerror=alert(1)>', 'count': 1},
]
MOCK = r'''(()=>{
 let listener;const model=MODEL;window.actions=[];
 window.fixtureSnapshot=patch=>{Object.assign(model,patch);listener({data:{kind:'snapshot',data:structuredClone(model)}});};
 window.chrome={webview:{addEventListener:(_,cb)=>listener=cb,postMessage:message=>{
   actions.push(message);
   setTimeout(()=>{
     if(message.action==='switchSubscription'){
       if(window.failSwitch){listener({data:{kind:'reply',id:message.id,ok:false,error:'Не удалось переключить подписку'}});return;}
       model.activeSubscription=message.data.id;model.name=model.subscriptions.find(s=>s.id===message.data.id).name;
     }
     fixtureSnapshot({});listener({data:{kind:'reply',id:message.id,ok:true}});
   },message.action==='switchSubscription'?300:0);
 }}};
})();'''.replace('MODEL', json.dumps(fixture, ensure_ascii=False))

with sync_playwright() as p:
    browser=p.chromium.launch(args=['--no-sandbox'])
    page=browser.new_page(viewport={'width':1080,'height':770})
    errors=[];page.on('pageerror',lambda e:errors.append(str(e)));page.add_init_script(MOCK)
    page.goto((ROOT/'Windows/ui/index.html').as_uri())
    page.locator('[data-page=servers]').click()
    trigger=page.locator('#subscriptionSelect');menu=page.locator('#subscriptionMenu');options=page.locator('#subscriptionOptions')
    trigger.click();expect(menu).to_be_visible();expect(trigger).to_have_attribute('aria-expanded','true')
    expect(options.locator('[data-subscription=sub1]')).to_have_attribute('aria-selected','true')
    expect(options.locator('[data-subscription=sub1] small')).to_have_text('31 сервер')
    expect(options.locator('[data-subscription=long] small')).to_have_text('112 серверов')
    assert options.locator('[onerror],script').count()==0
    expect(options.locator('[data-subscription=bad] strong')).to_contain_text('<img src=x onerror=alert(1)>')
    page.keyboard.press('ArrowDown');expect(options.locator('[data-subscription=sub2]')).to_be_focused()
    page.keyboard.press('Enter');expect(menu).not_to_be_visible();expect(trigger).to_be_disabled()
    page.evaluate('for(let i=0;i<20;i++)fixtureSnapshot({})');expect(trigger).to_be_disabled()
    expect(trigger).to_have_attribute('data-subscription','sub2');expect(trigger).to_be_enabled();expect(trigger).to_be_focused()
    assert page.evaluate("actions.filter(a=>a.action==='switchSubscription').length")==1
    trigger.click();trigger.click();expect(menu).not_to_be_visible()
    trigger.focus();page.keyboard.press('ArrowUp');expect(options.locator('[data-subscription=bad]')).to_be_focused()
    page.keyboard.press('Home');expect(options.locator('[data-subscription=sub1]')).to_be_focused()
    page.keyboard.press('End');expect(options.locator('[data-subscription=bad]')).to_be_focused()
    page.keyboard.press('Escape');expect(menu).not_to_be_visible();expect(trigger).to_be_focused()
    trigger.click();page.locator('#serverSearch').click();expect(menu).not_to_be_visible()
    trigger.click();page.keyboard.press('Tab');expect(page.locator('#addSubscriptionOption')).to_be_focused()
    page.keyboard.press('Tab');expect(menu).not_to_be_visible()
    # The menu and focused item survive telemetry snapshots without replaying animation.
    trigger.click();assert page.evaluate("()=>{const option=document.activeElement;for(let i=0;i<30;i++)fixtureSnapshot({});return option===document.activeElement}")
    page.keyboard.press('Escape')
    page.evaluate('window.failSwitch=true');trigger.click();options.locator('[data-subscription=sub1]').click()
    expect(page.locator('#toast')).to_have_text('Не удалось переключить подписку');expect(trigger).to_be_enabled()
    expect(trigger).to_have_attribute('data-subscription','sub2');page.evaluate('window.failSwitch=false')
    trigger.click();page.locator('#addSubscriptionOption').click();expect(menu).not_to_be_visible();expect(page.locator('#addDialog')).to_be_visible()
    page.keyboard.press('Escape');expect(page.locator('#addDialog')).not_to_be_visible()
    # Top-layer menu must stay within the viewport, including long names and many profiles.
    for width,height in [(800,600),(1080,770),(390,640)]:
        page.set_viewport_size({'width':width,'height':height});trigger.click()
        box=menu.bounding_box();assert box['x']>=0 and box['y']>=0 and box['x']+box['width']<=width and box['y']+box['height']<=height,box
        assert menu.evaluate('(e)=>e.scrollWidth<=e.clientWidth')
        page.keyboard.press('Escape')
    page.set_viewport_size({'width':1080,'height':770})
    for theme in ('dark','light'):
        page.evaluate('(theme)=>fixtureSnapshot({settings:{theme,accent:"lime",mode:"all"}})',theme)
        trigger.click();page.wait_for_timeout(300)
        page.screenshot(path=str(ROOT/f'Tests/ui-subscriptions-{theme}.png'),animations='disabled')
        page.keyboard.press('Escape');row=page.locator('[data-node=lv]');row.locator('.server-card').hover();page.wait_for_timeout(400)
        assert row.evaluate('(e)=>getComputedStyle(e).backgroundColor')==page.locator('body').evaluate('(e)=>getComputedStyle(e).getPropertyValue("--raised").trim()').replace('#282d2e','rgb(40, 45, 46)').replace('#e6e8e0','rgb(230, 232, 224)')
        assert row.locator('.server-card').evaluate('(e)=>getComputedStyle(e).backgroundColor')=='rgba(0, 0, 0, 0)'
        page.screenshot(path=str(ROOT/f'Tests/ui-server-hover-{theme}.png'),animations='disabled')
        row.locator('.ping-node').hover();assert row.evaluate('(e)=>e.matches(":hover")')
    many=[{'id':f's{i}','name':f'Подписка {i}','count':i} for i in range(40)]
    page.evaluate('(subscriptions)=>fixtureSnapshot({subscriptions,activeSubscription:"s0"})',many)
    trigger.click();page.keyboard.press('End');expect(options.locator('[data-subscription=s39]')).to_be_focused()
    box=menu.bounding_box();assert box['y']+box['height']<=770
    page.keyboard.press('Escape');page.evaluate('fixtureSnapshot({subscriptions:[],activeSubscription:"",hasSubscription:false})')
    trigger.click();expect(page.locator('#subscriptionEmpty')).to_be_visible();expect(page.locator('#addSubscriptionOption')).to_be_focused()
    assert not errors,errors
    browser.close()
print('Subscriptions: mouse, keyboard, dismissal, busy/error handling, stable snapshots, safe labels, viewport, themes and full-row hover passed.')
