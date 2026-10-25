"""Exercise the publication guard against real, isolated Git histories."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('release_ref', Path(__file__).resolve().parents[1] / 'Release/check-ref.py')
release_ref = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_ref)


class ReleaseReference(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='kot-release-ref-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git('init', '-b', 'main')
        (self.root / 'Core').mkdir()
        (self.root / 'Core/ClientIdentity.cs').write_text('public const string Version = "0.8.0";')
        self.git('add', '.')
        self.git('-c', 'user.name=Release fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'main')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        self.tag = {'GITHUB_REF_TYPE': 'tag', 'GITHUB_REF_NAME': 'v0.8.0'}
        self.manual = {'GITHUB_REF_TYPE': 'branch', 'GITHUB_REF': 'refs/heads/main', 'PUBLISH_RELEASE': 'true'}

    def git(self, *args):
        return subprocess.check_output(['git', *args], cwd=self.root, stderr=subprocess.STDOUT,
                                       env=dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM='1'),
                                       timeout=15).decode().strip()

    def fork(self):
        self.git('switch', '-c', 'candidate')
        self.git('-c', 'user.name=Release fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--allow-empty', '-m', 'unmerged')

    def test_tag_on_main(self):
        self.assertEqual(release_ref.check(self.root, self.tag), '0.8.0')

    def test_tag_on_an_earlier_main_commit(self):
        previous = self.git('rev-parse', 'HEAD')
        self.git('-c', 'user.name=Release fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--allow-empty', '-m', 'new main')
        self.git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        self.git('checkout', '--detach', previous)
        self.assertEqual(release_ref.check(self.root, self.tag), '0.8.0')

    def test_tag_on_unmerged_branch_is_rejected(self):
        self.fork()
        with self.assertRaisesRegex(ValueError, 'origin/main'):
            release_ref.check(self.root, self.tag)

    def test_tag_must_match_version(self):
        with self.assertRaisesRegex(ValueError, 'version disagree'):
            release_ref.check(self.root, dict(self.tag, GITHUB_REF_NAME='v0.8.1'))

    def test_manual_main_publication(self):
        self.assertEqual(release_ref.check(self.root, self.manual), '0.8.0')

    def test_manual_publication_from_another_branch_is_rejected(self):
        self.fork()
        with self.assertRaisesRegex(ValueError, 'only allowed from main'):
            release_ref.check(self.root, dict(self.manual, GITHUB_REF='refs/heads/candidate'))

    def test_candidate_build_on_unmerged_branch_is_allowed(self):
        self.fork()
        self.assertEqual(release_ref.check(self.root, {'GITHUB_REF_TYPE': 'branch',
                                                       'GITHUB_REF': 'refs/heads/candidate', 'PUBLISH_RELEASE': 'false'}), '0.8.0')

    def test_missing_main_history_is_rejected(self):
        self.git('update-ref', '-d', 'refs/remotes/origin/main')
        with self.assertRaisesRegex(ValueError, 'origin/main'):
            release_ref.check(self.root, self.tag)


if __name__ == '__main__':
    unittest.main()
