"""Keep runtime files and required notices in the installed distribution."""
from pathlib import Path
import argparse
import json
import shutil


def prepare(payload: Path, source: Path):
    notices = payload / 'licenses'
    shutil.copytree(source / 'licenses', notices, dirs_exist_ok=True)
    shutil.copy2(source / 'LICENSE.txt', notices / 'kot-LICENSE.txt')
    shutil.copy2(source / 'THIRD-PARTY.txt', notices / 'THIRD-PARTY.txt')
    for name in json.loads((source / 'Release/legacy-documents.json').read_text()):
        path = payload / name
        if path.is_file():
            path.unlink()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('payload', type=Path)
    args = parser.parse_args()
    prepare(args.payload, Path(__file__).resolve().parent.parent)
