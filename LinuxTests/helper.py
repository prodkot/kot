#!/usr/bin/python3
"""Validation tests; no host network operations."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('helper', Path(__file__).resolve().parents[1] / 'Linux/network-helper.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class Validation(unittest.TestCase):
    def envelope(self):
        return {'guard': True, 'config': {'log': {'loglevel': 'warning'}, 'inbounds': [
            {'protocol': 'tun', 'settings': {'name': 'kot-tun', 'mtu': 1500}},
            {'protocol': 'http', 'listen': '127.0.0.1', 'port': 19441}],
            'outbounds': [{'protocol': 'freedom'}]}}

    def test_valid_tun(self):
        helper.validate(self.envelope())

    def test_no_arbitrary_root_operation(self):
        for key in ('command', 'executable', 'path', 'uid', 'nft', 'interface'):
            with self.subTest(key=key):
                request = self.envelope()
                request[key] = '/bin/sh'
                with self.assertRaises(ValueError):
                    helper.validate(request)

    def test_listener_and_config_validation(self):
        mutations = [
            lambda c: c['inbounds'][1].update(listen='0.0.0.0'),
            lambda c: c['inbounds'][1].update(port=80),
            lambda c: c['inbounds'][0]['settings'].update(name='eth0'),
            lambda c: c['inbounds'][0]['settings'].update(autoSystemWfpBlockLeak=['dns']),
            lambda c: c.update(api={}),
            lambda c: c['log'].update(error='/etc/shadow'),
            lambda c: c['outbounds'][0].update(certificateFile='/etc/shadow'),
            lambda c: c['outbounds'][0].update(streamSettings={'sockopt': {'mark': 123}}),
        ]
        for mutate in mutations:
            request = self.envelope()
            mutate(request['config'])
            with self.assertRaises(ValueError):
                helper.validate(request)

    def test_assets_not_paths(self):
        request = self.envelope()
        request['assets'] = {'../../etc/shadow': ''}
        with self.assertRaises(ValueError):
            helper.validate(request)


class RoutingPolicy(unittest.TestCase):
    def test_only_core_uid_bypasses_tun_in_both_families(self):
        calls = []

        def command(*args, **kwargs):
            calls.append(args)
            if args == ('/usr/sbin/ip', 'link', 'show', 'kot-tun'):
                return subprocess.CompletedProcess(args, 1, b'', b'')
            output = b'[{"ifindex":42}]' if 'link' in args and '-j' in args else b'[]'
            return subprocess.CompletedProcess(args, 0, output, b'')

        with tempfile.TemporaryDirectory(prefix='kot-policy-') as directory:
            with patch.object(helper, 'STATE', Path(directory)), patch.object(helper, 'command', command), \
                    patch.object(helper.os, 'open', return_value=100), patch.object(helper.fcntl, 'ioctl'):
                self.assertEqual(helper.prepare(12345), 100)
                for family in ('-4', '-6'):
                    self.assertIn(('/usr/sbin/ip', family, 'rule', 'add', 'pref', '10780',
                                   'uidrange', '12345-12345', 'lookup', 'main'), calls)
                    self.assertIn(('/usr/sbin/ip', family, 'rule', 'add', 'pref', '10782',
                                   'lookup', helper.TABLE), calls)
                self.assertFalse(any('suppress_prefixlength' in args for args in calls))
                self.assertEqual(json.loads((Path(directory) / 'network').read_text())['ifindex'], 42)


if __name__ == '__main__':
    unittest.main()
