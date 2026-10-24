#!/usr/bin/python3 -I
"""Restricted pkexec supervisor. This file and /usr/lib/kot must be root-owned.

No paths, executable names, firewall text or commands are supplied by the caller.
Xray is an unprivileged dedicated system user, not root or the GUI user.
"""
import base64
import ctypes
import fcntl
import json
import os
import pathlib
import pwd
import selectors
import select
import signal
import struct
import subprocess
import sys
import threading
import time

ROOT = pathlib.Path('/usr/lib/kot')
STATE = pathlib.Path('/run/kot-network')
TABLE = '47811'
MARK = '0x4b6f74'
OWNER = 'io.github.prodkot.kot.network.v1'
ENV = {'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LANG': 'C.UTF-8'}


def command(*args, input=None, optional=False):
    result = subprocess.run(args, input=input, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env=ENV, timeout=10, check=False)
    if result.returncode and not optional:
        raise RuntimeError('network operation failed: ' + pathlib.Path(args[0]).name)
    return result


def validate(envelope):
    if not isinstance(envelope, dict) or set(envelope) - {'config', 'guard', 'assets'}:
        raise ValueError('invalid request')
    config = envelope['config']
    if type(envelope.get('guard')) is not bool or not isinstance(config, dict):
        raise ValueError('invalid request')
    if len(json.dumps(config)) > 8 * 1024 * 1024 or not isinstance(config.get('outbounds'), list) or not 1 <= len(config['outbounds']) <= 128:
        raise ValueError('oversized configuration')
    if set(config) - {'log', 'dns', 'routing', 'outbounds', 'inbounds', 'stats', 'policy', 'metrics', 'observatory', 'burstObservatory'}:
        raise ValueError('unsupported config section')
    if config.get('log') != {'loglevel': 'warning'}:
        raise ValueError('only in-memory warning log is allowed')
    inputs = config.get('inbounds', [])
    if not 2 <= len(inputs) <= 4:
        raise ValueError('invalid listeners')
    tun = [i for i in inputs if i.get('protocol') == 'tun']
    if len(tun) > 1 or tun and tun[0].get('settings') != {'name': 'kot-tun', 'mtu': 1500}:
        raise ValueError('invalid TUN settings')
    ports = set()
    for item in inputs:
        if item.get('protocol') == 'tun':
            continue
        port = item.get('port')
        if item.get('protocol') != 'http' or item.get('listen') != '127.0.0.1' or type(port) is not int or not 1024 <= port <= 65535 or port in ports:
            raise ValueError('non-local or duplicate listener')
        ports.add(port)
    def walk(value):
        if isinstance(value, dict):
            for key, child in value.items():
                # Files, log sinks, sockets and external execution are forbidden.
                if key.lower().endswith(('path', 'file')) and key not in {'path'}:
                    raise ValueError('local file reference')
                if key in {'certificateFile', 'keyFile', 'keyLog', 'masterKeyLog', 'interface', 'mark', 'sendThrough', 'sockoptFile', 'plugin', 'exec', 'process'}:
                    raise ValueError('unsafe runtime option')
                if key in {'allowInsecure', 'insecure', 'disableSystemRoot'} and child is not False:
                    raise ValueError('TLS verification bypass')
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)
        elif isinstance(value, str):
            if '\x00' in value or 'ext:' in value or value.startswith(('unix:', 'file:', '/proc/', '/dev/')):
                raise ValueError('external file reference')
    walk(config)
    assets = envelope.get('assets', {})
    if not isinstance(assets, dict) or set(assets) - {'geoip.dat', 'geosite.dat'}:
        raise ValueError('invalid assets')
    for value in assets.values():
        if len(base64.b64decode(value, validate=True)) > 64 * 1024 * 1024:
            raise ValueError('oversized asset')
    return config


