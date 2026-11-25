"""Actual UI + REALITY traffic from production import/refresh/store/build code.

The bridge is a test adapter, not Windows/WebView2/TUN. All inputs are synthetic;
configs and child output stay in memory and are never logged.
"""
import argparse
import contextlib as ctx
import http.client
import http.server
import json
from pathlib import Path
import socket
import signal
import ssl
import subprocess
import tempfile
import threading
import time
from urllib.parse import quote
from playwright.sync_api import sync_playwright, expect
from flags_ui import MODEL, ROOT
from xhttp_integration import TLSWeb, Web

parser = argparse.ArgumentParser()
parser.add_argument('--core', required=True)
parser.add_argument('--dotnet', default='dotnet')
parser.add_argument('--tests', default='Tests/bin/Release/net10.0/Kot.Tests.dll')
parser.add_argument('--expect-missing', action='store_true', help='Reproduce Dev 8, not a passing candidate')
args = parser.parse_args()
def cancelled(signum, frame):
    raise SystemExit('REALITY cycle cancelled')
signal.signal(signal.SIGTERM, cancelled)
LOGIN = 'abcdefab-cdef-4abc-8def-abcdefabcdef'
PUBLIC = 'j0DFrbaPJWJK5bIU6nZ6bslNgp09e14a0bpvPiE4KF8'
PRIVATE = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHl8'
STATES = {'A': ('127.0.0.1', '11111111'), 'B': ('127.0.0.2', '22222222'),
          'C': ('127.0.0.1', '33333333'), 'S': ('127.0.0.3', '44444444')}


def port():
    with socket.socket() as s:
        s.bind(('0.0.0.0', 0))
        return s.getsockname()[1]


@ctx.contextmanager
def running(config, host, listener):
    with subprocess.Popen([args.core, 'run', '-c', 'stdin:'], stdin=subprocess.PIPE,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as proc:
        try:
            proc.stdin.write(json.dumps(config).encode()); proc.stdin.close()
            for _ in range(120):
                if proc.poll() is not None: raise RuntimeError('Fixture Xray exited during startup')
                try:
                    with socket.create_connection((host, listener), .1): break
                except OSError: time.sleep(.04)
            else: raise TimeoutError('Fixture Xray startup')
            yield proc
        finally:
            if proc.poll() is None:
                proc.terminate()
                try: proc.wait(timeout=3)
                except subprocess.TimeoutExpired: proc.kill(); proc.wait(timeout=3)


@ctx.contextmanager
def server(server):
    thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
    try: yield server.server_port
    finally: server.shutdown(); server.server_close(); thread.join(3)


def marker(label):
    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200); self.end_headers(); self.wfile.write(label.encode())
        def log_message(self, *args): pass
    return http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)


def endpoint(state, listen, target, response):
    host, sid = STATES[state]
    return {'log': {'loglevel': 'none'}, 'inbounds': [{'listen': host, 'port': listen, 'protocol': 'vless',
        'settings': {'clients': [{'id': LOGIN, 'flow': 'xtls-rprx-vision'}], 'decryption': 'none'},
        'streamSettings': {'network': 'tcp', 'security': 'reality', 'realitySettings': {
            'target': f'127.0.0.1:{target}', 'serverNames': ['fixture.test'], 'privateKey': PRIVATE, 'shortIds': [sid]}}}],
        'outbounds': [{'protocol': 'freedom', 'settings': {'redirect': f'127.0.0.1:{response}',
            'finalRules': [{'action': 'allow', 'ip': ['127.0.0.0/8', '192.0.2.1/32']}]}}]}


def route(proxy):
    with ctx.closing(http.client.HTTPConnection('127.0.0.1', proxy, timeout=5)) as conn:
        conn.request('GET', 'http://192.0.2.1:18080/marker')
        reply = conn.getresponse()
        assert reply.status == 200, 'Marker request failed'
        return reply.read(16).decode()


def feed(states, listen, native):
    rows = []
    for state in states:
        host, sid = STATES[state]; name = 'Швеция' if state == 'S' else 'Германия'
        if native:
            rows.append({'remarks': name, 'inbounds': [], 'outbounds': [{'tag': 'proxy', 'protocol': 'vless',
                'settings': {'vnext': [{'address': host, 'port': listen, 'users': [{'id': LOGIN,
                    'encryption': 'none', 'flow': 'xtls-rprx-vision'}]}]},
                'streamSettings': {'network': 'tcp', 'security': 'reality', 'realitySettings': {
                    'serverName': 'fixture.test', 'fingerprint': 'firefox', 'publicKey': PUBLIC, 'shortId': sid}}}]})
        else:
            rows.append(f'vless://{LOGIN}@{host}:{listen}?security=reality&pbk={PUBLIC}&sid={sid}'
                        f'&sni=fixture.test&fp=firefox&flow=xtls-rprx-vision#{quote(name)}')
    return json.dumps(rows) if native else '\n'.join(rows)


MOCK = r'''(()=>{let listener;const model=MODEL;
window.fixtureSnapshot=patch=>{Object.assign(model,patch);listener({data:{kind:'snapshot',data:structuredClone(model)}});};
window.chrome={webview:{addEventListener:(_,cb)=>listener=cb,postMessage:async message=>{
 if(message.action==='refresh')fixtureSnapshot(await window.refreshFixture());else fixtureSnapshot({});
 listener({data:{kind:'reply',id:message.id,ok:true}});
}}};})();'''.replace('MODEL', json.dumps(MODEL))

