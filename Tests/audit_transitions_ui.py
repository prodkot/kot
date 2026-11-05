"""F2/F4 regression scenarios: production UI, deliberately controlled bridge."""
import ast
import os
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
tree = ast.parse((ROOT / 'Tests/routing_ui.py').read_text())
MOCK = next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign)
            and any(isinstance(t, ast.Name) and t.id == 'MOCK' for t in n.targets))
MOCK = MOCK.replace(" const codes=", """ for(const p of model.routing.profiles)p.revision=JSON.stringify([p.rules,p.sourceUrl,p.refreshHours]);
 const codes=""")
MOCK = MOCK.replace("window.routingRequests=[];", """window.routingRequests=[];window.importRequests=[];const held=new Map();
window.importHeld=()=>[...held.values()].map(m=>structuredClone(m));
window.importReply=id=>{const msg=held.get(id);callback({data:{kind:'reply',id:msg.id,ok:true}});};
window.fixtureRoute=()=>{model.routing.profiles=[{id:'r1',rules:{version:1,name:'Initial',defaultAction:'proxy',blockDomains:['old.example']},sourceUrl:'https://example.org/rules',refreshHours:1}];model.routing.active='r1';snapshot();};
window.fixtureRefreshRules=()=>{model.routing.profiles[0].rules.blockDomains.push('new-block.example');snapshot();};
""")
MOCK = MOCK.replace(" const find=()=>model.routing.profiles.find(p=>p.id===msg.data.id);", """ const find=()=>model.routing.profiles.find(p=>p.id===msg.data.id);
 window.importRequests.push(structuredClone(msg));
 if(['import','importText','importServersFile'].includes(msg.action)){held.set(msg.data.operationId||String(msg.id),msg);return;}
 if(msg.action==='cancelImport'){reply();return;}
 if(msg.action==='routingSave'&&find()&&msg.data.baseRevision!==find().revision){
   reply(false,'Профиль маршрутизации изменился. Черновик сохранён. Нажмите «Сбросить» для загрузки актуальных правил.');return;
 }
""")
TIMER = "const originalTimeout=window.setTimeout;window.setTimeout=(f,ms,...args)=>originalTimeout(f,ms===120000?1200:ms,...args);"

with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'], executable_path=os.environ.get('KOT_TEST_CHROMIUM'))
    try:
        def new_page():
            page = browser.new_page(viewport={'width': 1080, 'height': 800})
            page.add_init_script(TIMER + '\n' + MOCK)
            page.goto((ROOT / 'Windows/ui/index.html').as_uri())
            return page

        page = new_page()
        page.locator('[data-open-add]').first.click()
        page.locator('#importServersFile').click()
        page.wait_for_function("()=>importHeld().length===1")
        first = page.evaluate("importHeld()[0].data.operationId")
        assert first, "file imports need an operation ID"
        expect(page.locator('#addForm [type=submit]')).to_be_disabled()
        page.locator('#cancelAdd').click()
        expect(page.locator('#addDialog')).not_to_be_visible()
        page.wait_for_function("()=>importRequests.some(m=>m.action==='cancelImport'&&m.data.operationId===importHeld()[0].data.operationId)")
        page.locator('[data-open-add]').first.click()
        page.locator('#importServersFile').click()
        page.wait_for_function("()=>importHeld().length===2")
        second = page.evaluate("importHeld()[1].data.operationId")
        assert first != second
        page.evaluate("id=>importReply(id)", first)
        expect(page.locator('#importServersFile')).to_be_disabled()
        expect(page.locator('#addDialog')).to_be_visible()
        expect(page.locator('#addError')).to_have_text('')
        page.keyboard.press('Escape')
        expect(page.locator('#addDialog')).not_to_be_visible()
        page.wait_for_function("()=>importRequests.some(m=>m.action==='cancelImport'&&m.data.operationId===importHeld()[1].data.operationId)")
        print('PASS F2 shared file/form state, matching cancellation, late old reply, Escape')
        page.close()

        page = new_page()
        page.locator('[data-open-add]').first.click()
        page.locator('#profileLink').fill('https://example.org/sub')
        page.locator('#addForm [type=submit]').click()
        expect(page.locator('#addError')).to_have_text('Операция заняла слишком много времени.')
        page.wait_for_function("()=>importRequests.some(m=>m.action==='cancelImport'&&m.data.operationId===importHeld()[0].data.operationId)")
        first = page.evaluate("importHeld()[0].data.operationId")
        page.locator('#addForm [type=submit]').click()
        page.wait_for_function("()=>importHeld().length===2")
        page.evaluate("id=>importReply(id)", first)
        expect(page.locator('#addForm [type=submit]')).to_be_disabled()
        expect(page.locator('#addDialog')).to_be_visible()
        print('PASS F2 timeout cancels backend immediately; old response cannot clear the next operation')
        page.close()

        for edit_list in [False, True]:
            page = new_page()
            page.evaluate('fixtureRoute()')
            page.locator('#homeRoutingButton').click()
            page.locator('#routingName').fill('My draft')
            if edit_list:
                page.locator('#routingBlockDomains').fill('my-block.example')
            page.evaluate('fixtureRefreshRules()')
            page.locator('#routingForm [type=submit]').click()
            expect(page.locator('#routingError')).to_contain_text('Черновик')
            expect(page.locator('#routingName')).to_have_value('My draft')
            expect(page.locator('#routingBlockDomains')).to_have_value('my-block.example' if edit_list else 'old.example')
            assert 'new-block.example' in page.evaluate('fixtureProfiles().profiles[0].rules.blockDomains')
            page.locator('#routingReset').click()
            expect(page.locator('#routingBlockDomains')).to_have_value('old.example\nnew-block.example')
            page.evaluate('fixtureSnapshot();fixtureSnapshot()')
            page.locator('#routingName').fill('Fresh draft')
            page.locator('#routingForm [type=submit]').click()
            expect(page.locator('#routingError')).to_have_text('')
            expect(page.locator('#routingActive')).to_be_enabled()
            assert page.evaluate('fixtureProfiles().profiles[0].rules.name') == 'Fresh draft'
            print('PASS F4 conflict preserves draft and current rules; reset/repeated snapshots allow an explicit save')
            page.close()
    finally:
        browser.close()
