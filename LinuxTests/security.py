#!/usr/bin/python3
"""A missing/locked Secret Service must not silently produce a plaintext key."""
import os
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
for operation in ('lookup', 'create'):
    result = subprocess.run(['/usr/bin/python3', '-I', str(root / 'Linux/keyring.py'), operation],
                            env=dict(os.environ, DBUS_SESSION_BUS_ADDRESS='unix:path=/tmp/opencode/nonexistent-kot-test-bus'),
                            capture_output=True, timeout=15)
    assert result.returncode != 0 and not result.stdout, 'Secret Service failure leaked/created a key'
print('PASS unavailable Secret Service refuses lookup and creation; no plaintext fallback or key output')
