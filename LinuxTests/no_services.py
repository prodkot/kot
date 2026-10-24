#!/usr/bin/python3
"""Real backend regression: no service init, RPC, autostart or update checks."""
import base64
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time

app = Path(sys.argv[1]).resolve()
fixture = Path(sys.argv[2]).resolve()
root = Path(tempfile.mkdtemp(prefix='kot-no-services-'))
env = dict(os.environ)
for key in ('XDG_CONFIG_HOME', 'XDG_DATA_HOME', 'XDG_CACHE_HOME'):
    path = root / key
    path.mkdir(mode=0o700)
    env[key] = str(path)
service_root = Path(env['XDG_DATA_HOME']) / 'kot/services/telegram'
service_root.mkdir(parents=True, mode=0o700)
sentinel = service_root / 'current.json'
sentinel.write_bytes(b'corrupt old service manifest; do not read or modify')
process = subprocess.Popen([str(app / 'Kot.Linux'), '--backend'], env=env,
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           text=True, encoding='utf-8')
messages = queue.Queue()


def read():
    for line in process.stdout:
        messages.put(json.loads(line))


threading.Thread(target=read, daemon=True).start()
sequence = 0
snapshots = []


def rpc(action, data=None):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'id': sequence, 'action': action, 'data': data or {}}) + '\n')
    process.stdin.flush()
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        message = messages.get(timeout=max(0.1, deadline - time.monotonic()))
        if message.get('kind') == 'snapshot':
            snapshots.append(message['data'])
            assert 'services' not in message['data'], 'service data is still exposed to UI'
        if message.get('kind') == 'reply' and message.get('id') == sequence:
            return message
    raise TimeoutError(action)


try:
    assert rpc('ready')['ok'], 'old corrupt service package blocks startup'
    imported = rpc('backupImport', {'bytes': base64.b64encode(fixture.read_bytes()).decode(),
                                    'password': 'kot-services-backup-test'})
    assert imported['ok'], imported
    for action in ('serviceAction', 'serviceSettings', 'serviceUpdateSettings', 'serviceRotateSecret',
                   'serviceSource', 'serviceCopyLink', 'serviceOpenTelegram', 'serviceCancel'):
        result = rpc(action, {'service': 'telegram', 'operation': 'install', 'enabled': False})
        assert not result['ok'] and 'убраны' in result['error'], (action, result)
    assert not rpc('view', {'page': 'services'})['ok']
    # Also pass the former 30-second automatic update-check deadline.
    time.sleep(32)
    assert rpc('heartbeat')['ok']
    assert sentinel.read_bytes() == b'corrupt old service manifest; do not read or modify'
    assert not Path(env['XDG_CACHE_HOME'], 'kot/services').exists()
    assert sorted(p.relative_to(service_root).as_posix() for p in service_root.rglob('*')) == ['current.json']
    exported = rpc('backupExport', {'password': 'kot-services-backup-test'})
    assert exported['ok'], exported
    target = root / 'preserved.kotbackup'
    target.write_bytes(base64.b64decode(exported['data']['bytes'], validate=True))
    subprocess.run(['dotnet', 'run', '--project', 'LinuxTests/Kot.LinuxTests.csproj', '-c', 'Release',
                    '--no-build', '--', '--verify-services-backup', str(target)], check=True, timeout=20)
    assert snapshots and all('services' not in s for s in snapshots)
    print('PASS real backend ignores old service packages/autostart, rejects service RPCs and preserves settings')
finally:
    if process.poll() is None:
        process.stdin.write(json.dumps({'id': -1, 'action': 'shutdown', 'data': {}}) + '\n')
        process.stdin.flush()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    if process.returncode != 0:
        print(process.stderr.read(4000), file=sys.stderr)
        raise SystemExit(1)
