#!/usr/bin/env python3
"""Publish an explicit preview only; never replace companion-latest or update manifests."""
from pathlib import Path
import hashlib
import shutil
import subprocess
import sys
import zipfile
from qa import ROOT, check_push_target, git, push, run


def main():
    check_push_target(git('branch', '--show-current'), git('status', '--porcelain'))
    push()  # Full gate (or exact recent receipt), then push the tested commit.
    head = git('rev-parse', 'HEAD')
    short = head[:12]
    tag = 'plex-repair-preview-' + short
    output = ROOT / 'artifacts/previews' / short
    output.mkdir(parents=True, exist_ok=True)
    notes = f'''Preview build from {head}. Windows compilation and disposable Linux Plex/Jellyfin
checks passed. Windows/WinFsp, the friend's actual Funnel and real Plex clients still
need testing. This is not the stable rolling release and does not update the plugin
catalog. Existing assembly versions are retained for this manually installed preview.

Windows: install WinFsp and place rclone.exe beside Companion or on PATH. Extract into
a separate test folder, run under the same Windows account as Plex, choose libraries,
start the media mount and Add to Plex. The source Jellyfin also needs the preview DLL.

Source Jellyfin: back up the current plugin folder, stop the test server, replace its
Federation DLL with the preview DLL, and restart. Do not run duplicate Federation DLLs.
Keep your existing private configuration. Start with a test server before production.

Do not send companion-state.json, media-mount.conf or owner keys in bug reports.
See Companion-setup.md and the repository TODO for validation scope and limitations.
'''
    windows = output / 'FederationCompanion-win-x64-preview.zip'
    source = ROOT / 'artifacts/qa/windows'
    if not (source / 'FederationCompanion.exe').is_file():
        raise RuntimeError('Windows artifact missing; run test.sh --all without --reuse.')
    with zipfile.ZipFile(windows, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(source.rglob('*')):
            if path.is_file() and path.suffix != '.pdb':
                if path.name in ('companion-state.json', 'media-mount.conf') or path.suffix == '.log':
                    raise RuntimeError('Private runtime file found in publish output; refusing packaging.')
                archive.write(path, path.relative_to(source))
        archive.writestr('PREVIEW.txt', notes)
        archive.write(ROOT / 'Companion/README.md', 'Companion-setup.md')
    plugin = output / 'Jellyfin-Federation-preview.zip'
    with zipfile.ZipFile(plugin, 'w', zipfile.ZIP_DEFLATED) as archive:
        archive.write(ROOT / 'bin/Release/net9.0/Jellyfin.Plugin.Federation.dll', 'Jellyfin.Plugin.Federation.dll')
        archive.writestr('PREVIEW.txt', notes)
    checksum = output / 'SHA256SUMS.txt'
    checksum.write_text(''.join(hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.name + '\n' for path in [windows, plugin]))
    release_notes = output / 'release-notes.md'
    release_notes.write_text('''Fixes third-party media being shared onward, wrong-server Downloads results, missing
Plex media information and native `.strm` playback assumptions. Includes explicit
library selection, managed media mounting, diagnostics and safer cleanup.

Validation: automated plugin/Companion/UI suites twice, Windows cross-build, and
disposable real Jellyfin → Companion → rclone → Plex analysis/decode, ranges,
ownership revocation, source outage and mount restart. Three push-gate regression
tests also pass. The full Windows/Funnel/client release matrix is still outstanding.

**Preview only:** this does not replace companion-latest or change the plugin catalog.
Read PREVIEW.txt before installing. The Windows build needs WinFsp + rclone and the
source server needs the included plugin preview. Assembly versions are unchanged.

Future checks/pushes: `scripts/test.sh --all`, `scripts/push.sh`, and
`scripts/release-preview.sh`. Matching recent full checks are reused automatically.
''')
    existing = subprocess.run(['gh', 'release', 'view', tag], cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if existing.returncode == 0:
        print('Preview already exists:', tag)
        return
    run(['gh', 'release', 'create', tag, windows, plugin, checksum, '--target', head,
         '--prerelease', '--latest=false', '--title', 'Plex federation repair preview ' + short,
         '--notes-file', release_notes])
    run(['gh', 'release', 'view', tag, '--json', 'url', '--jq', '.url'])


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        print('FAIL:', error, file=sys.stderr); sys.exit(1)