def guard(uid):
    # One nft transaction; no unfiltered replace gap. Never flush the ruleset.
    existing = command('/usr/sbin/nft', 'list', 'table', 'inet', 'kot_guard', optional=True).returncode == 0
    if existing and not (STATE / 'guard').exists():
        raise RuntimeError('foreign kot_guard table: refusing to change it')
    if existing:
        verify_guard_owner()
    (STATE / 'guard').write_text('owned by kot\n')
    text = f'''add table inet kot_guard {{ comment "{OWNER}"; }}
flush table inet kot_guard
add chain inet kot_guard output {{ type filter hook output priority -10; policy drop; }}
add chain inet kot_guard input {{ type filter hook input priority -10; policy drop; }}
add rule inet kot_guard output oifname "lo" accept
add rule inet kot_guard input iifname "lo" accept
add rule inet kot_guard output meta skuid {uid} ct mark set {MARK} accept
add rule inet kot_guard input ct mark {MARK} accept
add rule inet kot_guard output oifname "kot-tun" accept
add rule inet kot_guard input iifname "kot-tun" accept
add rule inet kot_guard output udp sport 68 udp dport 67 accept
add rule inet kot_guard input udp sport 67 udp dport 68 accept
add rule inet kot_guard output udp sport 546 udp dport 547 accept
add rule inet kot_guard input udp sport 547 udp dport 546 accept
add rule inet kot_guard output icmpv6 type {{ nd-router-solicit, nd-neighbor-solicit, nd-neighbor-advert }} ip6 hoplimit 255 accept
add rule inet kot_guard input icmpv6 type {{ nd-router-advert, nd-neighbor-solicit, nd-neighbor-advert }} ip6 hoplimit 255 accept
'''
    command('/usr/sbin/nft', '-f', '-', input=text.encode())


def verify_guard_owner():
    value = json.loads(command('/usr/sbin/nft', '-j', 'list', 'table', 'inet', 'kot_guard').stdout)
    tables = [item['table'] for item in value['nftables'] if 'table' in item]
    if len(tables) != 1 or tables[0].get('comment') != OWNER:
        raise RuntimeError('kot_guard ownership changed; refusing to touch it')


def release_guard():
    if (STATE / 'guard').exists():
        result = command('/usr/sbin/nft', 'list', 'table', 'inet', 'kot_guard', optional=True)
        if result.returncode == 0:
            verify_guard_owner()
            command('/usr/sbin/nft', 'delete', 'table', 'inet', 'kot_guard')
        (STATE / 'guard').unlink()


def cleanup_network():
    # These exact tuples are ours; never flush main tables or remove by PID/name.
    if not (STATE / 'network').exists():
        return
    receipt = json.loads((STATE / 'network').read_text())
    uid = receipt.get('uid')
    if type(uid) is not int or uid < 0:
        raise RuntimeError('invalid network receipt')
    for family in ('-4', '-6'):
        command('/usr/sbin/ip', family, 'rule', 'del', 'pref', '10782', 'lookup', TABLE, optional=True)
        command('/usr/sbin/ip', family, 'rule', 'del', 'pref', '10781', 'lookup', 'main', 'suppress_prefixlength', '0', optional=True)
        command('/usr/sbin/ip', family, 'rule', 'del', 'pref', '10780', 'uidrange', f'{uid}-{uid}', 'lookup', 'main', optional=True)
        command('/usr/sbin/ip', family, 'route', 'flush', 'table', TABLE)
    link = command('/usr/sbin/ip', '-j', 'link', 'show', 'kot-tun', optional=True)
    if link.returncode == 0:
        current = json.loads(link.stdout)
        if current and current[0].get('ifindex') == receipt.get('ifindex'):
            command('/usr/sbin/ip', 'link', 'del', 'kot-tun')
    (STATE / 'network').unlink()


