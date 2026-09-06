#!/usr/bin/env python3
"""Download a pinned rclone binary into a Companion publish folder.

Keep VERSION / ARCHIVES in sync with Companion/RcloneBootstrapper.cs.
"""
from __future__ import annotations

import hashlib
import io
import sys
import urllib.request
import zipfile
from pathlib import Path

VERSION = '1.75.1'
BASE = f'https://downloads.rclone.org/v{VERSION}'
ARCHIVES = {
    'win-x64': ('windows-amd64', 'rclone.exe', '200eb602c126d82aa38b51e0f6b9ae837473ff99b51278d3f6f837574c494d6e'),
    'linux-x64': ('linux-amd64', 'rclone', '982b5aa772841168f8e380f139e9e787b2a105403e32b94da8676a0e1c0a13ab'),
    'osx-x64': ('osx-amd64', 'rclone', '29253d0288b8fbbac46baad6e5f6add6cb01d462c79f10805bbd4631c4cdf82c'),
    'osx-arm64': ('osx-arm64', 'rclone', 'c61d7a371c62bcbbe882c3423aa4b8bf63485c248dd0f692997b8f0c3f6d0c6f'),
}
MAX_BYTES = 80 * 1024 * 1024


def bundle(rid: str, dest: str) -> Path:
    if rid not in ARCHIVES:
        raise SystemExit(f'Unknown Companion RID {rid}')
    os_arch, binary, expected = ARCHIVES[rid]
    name = f'rclone-v{VERSION}-{os_arch}.zip'
    url = f'{BASE}/{name}'
    request = urllib.request.Request(url, headers={'User-Agent': 'FederationCompanion-bundle/1.75.1'})
    with urllib.request.urlopen(request, timeout=120) as response:
        data = response.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise SystemExit(f'{name} exceeded the size limit')
    digest = hashlib.sha256(data).hexdigest()
    if digest != expected:
        raise SystemExit(f'sha256 mismatch for {name}: {digest}')
    target_dir = Path(dest)
    target_dir.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for info in archive.infolist():
            if info.is_dir():
                continue
            normalized = info.filename.replace('\\', '/')
            if any(part == '..' for part in normalized.split('/')):
                continue
            file_name = Path(normalized).name.lower()
            if file_name not in ('rclone', 'rclone.exe'):
                continue
            if info.file_size > MAX_BYTES:
                raise SystemExit('rclone entry exceeded the size limit')
            target = target_dir / binary
            target.write_bytes(archive.read(info))
            target.chmod(0o755)
            return target
    raise SystemExit(f'{name} did not contain an rclone binary')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit('usage: bundle_rclone.py <rid> <dest-dir>')
    print('bundled', bundle(sys.argv[1], sys.argv[2]))
