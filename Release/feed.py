"""Create a VPS update feed for an already signed kot. release ZIP."""
import argparse, json, zipfile
from pathlib import Path
from urllib.parse import urlparse
p=argparse.ArgumentParser()
p.add_argument('archive',type=Path)
p.add_argument('--url',required=True,help='Public HTTPS URL of this exact signed ZIP')
p.add_argument('--output',type=Path,default=Path('latest.json'))
a=p.parse_args();url=urlparse(a.url)
if url.scheme!='https' or not url.netloc or url.username or url.password or url.fragment:raise SystemExit('Use a public HTTPS archive URL without credentials')
with zipfile.ZipFile(a.archive) as z:
 if z.testzip() is not None:raise SystemExit('Damaged ZIP')
 if 'release.sig' not in z.namelist():raise SystemExit('Archive must be signed first')
 manifest=json.loads(z.read('release.json'))
 # This script does not replace cryptographic validation in the client.
 version=manifest['version']
a.output.write_text(json.dumps({'version':version,'url':a.url,'size':a.archive.stat().st_size},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Wrote update feed for '+version+': '+str(a.output))
