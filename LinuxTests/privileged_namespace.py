#!/usr/bin/python3
"""Installed helper lifecycle in private mount/network namespaces, never host /run.

Tests the real dedicated UID, privilege drop and retained guard. The interactive
polkit authentication dialog and systemd-resolved are not exercised here.
"""
import json
import os
from pathlib import Path
import pwd
import selectors
import signal
import socket
import subprocess
import time


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=10).stdout


if (os.geteuid() != 0 or not os.environ.get('KOT_HOST_NETNS') or
        os.readlink('/proc/self/ns/net') == os.environ['KOT_HOST_NETNS']):
    raise SystemExit('Refusing host network. Run sudo env KOT_HOST_NETNS=... unshare -nm.')

# Isolate the production helper's exact /run path and all mount propagation.
command('mount', '--make-rprivate', '/')
command('mount', '-t', 'tmpfs', '-o', 'mode=755,nosuid,nodev', 'tmpfs', '/run')
command('/usr/sbin/ip', 'link', 'set', 'lo', 'up')
caller = int(os.environ['KOT_TEST_CALLER_UID'])
assert caller >= 1000 and pwd.getpwuid(caller).pw_uid == caller
account = pwd.getpwnam('kot-core')
assert 0 < account.pw_uid < 1000
helper = '/usr/lib/kot/network-helper.py'
env = dict(os.environ, PKEXEC_UID=str(caller))


def alive(pid):
    try:
        return Path(f'/proc/{pid}/stat').read_text().split(') ', 1)[1][0] != 'Z'
    except FileNotFoundError:
        return False


def wait_dead(pid):
    deadline = time.monotonic() + 5
    while alive(pid) and time.monotonic() < deadline:
        time.sleep(0.02)
    assert not alive(pid), 'helper left its owned core running'


def guard_present():
    return subprocess.run(['/usr/sbin/nft', 'list', 'table', 'inet', 'kot_guard'],
                          capture_output=True, timeout=5).returncode == 0


def start():
    probes = [socket.socket(), socket.socket()]
    try:
        for probe in probes:
            probe.bind(('127.0.0.1', 0))
        ports = [probe.getsockname()[1] for probe in probes]
    finally:
        for probe in probes:
            probe.close()
    config = {'log': {'loglevel': 'warning'}, 'inbounds': [
        {'protocol': 'http', 'listen': '127.0.0.1', 'port': port} for port in ports],
        'outbounds': [{'protocol': 'freedom'}]}
    process = subprocess.Popen([helper], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, env=env)
    try:
        process.stdin.write(json.dumps({'guard': True, 'config': config}).encode() + b'\n')
        process.stdin.flush()
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            assert selector.select(10), 'helper startup timed out'
            assert process.stdout.readline() == b'{"event":"started"}\n'
        children = Path(f'/proc/{process.pid}/task/{process.pid}/children').read_text().split()
        assert len(children) == 1
        child = int(children[0])
        status = dict(line.split(':', 1) for line in Path(f'/proc/{child}/status').read_text().splitlines() if ':' in line)
        assert set(status['Uid'].split()) == {str(account.pw_uid)}
        assert set(status['Gid'].split()) == {str(account.pw_gid)}
        assert not status['Groups'].strip()
        assert int(status['CapEff'].strip(), 16) == 0 and status['NoNewPrivs'].strip() == '1'
        assert guard_present()
        # Ensure startup acknowledgment is followed by actual working listeners.
        deadline = time.monotonic() + 5
        while True:
            try:
                with socket.create_connection(('127.0.0.1', ports[0]), timeout=0.3):
                    break
            except OSError:
                assert process.poll() is None and time.monotonic() < deadline
                time.sleep(0.02)
        return process, child
    except BaseException:
        process.kill()
        process.communicate(timeout=5)
        raise


for event in ('stop', 'eof', 'core-crash', 'supervisor-crash'):
    process = None
    child = None
    try:
        process, child = start()
        if event == 'stop':
            process.stdin.write(b'stop\n')
            process.stdin.flush()
        elif event == 'eof':
            process.stdin.close()
            process.stdin = None
        elif event == 'core-crash':
            os.kill(child, signal.SIGKILL)
        else:
            process.kill()
        process.communicate(timeout=15)
        wait_dead(child)
        if event == 'stop':
            assert process.returncode == 0 and not guard_present()
        else:
            assert process.returncode != 0 and guard_present()
        print('PASS installed helper dedicated UID, no capabilities and guard lifecycle: ' + event, flush=True)
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.communicate(timeout=5)
        subprocess.run([helper, 'recover'], env=env, check=True, capture_output=True, timeout=15)
        if child is not None:
            wait_dead(child)
        assert not guard_present()
