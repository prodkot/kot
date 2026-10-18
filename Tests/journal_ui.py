"""Read a live, rotating journal without losing the viewport or selection."""
import ast
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
tree = ast.parse((ROOT / 'Tests/ui.py').read_text())
MOCK = next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign)
            and any(isinstance(t, ast.Name) and t.id == 'mock' for t in n.targets))
LOG = '\n'.join(f'[12:00] [core] event {i:04d} ' + 'example.test connection data ' * 3 for i in range(900))


def publish(page, text):
    page.evaluate('journal=>fixtureSnapshot({journal})', text)


def at_bottom(box):
    return box.evaluate('e=>e.scrollHeight-e.scrollTop-e.clientHeight<=2')


with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        for width, height in [(800, 600), (960, 640), (1200, 800)]:
            page = browser.new_page(viewport={'width': width, 'height': height})
            errors = []
            page.on('pageerror', lambda e: errors.append(str(e)))
            page.add_init_script(MOCK)
            try:
                page.goto((ROOT / 'Windows/ui/index.html').as_uri())
                expect(page.locator('#startupScreen')).to_be_hidden()
                page.evaluate("fixtureNavigate('logs')")
                box = page.locator('#journalText')
                expect(box).to_be_visible()
                assert page.locator('[data-log-tab], #connectionList').count() == 0
                expect(page.locator('#logEmpty')).to_have_text('Нет событий')
                publish(page, LOG[-65536:])
                expect(box).to_contain_text('event 0899')
                assert at_bottom(box)
                assert page.locator('.main').evaluate('e=>e.scrollHeight<=e.clientHeight+1'), 'only the journal should scroll'
                bounds=box.bounding_box()
                assert bounds['y']+bounds['height']<=height
                publish(page, (LOG + '\nlive event')[-65536:])
                expect(box).to_contain_text('live event')
                assert at_bottom(box)

                # Real wheel scrolling holds the text, even if many host tails
                # overwrite their entire beginning before the user returns.
                box.hover()
                page.mouse.wheel(0, -450)
                expect(page.locator('#logStatus')).to_have_text('Чтение журнала')
                old_text = box.text_content()
                top = box.evaluate('e=>e.scrollTop')
                for batch in range(5):
                    publish(page, (LOG + ('\nreplacement %d ' % batch) * 7000)[-65536:])
                assert box.text_content() == old_text
                assert box.evaluate('e=>e.scrollTop') == top
                expect(page.locator('#latestLogs')).to_have_text('К новым записям')
                assert not at_bottom(box)
                if width == 960:
                    page.screenshot(path=str(ROOT / 'Tests/ui-journal-reading.png'), animations='disabled')
                page.locator('#latestLogs').click()
                expect(box).to_contain_text('replacement 4')
                assert at_bottom(box)
                expect(page.locator('#latestLogs')).to_be_hidden()

                # A host snapshot can arrive before the browser scroll event.
                publish(page, LOG[-65536:])
                old_text = box.text_content()
                page.evaluate('''()=>{
                  document.getElementById('journalText').scrollTop=1000;
                  fixtureSnapshot({journal:'replacement before scroll dispatch'});
                }''')
                assert box.text_content() == old_text
                assert box.evaluate('e=>e.scrollTop') == 1000
                page.locator('#latestLogs').click()

                # Selection at the live end is preserved as well, including
                # snapshots arriving before selectionchange dispatch.
                publish(page, 'select this text\nlast event')
                page.evaluate('''()=>{
                  const box=document.getElementById('journalText'),r=document.createRange();
                  r.setStart(box.firstChild,0);r.setEnd(box.firstChild,16);
                  getSelection().removeAllRanges();getSelection().addRange(r);
                  fixtureSnapshot({journal:'new selected event'});
                }''')
                assert page.evaluate('getSelection().toString()') == 'select this text'
                expect(box).to_have_text('select this text\nlast event')
                page.locator('#latestLogs').click()
                expect(box).to_have_text('new selected event')

                # Keyboard reading, resize, navigation and explicit pause keep
                # the same snapshot. Continue intentionally returns to live.
                publish(page, LOG[-65536:])
                box.focus()
                page.keyboard.press('Home')
                expect(page.locator('#logStatus')).to_have_text('Чтение журнала')
                old_text = box.text_content()
                page.set_viewport_size({'width': width + 20, 'height': height + 20})
                publish(page, 'background event')
                assert box.text_content() == old_text
                page.evaluate("fixtureNavigate('settings')")
                publish(page, None)  # Native host omits the journal off-page.
                page.evaluate("fixtureNavigate('logs')")
                assert box.text_content() == old_text
                publish(page, 'navigation event')
                page.locator('#latestLogs').click()
                expect(box).to_have_text('navigation event')
                page.locator('#pauseLogs').click()
                publish(page, 'paused event')
                expect(box).to_have_text('navigation event')
                expect(page.locator('#logStatus')).to_have_text('На паузе')
                page.locator('#pauseLogs').click()
                expect(box).to_have_text('paused event')

                # Filters work on the held snapshot. A filtered empty or short
                # result must not inadvertently restart live updates.
                publish(page, '[app] ready\n[core] ERROR old failure\n[core] WARN retry\n<script>alert(1)</script>')
                page.locator('#pauseLogs').click()
                page.locator('#logLevel').select_option('error')
                expect(box).to_have_text('[core] ERROR old failure')
                publish(page, '[core] ERROR new failure')
                expect(box).to_have_text('[core] ERROR old failure')
                page.locator('#logSearch').fill('missing')
                expect(page.locator('#logEmpty')).to_have_text('Ничего не найдено')
                page.locator('#logSearch').fill('')
                page.locator('#logLevel').select_option('warning')
                expect(box).to_have_text('[core] WARN retry')
                page.locator('#logLevel').select_option('all')
                expect(box).to_contain_text('<script>')
                assert box.locator('script').count() == 0
                page.locator('#copyViewLog').click()
                expect(page.locator('#toast')).to_have_text('Полный лог скопирован')
                expect(box).to_contain_text('old failure')
                page.locator('#latestLogs').click()
                expect(box).to_have_text('[core] ERROR new failure')
                expect(page.locator('#pauseLogs')).to_have_attribute('aria-pressed', 'false')
                publish(page, '')
                expect(page.locator('#logEmpty')).to_have_text('Нет событий')
                expect(box).to_have_text('')
                assert page.evaluate('document.documentElement.scrollWidth===innerWidth')
                assert not errors, errors
                print(f'PASS journal {width}x{height}: following, wheel, tail rotation, selection, keyboard, pause, navigation, filters, copy', flush=True)
            finally:
                page.close()
    finally:
        browser.close()
