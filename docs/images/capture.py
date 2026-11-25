"""Capture documentation from unmodified Windows/ui with synthetic host data.

Requires Playwright + Chromium. Run from any directory; no real VPN is started.
No UI markup/styles are substituted. Network requests are denied.
"""
import ast
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
IMAGES = ROOT / 'docs/images'
VERSION = ET.parse(ROOT / 'Directory.Build.props').findtext('.//VersionPrefix')
# Reuse only the bridge string, without importing or running the test module.
MOCK = next(ast.literal_eval(n.value) for n in ast.parse((ROOT / 'Tests/statistics_ui.py').read_text()).body
            if isinstance(n, ast.Assign) and any(getattr(t, 'id', '') == 'MOCK' for t in n.targets))
NODES = [dict(id=key, name=name, countryCode=country, code=code, protocol=protocol,
              ping=dict(status='ok', ms=ms, mode='http', successes=2, attempts=2))
         for key, name, country, code, protocol, ms in [
             ('nl', 'Нидерланды', 'NL', 'VL', 'vless', 42),
             ('de', 'Германия', 'DE', 'VL', 'vless', 68),
             ('fi', 'Финляндия', 'FI', 'TR', 'trojan', 91),
             ('lv', 'Латвия', 'LV', 'HY', 'hysteria2', 56)]]
RULES = dict(version=1, name='На каждый день', defaultAction='proxy',
             directDomains=['.ru', '.рф'], proxyDomains=['example.org'], blockDomains=['ads.example'],
             directCountries=['RU'], proxyCountries=[], blockCountries=[],
             directDns='cloudflare', remoteDns='adguard')
SETTINGS = dict(theme='dark', accent='lime', mode='tun', startup=False, autoConnect=False,
                tray=True, sendHwid=False, killSwitch=False, killSwitchActive=False, bypass='')
PATCH = dict(version=VERSION, displayVersion=VERSION, buildNumber='',
             nodes=NODES, selected='nl', activeNode=NODES[0], nextNode='nl',
             subscriptions=[dict(id='s', name='Личная подписка', count=4)], favorites=['nl', 'de'],
             updated='2026-11-24T10:00:00Z', settings=SETTINGS,
             routing=dict(active='daily', profiles=[dict(id='daily', rules=RULES, sourceUrl='', refreshHours=0, updated=None)], errors={}),
             geoIp=dict(busy=False, error='', refreshHours=24, countries=[dict(code='RU', ready=True, updated='2026-11-24T10:00:00Z')]))


def capture():
    outputs = []
    with sync_playwright() as p:
        browser = p.chromium.launch(args=['--no-sandbox'])
        try:
            page = browser.new_page(viewport=dict(width=960, height=720), device_scale_factor=1.5,
                                    timezone_id='Europe/Moscow', locale='ru-RU', reduced_motion='reduce')
            page.set_default_timeout(8000)
            errors = []
            page.on('pageerror', lambda e: errors.append(str(e)))
            page.route('https://**/*', lambda r: r.abort())
            page.route('http://**/*', lambda r: r.abort())
            page.add_init_script(MOCK)
            page.clock.install(time=datetime(2026, 11, 24, 12, tzinfo=timezone.utc))
            page.goto((ROOT / 'Windows/ui/index.html').as_uri())
            expect(page.locator('#startupScreen')).to_be_hidden()
            page.clock.pause_at(datetime(2026, 11, 24, 12, 0, 5, tzinfo=timezone.utc))
            page.evaluate('patch => fixtureStatistics(patch)', PATCH)
            page.evaluate('''() => {
                const s=fixtureStatisticsData();
                s.started='2026-11-24T11:40:26Z';
                s.firstRecorded='2026-09-01T10:00:00Z';
                s.detail={firstRecorded:s.firstRecorded,error:'',days:[],speeds:[]};
                for(const period of ['session','week','month','allTime'])
                    s.detail[period]=[{app:'Не определено',route:'unknown',download:s[period].download,upload:s[period].upload}];
                fixtureStatistics({statistics:s});
            }''')
            expect(page.locator('#titleVersion')).to_have_text(VERSION)
            page.evaluate('document.fonts.ready')

            def shot(paths):
                page.mouse.move(0, 0)
                page.clock.run_for(500)
                data = page.screenshot(animations='disabled')
                for path in paths:
                    dest = ROOT / path
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(data)
                    outputs.append(dict(path=path, sha256=hashlib.sha256(data).hexdigest()))
                print('Captured', paths[0], flush=True)

            page.set_viewport_size(dict(width=960, height=640))
            expect(page.locator('#stateLabel')).to_have_text('Подключено')
            shot(['docs/images/readme/connection.png', 'site/assets/screens/home.png'])
            page.set_viewport_size(dict(width=960, height=720))
            page.evaluate('fixtureNavigate("servers")')
            page.evaluate('settings => fixtureStatistics({settings})', {**SETTINGS, 'theme': 'light'})
            shot(['docs/images/readme/servers.png'])
            page.evaluate('settings => fixtureStatistics({settings})', SETTINGS)
            shot(['site/assets/screens/servers.png'])
            page.evaluate('fixtureNavigate("settings")')
            shot(['site/assets/screens/settings.png'])
            page.locator('#routingButton').click()
            expect(page.locator('#routingName')).to_have_value(RULES['name'])
            shot(['docs/images/routing-dark.png', 'site/assets/screens/routing.png'])
            page.locator('#geoIpButton').click()
            expect(page.locator('#geoIpDialog')).to_be_visible()
            shot(['docs/images/geoip-dark.png'])
            page.locator('#closeGeoIp').click()
            page.locator('#closeRouting').click()
            page.evaluate('fixtureNavigate("statistics")')
            expect(page.locator('#statisticsDownload')).to_have_text('512 МБ')
            shot(['docs/images/statistics-dark.png', 'site/assets/screens/statistics.png'])
            assert not errors, errors
            manifest = dict(version=VERSION, sourceCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                            renderer='Chromium ' + browser.version, host='synthetic bridge, no Windows host or VPN',
                            viewport={'home': [960, 640], 'other': [960, 720]}, deviceScaleFactor=1.5,
                            uiSha256={str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
                                      for path in sorted((ROOT/'Windows/ui').iterdir()) if path.is_file() and path.suffix in ('.js','.html','.css')},
                            images=outputs)
            (IMAGES / 'capture.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
        finally:
            browser.close()

if __name__ == '__main__':
    capture()
