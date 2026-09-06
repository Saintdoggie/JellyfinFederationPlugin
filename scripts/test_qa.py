import json
import re
from pathlib import Path
import subprocess
import tempfile
import unittest
from qa import ROOT, check_push_target, fingerprint, reusable
from bundle_rclone import ARCHIVES, MAX_BINARY_BYTES, VERSION


class PushGateTests(unittest.TestCase):
    def test_protected_or_detached_branch_and_dirty_tree_are_rejected(self):
        for branch, status in [('master', ''), ('main', ''), ('', ''), ('fix/media', ' M file')]:
            with self.subTest(branch=branch, status=status), self.assertRaises(RuntimeError):
                check_push_target(branch, status)
        check_push_target('fix/media', '')

    def test_cache_requires_matching_scope_signature_success_and_recent_time(self):
        report = {'passed': True, 'signature': 'exact', 'scope': 'full', 'completedAt': 100}
        self.assertTrue(reusable(report, 'exact', 'full', now=101))
        for changed in [{'passed': False}, {'signature': 'different'}, {'scope': 'unit'}, {'completedAt': -90000}, {'completedAt': 200}]:
            self.assertFalse(reusable({**report, **changed}, 'exact', 'full', now=101))

    def test_fingerprint_includes_unstaged_and_untracked_files_but_not_artifacts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(['git', 'init', '-q', directory], check=True)
            (root / '.gitignore').write_text('artifacts/\n')
            (root / 'tracked').write_text('before')
            subprocess.run(['git', 'add', '.'], cwd=root, check=True)
            before = fingerprint(root)
            (root / 'tracked').write_text('after')
            self.assertNotEqual(before, fingerprint(root))
            changed = fingerprint(root)
            (root / 'new-test.py').write_text('new test')
            self.assertNotEqual(changed, fingerprint(root))
            final = fingerprint(root)
            (root / 'artifacts').mkdir(); (root / 'artifacts/result').write_text('generated')
            self.assertEqual(final, fingerprint(root))
            (root / 'tracked').unlink()
            self.assertNotEqual(final, fingerprint(root))

    def test_rclone_pin_matches_companion_bootstrapper(self):
        source = (ROOT / 'Companion/RcloneBootstrapper.cs').read_text()
        version = re.search(r'public const string Version = "([^"]+)"', source)
        self.assertEqual(VERSION, version.group(1))
        hashes = dict(re.findall(r'\["([^"]+)"\] = "([0-9a-f]{64})"', source))
        for os_arch, _binary, sha in ARCHIVES.values():
            self.assertEqual(sha, hashes[os_arch])
        workflow = (ROOT / '.github/workflows/companion-release.yml').read_text()
        self.assertIn('scripts/bundle_rclone.py', workflow)
        # rclone 1.75.1 windows-amd64 rclone.exe is 85,192,704 bytes uncompressed.
        self.assertGreater(MAX_BINARY_BYTES, 85_192_704)


if __name__ == '__main__':
    unittest.main()
