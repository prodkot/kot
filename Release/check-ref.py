"""Allow release candidates anywhere, publication only from main history."""
import os
from pathlib import Path
import re
import subprocess
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from product_version import read

ROOT = Path(__file__).resolve().parents[1]


def check(root, environment):
    version, public, _ = read(root)
    tag = environment.get('GITHUB_REF_TYPE') == 'tag'
    publish = environment.get('PUBLISH_RELEASE') == 'true'
    if tag and environment.get('GITHUB_REF_NAME') != 'v' + version:
        raise ValueError('Tag and application version disagree.')
    if publish and not tag and environment.get('GITHUB_REF') != 'refs/heads/main':
        raise ValueError('Manual publication is only allowed from main.')
    if tag or publish:
        if public != version:
            raise ValueError("A prerelease cannot be published to the stable channel.")
        result = subprocess.run(['git', 'merge-base', '--is-ancestor', 'HEAD', 'refs/remotes/origin/main'],
                                cwd=root, capture_output=True, timeout=15)
        if result.returncode:
            raise ValueError('Release commit must be in origin/main history; fetch the complete main branch first.')
    return version


if __name__ == '__main__':
    try:
        print('PASS release reference: ' + check(ROOT, os.environ))
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
