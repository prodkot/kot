"""Real sing-box -> authenticated SOCKS -> Xray XHTTP chains, without changing host routes."""
import argparse
import hashlib
import zipfile
import contextlib
import http.client
import http.server
import json
import pathlib
import socket
import socketserver
import ssl
import struct
import subprocess
import tempfile
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
UUID = '11111111-1111-4111-8111-111111111111'
BODY = b'kot-xhttp-fixture-ok'


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def recv(sock, size):
    result = b''
    while len(result) < size:
        data = sock.recv(size - len(result))
        if not data:
            raise OSError('Unexpected SOCKS EOF')
        result += data
    return result


def socks_address(sock):
    head = recv(sock, 4)
    assert head[:3] == b'\x05\x00\x00', head
    size = {1: 4, 4: 16}.get(head[3])
    address = recv(sock, size) if size else recv(sock, recv(sock, 1)[0])
    return address, struct.unpack('!H', recv(sock, 2))[0]


def greeting(sock, credentials=None):
    sock.sendall(b'\x05\x01' + (b'\x02' if credentials else b'\x00'))
    assert recv(sock, 2) == (b'\x05\x02' if credentials else b'\x05\x00')
    if credentials:
        user, password = (value.encode() for value in credentials)
        sock.sendall(b'\x01' + bytes([len(user)]) + user + bytes([len(password)]) + password)
        assert recv(sock, 2) == b'\x01\x00'


def udp(proxy_port, target_port, credentials=None):
    with socket.create_connection(('127.0.0.1', proxy_port), 3) as control, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(('127.0.0.1', 0)); sock.settimeout(5)
        greeting(control, credentials)
        control.sendall(b'\x05\x03\x00\x01\x7f\x00\x00\x01' + struct.pack('!H', sock.getsockname()[1]))
        address, relay = socks_address(control)
        sock.sendto(b'\x00\x00\x00\x01\x7f\x00\x00\x01' + struct.pack('!H', target_port) + BODY, (socket.inet_ntoa(address), relay))
        packet = sock.recv(4096)
        assert packet.endswith(BODY), packet


def proxy_http(proxy_port, web_port, credentials=None):
    conn = http.client.HTTPConnection('127.0.0.1', proxy_port, timeout=5)
    try:
        if credentials:
            conn.sock = socket.create_connection(('127.0.0.1', proxy_port), 5)
            greeting(conn.sock, credentials)
            conn.sock.sendall(b'\x05\x01\x00\x01\x7f\x00\x00\x01' + struct.pack('!H', web_port))
            socks_address(conn.sock)
        else:
            conn.set_tunnel('127.0.0.1', web_port)
        conn.request('GET', '/')
        response = conn.getresponse()
        assert response.status == 200 and response.read() == BODY
    finally:
        conn.close()


class Web(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.send_header('Content-Length', str(len(BODY)))
        self.end_headers(); self.wfile.write(BODY)

    def log_message(self, *args):
        pass


class Echo(socketserver.BaseRequestHandler):
    def handle(self):
        data, sock = self.request
        sock.sendto(data, self.client_address)


class TLSWeb(http.server.ThreadingHTTPServer):
    # Accept raw sockets first: a readiness probe without ClientHello must not
    # block the accept loop before the real REALITY handshake arrives.
    def finish_request(self, request, client_address):
        request.settimeout(8)
        try:
            with self.context.wrap_socket(request, server_side=True) as tls:
                self.RequestHandlerClass(tls, client_address, self)
        except (OSError, ssl.SSLError):
            pass


class Processes:
    def __init__(self, directory):
        self.directory, self.children, self.logs = directory, [], []

    def start(self, exe, config, listener, xray=False):
        name = f'{len(self.children)}-{exe.name}'
        path = self.directory / (name + '.json'); path.write_text(json.dumps(config))
        command = [str(exe), 'run', '-c', str(path)]
        validation = [str(exe), 'run', '-test', '-c', str(path)] if xray else [str(exe), 'check', '-c', str(path)]
        test = subprocess.run(validation, capture_output=True, timeout=15)
        assert test.returncode == 0, test.stderr.decode(errors='replace')
        log = open(self.directory / (name + '.log'), 'wb'); self.logs.append(log)
        child = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log, stderr=log)
        self.children.append(child)
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            if child.poll() is not None:
                log.flush()
                raise AssertionError((self.directory / (name + '.log')).read_text()[-6000:])
            try:
                with socket.create_connection(('127.0.0.1', listener), .1):
                    return child
            except OSError:
                time.sleep(.05)
        raise AssertionError('Core listener startup timeout')

    def close(self):
        for child in self.children:
            if child.poll() is None:
                child.terminate()
        for child in self.children:
            try:
                child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                child.kill(); child.wait(timeout=5)
        for log in self.logs:
            log.close()
        for log in self.directory.glob('*.log'):
            assert log.stat().st_size < 1024 * 1024, 'Unbounded fixture log'


