"""Build the pinned, unmodified official Xray source and its signed Wintun DLL."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
WINTUN_URL = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
WINTUN_SHA = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'


def build(destination, target):
    pin = json.loads((ROOT / 'upstream/xray-experiment.json').read_text())
    archive = ROOT / 'upstream' / ('Xray-core-' + pin['commit'] + '-source.tar.gz')
    if hashlib.sha256(archive.read_bytes()).hexdigest() != pin['sourceSha256']:
        raise ValueError('Pinned Xray source checksum mismatch')
    go = shutil.which('go')
    if not go or subprocess.check_output([go, 'version'], text=True).split()[2] != pin['goVersion']:
        raise ValueError('Build requires the pinned ' + pin['goVersion'])
    cache = ROOT / '.build-cache/xray-source'
    cache.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive) as source:
        source.extractall(cache, filter='data')
    folder = cache / ('Xray-core-' + pin['commit'])
    destination.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, GOOS=target, GOARCH='amd64', CGO_ENABLED='0')
    subprocess.run([go, 'build', '-buildvcs=false', '-trimpath', '-ldflags=-s -w', '-o',
                    str((destination / ('xray.exe' if target == 'windows' else 'xray')).resolve()), './main'],
                   cwd=folder, env=env, check=True, timeout=600)
    shutil.copy2(ROOT / 'upstream/xray-experiment.json', destination / 'core-version.json')
    if target == 'windows':
        with urllib.request.urlopen(WINTUN_URL, timeout=60) as response:
            data = response.read(2 * 1024 * 1024 + 1)
        if hashlib.sha256(data).hexdigest() != WINTUN_SHA:
            raise ValueError('Wintun checksum mismatch')
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            (destination / 'wintun.dll').write_bytes(z.read('wintun/bin/amd64/wintun.dll'))
    print('PASS official Xray source + signed Wintun: ' + pin['commit'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('destination', type=Path)
    parser.add_argument('--target', choices=['windows', 'linux'], default='windows')
    args = parser.parse_args()
    build(args.destination.resolve(), args.target)
