"""Regression: only pinned archive members may enter the Go build tree."""
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('xray_build', Path(__file__).resolve().parents[1] / 'Release/xray-core.py')
xray = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xray)


class SourceTree(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='kot-source-tree-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.commit = 'a' * 40
        self.cache = self.root / 'cache'
        self.folder = self.cache / ('Xray-core-' + self.commit)
        self.archive = self.root / 'source.tar.gz'
        with tarfile.open(self.archive, 'w:gz') as archive:
            data = b'package main\n'
            info = tarfile.TarInfo('Xray-core-' + self.commit + '/main/main.go')
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))

    def test_extra_go_source_cannot_survive_repeated_builds(self):
        (self.folder / 'main').mkdir(parents=True)
        (self.folder / 'main/injected.go').write_text('package main\nfunc init() { panic("stale") }')
        (self.folder / 'main/main.go').write_text('modified cached source')
        with patch.object(xray, 'ROOT', self.root):
            result = xray.extract_source(self.archive, self.cache, self.commit)
            self.assertEqual(result, self.folder)
            self.assertEqual((result / 'main/main.go').read_bytes(), b'package main\n')
            self.assertFalse((result / 'main/injected.go').exists())
            xray.extract_source(self.archive, self.cache, self.commit)
            self.assertEqual(list((result / 'main').iterdir()), [result / 'main/main.go'])

    def test_external_cache_is_rejected_before_deletion(self):
        with patch.object(xray, 'ROOT', self.root / 'other'):
            with self.assertRaisesRegex(ValueError, 'workspace'):
                xray.extract_source(self.archive, self.cache, self.commit)
        self.assertFalse(self.cache.exists())

    def test_pin_cannot_escape_cache(self):
        with patch.object(xray, 'ROOT', self.root):
            with self.assertRaisesRegex(ValueError, 'commit'):
                xray.extract_source(self.archive, self.cache, '../outside')


if __name__ == '__main__':
    unittest.main()
