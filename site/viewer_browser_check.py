"""Check the production viewer, timers, punctuation and cat under the Pages subpath."""
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Thread
import json
from playwright.sync_api import sync_playwright, expect
from build import build


class Handler(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def check():
    site = Path(__file__).resolve().parent
    release = json.loads((site / 'release.json').read_text())
    with TemporaryDirectory() as directory:
        root = Path(directory)
        build(root / 'kot', release)
        server = ThreadingHTTPServer(('127.0.0.1', 0), partial(Handler, directory=str(root)))
        worker = Thread(target=server.serve_forever, daemon=True)
        worker.start()
        url = f'http://127.0.0.1:{server.server_port}/kot/'
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch(args=['--no-sandbox'])
                try:
                    page = browser.new_page(viewport={'width': 1440, 'height': 1000})
                    errors, failures = [], []
                    page.on('pageerror', lambda error: errors.append(str(error)))
                    page.on('response', lambda response: failures.append(response.url) if response.status >= 400 else None)
                    page.goto(url, wait_until='networkidle')
                    page.evaluate('() => document.fonts.ready')
                    # The timer starts itself, then gives every manual action a fresh five seconds.
                    page.clock.install()
                    page.clock.fast_forward(5300)
                    expect(page.locator('#view-servers')).to_have_attribute('aria-selected', 'true')
                    page.locator('#view-settings').click()
                    page.clock.fast_forward(4900)
                    expect(page.locator('#view-settings')).to_have_attribute('aria-selected', 'true')
                    page.locator('#view-settings').click()
                    page.clock.fast_forward(4900)
                    expect(page.locator('#view-settings')).to_have_attribute('aria-selected', 'true')
                    page.clock.fast_forward(200)
                    expect(page.locator('#view-home')).to_have_attribute('aria-selected', 'true')
                    page.close()

                    page = browser.new_page(viewport={'width': 1440, 'height': 1000}, reduced_motion='reduce')
                    page.on('pageerror', lambda error: errors.append(str(error)))
                    page.on('response', lambda response: failures.append(response.url) if response.status >= 400 else None)
                    page.goto(url, wait_until='networkidle')
                    page.clock.install()
                    page.clock.fast_forward(12000)
                    expect(page.locator('#view-home')).to_have_attribute('aria-selected', 'true')
                    for tab in page.get_by_role('tab').all():
                        tab.click()
                        expect(tab).to_have_attribute('aria-selected', 'true')
                        dots = page.locator('[data-explorer-copy] .text-dot')
                        assert dots.count() > 0
                        assert dots.first.evaluate("el => getComputedStyle(el, '::after').backgroundColor") == 'rgb(242, 244, 237)'
                        assert dots.first.evaluate("el => getComputedStyle(el, '::after').borderRadius") == '0px'
                    brand = page.locator('.vw-header .text-dot')
                    assert brand.evaluate("el => getComputedStyle(el, '::after').backgroundColor") == 'rgb(213, 248, 140)'
                    assert brand.evaluate("el => getComputedStyle(el, '::after').width === getComputedStyle(el, '::after').height")
                    for width in (320, 375, 390, 600, 768, 1000, 1440):
                        page.set_viewport_size({'width': width, 'height': 900})
                        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), width
                        page.locator('.kot-pet').scroll_into_view_if_needed()
                        page.wait_for_timeout(50)
                        before = page.locator('.vw-install .kot-download').bounding_box()
                        page.locator('.kot-pet').press('Enter')
                        expect(page.locator('#install')).to_have_class('vw-install vw-wrap reveal is-purring')
                        expect(page.locator('.install-title-secret')).not_to_have_attribute('aria-hidden', 'true')
                        expect(page.locator('[data-cat-status]')).to_have_text('Мяу, и интернет у тебя в руках.')
                        after = page.locator('.vw-install .kot-download').bounding_box()
                        assert abs(before['y'] - after['y']) < 1, (width, before, after)
                        page.clock.fast_forward(5100)
                        expect(page.locator('.install-title-secret')).to_have_attribute('aria-hidden', 'true')
                    for link in page.locator('[data-release-download]').all():
                        expect(link).to_have_attribute('href', release['download'])
                    assert not errors, errors
                    assert not failures, failures
                    page.close()
                    nojs = browser.new_page(java_script_enabled=False)
                    nojs.goto(url, wait_until='networkidle')
                    expect(nojs.locator('.demo-nojs')).to_be_visible()
                    expect(nojs.locator('[data-release-download]').first).to_have_attribute('href', release['download'])
                    assert nojs.locator('.vw-header .text-dot').evaluate("el => getComputedStyle(el, '::after').backgroundColor") == 'rgb(213, 248, 140)'
                    nojs.close()
                finally:
                    browser.close()
        finally:
            server.shutdown()
            server.server_close()
            worker.join(timeout=5)
    print('Viewer: Pages subpath, autoplay, repeated 5s pause, reduced motion, square colors, 7 viewport widths, cat text/restore, stable button position and no-JS downloads passed.')


if __name__ == '__main__':
    check()