scratch = ROOT / 'Tests/generated'; scratch.mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(dir=scratch, prefix='reality-') as folder, sync_playwright() as pw:
    temp = Path(folder); cert, key = temp/'cert.pem', temp/'key.pem'
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(key),
        '-out', str(cert), '-days', '1', '-subj', '/CN=fixture.test'], check=True,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15)
    tls = TLSWeb(('127.0.0.1', 0), Web)
    tls.context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); tls.context.load_cert_chain(cert, key)
    tls.context.set_alpn_protocols(['h2'])
    browser = pw.chromium.launch(args=['--no-sandbox'])
    try:
        with server(tls) as target, server(marker('DE')) as de, server(marker('SE')) as se:
            listen, health, proxy = port(), port(), port()
            with running(endpoint('S', listen, target, se), STATES['S'][0], listen):
                for native in (False, True):
                    for path in [('A','B','C'), ('B','C'), ('A','C'), ('A','gone'), ('A','ambiguous')]:
                        with tempfile.TemporaryDirectory(dir=temp) as profile_dir, ctx.ExitStack() as cleanup:
                            page = browser.new_page(viewport={'width':960, 'height':640}); cleanup.callback(page.close)
                            errors=[]; page.on('pageerror', lambda err: errors.append(str(err))); page.add_init_script(MOCK)
                            def client(states=None, live=None):
                                data={'live':live, 'health':health, 'proxy':proxy}
                                if states is not None: data['feed']=feed(states, listen, native)
                                run=subprocess.run([args.dotnet, args.tests, 'reality-cycle', profile_dir],
                                    input=json.dumps(data), text=True, capture_output=True, timeout=15)
                                assert run.returncode==0, 'Core cycle fixture failed'
                                return json.loads(run.stdout)
                            result=client(['S',path[0]]); live=result['next']; original=live['Id']
                            servers=cleanup.enter_context(ctx.ExitStack()); clients=cleanup.enter_context(ctx.ExitStack())
                            servers.enter_context(running(endpoint(path[0],listen,target,de), STATES[path[0]][0],listen))
                            active_process=clients.enter_context(running(result['runtime'],'127.0.0.1',proxy))
                            assert route(proxy)=='DE'
                            page.expose_function('refreshFixture',lambda: result['view'])
                            page.goto((ROOT/'Windows/ui/index.html').as_uri()); page.locator('[data-page=servers]').click()
                            page.evaluate('v=>fixtureSnapshot(v)',client(live=live)['view'])
                            for state in path[1:]:
                                rows=['S'] if state=='gone' else ['S','B','C'] if state=='ambiguous' else ['S',state]
                                result=client(rows,live); page.locator('#refreshButton').click()
                                assert active_process.poll() is None and route(proxy)=='DE', 'Refresh changed the running route'
                                blocked=state in ('gone','ambiguous') or args.expect_missing
                                assert result['view']['selected']==original, 'Refresh silently changed the selection'
                                if blocked:
                                    expect(page.locator('.server-card[aria-pressed=true]')).to_have_count(0)
                                    expect(page.locator('#selectedName')).to_have_text('Выбрать сервер')
                                    expect(page.locator('#selectedCity')).to_contain_text('Германия')
                                    assert result['runtime'] is None and result['notice'], 'Unsafe replacement became connectable'
                                else:
                                    expect(page.locator('.server-card[aria-pressed=true]')).to_have_count(1)
                                    expect(page.locator(f'[data-node="{original}"] .server-card')).to_have_attribute('aria-pressed','true')
                                    expect(page.locator(f'[data-node="{original}"] small')).to_contain_text('Подключён')
                                    expect(page.locator('#selectedName')).to_have_text('Германия')
                                    expect(page.locator('#sidebarServer')).to_have_text('Германия')
                                    assert not result['notice'] and not result['view']['needsSelection']
                                assert page.locator('.server-card[aria-pressed=true] strong').all_text_contents()!=['Швеция']
                                for theme in ('dark','light'):
                                    page.evaluate('theme=>fixtureSnapshot({settings:{theme,accent:"lime",mode:"all"}})',theme)
                                    if path==('A','B','C') and state=='B':
                                        suffix='before' if args.expect_missing else 'after'
                                        page.screenshot(path=str(scratch/f'reality-{suffix}-{theme}.png'),animations='disabled')
                                clients.close(); servers.close(); restarted=client()
                                page.evaluate('v=>fixtureSnapshot(v)',restarted['view'])
                                if blocked:
                                    assert restarted['runtime'] is None and restarted['view']['needsSelection']
                                    if args.expect_missing or state=='ambiguous': break
                                    result=client(['S','B']); state='B'
                                    assert result['runtime'] is not None and not result['notice']
                                servers.enter_context(running(endpoint(state,listen,target,de),STATES[state][0],listen)); live=result['next']
                                with running(result['runtime'],'127.0.0.1',proxy): assert route(proxy)=='DE', 'Wrong reconnect exit'
                                restarted=client(); active_process=clients.enter_context(running(restarted['runtime'],'127.0.0.1',proxy))
                                assert route(proxy)=='DE'
                                page.evaluate('v=>fixtureSnapshot(v)',client(live=live)['view'])
                                expect(page.locator(f'[data-node="{original}"] .server-card')).to_have_attribute('aria-pressed','true')
                                expect(page.locator('#selectedName')).to_have_text('Германия')
                            assert not errors, 'Browser errors in cycle'
                            print('REPRODUCED missing target' if args.expect_missing else 'PASS UI + REALITY cycle',
                                  'native' if native else 'links','/'.join(path),flush=True)
    finally: browser.close()
