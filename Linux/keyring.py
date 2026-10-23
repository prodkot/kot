#!/usr/bin/python3
"""Secret Service only. No filesystem/environment/plaintext fallback."""
import base64
import os
import sys
import gi
gi.require_version('Secret', '1')
from gi.repository import Secret

try:
    schema = Secret.Schema.new('io.github.prodkot.kot', Secret.SchemaFlags.NONE,
                               {'purpose': Secret.SchemaAttributeType.STRING})
    attrs = {'purpose': 'profile-key-v1'}
    key = Secret.password_lookup_sync(schema, attrs, None)
    if key is None:
        # Never replace a missing key when an encrypted profile already exists.
        if sys.argv[1:] != ['create']:
            raise RuntimeError('key missing')
        key = base64.b64encode(os.urandom(32)).decode('ascii')
        if not Secret.password_store_sync(schema, attrs, Secret.COLLECTION_DEFAULT,
                                          'kot. — ключ профиля', key, None):
            raise RuntimeError('keyring locked')
    if len(base64.b64decode(key, validate=True)) != 32:
        raise RuntimeError('invalid key')
    print(key)
except Exception:
    print('Secret Service недоступен, заблокирован или ключ отсутствует. Разблокируйте связку ключей; профиль не изменён.', file=sys.stderr)
    sys.exit(1)
