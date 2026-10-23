#!/usr/bin/python3
"""Crash the exact parent fixture; verify its owned child/grandchild cleanup."""
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

root = Path(tempfile.mkdtemp(prefix='kot-process-test-', dir='/tmp/opencode'))
pidfile = root / 'pids.json'
launcher = Path(__file__).resolve().parents[1] / 'Linux/process-launcher.py'
worker = "import os,subprocess,time,json,pathlib; c=subprocess.Popen(['/bin/sleep','60']); pathlib.Path(" + repr(str(pidfile)) + ").write_text(json.dumps([os.getpid(),c.pid])); time.sleep(60)"
parent_code = "import subprocess,time; p=subprocess.Popen(['/usr/bin/python3','-I'," + repr(str(launcher)) + ",'/usr/bin/python3','-c'," + repr(worker) + "]); time.sleep(60)"
parent = subprocess.Popen(['/usr/bin/python3', '-c', parent_code], start_new_session=True)
pids = []


def running(pid):
    try:
        return Path(f'/proc/{pid}/stat').read_text().split(') ', 1)[1][0] != 'Z'
    except FileNotFoundError:
        return False


try:
    deadline = time.monotonic() + 5
    while not pidfile.exists() and time.monotonic() < deadline:
        time.sleep(0.02)
    pids = json.loads(pidfile.read_text())
    parent.kill()
    parent.wait(timeout=2)
    deadline = time.monotonic() + 5
    while any(running(pid) for pid in pids) and time.monotonic() < deadline:
        time.sleep(0.02)
    assert not any(running(pid) for pid in pids), 'orphaned process tree'
    print('PASS backend-parent SIGKILL causes pidfd supervisor to stop owned child and grandchild')
finally:
    if parent.poll() is None:
        parent.kill()
        parent.wait(timeout=2)
    if pids and running(pids[0]):
        os.killpg(pids[0], signal.SIGKILL)
