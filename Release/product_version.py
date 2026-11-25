"""Read the same public version fields as MSBuild, independently of CI run IDs."""
import re
from xml.etree import ElementTree


def read(root):
    fields = ElementTree.parse(root / 'Directory.Build.props').getroot().find('PropertyGroup')
    base = fields.findtext('VersionPrefix')
    channel = fields.findtext('KotChannel') or ''
    number = fields.findtext('KotPrereleaseNumber') or ''
    if not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', base or ''):
        raise ValueError('Invalid application version')
    if not channel and not number:
        return base, base, base
    if channel not in ('Dev', 'Beta', 'RC') or not re.fullmatch(r'[1-9][0-9]*', number):
        raise ValueError('Invalid prerelease channel or number')
    return base, f'{base}-{channel.lower()}.{number}', f'{base} {channel} {number}'
