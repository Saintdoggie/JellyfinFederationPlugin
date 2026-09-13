"""Second disposable Companion/Plex receiver; called only by the isolated full gate."""
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import urllib.parse
import uuid


def exercise(work, published_app, dotnet, tools, source, source_owner, plex, plex_headers, movie_file,
             request, wait_for, check, command, container):
    # Source Plex gets a library backed by synthetic local files, alongside its
    # existing Jellyfin import. Only the locally owned library can be forwarded.
    query = urllib.parse.urlencode({'name': 'Owned QA Movies', 'type': 'movie', 'agent': 'tv.plex.agents.movie',
                                   'scanner': 'Plex Movie', 'language': 'en-US', 'location': '/owned/Movies'})
    request(plex, '/library/sections?' + query, plex_headers, method='POST', expected=201, raw=True)
    sections = request(plex, '/library/sections', plex_headers)['MediaContainer']['Directory']
    section = next(s['key'] for s in sections if s['title'] == 'Owned QA Movies')
    request(plex, '/library/sections/' + section + '/refresh', plex_headers, raw=True)
    wait_for(lambda: request(plex, '/library/sections/' + section + '/all', plex_headers)['MediaContainer'].get('Metadata'), 'Owned Plex movie was not scanned')
    request(source, '/api/libraries/refresh', source_owner, {}, method='POST')
    request(source, '/api/libraries/toggle', source_owner, {'sectionKey': section, 'shared': True})
    request(source, '/api/public-url', source_owner, {'url': 'https://source-companion.example'})
    request_id, secret = uuid.uuid4().hex, secrets.token_hex(32)
    offer = {'id': request_id, 'secret': secret, 'offer': {'url': 'https://receiver-companion.example',
             'token': secrets.token_hex(32), 'federationId': str(uuid.uuid4()), 'name': 'Disposable Plex receiver'}}
    request(source, '/api/companion/requests', data=offer)
    request(source, '/api/companion/requests', expected=401, raw=True)
    request(source, '/api/companion/requests/' + request_id + '/accept', data={}, expected=401, raw=True)
    request(source, '/api/companion/requests/' + request_id + '/result', expected=401, raw=True)
    request(source, '/api/companion/requests/' + request_id + '/accept', source_owner, {})
    reply = request(source, '/api/companion/requests/' + request_id + '/result', {'X-Companion-Request': secret})
    token = reply['offer']['token']
    peer_headers = {'X-Federation-Token': token}
    libraries = request(source, '/Plugins/Federation/Peer/Libraries', peer_headers)['Items']
    check([l['Id'] for l in libraries] == [section], 'Companion forwarded an imported/private Plex library')
    request(source, '/Plugins/Federation/Peer/Libraries', expected=401, raw=True)

    app = work / 'peer-receiver'
    shutil.copytree(published_app, app)
    shutil.copy(tools['rclone'], app / 'rclone')
    xdg, temp = app / 'xdg', app / 'tmp'
    xdg.mkdir(); temp.mkdir()
    env = dict(os.environ, XDG_DATA_HOME=str(xdg), TMPDIR=str(temp))
    process, receiver_name = None, None
    log = (app / 'private-runtime.log').open('wb')
    os.chmod(app / 'private-runtime.log', 0o600)
    try:
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); port = listener.getsockname()[1]
        process = subprocess.Popen([dotnet, str(app / 'FederationCompanion.dll'), '--no-browser', '--urls', f'http://127.0.0.1:{port}'],
                                   cwd=app, env=env, stdout=log, stderr=log)
        receiver = f'http://127.0.0.1:{port}'
        wait_for(lambda: request(receiver, '/', raw=True)[1], 'Second Companion did not start')
        state = json.loads((app / 'companion-state.json').read_text())
        owner = {'X-Companion-Admin': state['AdminAccessKey']}
        request(receiver, '/api/playback-url', owner, {'url': receiver})
        # The fixture uses the shared Jellyfin-compatible protocol over loopback.
        # Public Plex connections use the DNS-validated client, tested separately.
        code = base64.b64encode(json.dumps({'Url': source, 'Token': token, 'Name': 'Owned Plex'}).encode()).decode()
        preview = request(receiver, '/api/import/preview', owner, {'code': code})
        check(len(preview['libraries']) == 1, 'Receiving Companion did not discover the owned library')
        imported = request(receiver, '/api/import/connect', owner, {'code': code, 'libraryIds': [section]})
        check(imported['mountedItemCount'] == 1, 'Plex-to-Plex import lacks video file information')
        check(request(receiver, '/api/media-mount/start', owner, {}, method='POST')['ready'], 'Second media mount did not start')
        state = json.loads((app / 'companion-state.json').read_text())
        mounted = state['ImportPeers'][0]['MountedFiles'][0]
        mount = Path(state['MediaMountRoot'])
        check(hashlib.sha256((mount / imported['id'] / mounted['Path']).read_bytes()).digest() == hashlib.sha256(movie_file.read_bytes()).digest(), 'Plex-to-Plex bytes differ')
        media_path = '/media/' + imported['id'] + '/' + urllib.parse.quote(mounted['Path'])
        mount_auth = {'Authorization': 'Bearer ' + state['MediaAccessKey']}
        _, body = request(receiver, media_path, {**mount_auth, 'Range': 'bytes=10-29'}, expected=206, raw=True)
        check(len(body) == 20, 'Plex-to-Plex Range failed')
        receiver_name = 'fed-qa-plex-peer-' + secrets.token_hex(5)
        config = work / 'plex-peer'; config.mkdir()
        receiver_plex = container(receiver_name, ['-p', '127.0.0.1::32400', '-v', f'{config}:/config', '-v', f'{mount}:/media:ro',
            '-e', 'PLEX_MEDIA_SERVER_APPLICATION_SUPPORT_DIR=/config', '-e', 'LD_LIBRARY_PATH=/usr/lib/plexmediaserver'],
            tools['plex_image'], '/usr/lib/plexmediaserver/Plex Media Server')
        wait_for(lambda: request(receiver_plex, '/identity', plex_headers), 'Second Plex did not start')
        request(receiver, '/api/plex/connect-local', owner, {'url': receiver_plex, 'token': 'disposable-qa'})
        request(receiver, '/api/media-mount', owner, {'localPath': str(mount), 'plexPath': '/media'})
        attached = wait_for(lambda: request(receiver, f"/api/import/peers/{imported['id']}/add-to-plex", owner, {}, method='POST'), 'Second Plex could not attach imports')
        def analyzed():
            items = request(receiver_plex, '/library/sections/' + attached['plexMovieSectionKey'] + '/all', plex_headers)['MediaContainer'].get('Metadata', [])
            if not items: return None
            item = request(receiver_plex, '/library/metadata/' + items[0]['ratingKey'], plex_headers)['MediaContainer']['Metadata'][0]
            sources = item.get('Media', [])
            return sources[0] if sources and sources[0].get('videoCodec') and sources[0].get('audioCodec') else None
        source_media = wait_for(analyzed, 'Second Plex did not analyze the imported file', 120)
        check(source_media['videoCodec'] == 'h264' and source_media['audioCodec'] == 'aac', 'Plex-to-Plex codecs differ')
        command([tools['ffmpeg'], '-nostdin', '-v', 'error', '-headers', 'X-Plex-Token: disposable-qa\r\n',
                 '-i', receiver_plex + source_media['Part'][0]['key'], '-t', '1', '-f', 'null', '-'])
        request(receiver, '/api/libraries/refresh', owner, {})
        request(receiver, '/api/libraries/toggle', owner, {'sectionKey': attached['plexMovieSectionKey'], 'shared': True}, expected=400, raw=True)
        item_id = mounted['ItemId']
        grant = request(source, '/Plugins/Federation/PlaybackToken', peer_headers, {'ItemId': item_id})['token']
        request(source, '/api/libraries/toggle', source_owner, {'sectionKey': section, 'shared': False})
        request(source, '/Plugins/Federation/DirectStream/' + item_id + '?token=' + urllib.parse.quote(grant), method='HEAD', expected=403, raw=True)
        request(receiver, media_path, mount_auth, method='HEAD', expected=403, raw=True)
        # Actual request-level authorization, not a stale cached filesystem read.
        print('LIVE PASS: two Companion processes, two Plex servers, owned-only import, mounted byte identity, Range, Plex analysis/decode and live revocation', flush=True)
    finally:
        if receiver_name:
            subprocess.run([tools['engine'], 'stop', '-t', '5', receiver_name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if process and process.poll() is None:
            process.terminate()
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
        log.close()
