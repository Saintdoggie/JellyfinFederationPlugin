"""Disposable Linux Jellyfin/Companion/rclone/Plex gate; never uses an existing server."""
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


def smoke(repo, dotnet, published_app, tools):
    work = Path(tempfile.mkdtemp(prefix='federation-plex-qa-'))
    os.chmod(work, 0o700)
    engine = tools['engine']
    suffix = secrets.token_hex(5)
    jf_name, plex_name = 'fed-qa-jf-' + suffix, 'fed-qa-plex-' + suffix
    created = []
    process = None
    log = None
    stage = 'initialize'
    app = work / 'companion'
    mount = app / 'plex-media'

    def command(args, *, capture=True):
        result = subprocess.run([str(a) for a in args], check=True, text=True,
                                stdout=subprocess.PIPE if capture else None,
                                stderr=subprocess.PIPE if capture else None)
        return result.stdout.strip() if capture else None

    def check(value, message):
        if not value:
            raise RuntimeError(message)

    def request(base, path, headers=None, data=None, method=None, expected=200, raw=False):
        outgoing = dict(headers or {})
        if data is not None:
            outgoing['Content-Type'] = 'application/json'
        req = urllib.request.Request(base + path, headers=outgoing,
                                     data=None if data is None else json.dumps(data).encode(), method=method)
        try:
            with urllib.request.urlopen(req, timeout=20) as response:
                status, incoming, body = response.status, dict(response.headers), response.read()
        except urllib.error.HTTPError as error:
            status, incoming, body = error.code, dict(error.headers), error.read()
        if status != expected:
            # Do not print upstream response bodies, query tokens or credentials.
            raise RuntimeError(f'{path.split("?")[0]} returned HTTP {status}; expected {expected}')
        if raw:
            return incoming, body
        return json.loads(body) if body else None

    def wait_for(action, message, seconds=90):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            try:
                value = action()
                if value:
                    return value
            except (OSError, ValueError, RuntimeError):
                pass
            time.sleep(1)
        raise RuntimeError(message)

    def container(name, args, image, entrypoint=None):
        command_args = [engine, 'run', '-d', '--name', name, '--security-opt', 'label=disable']
        command_args += args
        if entrypoint:
            command_args += ['--entrypoint', entrypoint]
        command_args += [image]
        # Register before run: a failed start may still create a container.
        created.append(name)
        command(command_args)
        mapping = command([engine, 'port', name]).splitlines()[0].rsplit(' -> ', 1)[-1]
        return 'http://' + mapping

    def start_app():
        nonlocal process, log
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        log = (work / 'private-companion.log').open('ab')
        os.chmod(work / 'private-companion.log', 0o600)
        process = subprocess.Popen([dotnet, str(app / 'FederationCompanion.dll'), '--urls', f'http://127.0.0.1:{port}'],
                                   cwd=app, stdout=log, stderr=subprocess.STDOUT)
        base = f'http://127.0.0.1:{port}'
        wait_for(lambda: request(base, '/', raw=True)[1], 'Companion did not start')
        state = json.loads((app / 'companion-state.json').read_text())
        return base, {'X-Companion-Admin': state['AdminAccessKey']}

    def stop_app():
        nonlocal process, log
        if process and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=5)
        process = None
        if log:
            log.close(); log = None

    try:
        print('LIVE: creating isolated source media and servers', flush=True)
        media = work / 'media'
        movie_file = media / 'Movies/QA Movie (2026)/QA Movie (2026).mkv'
        movie_file.parent.mkdir(parents=True)
        command([tools['ffmpeg'], '-nostdin', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=24',
                 '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000', '-t', '2',
                 '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-y', movie_file])
        for number in (1, 2):
            episode = media / f'Shows/QA Show/Season 01/QA Show - S01E{number:02d}.mkv'
            episode.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(movie_file, episode)
        config = work / 'jellyfin'
        plugin = config / 'plugins/Federation'
        plugin.mkdir(parents=True)
        shutil.copy(repo / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
        jf = container(jf_name, ['-p', '127.0.0.1::8096', '-v', f'{config}:/config', '-v', f'{media}:/media:ro'], tools['jellyfin_image'])
        stage = 'Jellyfin startup and import'
        wait_for(lambda: request(jf, '/System/Info/Public'), 'Jellyfin did not start')
        password = secrets.token_urlsafe(32)
        wait_for(lambda: request(jf, '/Startup/User'), 'Jellyfin startup user endpoint was not ready')
        request(jf, '/Startup/Configuration', data={'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, expected=204)
        request(jf, '/Startup/User', data={'Name': 'qa-admin', 'Password': password}, expected=204)
        request(jf, '/Startup/RemoteAccess', data={'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False}, expected=204)
        request(jf, '/Startup/Complete', data={}, expected=204)
        auth = request(jf, '/Users/AuthenticateByName', data={'Username': 'qa-admin', 'Pw': password},
                       headers={'Authorization': 'MediaBrowser Client="Federation QA", Device="Disposable", DeviceId="federation-qa", Version="1"'})
        admin = {'Authorization': 'MediaBrowser Token="' + auth['AccessToken'] + '"'}
        user = auth['User']['Id']
        for name, kind, path in [('QA Movies', 'movies', '/media/Movies'), ('QA Shows', 'tvshows', '/media/Shows')]:
            request(jf, '/Library/VirtualFolders?' + urllib.parse.urlencode({'name': name, 'collectionType': kind, 'refreshLibrary': 'true'}),
                    admin, {'LibraryOptions': {'PathInfos': [{'Path': path}], 'EnableRealtimeMonitor': False, 'EnableInternetProviders': False}}, expected=204)
        native = wait_for(lambda: (lambda d: d if len(d['Items']) == 3 else None)(request(jf,
            f'/Users/{user}/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=MediaSources', admin)), 'Synthetic library did not finish scanning', 120)
        movie_id = next(i['Id'] for i in native['Items'] if i['Type'] == 'Movie')
        federation_config = request(jf, '/Plugins/Federation/Configuration', admin)
        federation_config['ServerUrl'] = jf
        federation_config['InternalServerUrl'] = 'http://127.0.0.1:8096'
        request(jf, '/Plugins/Federation/Configuration', admin, federation_config)
        source = request(jf, '/Plugins/Federation/Servers/Companion', admin, {'Name': 'Disposable Companion', 'ShareAllLibraries': True})
        code = source['connectCode']
        decoded = json.loads(base64.b64decode(code))
        peer_token = decoded.get('Token') or decoded.get('token')
        peer_headers = {'X-Federation-Token': peer_token}
        shutil.copytree(published_app, app)
        shutil.copy(tools['rclone'], app / 'rclone')
        companion, owner = start_app()
        libraries = request(companion, '/api/import/preview', owner, {'code': code})['libraries']
        library_ids = [l['id'] for l in libraries]
        imported = request(companion, '/api/import/connect', owner, {'code': code, 'libraryIds': library_ids})
        peer_id = imported['id']
        check(imported['mountedItemCount'] == 3, 'Imported videos lack expected media information')
        catalog = request(companion, f'/api/import/peers/{peer_id}/catalog', owner)['items']
        check(sorted((i['season'], i['episode']) for i in catalog if i['episode'] is not None) == [(1, 1), (1, 2)], 'Source episode numbering changed')
        stage = 'managed mount and restart'
        check(request(companion, '/api/media-mount/start', owner, {}, method='POST')['ready'], 'Managed rclone mount failed; check FUSE permissions')
        stop_app()
        check(not os.path.ismount(mount), 'Companion did not unmount during shutdown')
        companion, owner = start_app()
        wait_for(lambda: request(companion, '/api/media-mount/status', owner)['ready'], 'Managed mount did not restore after restart')
        # A different ephemeral port after restart is intentional: managed config must update.
        state = json.loads((app / 'companion-state.json').read_text())
        mounted_movie = next(f for f in state['ImportPeers'][0]['MountedFiles'] if f['ItemId'].replace('-', '') == movie_id.replace('-', ''))
        media_path = '/media/' + peer_id + '/' + urllib.parse.quote(mounted_movie['Path'])
        mount_headers = {'Authorization': 'Bearer ' + state['MediaAccessKey']}
        headers, body = request(companion, media_path, mount_headers, method='HEAD', raw=True)
        check(not body and int(headers['Content-Length']) == movie_file.stat().st_size, 'HEAD length/body mismatch')
        for byte_range in ['bytes=10-29', 'bytes=-20']:
            _, body = request(companion, media_path, {**mount_headers, 'Range': byte_range}, expected=206, raw=True)
            check(len(body) == 20, 'Range returned wrong byte count')
        check(hashlib.sha256((mount / peer_id / mounted_movie['Path']).read_bytes()).digest() == hashlib.sha256(movie_file.read_bytes()).digest(), 'Mounted video bytes differ from source')
        print('LIVE PASS: source numbering, real media mount, HEAD/ranges and automatic restart', flush=True)

        stage = 'outgoing ownership and reconciliation'
        original = request(jf, f'/Users/{user}/Items/{movie_id}', admin)
        minted = request(jf, '/Plugins/Federation/PlaybackToken', peer_headers, {'ItemId': movie_id})
        token = minted.get('Token') or minted.get('token')
        changed = json.loads(json.dumps(original))
        changed.setdefault('ProviderIds', {})['FederationKey'] = 'qa-third-party/' + movie_id
        try:
            request(jf, '/Items/' + movie_id, admin, changed, expected=204)
            check(not request(jf, '/Plugins/Federation/Peer/Items?mediaType=Movie&limit=200', peer_headers)['Items'], 'Third-party media leaked into outgoing catalog')
            request(jf, '/Plugins/Federation/Peer/Items/' + movie_id, peer_headers, expected=404, raw=True)
            for purpose in ['Playback', 'Download']:
                request(jf, '/Plugins/Federation/PlaybackToken', peer_headers, {'ItemId': movie_id, 'Purpose': purpose}, expected=403, raw=True)
            request(jf, '/Plugins/Federation/DirectStream/' + movie_id + '?token=' + urllib.parse.quote(token), method='HEAD', expected=403, raw=True)
            after = request(companion, f'/api/import/peers/{peer_id}/sync', owner, {}, method='POST')
            check(after['mountedItemCount'] == 2, 'Previously forwarded movie remained mounted after sync')
        finally:
            request(jf, '/Items/' + movie_id, admin, original, expected=204)
            request(companion, f'/api/import/peers/{peer_id}/sync', owner, {}, method='POST')
        request(companion, f'/api/import/peers/{peer_id}/libraries', owner, {'libraryIds': []})
        request(companion, media_path, mount_headers, expected=404, raw=True)
        check(request(companion, f'/api/import/peers/{peer_id}/libraries', owner, {'libraryIds': library_ids})['mountedItemCount'] == 3, 'Reselect did not restore selected libraries')
        command([engine, 'stop', jf_name])
        try:
            after = request(companion, f'/api/import/peers/{peer_id}/sync', owner, {}, method='POST')
            check(after['mountedItemCount'] == 3 and after['lastError'], 'Outage cleared catalog or was reported as success')
        finally:
            command([engine, 'start', jf_name])
        wait_for(lambda: request(jf, '/System/Info/Public'), 'Source did not restart')
        request(companion, f'/api/import/peers/{peer_id}/sync', owner, {}, method='POST')
        print('LIVE PASS: onward-sharing denial, old token revocation, removal/reselect and outage preservation', flush=True)

        stage = 'real Plex scan and decode'
        plex_config = work / 'plex'; plex_config.mkdir()
        plex = container(plex_name, ['-p', '127.0.0.1::32400', '-v', f'{plex_config}:/config', '-v', f'{mount}:/media:ro',
            '-e', 'PLEX_MEDIA_SERVER_APPLICATION_SUPPORT_DIR=/config', '-e', 'LD_LIBRARY_PATH=/usr/lib/plexmediaserver'],
            tools['plex_image'], '/usr/lib/plexmediaserver/Plex Media Server')
        plex_headers = {'Accept': 'application/json', 'X-Plex-Token': 'disposable-qa'}
        wait_for(lambda: request(plex, '/identity', plex_headers), 'Disposable Plex did not start')
        request(companion, '/api/plex/connect-local', owner, {'url': plex, 'token': 'disposable-qa'})
        request(companion, '/api/media-mount', owner, {'localPath': str(mount), 'plexPath': '/media'})
        attached = wait_for(lambda: request(companion, f'/api/import/peers/{peer_id}/add-to-plex', owner, {}, method='POST'), 'Plex was not ready to attach the imported libraries', 90)
        section = attached['plexMovieSectionKey']
        def analyzed_movie():
            items = request(plex, '/library/sections/' + section + '/all', plex_headers)['MediaContainer'].get('Metadata', [])
            if not items:
                return None
            item = request(plex, '/library/metadata/' + items[0]['ratingKey'], plex_headers)['MediaContainer']['Metadata'][0]
            sources = item.get('Media', [])
            return sources[0] if sources and sources[0].get('videoCodec') and sources[0].get('audioCodec') else None
        source_media = wait_for(analyzed_movie, 'Plex did not analyze mounted video/audio', 120)
        check(source_media['videoCodec'] == 'h264' and source_media['audioCodec'] == 'aac', 'Plex codec analysis differs from source')
        command([tools['ffmpeg'], '-nostdin', '-v', 'error', '-headers', 'X-Plex-Token: disposable-qa\r\n',
                 '-i', plex + source_media['Part'][0]['key'], '-t', '1', '-f', 'null', '-'])
        request(companion, '/api/libraries/refresh', owner, {}, method='POST')
        request(companion, '/api/libraries/toggle', owner, {'sectionKey': section, 'shared': True}, expected=400, raw=True)
        print('LIVE PASS: Plex H.264/AAC scan, actual Plex-served media decode, imported-section sharing denied', flush=True)
    except Exception as error:
        # Keep private logs off terminal/CI; exceptions can include credential-bearing URLs.
        detail = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        raise RuntimeError(f'Live gate failed at {stage}: {detail}. Private fixture logs retained at {work}; do not publish them.') from None
    finally:
        # Stop Plex before the FUSE owner so its bind mount cannot hold the mount open.
        for name in reversed(created):
            subprocess.run([engine, 'stop', '-t', '5', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        stop_app()
        if os.path.ismount(mount):
            unmount = shutil.which('fusermount3') or shutil.which('fusermount')
            if unmount:
                subprocess.run([unmount, '-u', str(mount)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for name in reversed(created):
            subprocess.run([engine, 'rm', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # Retain private fixture data for debugging; never recursively delete through a mount.
        print('LIVE: disposable servers/process stopped; private fixture data retained in the system temporary directory.', flush=True)
