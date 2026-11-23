"""End-to-end refresh with two distinguishable Xray exits and a durable kot profile."""
import argparse
import contextlib
import http.client
import http.server
import json
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time

parser = argparse.ArgumentParser()
parser.add_argument('--core', required=True)
parser.add_argument('--dotnet', default='dotnet')
parser.add_argument('--tests', default='Tests/bin/Release/net10.0/Kot.Tests.dll')
args = parser.parse_args()
old = 'abcdefab-cdef-4abc-8def-abcdefabcdef'
new = '22222222-2222-4222-8222-222222222222'


def ports(count):
    with contextlib.ExitStack() as stack:
        sockets = [stack.enter_context(socket.socket()) for _ in range(count)]
        for sock in sockets:
            sock.bind(('127.0.0.1', 0))
        return [sock.getsockname()[1] for sock in sockets]


@contextlib.contextmanager
def marker(label):
    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.end_headers()
            self.wfile.write(label.encode())

        def log_message(self, *args):
            pass

    with http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield server.server_port
        finally:
            server.shutdown()
            thread.join(2)


@contextlib.contextmanager
def running(config, port):
    with subprocess.Popen([args.core, 'run', '-c', 'stdin:'], stdin=subprocess.PIPE,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as proc:
        try:
            proc.stdin.write(json.dumps(config).encode())
            proc.stdin.close()
            for _ in range(120):
                if proc.poll() is not None:
                    raise RuntimeError('Xray exited during fixture startup')
                try:
                    with socket.create_connection(('127.0.0.1', port), 0.1):
                        break
                except OSError:
                    time.sleep(.04)
            else:
                raise TimeoutError('Xray startup')
            yield proc
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=3)


def endpoint(port, marker_port, credential):
    return {'inbounds': [{'listen': '127.0.0.1', 'port': port, 'protocol': 'vless',
                          'settings': {'clients': [{'id': credential}], 'decryption': 'none'}}],
            'outbounds': [{'protocol': 'freedom', 'settings': {'finalRules': [{'action': 'allow', 'ip': ['127.0.0.1/32', '192.0.2.1/32']}],
                                                           'redirect': f'127.0.0.1:{marker_port}'}}]}


def route(port):
    with contextlib.closing(http.client.HTTPConnection('127.0.0.1', port, timeout=4)) as conn:
        conn.request('GET', 'http://192.0.2.1:18080/marker')
        response = conn.getresponse()
        assert response.status == 200
        return response.read(16).decode()


scratch = Path('Tests/generated')
scratch.mkdir(exist_ok=True)
with marker('DE') as de_marker, marker('SE') as se_marker:
    de, se, health, proxy = ports(4)
    with running(endpoint(se, se_marker, old), se):
        for case in ('rotate', 'case', 'gone', 'ambiguous', 'other-address'):
            with tempfile.TemporaryDirectory(dir=scratch) as folder:
                def client(action):
                    cmd = [args.dotnet, args.tests, 'subscription-cycle', action, folder,
                           *map(str, (de, se, health, proxy))]
                    return json.loads(subprocess.check_output(cmd, timeout=20))

                with running(endpoint(de, de_marker, old), de):
                    with running(client('initial')['next'], proxy) as live:
                        assert route(proxy) == 'DE'
                        saved = client(case)
                        assert live.poll() is None and route(proxy) == 'DE' and saved['selectedOriginal']
                if case in ('gone', 'ambiguous', 'other-address'):
                    assert saved['next'] is None and saved['notice'] and client('restart')['next'] is None
                    if case != 'gone':
                        with running(client('select-sweden')['next'], proxy):
                            assert route(proxy) == 'SE'
                        print('PASS live DE, reconnect and restart blocked until explicit SE:', case, flush=True)
                        continue
                    saved = client('return')
                assert saved['next'] and not saved['notice'], case + ': uniquely returning peer needs confirmation'
                # The previous password/UUID will now fail a real handshake.
                credential = old if case == 'case' else new
                with running(endpoint(de, de_marker, credential), de):
                    with running(saved['next'], proxy):
                        assert route(proxy) == 'DE'
                    restarted = client('restart')
                    assert restarted['selectedOriginal']
                    with running(restarted['next'], proxy):
                        assert route(proxy) == 'DE'
                print('PASS live, reconnect and restart traffic through DE:', case, flush=True)
