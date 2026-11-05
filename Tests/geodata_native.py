"""Cross-check the exact Core-accepted schema fixtures with the pinned Xray."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory

parser = argparse.ArgumentParser()
parser.add_argument('--core', required=True, type=Path)
args = parser.parse_args()
core = args.core.resolve()
fixtures = Path(__file__).resolve().parent / 'generated/geodata'
config = json.loads((fixtures / 'config.json').read_text())
config['routing']['rules'].append({'type':'field', 'ip':['geoip:ru'], 'outboundTag':'proxy'})
with TemporaryDirectory(prefix='kot-geodata-native-') as folder:
    assets = Path(folder)
    for name in ['geoip.dat', 'geosite.dat']:
        shutil.copy2(fixtures / name, assets / name)
    def run():
        return subprocess.run([str(core), 'run', '-test', '-format', 'json', '-c', 'stdin:'],
            input=json.dumps(config), text=True, capture_output=True, timeout=15,
            env={**os.environ, 'XRAY_LOCATION_ASSET':str(assets)})
    valid = run()
    assert valid.returncode == 0, valid.stdout + valid.stderr
    print('PASS native Xray accepts Core-validated GeoSite and CIDR structures')
    for name in ['geosite.dat', 'geoip.dat']:
        (assets / name).write_bytes(bytes([10, 2, 255, 255]))
        rejected = run()
        assert rejected.returncode == 23, rejected.stdout + rejected.stderr
        print('PASS native Xray rejects the nested corruption denied by Core: ' + name)
        shutil.copy2(fixtures / name, assets / name)
    assert run().returncode == 0
    print('PASS native validation recovers with valid bytes at the same asset paths')
