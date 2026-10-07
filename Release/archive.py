"""Write only payload files with portable paths accepted by the updater."""
from pathlib import Path
import argparse
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
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for file in sorted(payload.rglob('*')):
        if file.is_file():
            archive.write(file, file.relative_to(payload).as_posix())
