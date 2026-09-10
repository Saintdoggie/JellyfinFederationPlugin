"""Run the Linux installer with local release fixtures; no network or live app."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]

class CompanionInstallerTests(unittest.TestCase):
    def install(self, bad_hash=False, arch='x86_64'):
        work = Path(self.enterContext(tempfile.TemporaryDirectory()))
        fixture = work / 'release'; fixture.mkdir()
        asset = fixture / 'FederationCompanion-linux-x64.zip'
        with zipfile.ZipFile(asset, 'w') as archive:
            archive.writestr('FederationCompanion', '#!/bin/sh\nexit 0\n')
        digest = '0' * 64 if bad_hash else hashlib.sha256(asset.read_bytes()).hexdigest()
        (fixture / 'SHA256SUMS').write_text(digest + '  ' + asset.name + '\n')
        helpers = work / 'bin'; helpers.mkdir()
        (helpers / 'curl').write_text('''#!/bin/bash
while [[ $# -gt 0 ]]; do
 if [[ "$1" == -o ]]; then target="$2"; shift 2; else url="$1"; shift; fi
done
cp "$REVIEW_FIXTURE/${url##*/}" "$target"
''')
        (helpers / 'uname').write_text('#!/bin/sh\ncase "$1" in -s) echo Linux;; -m) echo "$REVIEW_ARCH";; esac\n')
        for p in helpers.iterdir(): p.chmod(0o755)
        install = work / 'space $dollar `literal` 100%files "quoted"'
        env = dict(os.environ, PATH=str(helpers)+':'+os.environ['PATH'], REVIEW_FIXTURE=str(fixture), REVIEW_ARCH=arch,
                   FEDERATION_COMPANION_DIR=str(install), XDG_DATA_HOME=str(work/'data'), DISPLAY='', WAYLAND_DISPLAY='')
        result = subprocess.run(['bash', str(ROOT/'Companion/install.sh')], env=env, text=True, capture_output=True)
        return result, work, install, env

    def test_headless_install_verifies_archive_and_creates_valid_launcher(self):
        result, work, install, env = self.install()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(os.access(install/'FederationCompanion', os.X_OK))
        launcher = work/'data/applications/federation-companion.desktop'
        self.assertTrue(launcher.is_file())
        self.assertIn('%%files', launcher.read_text())
        if shutil.which('desktop-file-validate'):
            checked = subprocess.run(['desktop-file-validate', str(launcher)], capture_output=True, text=True)
            self.assertEqual(0, checked.returncode, checked.stdout+checked.stderr)
        again = subprocess.run(['bash', str(ROOT/'Companion/install.sh')], env=env, capture_output=True)
        self.assertNotEqual(0, again.returncode)
        self.assertTrue((install/'FederationCompanion').exists())

    def test_checksum_mismatch_leaves_installation_untouched(self):
        result, _, install, _ = self.install(bad_hash=True)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(install.exists())

    def test_unsupported_architecture_is_rejected(self):
        result, _, install, _ = self.install(arch='aarch64')
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(install.exists())
