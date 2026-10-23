#!/usr/bin/python3
"""Launch isolated dconf daemon with the SAME XDG paths as its clients."""
import os
from pathlib import Path
import subprocess
import tempfile

root = tempfile.mkdtemp(prefix='kot-dconf-', dir='/tmp/opencode')
env = dict(os.environ, KOT_TEST_ROOT=root, KOT_ISOLATED_DBUS='1')
for key in ('XDG_CONFIG_HOME', 'XDG_DATA_HOME', 'XDG_CACHE_HOME', 'XDG_RUNTIME_DIR'):
    path = Path(root) / key
    path.mkdir(mode=0o700)
    env[key] = str(path)
subprocess.run(['dbus-run-session', '--', 'dotnet', 'run', '--project', 'LinuxTests/Kot.LinuxTests.csproj',
                '-c', 'Release', '--', 'artifacts/core/xray', '--gnome'],
               env=env, check=True, timeout=120)
