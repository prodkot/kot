"""Render subscription flag names with the client's CSP and WebView message format."""
import json
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
MODEL = {
    'version': '0.5.2', 'name': 'Личная подписка', 'hasSubscription': True,
    'nodes': [
        {'id': 'kr', 'name': '🇰🇷 Wi-Fi локации', 'protocol': 'vless', 'code': 'VL'},
        {'id': 'fi', 'name': '🇫🇮 Финляндия [ ⚡ ]', 'protocol': 'trojan', 'code': 'TR'},
        {'id': 'lv', 'name': '🇱🇻 Латвия [ ⚡ ]', 'protocol': 'vless', 'code': 'VL'},
        {'id': 'de', 'name': '🇩🇪 Германия [ ⚡ ]', 'protocol': 'vless', 'code': 'VL'},
        {'id': 'nl', 'name': '🇳🇱 Нидерланды [ ⚡ ]', 'protocol': 'vless', 'code': 'VL'},
    ],
    'selected': 'de', 'state': 'idle', 'error': '', 'favorites': [],
    'settings': {'theme': 'dark', 'accent': 'lime', 'mode': 'all'},
    'ping': {'settings': {'sort': 'none'}},
    'subscriptions': [{'id': 'sub1', 'name': 'Личная подписка', 'count': 5}], 'activeSubscription': 'sub1',
}
MOCK = r'''(() => {
  let listener; let model = MODEL;
  window.fixtureSnapshot = patch => {
    Object.assign(model, patch);
    listener({data: {kind: 'snapshot', data: structuredClone(model)}});
  };
  window.chrome = {webview: {
    addEventListener: (_, callback) => listener = callback,
    postMessage: message => setTimeout(() => {
      if(message.action === 'select') model.selected = message.data.id;
      fixtureSnapshot({});
      listener({data: {kind: 'reply', id: message.id, ok: true}});
    }, 0)
  }};
})();'''.replace('MODEL', json.dumps(MODEL, ensure_ascii=False))


def check():
    with sync_playwright() as p:
        browser = p.chromium.launch(args=['--no-sandbox'])
        page = browser.new_page(viewport={'width': 1080, 'height': 770})
        errors, requests = [], []
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.on('request', lambda request: requests.append(request.url))
        page.add_init_script(MOCK)
        page.goto((ROOT / 'Windows/ui/index.html').as_uri())
        expect(page.locator('#selectedName img')).to_have_attribute('alt', 'DE')
        page.locator('[data-page=servers]').click()
        expect(page.locator('#serverList .server-copy img')).to_have_count(5)
        assert page.locator('#serverList .server-copy img').evaluate_all('(images)=>images.map(i=>i.alt)') == ['KR', 'FI', 'LV', 'DE', 'NL']
        page.wait_for_function("[...document.querySelectorAll('.country-flag')].every(i=>i.complete&&i.naturalWidth>0)")
        # Protocol badges must not become flags for Turkey or South Sudan.
        expect(page.locator('[data-node=fi] .country')).to_have_text('TR')
        assert page.locator('#serverList .country img').count() == 0
        expect(page.locator('[data-node=fi] strong')).to_have_text(' Финляндия [ ⚡ ]')
        page.locator('#serverSearch').fill('Финляндия')
        expect(page.locator('#serverList .server-card')).to_have_count(1)
        page.locator('#serverList .server-card').click()
        expect(page.locator('#selectedName img')).to_have_attribute('alt', 'FI')
        # A telemetry tick must preserve loaded images instead of rebuilding them.
        assert page.evaluate("()=>{const image=document.querySelector('#selectedName img');for(let n=0;n<30;n++)fixtureSnapshot({});return image===document.querySelector('#selectedName img');}")
        page.evaluate("fixtureSnapshot({selected:'auto',automaticNode:'nl'})")
        expect(page.locator('#selectedCity img')).to_have_attribute('alt', 'NL')
        page.evaluate("fixtureSnapshot({selected:'de'})")
        page.locator('[data-page=servers]').click()
        page.locator('#serverSearch').fill('')
        for theme in ('dark', 'light'):
            page.evaluate('(theme)=>fixtureSnapshot({settings:{theme,accent:"lime",mode:"all"}})', theme)
            page.locator('#serverSearch').blur()
            page.wait_for_timeout(450)
            page.screenshot(path=str(ROOT / f'Tests/ui-flags-{theme}.png'), animations='disabled')
        # All supplied assets must decode offline under the production CSP.
        codes = [p.stem for p in sorted((ROOT / 'Windows/ui/flags').glob('*.svg'))]
        assert page.evaluate('''async codes => (await Promise.all(codes.map(code=>new Promise(resolve=>{
            const image=new Image();image.onload=()=>resolve(image.naturalWidth>0);
            image.onerror=()=>resolve(false);image.src='flags/'+code+'.svg';
        })))).every(Boolean)''', codes)
        # Malicious names stay text, including names mixed with genuine flag pairs.
        bad = "🇩🇪 <img src=https://example.org/x onerror=alert(1)> 🇿🇿 plain TR SS"
        page.evaluate('(name)=>fixtureSnapshot({selected:"bad",nodes:[{id:"bad",name,code:"SS",protocol:"shadowsocks"}]})', bad)
        expect(page.locator('#serverList strong img')).to_have_count(1)
        expect(page.locator('#serverList strong')).to_contain_text('<img src=https://example.org/x onerror=alert(1)> ZZ plain TR SS')
        expect(page.locator('#serverList .country')).to_have_text('SS')
        assert not page.locator('#serverList script, #serverList [onerror]').count()
        assert all(url.startswith('file:') for url in requests), requests
        assert not errors, errors
        browser.close()
    print('Flags: subscription names, offline assets, search/select, automatic node, themes, stable snapshots and untrusted text passed.')


if __name__ == '__main__':
    check()
