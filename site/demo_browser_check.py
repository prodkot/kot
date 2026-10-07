"""Exercise the packaged demo in a real browser, including isolation and mobile use."""
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Thread
from urllib.parse import urlsplit
import json
from playwright.sync_api import sync_playwright, expect
from build import build


class Handler(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def check():
    with TemporaryDirectory() as directory:
        root = Path(directory)
        build(root, json.loads((Path(__file__).parent / 'release.json').read_text()))
        server = ThreadingHTTPServer(('127.0.0.1', 0), partial(Handler, directory=str(root)))
        Thread(target=server.serve_forever, daemon=True).start()
        url = f'http://127.0.0.1:{server.server_port}/'
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch(args=['--no-sandbox'])
                page = browser.new_page(viewport={'width': 1440, 'height': 1000})
                errors, network = [], []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.on('request', lambda request: network.append(request.url))
                page.goto(url)
                frame = page.frame_locator('#client-demo')
                expect(frame.locator('#selectedName')).to_have_text('Нидерланды')
                expect(frame.locator('#selectedCountry img')).to_have_attribute('alt', 'NL')
                assert frame.locator('#selectedCountry img').evaluate('async image => {await image.decode();return image.naturalWidth>0;}')
                assert page.locator('#client-demo').get_attribute('sandbox') == 'allow-scripts allow-forms'
                assert frame.locator('body').evaluate("() => {try {return !!parent.document.body;} catch {return false;}}") is False
                frame.locator('body').evaluate('() => document.fonts.ready')
                assert frame.locator('body').evaluate("() => [...document.fonts].some(f => f.family === 'Manrope' && f.status === 'loaded')")

                # Cancelling a connection must cancel its delayed completion too.
                frame.locator('#powerButton').click()
                expect(frame.locator('#stateLabel')).to_have_text('Подключение')
                frame.locator('#powerButton').click()
                page.wait_for_timeout(1000)
                expect(frame.locator('#stateLabel')).to_have_text('Не подключено')
                frame.locator('#powerButton').click()
                expect(frame.locator('#stateLabel')).to_have_text('Подключено')
                expect(frame.locator('#downloadRate')).not_to_have_text('0 Б/с')

                frame.locator('[data-page=servers]').click()
                expect(frame.locator('#serverList .country img')).to_have_count(4)
                assert frame.locator('#serverList .country img').evaluate_all('async images => {await Promise.all(images.map(i=>i.decode()));return images.every(i=>i.naturalWidth>0);}')
                frame.locator('.favorite-node').nth(1).click()
                expect(frame.locator('.favorite-node').nth(1)).to_have_attribute('aria-pressed', 'true')
                frame.locator('#serverSearch').fill('Латвия')
                expect(frame.locator('#serverList .server-card')).to_have_count(1)
                frame.locator('#serverList .server-card').click()
                expect(frame.locator('#selectedName')).to_have_text('Латвия')
                frame.locator('[data-page=logs]').click()
                expect(frame.locator('#connectionList details')).to_have_count(3)
                frame.locator('#connectionList summary').first.click()
                page.wait_for_timeout(1100)
                expect(frame.locator('#connectionList details').first).to_have_attribute('open', '')
                expect(frame.locator('.connection-route').first).to_contain_text('Латвия')
                frame.locator('#pauseLogs').click()
                frozen = frame.locator('#connectionList').inner_text()
                page.wait_for_timeout(1200)
                assert frame.locator('#connectionList').inner_text() == frozen
                frame.locator('#pauseLogs').click()
                frame.locator('[data-log-tab=journal]').click()
                frame.locator('#logSearch').fill('Выбран сервер')
                expect(frame.locator('#journalText')).to_contain_text('Латвия')
                frame.locator('#logSearch').fill('')

                frame.locator('[data-page=settings]').click()
                frame.locator('[data-theme=light]').click()
                frame.locator('.accent-choice:has(input[value=purple])').click()
                expect(frame.locator('body')).to_have_class('theme-light')
                expect(frame.locator('body')).to_have_attribute('data-accent', 'purple')
                frame.locator('[data-setting=killSwitch]').click()
                frame.locator('#demo-failure').click()
                expect(frame.locator('#connectionError')).to_contain_text('Kill switch')
                frame.locator('[data-page=logs]').click()
                frame.locator('[data-log-tab=journal]').click()
                frame.locator('#logLevel').select_option('error')
                expect(frame.locator('#journalText')).to_contain_text('ERROR')
                expect(frame.locator('#journalText')).not_to_contain_text('INFO')
                frame.locator('[data-page=settings]').click()
                frame.locator('[data-setting=killSwitch]').click()
                frame.locator('#hwidButton').click()
                expect(frame.locator('#hwidValue')).to_have_value('DEMO-DEVICE-ID')
                frame.locator('#copyHwid').click()
                expect(frame.locator('#hwidError')).to_contain_text('Это пример')
                frame.locator('#closeDevice').click()
                expect(frame.locator('#deviceDialog')).not_to_be_visible()
                frame.locator('#onlineUpdateButton').click()
                expect(frame.locator('#updateStatus')).to_contain_text('Настоящие обновления доступны в программе.')
                frame.locator('#closeOnlineUpdate').click()
                expect(frame.locator('#onlineUpdateDialog')).not_to_be_visible()

                # Fullscreen keeps the same document and state; Escape works inside it.
                page.locator('#expand-demo').click()
                assert page.locator('#demo-window').evaluate("el => el.matches(':modal')")
                expect(frame.locator('body')).to_have_attribute('data-accent', 'purple')
                page.evaluate("window.postMessage({kind:'kot-demo',close:true}, '*')")
                assert page.locator('#demo-window').evaluate("el => el.matches(':modal')")
                frame.locator('#hwidButton').click()
                frame.locator('#hwidValue').press('Escape')
                expect(frame.locator('#deviceDialog')).not_to_be_visible()
                assert page.locator('#demo-window').evaluate("el => el.matches(':modal')")
                frame.locator('#hwidButton').press('Escape')
                expect(page.locator('#expand-demo')).to_have_attribute('aria-expanded', 'false')
                expect(frame.locator('body')).to_have_attribute('data-accent', 'purple')

                # Reset during a pending operation discards the old frame and timers.
                frame.locator('[data-page=home]').click()
                frame.locator('#powerButton').click()
                page.locator('#reset-demo').click()
                expect(frame.locator('#stateLabel')).to_have_text('Не подключено')
                expect(frame.locator('body')).not_to_have_class('theme-light')
                page.wait_for_timeout(1000)
                expect(frame.locator('#stateLabel')).to_have_text('Не подключено')
                frame.locator('[data-page=servers]').click()
                frame.locator('#pingAll').click()
                expect(frame.locator('#cancelPing')).to_be_visible()
                frame.locator('#cancelPing').click()
                page.wait_for_timeout(1300)
                expect(frame.locator('#pingAll')).to_be_enabled()
                expect(frame.locator('.ping-node')).to_have_count(4)
                assert not frame.locator('.ping-node[data-status=checking]').count()
                frame.locator('#pingAll').click()
                expect(frame.locator('#pingAll')).to_be_enabled(timeout=5000)
                expect(frame.locator('.ping-node[data-status=ok]')).to_have_count(4)

                # Import is intentionally local even for an external URL, and text is escaped.
                frame.locator('[data-open-add]').first.click()
                frame.locator('#profileName').fill('<img src=x onerror=alert(1)>')
                frame.locator('#profileLink').fill('https://not-a-subscription.example/private')
                frame.locator('#addForm [type=submit]').click()
                expect(frame.locator('#addDialog')).not_to_be_visible()
                expect(frame.locator('#subscriptionSelect')).to_have_attribute('data-subscription','sample-2')
                assert not frame.locator('#subscriptionSelect img').count()
                frame.locator('#subscriptionSelect').click()
                frame.locator('#subscriptionOptions [data-subscription=sample-1]').click()
                expect(frame.locator('#subscriptionSelect')).to_have_attribute('data-subscription','sample-1')
                assert all(urlsplit(request).netloc == urlsplit(url).netloc for request in network), network
                # CSP rejects any connection attempted from inside the sandbox.
                assert frame.locator('body').evaluate("async () => { try {await fetch('https://example.org');return false;}catch{return true;} }")

                for width in [320, 375, 390, 600, 700, 1000, 1440]:
                    page.set_viewport_size({'width': width, 'height': 900})
                    assert page.evaluate('document.documentElement.scrollWidth === innerWidth'), width
                    for section in ['servers', 'logs', 'settings', 'home']:
                        frame.locator('[data-page=' + section + ']').click()
                        expect(frame.locator('#page-' + section)).to_be_visible()
                        assert frame.locator('body').evaluate('() => document.documentElement.scrollWidth === innerWidth'), (width, section)
                    if width <= 390:
                        assert frame.locator('[data-page=servers]').bounding_box()['width'] >= 44
                        page.locator('#expand-demo').click()
                        frame.locator('[data-page=settings]').click()
                        frame.locator('#hwidButton').click()
                        bounds = frame.locator('#deviceDialog').bounding_box()
                        iframe = page.locator('#client-demo').bounding_box()
                        assert bounds['x'] >= iframe['x'] and bounds['x'] + bounds['width'] <= iframe['x'] + iframe['width'] + 1
                        frame.locator('#closeDevice').click()
                        expect(frame.locator('#deviceDialog')).not_to_be_visible()
                        page.locator('#close-demo').click()
                assert not errors, errors
                page.emulate_media(reduced_motion='reduce')
                frame.locator('[data-page=home]').click()
                frame.locator('#powerButton').click()
                expect(frame.locator('#stateLabel')).to_have_text('Подключено')
                assert frame.locator('.orbit').evaluate("el => getComputedStyle(el).animationName") == 'none'
                browser.close()
                nojs = p.chromium.launch(args=['--no-sandbox'])
                page = nojs.new_page(java_script_enabled=False)
                page.goto(url)
                expect(page.locator('.demo-nojs')).to_be_visible()
                expect(page.locator('.download-link').first).to_have_attribute('href', 'https://github.com/prodkot/kot/releases/latest')
                nojs.close()
        finally:
            server.shutdown()
    print('Demo: interaction, cancellation, isolation, fullscreen, 7 viewport widths, reduced motion and no-JS fallback passed.')


if __name__ == '__main__':
    check()