def prepare(uid):
    if command('/usr/sbin/ip', 'link', 'show', 'kot-tun', optional=True).returncode == 0:
        raise RuntimeError('kot-tun already exists; explicit recovery required')
    for family in ('-4', '-6'):
        rules = json.loads(command('/usr/sbin/ip', '-j', family, 'rule', 'show').stdout)
        if any(10780 <= r.get('priority', 0) <= 10782 for r in rules):
            raise RuntimeError('policy routing priorities occupied')
        routes = command('/usr/sbin/ip', '-j', family, 'route', 'show', 'table', TABLE, optional=True)
        if routes.returncode == 0 and json.loads(routes.stdout):
            raise RuntimeError('policy route table occupied')
    # Journal before the first mutation, so partial setup is recoverable.
    receipt = {'uid': uid, 'ifindex': None}
    (STATE / 'network').write_text(json.dumps(receipt))
    fd = os.open('/dev/net/tun', os.O_RDWR | os.O_CLOEXEC)
    try:
        fcntl.ioctl(fd, 0x400454ca, struct.pack('16sH', b'kot-tun', 0x0001 | 0x1000))
        receipt['ifindex'] = json.loads(command('/usr/sbin/ip', '-j', 'link', 'show', 'kot-tun').stdout)[0]['ifindex']
        (STATE / 'network').write_text(json.dumps(receipt))
        command('/usr/sbin/ip', 'link', 'set', 'kot-tun', 'mtu', '1500', 'up')
        command('/usr/sbin/ip', 'addr', 'add', '172.28.231.1/30', 'dev', 'kot-tun')
        command('/usr/sbin/ip', '-6', 'addr', 'add', 'fdfe:dcba:231::1/126', 'dev', 'kot-tun', 'nodad')
        for family in ('-4', '-6'):
            command('/usr/sbin/ip', family, 'route', 'add', 'default', 'dev', 'kot-tun', 'table', TABLE)
            command('/usr/sbin/ip', family, 'rule', 'add', 'pref', '10780', 'uidrange', f'{uid}-{uid}', 'lookup', 'main')
            # Only the core may bypass the tunnel. A main-table lookup with a
            # suppressed default also accepts every more-specific physical route,
            # silently bypassing Xray's proxy/block policy (including DHCP routes).
            # LAN/direct decisions belong to Xray, whose UID uses main above.
            command('/usr/sbin/ip', family, 'rule', 'add', 'pref', '10782', 'lookup', TABLE)
        command('/usr/bin/resolvectl', 'dns', 'kot-tun', '172.28.231.2')
        command('/usr/bin/resolvectl', 'domain', 'kot-tun', '~.')
        command('/usr/bin/resolvectl', 'default-route', 'kot-tun', 'yes')
        return fd
    except BaseException:
        os.close(fd)
        raise


def wait_for_stop(child, input_fd=0):
    """Only an explicit complete stop request permits releasing protection."""
    with selectors.DefaultSelector() as selector:
        selector.register(input_fd, selectors.EVENT_READ)
        control = bytearray()
        while child.poll() is None:
            if selector.select(0.25):
                block = os.read(input_fd, 64)
                if not block:
                    return False
                control.extend(block)
                if b'\n' in control or len(control) >= 64:
                    return control == b'stop\n'
    return False


