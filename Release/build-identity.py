"""Create one identity per package; reuse it in every component of that package."""
import datetime
import json
import os
from pathlib import Path
import re
import secrets
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def build_id(env, now=None, token=None):
    if env.get('GITHUB_ACTIONS') == 'true':
        channels = {'Windows development build': 'dev', 'Windows release': 'release'}
        channel = channels.get(env.get('GITHUB_WORKFLOW'))
        number, attempt = env.get('GITHUB_RUN_NUMBER', ''), env.get('GITHUB_RUN_ATTEMPT', '')
        if not channel or not all(re.fullmatch(r'[1-9][0-9]{0,8}', n) for n in (number, attempt)):
            raise ValueError('Missing or invalid CI package identity')
        return f'{channel}.{number}.{attempt}'
    now = now or datetime.datetime.now(datetime.timezone.utc)
    return 'local.' + now.strftime('%Y%m%d%H%M%S') + '.' + (token or secrets.token_hex(4))


def identity():
    version = re.search(r'Version\s*=\s*"(\d+\.\d+\.\d+)"', (ROOT / 'Core/ClientIdentity.cs').read_text()).group(1)
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid source revision')
    build = build_id(os.environ)
    return {'schema': 1, 'version': version, 'build': build, 'displayVersion': f'{version} ({build})',
            'commit': commit, 'builtAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat()}


if __name__ == '__main__':
    print(json.dumps(identity()))
