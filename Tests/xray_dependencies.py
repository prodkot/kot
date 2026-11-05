"""Scan reachable code in the extracted Xray source with the build's locked dependencies.

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
for name in ('go.mod', 'go.sum'):
    expected = (ROOT / 'upstream' / ('xray-' + name)).read_text(encoding='utf-8')
    if (source / name).read_text(encoding='utf-8') != expected:
        raise SystemExit('Xray dependency lock differs from the build. Rebuild before scanning.')
environment = dict(os.environ, CGO_ENABLED='0', GOTOOLCHAIN='local', GOWORK='off', GOFLAGS='-mod=readonly')
subprocess.run(['go', 'run', 'golang.org/x/vuln/cmd/govulncheck@v1.9.0', '-show', 'traces', './main'],
               cwd=source, env=environment, check=True, timeout=300)
