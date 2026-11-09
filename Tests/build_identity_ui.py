"""Real UI shows and refreshes the stamped version, retaining old model support."""
import ast
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
MOCK = next(ast.literal_eval(n.value) for n in ast.parse((ROOT/'Tests/statistics_ui.py').read_text()).body
            if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'MOCK' for t in n.targets))
with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        page = browser.new_page(viewport={'width': 960, 'height': 640})
        page.set_default_timeout(5000)
        page.add_init_script(MOCK)
        page.route('https://api.github.com/**', lambda route: route.fulfill(json=[]))
        page.goto((ROOT/'Windows/ui/index.html').as_uri())
        for theme in ('dark', 'light'):
            for size in ((800, 600), (960, 640)):
                page.set_viewport_size(dict(zip(('width', 'height'), size)))
                for build in ('dev.123.1', 'dev.123.2', 'release.123.1', 'local.20261109140000.1234abcd'):
                    display = f'0.8.3 ({build})'
                    page.evaluate('(m)=>fixtureStatistics(m)', {'version':'0.8.3', 'displayVersion':display, 'buildNumber':build, 'settings':{'theme':theme}})
                    expect(page.locator('#titleVersion')).to_have_text(display)
                    expect(page.locator('#version')).to_contain_text(display)
                    box = page.locator('#titleVersion').bounding_box()
                    chrome = page.locator('.chrome').bounding_box()
                    assert box['x'] + box['width'] < chrome['x']
                    page.locator('[data-page=settings]').click()
                    page.locator('#aboutButton').click()
                    expect(page.locator('#aboutVersion')).to_have_text('Версия ' + display)
                    assert page.locator('#aboutVersion').evaluate('(e)=>e.scrollWidth<=e.clientWidth')
                    page.locator('#closeAbout').click()
        page.evaluate('fixtureStatistics({version:"0.8.2",displayVersion:null})')
        expect(page.locator('#titleVersion')).to_have_text('0.8.2')
        print('PASS version/build UI: header, details, about, new build snapshots, both themes, minimum size and legacy fallback')
    finally:
        browser.close()
