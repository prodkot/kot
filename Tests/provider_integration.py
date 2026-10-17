"""Exercise generated provider routing and DNS with real official cores and local servers."""
import argparse
import contextlib
import http.client
import http.server
import json
import pathlib
import socket
import socketserver
import struct
import tempfile
import threading

from xhttp_integration import ROOT, UUID, Processes, greeting, recv, socks_address, official_xray


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


class Web(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        body = self.server.body
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class Dns(socketserver.BaseRequestHandler):
    def handle(self):
        data, sock = self.request
        index = 12
        labels = []
        while data[index]:
            size = data[index]; index += 1
            labels.append(data[index:index + size].decode()); index += size
        end = index + 5
        # DNS names are case-insensitive; resolvers may randomize question case.
        name = '.'.join(labels).lower()
        self.server.queries.append(name)
        qtype = struct.unpack('!H', data[index + 1:index + 3])[0]
        answer = b'\xc0\x0c\x00\x01\x00\x01\x00\x00\x00\x3c\x00\x04\x7f\x00\x00\x01' if qtype == 1 else b''
        response = data[:2] + b'\x81\x80\x00\x01' + struct.pack('!H', bool(answer)) + b'\x00\x00\x00\x00' + data[12:end] + answer
        sock.sendto(response, self.client_address)


@contextlib.contextmanager
def serve(server):
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server
    finally:
        server.shutdown(); server.server_close(); thread.join(timeout=3)


def connect(port, host, target, credentials=None):
    sock = socket.create_connection(('127.0.0.1', port), 3)
    sock.settimeout(4)
    try:
        greeting(sock, credentials)
        encoded = host.encode('idna')
        sock.sendall(b'\x05\x01\x00\x03' + bytes([len(encoded)]) + encoded + struct.pack('!H', target))
        socks_address(sock)
        return sock
    except BaseException:
        sock.close()
        raise


def request(port, target, host, credentials=None):
    with connect(port, host, target, credentials) as sock:
        sock.sendall(f'GET / HTTP/1.1\r\nHost: {host.encode("idna").decode()}\r\nConnection: close\r\n\r\n'.encode())
        response = http.client.HTTPResponse(sock); response.begin()
        assert response.status == 200
        return response.read()


def query(port, name, credentials=None):
    question = b''.join(bytes([len(label)]) + label.encode() for label in name.encode('idna').decode().split('.')) + b'\x00\x00\x01\x00\x01'
    packet = b'\x4b\x54\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00' + question
    with connect(port, '1.1.1.1', 53, credentials) as sock:
        sock.sendall(struct.pack('!H', len(packet)) + packet)
        response = recv(sock, struct.unpack('!H', recv(sock, 2))[0])
        assert response[:2] == packet[:2] and response[-4:] == b'\x7f\x00\x00\x01', response.hex()


def run(core, xray, directory, bridge_only):
    processes = Processes(directory)
    direct = Server(('127.0.0.1', 0), Web); direct.body = b'direct'
    proxied = Server(('127.0.0.1', 0), Web); proxied.body = b'proxy'
    domestic = socketserver.ThreadingUDPServer(('127.0.0.1', 0), Dns); domestic.queries = []
    remote = socketserver.ThreadingUDPServer(('127.0.0.1', 0), Dns); remote.queries = []
    try:
        with serve(direct), serve(proxied), serve(domestic), serve(remote):
            server_port, bridge_port, health_port, egress_port, public_port, public_health = (processes.reserve() for _ in range(6))
            config = json.loads((ROOT / 'Tests/generated/provider/xray.json').read_text())
            client = json.loads((ROOT / 'Tests/generated/provider/sing-box.json').read_text())
            config['inbounds'][0]['port'] = bridge_port
            config['inbounds'][1]['port'] = health_port
            config['outbounds'][0]['settings']['vnext'][0]['port'] = server_port
            config['outbounds'][-1]['settings']['servers'][0]['port'] = egress_port
            config['dns']['servers'][0].update(address='127.0.0.1', port=domestic.server_address[1])
            config['dns']['servers'][1].update(address='127.0.0.1', port=remote.server_address[1])
            client['inbounds'][0]['listen_port'] = public_health
            client['inbounds'][1]['listen_port'] = egress_port
            client['inbounds'].append({'type': 'mixed', 'tag': 'user-in', 'listen': '127.0.0.1', 'listen_port': public_port})
            client['outbounds'][0]['server_port'] = bridge_port
            client['outbounds'][2]['server_port'] = health_port
            client['route']['auto_detect_interface'] = False
            # Test endpoints are intentionally local. Production keeps its LAN bypass.
            client['route']['rules'] = [r for r in client['route']['rules'] if 'ip_is_private' not in r]
            client['dns']['servers'][0] = {'type': 'udp', 'tag': 'bootstrap', 'server': '127.0.0.1', 'server_port': domestic.server_address[1]}
            credentials = ('fixture-user', 'a' * 64) if bridge_only else None
            relay = {'log': {'loglevel': 'warning'}, 'inbounds': [{'listen': '127.0.0.1', 'port': egress_port, 'protocol': 'socks', 'settings': {'auth': 'password', 'accounts': [{'user': 'physical-user', 'pass': 'b' * 64}], 'udp': True, 'ip': '127.0.0.1'}}], 'dns': {'hosts': {'domain:ru': '127.0.0.1', 'domain:xn--p1ai': '127.0.0.1'}}, 'outbounds': [{'protocol': 'freedom', 'settings': {'domainStrategy': 'UseIP'}}]}
            server = {'log': {'loglevel': 'warning'}, 'inbounds': [{'listen': '127.0.0.1', 'port': server_port, 'protocol': 'vless', 'settings': {'clients': [{'id': UUID}], 'decryption': 'none'}}], 'outbounds': [{'tag': 'web', 'protocol': 'freedom', 'settings': {'redirect': f'127.0.0.1:{proxied.server_port}'}}, {'tag': 'dns', 'protocol': 'freedom'}], 'routing': {'rules': [{'type': 'field', 'network': 'udp', 'outboundTag': 'dns'}]}}
            upstream = processes.start(xray, server, server_port, True)
            if bridge_only:
                processes.start(xray, relay, egress_port, True)
            processes.start(xray, config, bridge_port, True)
            if not bridge_only:
                processes.start(core, client, public_port)
            else:
                import subprocess
                path = directory / 'sing-box-check.json'; path.write_text(json.dumps(client))
                subprocess.run([str(core), 'check', '-c', str(path)], check=True, capture_output=True, timeout=15)
                public_port, public_health = bridge_port, health_port
            for name in ['website.ru', 'сайт.рф', 'sub.website.ru']:
                assert request(public_port, direct.server_port, name, credentials) == b'direct', name
            assert request(public_port, direct.server_port, 'website.example', credentials) == b'proxy'
            assert request(public_health, direct.server_port, 'website.ru', credentials) == b'proxy', 'Health check bypassed the proxy'
            blocked = False
            try:
                request(public_port, direct.server_port, 'blocked.example', credentials)
            except (OSError, http.client.HTTPException):
                blocked = True
            assert blocked, 'Provider block rule ignored'
            query(public_port, 'domestic-dns.ru', credentials)
            query(public_port, 'remote-dns.example', credentials)
            assert 'domestic-dns.ru' in domestic.queries and 'domestic-dns.ru' not in remote.queries, (domestic.queries, remote.queries)
            assert 'remote-dns.example' in remote.queries, remote.queries
            with socket.create_connection(('127.0.0.1', bridge_port), 2) as sock:
                sock.sendall(b'\x05\x01\x00'); assert recv(sock, 2) == b'\x05\xff'
            upstream.terminate(); upstream.wait(timeout=5)
            assert request(public_port, direct.server_port, 'still-works.ru', credentials) == b'direct'
            for port, name in [(public_port, 'website.example'), (public_health, 'website.ru')]:
                failed = False
                try:
                    request(port, direct.server_port, name, credentials)
                except (OSError, http.client.HTTPException):
                    failed = True
                assert failed, 'Dead proxy silently used a direct route'
            print('PASS provider rules: .ru/.рф direct, proxy by default, block, split DNS, authenticated bridges, independent health and no fallback', flush=True)
    except BaseException:
        for path in directory.glob('*.log'):
            print(path.name, path.read_text(errors='replace')[-2500:], flush=True)
        raise
    finally:
        processes.close()
        for server in (direct, proxied, domestic, remote):
            server.server_close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--core', required=True, type=pathlib.Path)
    parser.add_argument('--xray', type=pathlib.Path)
    parser.add_argument('--bridge-only', action='store_true')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='kot-provider-') as folder:
        run(args.core.resolve(), (args.xray or official_xray()).resolve(), pathlib.Path(folder), args.bridge_only)
