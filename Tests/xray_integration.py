"""Real single-core traffic, provider routing, DNS and authenticated metrics.
No Internet server or administrator privileges are needed on Linux.
"""
import argparse, base64, contextlib, copy, http.client, http.server, json, pathlib
import socket, socketserver, struct, subprocess, threading, time

parser = argparse.ArgumentParser()
parser.add_argument('--core', required=True)
args = parser.parse_args()
core = str(pathlib.Path(args.core).resolve())
folder = pathlib.Path('Tests/generated/native-xray')
processes = []
servers = []
logs = []

def check(value, description):
    if not value:
        raise AssertionError(description)
    print('PASS ' + description, flush=True)

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

def start(config, name, listen):
    config['log'] = {'loglevel': 'debug'}
    path = folder / (name + '-live.json')
    path.write_text(json.dumps(config))
    log = open(folder / (name + '-live.log'), 'w+')
    logs.append(log)
    process = subprocess.Popen([core, 'run', '-c', str(path)], stdin=subprocess.DEVNULL, stdout=log, stderr=log)
    processes.append(process)
    until = time.monotonic() + 8
    while time.monotonic() < until:
        if process.poll() is not None:
            log.seek(0)
            raise RuntimeError(name + ': ' + log.read(32000))
        try:
            with socket.create_connection(('127.0.0.1', listen), 0.1):
                return process
        except OSError:
            time.sleep(0.05)
    raise TimeoutError(name)

def request(proxy, url, auth=None, timeout=4):
    connection = http.client.HTTPConnection('127.0.0.1', proxy, timeout=timeout)
    try:
        headers = {'Connection': 'close'}
        if auth is not None:
            headers['Proxy-Authorization'] = 'Basic ' + base64.b64encode(auth.encode()).decode()
        connection.request('GET', url, headers=headers)
        response = connection.getresponse()
        return response.status, response.read(1024 * 1024)
    finally:
        connection.close()

class Web(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = b'native-xray-traffic'
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args): pass

class Dns(socketserver.BaseRequestHandler):
    def handle(self):
        data, sock = self.request
        cursor = 12
        labels = []
        while data[cursor]:
            length = data[cursor]; cursor += 1
            labels.append(data[cursor:cursor+length].decode('ascii'))
            cursor += length
        end = cursor + 5
        qtype = struct.unpack('!H', data[cursor+1:cursor+3])[0]
        self.server.queries.append(('.'.join(labels), qtype))
        answer = qtype == 1
        header = data[:2] + struct.pack('!HHHHH', 0x8180, 1, int(answer), 0, 0)
        response = header + data[12:end]
        if answer:
            response += b'\xc0\x0c' + struct.pack('!HHIH', 1, 1, 60, 4) + socket.inet_aton('127.0.0.1')
        sock.sendto(response, self.client_address)

def serve(server):
    servers.append(server)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server

try:
    for path in sorted(folder.glob('*.json')):
        if path.name.endswith('-live.json'): continue
        result = subprocess.run([core, 'run', '-test', '-c', str(path)], capture_output=True, timeout=15)
        check(result.returncode == 0, 'native core accepts ' + path.name + ': ' + result.stderr.decode(errors='replace')[-500:])
    web = serve(http.server.ThreadingHTTPServer(('127.0.0.1', 0), Web))
    web_port = web.server_address[1]
    dns = serve(socketserver.ThreadingUDPServer(('127.0.0.1', 19445), Dns)); dns.queries = []
    fallback = serve(socketserver.ThreadingUDPServer(('127.0.0.1', 0), Dns)); fallback.queries = []
    server_config = json.loads((folder / 'server.json').read_text())
    xhttp_port = port()
    xhttp_inbound = copy.deepcopy(server_config['inbounds'][0])
    xhttp_inbound['port'] = xhttp_port
    xhttp_inbound['streamSettings'] = {'network': 'xhttp', 'xhttpSettings': {'path': '/fixture', 'mode': 'auto'}}
    server_config['inbounds'].append(xhttp_inbound)
    server = start(server_config, 'server', 19441)
    provider = json.loads((folder / 'provider.json').read_text())
    provider['dns']['servers'][-1] = {'address': '127.0.0.1', 'port': fallback.server_address[1]}
    next(o for o in provider['outbounds'] if o['tag'] == 'direct')['streamSettings'] = {'sockopt': {'domainStrategy': 'UseIP'}}
    client = start(provider, 'provider', 19442)
    url = f'http://127.0.0.1:{web_port}/'
    check(request(19442, url) == (200, b'native-xray-traffic'), 'health traffic crosses a real VLESS server')
    for suffix in ('ru', 'xn--p1ai'):
        check(request(19443, f'http://native-fixture.{suffix}:{web_port}/') == (200, b'native-xray-traffic'), suffix + ' follows the provider direct route and provider DNS')
    status, body = request(19444, 'http://kot-metrics.internal/debug/vars', 'kot:' + 's' * 64)
    metrics = json.loads(body)
    check(status == 200 and metrics['stats']['inbound']['kot-in']['downlink'] > 0, 'authenticated native metrics expose actual user traffic')
    check(request(19444, 'http://kot-metrics.internal/debug/vars')[0] == 407, 'unauthenticated metrics access is denied')
    check(request(19444, 'http://kot-metrics.internal/debug/vars', 'kot:wrong-password')[0] == 407, 'incorrect metrics credentials are denied')
    xhttp = json.loads((folder / 'transport-4.json').read_text())
    xhttp['inbounds'] = [xhttp['inbounds'][0]]
    xhttp['inbounds'][0]['port'] = port()
    outbound = next(o for o in xhttp['outbounds'] if o['protocol'] == 'vless')
    outbound['settings']['vnext'][0]['address'] = '127.0.0.1'
    outbound['settings']['vnext'][0]['port'] = xhttp_port
    outbound['streamSettings'] = {'network': 'xhttp', 'security': 'none', 'xhttpSettings': {'path': '/fixture', 'mode': 'packet-up'}}
    xhttp_client = start(xhttp, 'xhttp', xhttp['inbounds'][0]['port'])
    check(request(xhttp['inbounds'][0]['port'], url) == (200, b'native-xray-traffic'), 'native XHTTP carries a real HTTP response without another core or SOCKS bridge')
    server.terminate(); server.wait(timeout=5)
    for suffix in ('ru', 'xn--p1ai'):
        check(request(19443, f'http://offline-fixture.{suffix}:{web_port}/') == (200, b'native-xray-traffic'), suffix + ' still opens directly while the VPN server is stopped')
    failed = False
    try:
        failed = request(19442, url, timeout=2)[0] != 200
    except (OSError, http.client.HTTPException):
        failed = True
    check(failed, 'the health check cannot silently use a provider direct bypass')
    check(any(q[1] == 28 for q in dns.queries), 'provider DNS was asked for empty AAAA responses')
    check(not fallback.queries, 'an empty provider AAAA answer never falls through to the remote resolver')
    print('Native Xray integration passed.', flush=True)
finally:
    for process in reversed(processes):
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=5)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
    for server in servers:
        server.shutdown(); server.server_close()
    for log in logs: log.close()
