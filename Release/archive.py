"""Write only payload files with relative paths accepted by the updater."""
from pathlib import Path
import argparse
import shutil
import subprocess
import tempfile
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('payload', type=Path)
parser.add_argument('output', type=Path)
args = parser.parse_args()
payload = args.payload.resolve(strict=True)
output = args.output.resolve()
if output.is_relative_to(payload):
    raise SystemExit('Keep the archive outside its payload.')
output.parent.mkdir(parents=True, exist_ok=True)
files = sorted(p.relative_to(payload).as_posix() for p in payload.rglob('*') if p.is_file())
# A fresh archive and a file-only list preserve the signed updater's exact inventory.
with tempfile.TemporaryDirectory(dir=output.parent) as scratch:
    target = Path(scratch) / 'payload.zip'
    seven_zip = shutil.which('7z')
    if seven_zip:
        names = Path(scratch) / 'files.txt'
        names.write_text('\n'.join(files) + '\n', encoding='utf-8')
        subprocess.run([seven_zip, 'a', '-tzip', '-mm=Deflate', '-mx=9', '-mfb=258',
                        '-mpass=15', '-scsUTF-8', '-spd', str(target), '@' + str(names)],
                       cwd=payload, check=True, timeout=600)
    else:
        with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in files:
                archive.write(payload / name, name)
    target.replace(output)