def chain(core, xray, directory, protocol, mode, web_port, udp_port, cert, key, reality_port=None, extra=False, xudp=False, negative_tls=False, bridge_only=False):
    generated = ROOT / 'Tests/generated/xhttp'
    client = json.loads((generated / f'{protocol}-{mode}-client.json').read_text())
    bridge = json.loads((generated / f'{protocol}-{mode}-xray.json').read_text())
    server_port, bridge_port, proxy_port = port(), port(), port()
    client['inbounds'][0]['listen_port'] = proxy_port
    client['outbounds'][0]['server_port'] = bridge_port
    client['route']['auto_detect_interface'] = False
    bridge['inbounds'][0]['port'] = bridge_port
    outbound = bridge['outbounds'][1]
    outbound['settings']['servers' if protocol == 'trojan' else 'vnext'][0]['port'] = server_port
    stream = outbound['streamSettings']
    stream['tlsSettings']['certificates'] = [{'certificateFile': str(cert), 'usage': 'verify'}]
    if negative_tls:
        stream['tlsSettings']['serverName'] = 'wrong.fixture.test'
    settings = {'clients': [{'password': 'fixture-password'}]} if protocol == 'trojan' else {'clients': [{'id': UUID}], **({'decryption': 'none'} if protocol == 'vless' else {})}
    server_stream = {'network': 'xhttp', 'security': 'tls', 'xhttpSettings': {'path': '/fixture', 'mode': 'auto'}, 'tlsSettings': {'alpn': ['h2'], 'certificates': [{'certificateFile': str(cert), 'keyFile': str(key)}]}}
    if reality_port:
        stream.pop('tlsSettings'); stream['security'] = 'reality'
        stream['realitySettings'] = {'serverName': 'fixture.test', 'fingerprint': 'chrome', 'publicKey': 'j0DFrbaPJWJK5bIU6nZ6bslNgp09e14a0bpvPiE4KF8', 'shortId': '0123456789abcdef'}
        server_stream.pop('tlsSettings'); server_stream['security'] = 'reality'
        server_stream['realitySettings'] = {'target': f'127.0.0.1:{reality_port}', 'serverNames': ['fixture.test'], 'privateKey': 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHl8', 'shortIds': ['0123456789abcdef']}
    if extra:
        stream['xhttpSettings']['extra'] = {'xPaddingBytes': '100-200', 'noSSEHeader': True, 'headers': {'X-Fixture': 'kot'}, 'xmux': {'maxConnections': 2, 'maxConcurrency': 0}, 'downloadSettings': {'address': '127.0.0.1', 'port': server_port, 'network': 'xhttp', 'security': 'tls', 'tlsSettings': stream['tlsSettings'], 'xhttpSettings': {'path': '/fixture', 'mode': 'stream-one'}}}
    if xudp:
        outbound['mux'] = {'enabled': True, 'concurrency': -1, 'xudpConcurrency': 8, 'xudpProxyUDP443': 'allow'}
    server = {'log': {'loglevel': 'warning'}, 'inbounds': [{'listen': '127.0.0.1', 'port': server_port, 'protocol': protocol, 'settings': settings, 'streamSettings': server_stream}], 'outbounds': [{'protocol': 'freedom'}]}
    processes = Processes(directory)
    try:
        upstream = processes.start(xray, server, server_port, True)
        sidecar = processes.start(xray, bridge, bridge_port, True)
        credentials = ('fixture', 'p' * 64) if bridge_only else None
        if bridge_only:
            path = directory / 'sing-box-check.json'; path.write_text(json.dumps(client))
            test = subprocess.run([str(core), 'check', '-c', str(path)], capture_output=True, timeout=15)
            assert test.returncode == 0, test.stderr.decode()
            proxy_port = bridge_port
        else:
            processes.start(core, client, proxy_port)
        if negative_tls:
            with contextlib.suppress(OSError, http.client.HTTPException):
                proxy_http(proxy_port, web_port, credentials)
                raise AssertionError('Wrong TLS name was accepted')
            print('PASS XHTTP TLS rejects wrong certificate name', flush=True)
            return
        proxy_http(proxy_port, web_port, credentials); udp(proxy_port, udp_port, credentials)
        with socket.create_connection(('127.0.0.1', bridge_port), 2) as sock:
            sock.sendall(b'\x05\x01\x00')
            assert recv(sock, 2) == b'\x05\xff', 'Unauthenticated Xray bridge accepted'
        sidecar.terminate(); sidecar.wait(timeout=5)
        rejected = False
        try:
            proxy_http(proxy_port, web_port, credentials)
        except (OSError, http.client.HTTPException):
            rejected = True
        assert rejected, 'Sidecar failure fell back to direct'
        print(f'PASS {protocol} XHTTP {mode} {"REALITY" if reality_port else "TLS"}{" extra/downloadSettings" if extra else ""}{" XUDP" if xudp else ""}: HTTP, UDP, authentication, no direct fallback', flush=True)
    except Exception:
        for log in directory.glob('*.log'):
            print(log.name, log.read_text(errors='replace')[-3000:], flush=True)
        raise
    finally:
        processes.close()


def official_xray():
    directory = ROOT / 'Tests/generated/xray'
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / 'Xray-linux-64.zip'
    if not archive.is_file():
        subprocess.run(['curl', '--fail', '--location', '--retry', '2', '--max-time', '60', 'https://github.com/XTLS/Xray-core/releases/download/v26.3.27/Xray-linux-64.zip', '--output', str(archive)], check=True, timeout=90)
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == '23cd9af937744d97776ee35ecad4972cf4b2109d1e0fe6be9930467608f7c8ae', 'Unexpected official Xray checksum'
    executable = directory / 'xray'
    with zipfile.ZipFile(archive) as bundle:
        executable.write_bytes(bundle.read('xray'))
    executable.chmod(0o755)
    return executable


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--core', required=True, type=pathlib.Path)
    parser.add_argument('--xray', type=pathlib.Path, help='Official Xray executable; otherwise fetch the pinned Linux binary')
    parser.add_argument('--bridge-only', action='store_true', help='Validate sing-box configs but exercise the Xray bridge directly on hosts without netlink')
    parser.add_argument('--case-index', type=int, help='Run one fixture while debugging')
    args = parser.parse_args()
    core, xray = args.core.resolve(), args.xray.resolve() if args.xray else official_xray()
    deadline = time.monotonic() + 180
    with tempfile.TemporaryDirectory(prefix='xhttp-', dir=ROOT / 'Tests/generated') as temp:
        directory = pathlib.Path(temp); cert, key = directory / 'cert.pem', directory / 'key.pem'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(key), '-out', str(cert), '-days', '1', '-subj', '/CN=fixture.test', '-addext', 'subjectAltName=DNS:fixture.test'], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15)
        web = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Web)
        echo = socketserver.ThreadingUDPServer(('127.0.0.1', 0), Echo)
        reality = TLSWeb(('127.0.0.1', 0), Web)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); context.load_cert_chain(cert, key); context.set_alpn_protocols(['h2'])
        reality.context = context
        servers = [web, echo, reality]
        for server in servers:
            threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            cases = [(p, m, None, False, False, False) for p in ('vless', 'vmess', 'trojan') for m in ('auto', 'packet-up', 'stream-up', 'stream-one')]
            cases += [('vless', m, reality.server_port, False, False, False) for m in ('auto', 'packet-up', 'stream-up', 'stream-one')]
            cases += [('vless', 'packet-up', None, True, False, False), ('vless', 'packet-up', None, False, True, False), ('vmess', 'stream-up', None, False, True, False), ('vless', 'auto', None, False, False, True)]
            for i, case in enumerate(cases):
                if args.case_index is not None and i != args.case_index:
                    continue
                assert time.monotonic() < deadline, 'XHTTP integration matrix exceeded three minutes'
                work = directory / str(i); work.mkdir()
                protocol, mode, dest, extra, xudp, negative = case
                chain(core, xray, work, protocol, mode, web.server_port, echo.server_address[1], cert, key, dest, extra, xudp, negative, args.bridge_only)
        finally:
            for server in servers:
                server.shutdown(); server.server_close()


if __name__ == '__main__':
    main()
