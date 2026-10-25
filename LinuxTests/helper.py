#!/usr/bin/python3
"""Validation tests; no host network operations."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

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

    def test_only_bundled_external_geofiles_are_allowed(self):
        for value in ('ext:geoip.dat:private', 'ext:geosite.dat:category-ads-all'):
            request = self.envelope()
            request['config']['routing'] = {'rules': [{'domain': [value]}]}
            helper.validate(request)
        for value in ('ext:/etc/shadow:x', 'ext:../../geoip.dat:private', 'ext:other.dat:x', 'ext:geoip.dat:'):
            with self.subTest(value=value):
                request = self.envelope()
                request['config']['routing'] = {'rules': [{'domain': [value]}]}
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


class Disconnect(unittest.TestCase):
    def control(self, blocks, exited=False):
        child = Mock()
        child.poll.return_value = 1 if exited else None
        selector = Mock()
        selector.select.return_value = [True]
        with patch.object(helper.selectors, 'DefaultSelector') as factory, \
                patch.object(helper.os, 'read', side_effect=blocks):
            factory.return_value.__enter__.return_value = selector
            return helper.wait_for_stop(child)

    def test_only_complete_explicit_stop_releases_guard(self):
        self.assertTrue(self.control([b'st', b'op\n']))
        for blocks in ([b''], [b'stop', b''], [b'anything\n'], [b'stop\nextra'], [b'x' * 64]):
            with self.subTest(blocks=blocks):
                self.assertFalse(self.control(blocks))

    def test_core_crash_is_not_a_successful_disconnect(self):
        self.assertFalse(self.control([], exited=True))


class Cleanup(unittest.TestCase):
    def test_guard_query_failure_preserves_recovery_receipt(self):
        def command(*args, optional=False, **kwargs):
            if optional:
                return subprocess.CompletedProcess(args, 1, b'', b'Operation not permitted')
            raise subprocess.CalledProcessError(1, args)
        with tempfile.TemporaryDirectory(prefix='kot-guard-') as directory:
            marker = Path(directory) / 'guard'
            marker.write_text('active')
            with patch.object(helper, 'STATE', Path(directory)), patch.object(helper, 'command', command):
                with self.assertRaises(subprocess.CalledProcessError):
                    helper.release_guard()
                self.assertTrue(marker.exists())

    def test_absent_guard_can_be_recovered(self):
        def command(*args, **kwargs):
            missing = 'table' in args
            return subprocess.CompletedProcess(args, int(missing), b'{"nftables":[]}', b'')
        with tempfile.TemporaryDirectory(prefix='kot-guard-') as directory:
            marker = Path(directory) / 'guard'
            marker.write_text('active')
            with patch.object(helper, 'STATE', Path(directory)), patch.object(helper, 'command', command):
                helper.release_guard()
                self.assertFalse(marker.exists())

    def test_failed_rule_removal_preserves_network_receipt(self):
        def command(*args, **kwargs):
            if 'del' in args:
                return subprocess.CompletedProcess(args, 1, b'', b'Operation not permitted')
            output = b'[{"priority":10782,"table":47811}]' if 'rule' in args else b'[]'
            return subprocess.CompletedProcess(args, 0, output, b'')
        with tempfile.TemporaryDirectory(prefix='kot-cleanup-') as directory:
            receipt = Path(directory) / 'network'
            receipt.write_text('{"uid":12345,"ifindex":42}')
            with patch.object(helper, 'STATE', Path(directory)), patch.object(helper, 'command', command):
                with self.assertRaisesRegex(RuntimeError, 'rules'):
                    helper.cleanup_network()
                self.assertTrue(receipt.exists())

    def test_successful_cleanup_removes_network_receipt(self):
        with tempfile.TemporaryDirectory(prefix='kot-cleanup-') as directory:
            receipt = Path(directory) / 'network'
            receipt.write_text('{"uid":12345,"ifindex":42}')
            result = subprocess.CompletedProcess([], 0, b'[]', b'')
            with patch.object(helper, 'STATE', Path(directory)), patch.object(helper, 'command', return_value=result):
                helper.cleanup_network()
                self.assertFalse(receipt.exists())


if __name__ == '__main__':
    unittest.main()
