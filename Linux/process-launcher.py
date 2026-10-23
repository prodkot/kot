#!/usr/bin/python3 -I
"""Unprivileged owned-session supervisor, including backend SIGKILL cleanup."""
import os
import select
import signal
import subprocess
import sys
import time

if len(sys.argv) < 2 or not os.path.isabs(sys.argv[1]) or os.geteuid() == 0:
    raise SystemExit(2)
parent = os.pidfd_open(os.getppid())
child = subprocess.Popen(sys.argv[1:], stdin=sys.stdin.buffer, stdout=sys.stdout.buffer,
                         stderr=sys.stderr.buffer, start_new_session=True)
stopping = False


def stop(signum=None, frame=None):
    global stopping
    stopping = True


signal.signal(signal.SIGTERM, stop)
signal.signal(signal.SIGINT, stop)
try:
    while child.poll() is None and not stopping:
        if select.select([parent], [], [], 0.1)[0]:
            break
    if child.poll() is None:
        try:
            os.killpg(child.pid, signal.SIGTERM)
        except PermissionError:
            pass  # pkexec helper authenticates separately and detects stdin EOF
        try:
            child.wait(timeout=2)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait(timeout=1)
            except PermissionError:
                child.wait(timeout=20)
finally:
    # Also remove grandchildren after their direct parent exited normally.
    try:
        os.killpg(child.pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        pass
    os.close(parent)
sys.exit(child.returncode if child.returncode >= 0 else 128 - child.returncode)