def main():
    os.umask(0o077)
    if os.geteuid() != 0 or not os.environ.get('PKEXEC_UID', '').isdecimal():
        raise RuntimeError('must be authenticated by polkit')
    caller = int(os.environ['PKEXEC_UID'])
    if caller < 1000 or pwd.getpwuid(caller).pw_uid != caller:
        raise RuntimeError('invalid interactive caller')
    STATE.mkdir(mode=0o700, exist_ok=True)
    lock = open(STATE / 'lock', 'a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if sys.argv[1:] == ['recover']:
        cleanup_network()
        release_guard()
        return
    if sys.argv[1:]:
        raise ValueError('invalid operation')
    data = bytearray()
    deadline = time.monotonic() + 35
    while b'\n' not in data:
        remaining = deadline - time.monotonic()
        if remaining <= 0 or not select.select([0], [], [], remaining)[0]:
            raise TimeoutError('request deadline')
        block = os.read(0, 65536)
        if not block:
            raise ValueError('incomplete request')
        data.extend(block)
        if len(data) > 180 * 1024 * 1024:
            raise ValueError('oversized request')
    if data.count(b'\n') != 1 or not data.endswith(b'\n'):
        raise ValueError('invalid request framing')
    envelope = json.loads(data)
    config = validate(envelope)
    account = pwd.getpwnam('kot-core')
    if account.pw_uid == 0 or account.pw_uid == caller:
        raise RuntimeError('invalid core account')
    cleanup_network()  # only journaled changes from an earlier crash
    child = None
    fd = None
    normal = False
    try:
        if envelope['guard']:
            guard(account.pw_uid)
        elif (STATE / 'guard').exists():
            raise RuntimeError('kill switch active; explicit recovery required')
        is_tun = any(i.get('protocol') == 'tun' for i in config['inbounds'])
        if is_tun:
            fd = prepare(account.pw_uid)
        assets = STATE / 'assets'
        assets.mkdir(mode=0o755, exist_ok=True)
        for name in ('geoip.dat', 'geosite.dat'):
            path = assets / name
            if path.exists():
                path.unlink()
        for name, encoded in envelope.get('assets', {}).items():
            (assets / name).write_bytes(base64.b64decode(encoded, validate=True))
            (assets / name).chmod(0o644)
        supervisor_pid = os.getpid()
        def demote():
            os.setgroups([])
            os.setgid(account.pw_gid)
            os.setuid(account.pw_uid)
            # setuid clears PR_SET_PDEATHSIG: set it AFTER dropping credentials.
            if ctypes.CDLL(None).prctl(1, signal.SIGKILL, 0, 0, 0) != 0:
                os._exit(1)
            if os.getppid() != supervisor_pid or ctypes.CDLL(None).prctl(38, 1, 0, 0, 0) != 0:
                os._exit(1)  # close the parent-death race; forbid gaining privileges
        # /run/kot-network is private, assets are exposed by an FD-relative path.
        assetfd = os.open(assets, os.O_RDONLY | os.O_DIRECTORY)
        env = dict(ENV, XRAY_LOCATION_ASSET=f'/proc/self/fd/{assetfd}')
        if fd is not None:
            env['XRAY_TUN_FD'] = str(fd)
        child = subprocess.Popen([str(ROOT / 'core/xray'), 'run', '-format', 'json', '-c', 'stdin:'],
                                 stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                 pass_fds=tuple(f for f in (fd, assetfd) if f is not None), env=env, cwd='/', preexec_fn=demote, start_new_session=True)
        os.close(assetfd)
        child.stdin.write(json.dumps(config, separators=(',', ':')).encode())
        child.stdin.close()
        print('{"event":"started"}', flush=True)
        def drain():
            try:
                while block := child.stdout.read(2048):
                    # Private pipe only. The .NET owner bounds and redacts this
                    # output before presenting diagnostics; never write a disk log.
                    os.write(1, block)
            except (BrokenPipeError, OSError):
                pass
        threading.Thread(target=drain, daemon=True).start()
        normal = wait_for_stop(child)
    finally:
        if child is not None and child.poll() is None:
            os.killpg(child.pid, signal.SIGTERM)
            try:
                child.wait(timeout=4)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait(timeout=4)
        try:
            cleanup_network()
        finally:
            if fd is not None:
                os.close(fd)
        if normal:
            release_guard()
        for path in (STATE / 'assets').glob('*.dat'):
            path.unlink()
    if not normal:
        # A zero exit status is a receipt of successful explicit disconnection.
        # EOF/core death must not let the client clear its retained-guard marker.
        raise RuntimeError('core stopped without an explicit disconnect')


if __name__ == '__main__':
    try:
        def interrupted(signum, frame):
            raise RuntimeError('supervisor interrupted')
        signal.signal(signal.SIGTERM, interrupted)
        main()
    except BaseException as error:
        # Configuration and upstream errors may contain credentials: never print them.
        print('kot network helper failed: ' + type(error).__name__, file=sys.stderr)
        sys.exit(1)
