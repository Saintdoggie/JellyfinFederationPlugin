#!/usr/bin/env python3
"""Repeatable local validation and guarded review-branch pushes. Standard library only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


def run(args, *, capture=False, cwd=ROOT, env=None):
    result = subprocess.run([str(a) for a in args], cwd=cwd, env=env, text=True,
                            stdout=subprocess.PIPE if capture else None, check=True)
    return result.stdout.strip() if capture else None


def git(*args, cwd=ROOT):
    return run(['git', *args], capture=True, cwd=cwd)


def fingerprint(root=ROOT):
    """Hash actual tracked and nonignored untracked contents, including unstaged edits."""
    names = subprocess.check_output(['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'], cwd=root)
    digest = hashlib.sha256()
    for raw in sorted(set(names.split(b'\0')) - {b''}):
        path = root / os.fsdecode(raw)
        digest.update(raw + b'\0')
        if path.is_symlink():
            digest.update(b'link:' + os.fsencode(os.readlink(path)))
        elif path.is_file():
            digest.update(b'file:' + str(path.stat().st_mode & 0o111).encode())
            with path.open('rb') as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(block)
        else:
            digest.update(b'missing')
        digest.update(b'\0')
    return digest.hexdigest()


def dotnet_path():
    options = [os.environ.get('DOTNET'), shutil.which('dotnet'), str(Path.home() / '.dotnet/dotnet')]
    for option in options:
        if option and Path(option).is_file():
            return str(Path(option).resolve())
    raise RuntimeError('Install .NET SDK 9, or set DOTNET to its executable path.')


def live_tools():
    engine = os.environ.get('CONTAINER_ENGINE') or shutil.which('podman') or shutil.which('docker')
    rclone = os.environ.get('RCLONE_BINARY') or shutil.which('rclone') or str(ROOT / 'artifacts/qa/tools/rclone')
    ffmpeg = os.environ.get('FFMPEG_BINARY') or shutil.which('ffmpeg')
    if sys.platform != 'linux' or not Path('/dev/fuse').exists():
        raise RuntimeError('The disposable live gate needs Linux with /dev/fuse. See scripts/README.md.')
    if not engine or not ffmpeg or not Path(rclone).is_file():
        raise RuntimeError('Live gate needs Podman/Docker, ffmpeg and rclone. See scripts/README.md.')
    images = [os.environ.get('JELLYFIN_TEST_IMAGE', 'docker.io/jellyfin/jellyfin:latest'),
              os.environ.get('PLEX_TEST_IMAGE', 'docker.io/plexinc/pms-docker:latest')]
    # Resolve once to immutable local image IDs. Never pull or update an image implicitly.
    image_ids = [run([engine, 'image', 'inspect', image, '--format', '{{.Id}}'], capture=True) for image in images]
    return {'engine': engine, 'rclone': str(Path(rclone).resolve()), 'ffmpeg': ffmpeg,
            'jellyfin_image': image_ids[0], 'plex_image': image_ids[1]}


def reusable(report, signature, scope, now=None):
    age = (time.time() if now is None else now) - report.get('completedAt', 0)
    return report.get('passed') is True and report.get('signature') == signature and report.get('scope') == scope and 0 <= age < 86400


def validate(*, full=False, reuse=False):
    dotnet = dotnet_path()
    tooling = {'dotnet': run([dotnet, '--version'], capture=True),
               'node': run(['node', '--version'], capture=True), 'npm': run(['npm', '--version'], capture=True)}
    if not tooling['dotnet'].startswith('9.'):
        raise RuntimeError('Use .NET SDK 9 for the Jellyfin 10.11 validation gate.')
    tools = live_tools() if full else None
    if tools:
        tooling.update(tools)
        tooling['rcloneVersion'] = run([tools['rclone'], 'version'], capture=True)
    scope = 'automated+windows-build+live' if full else 'automated'
    before = fingerprint()
    signature = hashlib.sha256(json.dumps([before, tooling], sort_keys=True).encode()).hexdigest()
    stamp = Path(git('rev-parse', '--git-path', 'federation-qa.json'))
    if not stamp.is_absolute():
        stamp = ROOT / stamp
    if reuse and stamp.exists():
        try:
            if reusable(json.loads(stamp.read_text()), signature, scope):
                print('PASS: reusing matching validation (same files/toolchain/images, less than 24 hours old).', flush=True)
                return
        except (ValueError, OSError, TypeError):
            pass
    # A failed rerun must invalidate any previous success, even for the same tree.
    stamp.unlink(missing_ok=True)
    print('Validating', scope, '— failures stop the push.', flush=True)
    run(['git', 'diff', '--check'])
    run(['git', 'diff', '--cached', '--check'])
    run(['npm', 'ci', '--no-audit', '--no-fund'])
    run([sys.executable, '-m', 'unittest', 'discover', '-s', 'scripts', '-p', 'test_*.py'])
    for project in ['JellyfinFederationPlugin.csproj', 'Companion']:
        run([dotnet, 'clean', project, '-c', 'Release', '--nologo', '-v', 'quiet'])
    run([dotnet, 'build', 'JellyfinFederationPlugin.csproj', '-c', 'Release', '--nologo', '-v', 'quiet'])
    app = ROOT / 'artifacts/qa/companion'
    # Do not retain runtime state or obsolete assets from a previous publish.
    if app.exists():
        shutil.rmtree(app)
    run([dotnet, 'publish', 'Companion', '-c', 'Release', '-o', app, '--nologo', '-v', 'quiet'])
    for number in [1, 2]:
        print(f'Automated suite {number}/2', flush=True)
        run([dotnet, 'test', 'Tests', '--nologo', '-v', 'quiet'])
        run([dotnet, 'test', 'Companion.Tests', '--nologo', '-v', 'quiet'])
        run(['npm', 'test'])
    if full:
        windows = ROOT / 'artifacts/qa/windows'
        if windows.exists():
            shutil.rmtree(windows)
        run([dotnet, 'publish', 'Companion', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
             '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
             '-o', ROOT / 'artifacts/qa/windows', '--nologo', '-v', 'quiet'])
        from plex_smoke import smoke
        smoke(ROOT, dotnet, app, tools)
    if fingerprint() != before:
        raise RuntimeError('Repository files changed during validation. Run the gate again on the final files.')
    report = {'passed': True, 'signature': signature, 'scope': scope, 'completedAt': time.time(),
              'sourceFingerprint': before, 'tooling': tooling,
              'limits': ['Windows runtime/Funnel and full real-client two-server release matrix remain separate.']}
    temp = stamp.with_suffix('.tmp')
    temp.write_text(json.dumps(report, indent=2) + '\n')
    temp.replace(stamp)
    print('PASS:', scope, 'Validation receipt:', stamp, flush=True)


def check_push_target(branch, status):
    if not branch:
        raise RuntimeError('Checkout a named review branch before pushing.')
    if branch in ('master', 'main'):
        raise RuntimeError('This script pushes review branches. master/main automatically publish Companion; complete the Windows/client release gates before merging.')
    if status:
        raise RuntimeError('Commit the intended changes first. This script never stages private or unrelated files automatically.')


def push(remote='origin'):
    branch = git('branch', '--show-current')
    check_push_target(branch, git('status', '--porcelain'))
    if remote not in git('remote').splitlines():
        raise RuntimeError('Choose a configured Git remote with --remote.')
    head = git('rev-parse', 'HEAD')
    validate(full=True, reuse=True)
    check_push_target(git('branch', '--show-current'), git('status', '--porcelain'))
    if git('branch', '--show-current') != branch or git('rev-parse', 'HEAD') != head:
        raise RuntimeError('Branch or commit changed during validation. Retry.')
    # Push exactly the tested commit. No force, tags, release API, or implicit merge.
    run(['git', 'push', '--set-upstream', remote, f'{head}:refs/heads/{branch}'])
    print('Pushed validated review branch:', branch, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    test = sub.add_parser('test'); test.add_argument('--all', action='store_true'); test.add_argument('--reuse', action='store_true')
    upload = sub.add_parser('push'); upload.add_argument('--remote', default='origin')
    args = parser.parse_args()
    if args.command == 'test':
        validate(full=args.all, reuse=args.reuse)
    else:
        push(args.remote)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, subprocess.CalledProcessError, OSError) as error:
        print('FAIL:', error, file=sys.stderr)
        sys.exit(1)
