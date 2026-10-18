"""Bind the QA kit to the exact already-built installer and installed payload."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import zipfile

root = Path(__file__).resolve().parent.parent

def build(publish, setup, output):
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    folder = output / 'qa-kit'
    folder.mkdir(parents=True, exist_ok=False)
    for name in ('Start-Checks.cmd', 'Checks.ps1', 'ProcessGuard.cs', 'README-ru.txt'):
        # Windows PowerShell 5.1 needs a BOM for Cyrillic script literals.
        text = (root / 'QA' / name).read_text(encoding='utf-8-sig')
        (folder / name).write_text(text, encoding='utf-8-sig' if name.endswith('.ps1') else 'utf-8', newline='\r\n')
    sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
    pin = json.loads((root / 'upstream/xray-experiment.json').read_text())
    manifest = {'schema': 1, 'commit': commit, 'version': '0.8.0', 'coreCommit': pin['commit'],
                'coreVersion': '26.10.10', 'goVersion': pin['goVersion'],
                'setup': {'name': setup.name, 'sha256': sha(setup)},
                'files': {p.relative_to(publish).as_posix(): sha(p) for p in sorted(publish.rglob('*')) if p.is_file()}}
    (folder / 'expected.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    target = output / ('Kot-QA-' + commit[:12] + '.zip')
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(folder.iterdir()):
            archive.write(path, path.name)
    (output / 'candidate.json').write_text(json.dumps({**{k: manifest[k] for k in ('commit','version','coreCommit')},
        'setup': manifest['setup'], 'qa': {'name': target.name, 'sha256': sha(target)}}, indent=2), encoding='utf-8')
    print('PASS QA kit bound to ' + commit)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--publish', type=Path, required=True)
    parser.add_argument('--setup', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    build(args.publish.resolve(), args.setup.resolve(), args.output.resolve())
