"""Exercise production TUN sniffing on Linux via an HTTP CONNECT test inlet.
Windows RoutingChecks separately exercises the actual Wintun/WFP path.
TLS uses a pinned fixture CA; no Internet, MITM or destination redirects.
"""
import copy, http.client, http.server, json, socket, ssl, subprocess, threading


def run(folder, start, port, check):
    cert, key = folder / 'sniff-cert.pem', folder / 'sniff-key.pem'
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                    '-keyout', str(key), '-out', str(cert), '-days', '1',
                    '-subj', '/CN=direct-sniff.ru', '-addext',
                    'subjectAltName=DNS:direct-sniff.ru,DNS:proxy-sniff.test,DNS:blocked-sniff.ru'],
                   stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, check=True, timeout=15)
    server_tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server_tls.load_cert_chain(cert, key)
    trust = ssl.create_default_context(cafile=str(cert))

    class Origin(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.send_header('Content-Length', '2')
            self.end_headers()
            self.wfile.write(b'OK')
        def log_message(self, *_): pass

    web = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Origin)
    web.socket = server_tls.wrap_socket(web.socket, server_side=True)
    thread = threading.Thread(target=web.serve_forever, daemon=True)
    thread.start()

    def fetch(proxy, host, ip='[2001:db8::dead]'):
        with socket.create_connection(('127.0.0.1', proxy), timeout=3) as sock:
            dest = f'{ip}:{web.server_port}'
            sock.sendall(f'CONNECT {dest} HTTP/1.1\r\nHost: {dest}\r\n\r\n'.encode())
            headers = b''
            while not headers.endswith(b'\r\n\r\n'):
                part = sock.recv(1)
                if not part: raise ConnectionError('EOF before CONNECT')
                headers += part
                if len(headers) > 4096: raise ValueError('oversized CONNECT header')
            if b' 200 ' not in headers.split(b'\r\n')[0]: raise ConnectionError(headers)
            with trust.wrap_socket(sock, server_hostname=host) as tls:
                tls.sendall(f'GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n'.encode())
                response = http.client.HTTPResponse(tls)
                response.begin()
                return response.status, response.read(1024)

    try:
        config = json.loads((folder / 'provider-sniff.json').read_text())
        inlet = next(i for i in config['inbounds'] if i['tag'] == 'kot-tun')
        # Replace only the privileged TUN transport. Keep its production sniffer,
        # inbound tag, provider routing, DNS and actual native egress behavior.
        inlet.update(protocol='http', settings={}, listen='127.0.0.1', port=port())
        config['inbounds'] = [inlet]
        old = copy.deepcopy(config)
        old['inbounds'][0]['sniffing']['routeOnly'] = True
        process = start(old, 'sniff-before', inlet['port'])
        try:
            for host in ('direct-sniff.ru', 'proxy-sniff.test'):
                failed = False
                try: failed = fetch(inlet['port'], host) != (200, b'OK')
                except (OSError, http.client.HTTPException): failed = True
                check(failed, 'old routeOnly reproduces TLS EOF on an unreachable browser IPv6: ' + host)
        finally:
            process.terminate(); process.wait(timeout=5)
        process = start(config, 'sniff-after', inlet['port'])
        try:
            for ip in ('[2001:db8::dead]', '127.0.0.2'):
                for host in ('direct-sniff.ru', 'proxy-sniff.test'):
                    check(fetch(inlet['port'], host, ip) == (200, b'OK'),
                          'sniffed destination recovers HTTPS via direct DNS / real VLESS without redirect: ' + host + ' ' + ip)
            failed = False
            try: failed = fetch(inlet['port'], 'blocked-sniff.ru') != (200, b'OK')
            except (OSError, http.client.HTTPException): failed = True
            check(failed, 'destination recovery preserves the explicit domain block')
        finally:
            process.terminate(); process.wait(timeout=5)
    finally:
        web.shutdown(); web.server_close(); thread.join(timeout=5)
