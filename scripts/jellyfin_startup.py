#!/usr/bin/env python3
"""Disposable two-Jellyfin HTTP startup benchmark. Does not measure rendered frames."""
import argparse
import concurrent.futures
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.request
import urllib.parse


def run(args):
    repo = Path(__file__).resolve().parent.parent
    dll = repo / 'bin/Release/net9.0/Jellyfin.Plugin.Federation.dll'
    if not dll.is_file():
        raise RuntimeError('Build the plugin in Release before benchmarking')
    engine = shutil.which('podman') or shutil.which('docker')
    if not engine or not shutil.which('ffmpeg'):
        raise RuntimeError('A container engine and ffmpeg are required')
    work = Path(tempfile.mkdtemp(prefix='federation-jj-startup-'))
    os.chmod(work, 0o700)
    suffix = secrets.token_hex(6)
    network = 'fed-startup-' + suffix
    created = []
    network_created = False
    stage = 'setup'

    def command(values):
        return subprocess.run([str(v) for v in values], check=True, capture_output=True,
                              text=True, timeout=180).stdout.strip()

    def request(base, path, headers=None, data=None, expected=200):
        outgoing = dict(headers or {})
        if data is not None:
            outgoing['Content-Type'] = 'application/json'
        req = urllib.request.Request(base + path, headers=outgoing,
            data=None if data is None else json.dumps(data).encode())
        with urllib.request.urlopen(req, timeout=90) as response:
            if response.status != expected:
                raise RuntimeError('Unexpected HTTP status')
            body = response.read()
        return json.loads(body) if body else None

    def wait(action):
        until = time.monotonic() + 120
        while time.monotonic() < until:
            try:
                result = action()
                if result:
                    return result
            except (OSError, ValueError, RuntimeError):
                pass
            time.sleep(1)
        raise RuntimeError('Fixture readiness deadline exceeded')

    def start(role, image):
        config = work / role
        plugin = config / 'plugins/Federation'
        plugin.mkdir(parents=True)
        shutil.copy(dll, plugin)
        name = network + '-' + role
        created.append(name)
        command([engine, 'run', '-d', '--name', name, '--network', network,
            '--network-alias', role, '--security-opt', 'label=disable', '-p', '127.0.0.1::8096',
            '-v', f'{config}:/config', '-v', f'{work / "media"}:/media:ro', image])
        base = 'http://' + command([engine, 'port', name, '8096']).splitlines()[0]
        wait(lambda: request(base, '/Startup/User'))
        password = secrets.token_urlsafe(32)
        request(base, '/Startup/Configuration', data={'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, expected=204)
        request(base, '/Startup/User', data={'Name': 'fixture-admin', 'Password': password}, expected=204)
        request(base, '/Startup/RemoteAccess', data={'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False}, expected=204)
        request(base, '/Startup/Complete', data={}, expected=204)
        auth = request(base, '/Users/AuthenticateByName', headers={'X-Emby-Authorization':
            'MediaBrowser Client="Federation benchmark", Device="Fixture", DeviceId="' + role + '", Version="1"'},
            data={'Username': 'fixture-admin', 'Pw': password})
        headers = {'X-Emby-Token': auth['AccessToken']}
        config = request(base, '/Plugins/Federation/Configuration', headers)
        config.update(ServerUrl=f'http://{role}:8096', InternalServerUrl='http://127.0.0.1:8096')
        request(base, '/Plugins/Federation/Configuration', headers, config)
        return base, headers, auth['User']['Id']

    def sample(base, headers, user, item, label, iteration):
        started = time.perf_counter()
        info = request(base, f'/Items/{item}/PlaybackInfo?UserId={user}', headers)
        metadata_ms = (time.perf_counter() - started) * 1000
        if not info.get('MediaSources'):
            raise RuntimeError('PlaybackInfo returned no media sources')
        started = time.perf_counter()
        req = urllib.request.Request(base + f'/Videos/{item}/stream?static=true',
            headers={**headers, 'Range': 'bytes=0-65535'})
        with urllib.request.urlopen(req, timeout=90) as response:
            headers_ms = (time.perf_counter() - started) * 1000
            first = response.read(1)
            first_byte_ms = (time.perf_counter() - started) * 1000
            body = first + response.read()
            if response.status != 206 or not response.headers.get('Content-Range', '').startswith('bytes 0-65535/'):
                raise RuntimeError('Static range response was incorrect')
            if hashlib.sha256(body).digest() != expected_prefix:
                raise RuntimeError('Federated stream bytes differ from source')
        return dict(path=label, iteration=iteration, playback_info_ms=metadata_ms,
                    stream_headers_ms=headers_ms, stream_first_byte_ms=first_byte_ms,
                    range_complete_ms=(time.perf_counter() - started) * 1000)

    try:
        image = command([engine, 'image', 'inspect', args.image, '--format', '{{.Id}}'])
        extension = 'mp4' if args.codec == 'h264' else 'mkv'
        media = work / f'media/Movies/Startup Fixture (2026)/Startup Fixture (2026).{extension}'
        media.parent.mkdir(parents=True)
        encoding = ['-c:v', 'libx264', '-preset', 'ultrafast', '-movflags', '+faststart'] if args.codec == 'h264' else [
            '-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p10le', '-x265-params', 'pools=2:frame-threads=2']
        width = args.height * 16 // 9
        command(['ffmpeg', '-nostdin', '-v', 'error', '-f', 'lavfi', '-i', f'testsrc2=size={width}x{args.height}:rate=24',
            '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000', '-t', '12',
            *encoding, '-c:a', 'aac', '-y', media])
        expected_prefix = hashlib.sha256(media.read_bytes()[:65536]).digest()
        command([engine, 'network', 'create', network])
        network_created = True
        stage = 'source startup'
        source, source_auth, source_user = start('source', image)
        stage = 'receiver startup'
        receiver, receiver_auth, receiver_user = start('receiver', image)
        stage = 'native scan'
        request(source, '/Library/VirtualFolders?name=Fixture&collectionType=movies&refreshLibrary=true', source_auth,
            {'LibraryOptions': {'PathInfos': [{'Path': '/media/Movies'}], 'EnableRealtimeMonitor': False,
                                'EnableInternetProviders': False}}, expected=204)
        def movie(base, headers, user):
            items = request(base, f'/Users/{user}/Items?Recursive=true&IncludeItemTypes=Movie', headers)['Items']
            return items[0]['Id'] if len(items) == 1 else None
        source_item = wait(lambda: movie(source, source_auth, source_user))
        def source_analyzed():
            info = request(source, f'/Items/{source_item}/PlaybackInfo?UserId={source_user}', source_auth)
            return any(s.get('Container') and any(t.get('Type') == 'Video' for t in s.get('MediaStreams', []))
                       for s in info.get('MediaSources', []))
        wait(source_analyzed)
        source_library = next(f['ItemId'] for f in request(source, '/Library/VirtualFolders', source_auth)
                              if f['Name'] == 'Fixture')
        stage = 'federation configuration'
        peer_token = secrets.token_urlsafe(32)
        request(source, '/Plugins/Federation/Servers', source_auth,
            {'Name': 'Fixture receiver', 'Url': 'http://receiver:8096', 'Enabled': True,
             'IssuedApiKey': peer_token, 'ShareAllLibraries': True})
        remote = request(receiver, '/Plugins/Federation/Servers', receiver_auth,
            {'Name': 'Fixture source', 'Url': 'http://source:8096', 'Enabled': True,
             'ApiKey': peer_token, 'StreamingMode': 1})['server']
        config = request(receiver, '/Plugins/Federation/Configuration', receiver_auth)
        server_id = remote['Id'] if 'Id' in remote else remote['id']
        config['LibraryMappings'] = [{'LocalLibraryName': 'Federated Fixture', 'MediaType': 'Movie',
            'RemoteServerIds': [server_id], 'RemoteLibrarySources': [{'ServerId': server_id,
                'RemoteLibraryId': source_library, 'RemoteLibraryName': 'Fixture'}],
            'Enabled': True, 'AutoProvision': True}]
        request(receiver, '/Plugins/Federation/Configuration', receiver_auth, config)
        request(receiver, '/Plugins/Federation/ProvisionLibraries', receiver_auth, {})
        request(receiver, '/Plugins/Federation/Refresh', receiver_auth, {})
        stage = 'federated item readiness'
        receiver_item = wait(lambda: movie(receiver, receiver_auth, receiver_user))
        print('BENCH: two-server import ready', flush=True)
        samples = []
        stage = 'startup measurements'
        for label, base, auth, user, item in [('local', source, source_auth, source_user, source_item),
                                             ('federated', receiver, receiver_auth, receiver_user, receiver_item)]:
            for iteration in range(args.samples):
                samples.append(sample(base, auth, user, item, label, iteration))
            print(f'BENCH: {label} samples complete', flush=True)
        stage = 'concurrent range measurements'
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            concurrent_samples = list(pool.map(lambda i: sample(receiver, receiver_auth, receiver_user,
                receiver_item, 'federated-concurrent', i), range(4)))
        samples.extend(concurrent_samples)
        browser_samples = []
        if args.playwright_module:
            stage = 'browser first-frame measurement'
            browser_cases = [('local', source, source_auth, source_item, False),
                             ('federated', receiver, receiver_auth, receiver_item, False)]
            if args.codec != 'h264':
                # This matrix targets HEVC -> H264 conversion, not browser HEVC support.
                browser_cases = []
            if args.transcode:
                browser_cases += [('local-transcode', source, source_auth, source_item, True),
                                  ('federated-transcode', receiver, receiver_auth, receiver_item, True)]
            for label, base, auth, item, transcode in browser_cases:
                for iteration in range(args.browser_samples):
                    query = {'api_key': auth['X-Emby-Token'], 'static': 'false' if transcode else 'true'}
                    if transcode:
                        query.update(VideoCodec='h264', AudioCodec='aac', VideoBitRate=500000,
                            AudioBitRate=128000, MaxWidth=args.transcode_width, EnableAutoStreamCopy='false',
                            AllowVideoStreamCopy='false', AllowAudioStreamCopy='false',
                            DeviceId='benchmark-' + suffix, PlaySessionId=secrets.token_hex(16))
                    route = f'/Videos/{item}/stream' + ('.mp4' if transcode else '')
                    payload = {'playwrightModule': str(args.playwright_module.resolve()), 'chromium': args.chromium,
                        'url': base + route + '?' + urllib.parse.urlencode(query)}
                    probe = subprocess.run(['node', str(repo / 'scripts/jellyfin_video_probe.cjs')],
                        input=json.dumps(payload), capture_output=True, text=True, timeout=80, check=True)
                    result = json.loads(probe.stdout)
                    if result.get('status') != 'ok':
                        raise RuntimeError('Browser did not render the synthetic video')
                    # MaxWidth is a ceiling; Jellyfin can scale further for bitrate.
                    if transcode and not 0 < result.get('video_width', 0) <= args.transcode_width < width:
                        raise RuntimeError('Transcode probe did not produce a valid downscaled output')
                    browser_samples.append({'path': label, 'iteration': iteration, **result})
                print(f'BENCH: {label} browser frames measured', flush=True)
        summary = {}
        for label in sorted({s['path'] for s in samples}):
            values = [s for s in samples if s['path'] == label]
            summary[label] = {}
            for field in ['playback_info_ms', 'stream_first_byte_ms', 'range_complete_ms']:
                ordered = sorted(s[field] for s in values)
                summary[label][field] = {'p50': ordered[math.ceil(len(ordered) * .5) - 1],
                                        'p95': ordered[math.ceil(len(ordered) * .95) - 1]}
        report = {'schema': 1, 'image_id': image, 'plugin_sha256': hashlib.sha256(dll.read_bytes()).hexdigest(),
            'scope': 'Synthetic video; admin; local container network; static proxy HTTP plus optional browser/forced-transcode probes',
            'source': {'codec': args.codec, 'height': args.height, 'container': extension, 'duration_seconds': 12},
            'limitations': ['HTTP timings exclude rendering; optional browser_samples measure frame callbacks on a plain video element', 'Optional forced transcode is not client device-profile negotiation',
                'First sample is first measured use, not guaranteed cold after scan',
                'Concurrent requests share an admin identity; not a watch-party test'],
            'samples': samples, 'summary': summary, 'browser_samples': browser_samples}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(summary, indent=2))
        print('BENCH PASS: range integrity and startup measurements; report: ' + str(args.output))
    except Exception as error:
        # HTTP/subprocess exceptions may contain secrets. Print only stage/type.
        raise RuntimeError(f'Benchmark failed during {stage} ({type(error).__name__}); private fixture: {work}') from None
    finally:
        for name in reversed(created):
            subprocess.run([engine, 'rm', '-f', name], capture_output=True, timeout=30)
        if network_created:
            subprocess.run([engine, 'network', 'rm', network], capture_output=True, timeout=30)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--samples', type=int, default=20)
    parser.add_argument('--image', default='docker.io/jellyfin/jellyfin:latest')
    parser.add_argument('--output', type=Path, default=Path('artifacts/qa/jellyfin-startup.json'))
    parser.add_argument('--playwright-module', type=Path, help='Installed Playwright module directory (optional frame probe)')
    parser.add_argument('--chromium', help='Chromium executable for the optional frame probe')
    parser.add_argument('--browser-samples', type=int, default=3)
    parser.add_argument('--transcode', action='store_true', help='Also measure forced H264/AAC MP4 transcoding in Chromium')
    parser.add_argument('--codec', choices=['h264', 'hevc10'], default='h264')
    parser.add_argument('--height', type=int, choices=[360, 720, 1080], default=360)
    parser.add_argument('--transcode-width', type=int, choices=[320, 640, 1280], default=320)
    arguments = parser.parse_args()
    if not 2 <= arguments.samples <= 100:
        parser.error('--samples must be between 2 and 100')
    if not 1 <= arguments.browser_samples <= 20:
        parser.error('--browser-samples must be between 1 and 20')
    if arguments.transcode and not arguments.playwright_module:
        parser.error('--transcode requires --playwright-module')
    if arguments.transcode and arguments.transcode_width >= arguments.height * 16 // 9:
        parser.error('Transcode width must be smaller than source width to verify conversion')
    if arguments.playwright_module and arguments.codec != 'h264' and not arguments.transcode:
        parser.error('HEVC browser measurements require --transcode')
    try:
        run(arguments)
    except RuntimeError as error:
        parser.exit(1, str(error) + '\n')
