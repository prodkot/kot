#!/usr/bin/python3
"""Run ONLY as root through unshare -nm. No host resolved/D-Bus is touched.

Proves native TUN FD traffic and IPv4/IPv6 nft blocking in a disposable netns.
Does not prove polkit, dedicated UID drop, resolved, UFW or clean Ubuntu install.
"""
import ctypes
import fcntl
import http.server
import importlib.util
import json
import os
from pathlib import Path
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
if os.geteuid() != 0 or os.readlink('/proc/self/ns/net') == os.environ.get('KOT_HOST_NETNS') or not os.environ.get('KOT_HOST_NETNS'):
    raise SystemExit('Refusing host network. Use documented unshare command.')

spec = importlib.util.spec_from_file_location('helper', ROOT / 'Linux/network-helper.py')
h = importlib.util.module_from_spec(spec)
spec.loader.exec_module(h)
h.STATE = Path(tempfile.mkdtemp(prefix='kot-netns-'))
real_command = h.command


def command(*args, **kwargs):
    if args[0] == '/usr/bin/resolvectl':
        # The host system bus is not namespaced: NEVER call it in this test.
        return subprocess.CompletedProcess(args, 0, b'', b'')
    return real_command(*args, **kwargs)


h.command = command
command('/usr/sbin/ip', 'link', 'set', 'lo', 'up')
command('/usr/sbin/ip', 'link', 'add', 'test-dummy', 'type', 'dummy')
command('/usr/sbin/ip', 'link', 'set', 'test-dummy', 'up')
command('/usr/sbin/ip', 'addr', 'add', '198.18.0.1/24', 'dev', 'test-dummy')
command('/usr/sbin/ip', '-6', 'addr', 'add', '2001:db8:1::1/64', 'dev', 'test-dummy', 'nodad')
# More-specific physical routes must not bypass the application's routing rules.
command('/usr/sbin/ip', 'route', 'add', '203.0.113.0/24', 'dev', 'test-dummy')
command('/usr/sbin/ip', '-6', 'route', 'add', '2001:db8:2::/64', 'dev', 'test-dummy')
command('/usr/sbin/nft', 'add', 'table', 'inet', 'foreign_test')
before = command('/usr/sbin/nft', 'list', 'table', 'inet', 'foreign_test').stdout
fd = h.prepare(0)
core = None
server = None
try:
    # The helper's policy selects TUN for non-core UIDs, for both families.
    for family, address in (('-4', '203.0.113.1'), ('-6', '2001:db8:2::1')):
        result = command('/usr/sbin/ip', family, 'route', 'get', address, 'uid', '12345').stdout
        assert b'kot-tun' in result, result
    print('PASS IPv4/IPv6 policy route selected for non-core UID')
    for family, address in (('-4', '198.18.0.2'), ('-6', '2001:db8:1::2')):
        result = command('/usr/sbin/ip', family, 'route', 'get', address, 'uid', '12345').stdout
        assert b'kot-tun' in result, result
    print('PASS connected and more-specific physical routes cannot bypass Xray policy')
    # A test-only mark selects the same policy for sockets in this isolated netns.
    for family in ('-4', '-6'):
        command('/usr/sbin/ip', family, 'rule', 'add', 'pref', '10779', 'fwmark', '123', 'lookup', h.TABLE)

    class Origin(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.send_header('Content-Length', '8')
            self.end_headers()
            self.wfile.write(b'real-tun')
        def log_message(self, *args):
            pass
    origin = http.server.ThreadingHTTPServer(('198.18.0.1', 0), Origin)
    threading.Thread(target=origin.serve_forever, daemon=True).start()
    port = origin.server_address[1]
    # TUN → real VLESS server → HTTP origin, not just opening a TUN interface.
    uuid = '11111111-1111-4111-8111-111111111111'
    server_config = {'inbounds': [{'listen': '127.0.0.1', 'port': 19491, 'protocol': 'vless', 'settings': {'decryption': 'none', 'clients': [{'id': uuid}]}}], 'outbounds': [{'protocol': 'freedom', 'settings': {'redirect': f'198.18.0.1:{port}', 'finalRules': [{'action': 'allow', 'ip': ['198.18.0.0/24']}]}}], 'log': {'loglevel': 'none'}}
    config = {'inbounds': [{'protocol': 'tun', 'tag': 'kot-tun', 'settings': {'name': 'kot-tun', 'mtu': 1500}}],
              'outbounds': [{'protocol': 'vless', 'tag': 'proxy', 'settings': {'vnext': [{'address': '127.0.0.1', 'port': 19491, 'users': [{'id': uuid, 'encryption': 'none'}]}]}}],
              'log': {'loglevel': 'none'}}
    exe = str((ROOT / 'artifacts/core/xray').resolve())
    server = subprocess.Popen([exe, 'run', '-c', 'stdin:', '-format', 'json'], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    server.stdin.write(json.dumps(server_config).encode()); server.stdin.close()
    core = subprocess.Popen([exe, 'run', '-c', 'stdin:', '-format', 'json'], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, pass_fds=(fd,), env=dict(os.environ, XRAY_TUN_FD=str(fd)))
    core.stdin.write(json.dumps(config).encode()); core.stdin.close()
    time.sleep(0.4)
    assert core.poll() is None and server.poll() is None
    # A remote target IP is translated by the VLESS server to the local origin.
    # Set the destination to the origin's IP, but force the tester into TUN.
    for family, target in ((socket.AF_INET, '198.18.0.2'), (socket.AF_INET6, '2001:db8:1::2')):
        with socket.socket(family) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_MARK, 123)
            s.settimeout(5)
            s.connect((target, port))
            s.sendall(b'GET / HTTP/1.1\r\nHost: local-test\r\nConnection: close\r\n\r\n')
            result = b''
            while block := s.recv(4096): result += block
            assert b'real-tun' in result, result
        print('PASS real TCP response through native TUN FD and VLESS: ' + target)
    echo = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    echo.bind(('198.18.0.1', port))
    def udp_echo():
        for _ in range(2):
            data, remote = echo.recvfrom(4096)
            echo.sendto(b'udp-through-tun:' + data, remote)
    thread = threading.Thread(target=udp_echo, daemon=True)
    thread.start()
    for family, target in ((socket.AF_INET, '198.18.0.2'), (socket.AF_INET6, '2001:db8:1::2')):
        with socket.socket(family, socket.SOCK_DGRAM) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_MARK, 123)
            s.settimeout(5)
            s.sendto(b'packet', (target, port))
            assert s.recvfrom(4096)[0] == b'udp-through-tun:packet'
        print('PASS real UDP response through native TUN FD and VLESS: ' + target)
    echo.close()
    core.terminate(); core.wait(timeout=5)
    # Arm with a different UID to test unpermitted physical egress of this UID.
    h.guard(12345)
    command('/usr/sbin/nft', 'add', 'rule', 'inet', 'kot_guard', 'output', 'counter', 'drop')
    for family, address in ((socket.AF_INET, '198.18.0.2'), (socket.AF_INET6, '2001:db8:1::2')):
        with socket.socket(family, socket.SOCK_DGRAM) as s:
            try:
                s.sendto(b'blocked', (address, 443))
            except PermissionError:
                pass
    rules = command('/usr/sbin/nft', 'list', 'table', 'inet', 'kot_guard').stdout
    assert b'policy drop' in rules and b'ip6 hoplimit 255' in rules and b'counter packets 2 ' in rules, rules
    print('PASS IPv4 and IPv6 physical UDP packets hit nft drop counter')
    # Replacing in one transaction also works while a retained guard is active.
    h.guard(12345)
    assert command('/usr/sbin/nft', 'list', 'table', 'inet', 'foreign_test').stdout == before
    h.cleanup_network()
    assert command('/usr/sbin/nft', 'list', 'table', 'inet', 'kot_guard').returncode == 0
    print('PASS cleanup preserves guard and foreign nft table')
    h.release_guard()
    assert command('/usr/sbin/nft', 'list', 'table', 'inet', 'foreign_test').stdout == before
    print('PASS explicit release changes only kot-owned firewall table')
finally:
    for p in (core, server):
        if p is not None and p.poll() is None:
            p.terminate()
            try: p.wait(timeout=4)
            except subprocess.TimeoutExpired: p.kill(); p.wait(timeout=4)
    os.close(fd)
    h.cleanup_network()
