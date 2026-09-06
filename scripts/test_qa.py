import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from qa import check_push_target, fingerprint, reusable


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


if __name__ == '__main__':
    unittest.main()
