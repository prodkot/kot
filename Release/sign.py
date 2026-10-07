"""Sign a published kot. directory. Private key must stay outside the release tree."""
import argparse, hashlib, json, subprocess
from pathlib import Path
p=argparse.ArgumentParser()
p.add_argument('folder', type=Path)
p.add_argument('version')
p.add_argument('--key', required=True, type=Path)
a=p.parse_args()
folder=a.folder.resolve(); key=a.key.resolve()
if key.is_relative_to(folder): raise SystemExit('Private key cannot be inside the published directory')
files={str(f.relative_to(folder)).replace('\\','/'):hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(folder.rglob('*')) if f.is_file() and f.name not in ('release.json','release.sig')}
for name in ('Kot.exe','core/sing-box.exe'):
    if name not in files: raise SystemExit('Missing release file: '+name)
manifest=folder/'release.json'
manifest.write_bytes(json.dumps({'version':a.version,'files':files},ensure_ascii=False,separators=(',',':')).encode('utf-8'))
subprocess.run(['openssl','dgst','-sha256','-sign',str(key),'-out',str(folder/'release.sig'),str(manifest)],check=True)
print('Signed '+a.version+': '+str(len(files))+' files')
