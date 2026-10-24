#!/usr/bin/env python3
"""Build an unsigned local .deb. No publisher signature verification is bypassed."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]


def run(args, **kwargs):
    subprocess.run(args, cwd=ROOT, check=True, timeout=900, **kwargs)


def build(output):
    pin = json.loads((ROOT / 'global.json').read_text())['sdk']['version']
    if subprocess.check_output(['dotnet', '--version'], cwd=ROOT, text=True).strip() != pin:
        raise ValueError('Build requires pinned .NET SDK ' + pin)
    if os.uname().machine != 'x86_64':
        raise ValueError('Only x86_64 is supported')
    # Each build has a fresh staging root; never package a previous user's files.
    output.mkdir(parents=True, exist_ok=True)
    staging = output / ('deb-root-' + str(time.time_ns()))
    app = staging / 'usr/lib/kot'
    app.mkdir(parents=True)
    run(['dotnet', 'publish', 'Linux/Kot.Linux.csproj', '-c', 'Release', '-r', 'linux-x64',
         '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:ContinuousIntegrationBuild=true',
         '-p:RestoreLockedMode=true', '-m:1', '-p:UseSharedCompilation=false', '-p:PathMap=' + str(ROOT) + '=/_/kot', '-o', str(app)])
    run(['python3', 'Release/xray-core.py', str(app / 'core'), '--target', 'linux'])
    shutil.copytree(ROOT / 'Windows/ui', app / 'ui', ignore=shutil.ignore_patterns('services.js'))
    for name in ('shell.py', 'keyring.py', 'network-helper.py'):
        shutil.copy2(ROOT / 'Linux' / name, app / name)
    (app / 'network-helper.py').chmod(0o755)
    for source, destination in (
        ('Linux/packaging/kot', 'usr/bin/kot'),
        ('Linux/packaging/io.github.prodkot.kot.desktop', 'usr/share/applications/io.github.prodkot.kot.desktop'),
        ('Linux/packaging/io.github.prodkot.kot.network.policy', 'usr/share/polkit-1/actions/io.github.prodkot.kot.network.policy'),
        ('Windows/ui/brand-icon.svg', 'usr/share/icons/hicolor/scalable/apps/io.github.prodkot.kot.svg'),
        ('LICENSE.txt', 'usr/share/doc/kot/copyright'),
        ('THIRD-PARTY.txt', 'usr/share/doc/kot/THIRD-PARTY.txt'),
        ('LINUX_PORT.md', 'usr/share/doc/kot/LINUX_PORT.md'),
        ('LINUX_TEST_REPORT.md', 'usr/share/doc/kot/LINUX_TEST_REPORT.md'),
        ('docs/LINUX.md', 'usr/share/doc/kot/LINUX.md'),
        ('docs/LINUX_AUDIT.md', 'usr/share/doc/kot/LINUX_AUDIT.md'),
    ):
        target = staging / destination
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / source, target)
    shutil.copytree(ROOT / 'licenses', staging / 'usr/share/doc/kot/licenses')
    (staging / 'usr/bin/kot').chmod(0o755)
    control = staging / 'DEBIAN'
    control.mkdir()
    size = sum(f.stat().st_size for f in staging.rglob('*') if f.is_file()) // 1024
    (control / 'control').write_text(f'''Package: kot
Version: 0.8.0~linux0.3
Section: net
Priority: optional
Architecture: amd64
Maintainer: kot. local Linux port <noreply@localhost>
Installed-Size: {size}
Depends: python3 (>= 3.12), python3-gi, gir1.2-gtk-3.0, gir1.2-webkit2-4.1, gir1.2-secret-1, libsecret-1-0, gsettings-desktop-schemas, dconf-gsettings-backend, pkexec, polkitd, iproute2, nftables, systemd-resolved, adduser, util-linux, libicu74 | libicu76 | libicu78 | libicu80, libssl3t64, libstdc++6, zlib1g, libgcc-s1
Recommends: gnome-keyring, gir1.2-ayatanaappindicator3-0.1
Homepage: https://github.com/prodkot/kot
Description: kot. Linux desktop VPN client (incomplete preview)
 Shared Russian UI and .NET Core with the pinned native Linux Xray.
 This local development build is not a publisher-signed update.
 See /usr/share/doc/kot/LINUX_TEST_REPORT.md for untested features.
''')
    for name in ('postinst', 'prerm'):
        shutil.copy2(ROOT / 'Linux/packaging' / name, control / name)
        (control / name).chmod(0o755)
    for path in staging.rglob('*'):
        if path.suffix.lower() == '.exe' or path.name.lower() in ('wintun.dll', 'webview2loader.dll', 'windivert.dll'):
            raise ValueError('Windows executable in Linux package: ' + str(path))
    # Stable metadata; dependency binaries/compilers are pinned separately.
    epoch = int(os.environ.get('SOURCE_DATE_EPOCH', '1792713600'))
    executables = {'usr/bin/kot', 'usr/lib/kot/Kot.Linux', 'usr/lib/kot/core/xray', 'usr/lib/kot/network-helper.py', 'DEBIAN/postinst', 'DEBIAN/prerm'}
    for path in [staging, *staging.rglob('*')]:
        path.chmod(0o755 if path.is_dir() or path.relative_to(staging).as_posix() in executables else 0o644)
        os.utime(path, (epoch, epoch), follow_symlinks=False)
    package = output / 'kot_0.8.0~linux0.3_amd64.deb'
    run(['dpkg-deb', '--root-owner-group', '-Zxz', '--build', str(staging), str(package)],
        env=dict(os.environ, SOURCE_DATE_EPOCH=str(epoch)))
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    (output / (package.name + '.sha256')).write_text(digest + '  ' + package.name + '\n')
    print('Artifact:', package)
    print('Extracted application:', app)
    return package


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/linux')
    args = parser.parse_args()
    build(args.output.resolve())
