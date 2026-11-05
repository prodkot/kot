"""Scan reachable code in the exact extracted, unmodified Xray source.

Build with Release/xray-core.py first. govulncheck binary-mode symbol results
alone cannot establish that a vulnerable dependency function is reachable.
"""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
pin = json.loads((ROOT / 'upstream/xray-experiment.json').read_text())
source = ROOT / '.build-cache/xray-source' / ('Xray-core-' + pin['commit'])
if not (source / 'go.mod').is_file():
    raise SystemExit('Build the pinned Xray source before scanning dependencies.')
environment = dict(os.environ, GOTOOLCHAIN='local')
subprocess.run(['go', 'run', 'golang.org/x/vuln/cmd/govulncheck@v1.9.0', '-show', 'traces', './main'],
               cwd=source, env=environment, check=True, timeout=300)
