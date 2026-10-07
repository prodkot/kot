"""Generate explicit uninstall instructions. Never recursively delete user folders."""
from pathlib import Path
import argparse
import json
p=argparse.ArgumentParser();p.add_argument('payload',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
files=sorted(f.relative_to(a.payload) for f in a.payload.rglob('*') if f.is_file())
dirs=sorted({d for f in files for d in f.parents if str(d)!='.'},key=lambda d:len(d.parts),reverse=True)
def q(s):return str(s).replace('\\','/').replace('$','$$').replace('"','$\\"').replace('/','\\')
a.output.write_text('\n'.join(['  Delete "$INSTDIR\\'+q(f)+'"' for f in files]+['  RMDir "$INSTDIR\\'+q(d)+'"' for d in dirs])+'\n',encoding='utf-8')
legacy=json.loads((Path(__file__).parent/'legacy-documents.json').read_text())
if any('/' in n or '\\' in n or '$' in n or '"' in n or '*' in n or '?' in n for n in legacy):
    raise SystemExit('Cleanup only accepts exact root file names')
a.output.with_name('install-cleanup.nsh').write_text('\n'.join('  Delete "$INSTDIR\\'+q(n)+'"' for n in legacy)+'\n',encoding='utf-8')
