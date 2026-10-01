#!/usr/bin/env python3
"""Real, disposable Plex -> Jellyfin metadata/playback/outage regression gate."""
import concurrent.futures
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = Path(__file__).resolve().parents[1]
FFMPEG_PREFIX = []
JELLYFIN_IMAGE = os.environ.get("JELLYFIN_TEST_IMAGE", "docker.io/jellyfin/jellyfin:12.0")


def run(args):
    p = subprocess.run([str(a) for a in (FFMPEG_PREFIX + list(args)[1:] if args[0] == 'ffmpeg' else args)], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return p.stdout.decode().strip()


def check(value, message):
    if not value:
        raise RuntimeError(message)


def request(base, path, headers=None, data=None, method=None, expected=200, raw=False):
    headers = dict(headers or {})
    if data is not None and not isinstance(data, bytes):
        headers['Content-Type'] = 'application/json'
        data = json.dumps(data).encode()
    req = urllib.request.Request(base + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=45) as r:
            status, incoming, body = r.status, dict(r.headers), r.read()
    except urllib.error.HTTPError as e:
        status, incoming, body = e.code, dict(e.headers), e.read()
    if status != expected:
        raise RuntimeError(f'{path.split("?")[0]}: HTTP {status}, expected {expected}')
    return (incoming, body) if raw else json.loads(body) if body else None


def wait_for(fn, label, seconds=150):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        try:
            result = fn()
            if result:
                return result
        except (OSError, ValueError, RuntimeError):
            pass
        time.sleep(1)
    raise RuntimeError(label)


def smoke(companion_files=False):
    global FFMPEG_PREFIX
    work = Path(tempfile.mkdtemp(prefix='federation-metadata-qa-'))
    os.chmod(work, 0o700)
    FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
                     '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', JELLYFIN_IMAGE]
    suffix = secrets.token_hex(5)
    plex_name, jf_name = 'fed-meta-plex-' + suffix, 'fed-meta-jf-' + suffix
    network = 'fed-meta-' + suffix
    created = []
    stage = 'fixture media'
    try:
        print('LIVE: generating actual video/audio/subtitle files', flush=True)
        media = work / 'media'
        mkv = media / 'Movies/QA Tracks (2026)/QA Tracks (2026).mkv'
        mp4 = media / 'Movies/QA MP4 (2026)/QA MP4 (2026).mp4'
        for p in (mkv, mp4):
            p.parent.mkdir(parents=True)
        sub = work / 'qa.srt'
        sub.write_text('1\n00:00:00,000 --> 00:00:02,000\nSynthetic subtitle\n')
        run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24', '-f', 'lavfi', '-i',
             'sine=frequency=440:sample_rate=48000', '-i', sub, '-t', '4', '-map', '0:v', '-map', '2:s', '-map', '1:a',
             '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-c:s', 'srt', '-y', mkv])
        run(['ffmpeg', '-v', 'error', '-i', mkv, '-map', '0:v', '-map', '0:a', '-c', 'copy', '-y', mp4])
        for n in (1, 2):
            episode = media / f'Shows/QA Series/Season 01/QA Series - S01E{n:02d}.mkv'
            episode.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(mkv, episode)
        posters = []
        for color in ('red', 'blue'):
            poster = work / (color + '.jpg')
            run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', f'color={color}:s=300x450', '-frames:v', '1', '-y', poster])
            posters.append(poster)
        pod_ports = ['-p', '127.0.0.1::32400', '-p', '127.0.0.1::8096']
        if companion_files: pod_ports += ['-p', '127.0.0.1::5000', '-p', '127.0.0.1::5001']
        run(['podman', 'pod', 'create', '--name', network] + pod_ports)
        infra = json.loads(run(['podman', 'pod', 'inspect', network]))[0]['InfraContainerID']

        def container(name, image, port, args, entry=None):
            cmd = ['podman', 'run', '-d', '--name', name, '--pod', network, '--security-opt', 'label=disable'] + args
            if entry:
                cmd += ['--entrypoint', entry]
            created.append(name)
            run(cmd + [image])
            return 'http://' + run(['podman', 'port', infra, f'{port}/tcp'])

        plex_config = work / 'plex'; plex_config.mkdir()
        plex_args = ['-v', f'{plex_config}:/config', '-v', f'{media}:/media:ro']
        if companion_files:
            # The public image installs the current Plex binary during its normal initialization.
            plex_args += ['-e', 'PLEX_UID=0', '-e', 'PLEX_GID=0', '-e', 'TZ=UTC']
            plex_entry = None
        else:
            plex_args += ['-e', 'PLEX_MEDIA_SERVER_APPLICATION_SUPPORT_DIR=/config', '-e', 'LD_LIBRARY_PATH=/usr/lib/plexmediaserver']
            plex_entry = '/usr/lib/plexmediaserver/Plex Media Server'
        plex = container(plex_name, os.environ.get('PLEX_TEST_IMAGE', 'docker.io/plexinc/pms-docker:latest'), 32400,
                         plex_args, plex_entry)
        token = secrets.token_urlsafe(32)
        plex_headers = {'Accept': 'application/json', 'X-Plex-Token': token}
        stage = 'Plex startup and real analysis'
        identity = wait_for(lambda: request(plex, '/identity', plex_headers), 'Plex startup failed')['MediaContainer']
        print(f"LIVE: Plex {identity.get('version', 'unknown')}, claimed={identity.get('claimed', 'unknown')} (not an account entitlement test)", flush=True)
        sections = []
        for name, kind, path, scanner in [('QA Movies', 'movie', '/media/Movies', 'Plex Movie'),
                                          ('QA Shows', 'show', '/media/Shows', 'Plex TV Series')]:
            query = urllib.parse.urlencode({'name': name, 'type': kind, 'agent': 'tv.plex.agents.series' if kind == 'show' else 'tv.plex.agents.movie',
                                            'scanner': scanner, 'language': 'en-US', 'location': path})
            def create_section():
                existing = request(plex, '/library/sections', plex_headers)['MediaContainer'].get('Directory', [])
                matched = next((s for s in existing if s['title'] == name), None)
                if matched:
                    return matched['key']
                request(plex, '/library/sections?' + query, plex_headers, method='POST', expected=201, raw=True)
                return None
            section = wait_for(create_section, 'Plex agents did not become ready to create section', 90)
            sections.append((section, kind))
            request(plex, f'/library/sections/{section}/refresh', plex_headers, raw=True)

        def analyzed():
            items = request(plex, f'/library/sections/{sections[0][0]}/all', plex_headers)['MediaContainer'].get('Metadata', [])
            if len(items) != 2:
                return None
            details = [request(plex, '/library/metadata/' + i['ratingKey'], plex_headers)['MediaContainer']['Metadata'][0] for i in items]
            return details if all(i.get('Media', [{}])[0].get('audioCodec') for i in details) else None
        plex_movies = wait_for(analyzed, 'Plex failed to analyze both files')
        plex_movie = next(i for i in plex_movies if 'Tracks' in i['title'])
        rating = plex_movie['ratingKey']
        request(plex, f'/library/metadata/{rating}/posters', {**plex_headers, 'Content-Type': 'image/jpeg'}, posters[0].read_bytes(), expected=200, raw=True)

        file_fixture = None
        source_url, source_token = 'http://127.0.0.1:32400', token
        if companion_files:
            from companion_file_fixture import CompanionFileFixture
            file_fixture = CompanionFileFixture(REPO, work, network, created, JELLYFIN_IMAGE, media, request, wait_for, run)
            file_fixture.enable(token, plex_movies, sections)
            source_url, source_token = file_fixture.source, file_fixture.token

        config = work / 'jellyfin'
        plugin = config / 'plugins/Federation'; plugin.mkdir(parents=True)
        shutil.copy(REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
        jf = container(jf_name, JELLYFIN_IMAGE, 8096, ['-v', f'{config}:/config'])
        stage = 'Jellyfin startup'
        wait_for(lambda: request(jf, '/Startup/User'), 'Jellyfin startup failed')
        request(jf, '/Startup/Configuration', data={'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, expected=204)
        password = secrets.token_urlsafe(32)
        request(jf, '/Startup/User', data={'Name': 'qa-admin', 'Password': password}, expected=204)
        request(jf, '/Startup/RemoteAccess', data={'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False}, expected=204)
        request(jf, '/Startup/Complete', data={}, expected=204)
        auth_headers = {'Authorization': 'MediaBrowser Client="Federation metadata QA", Device="Sandbox", DeviceId="metadata-qa", Version="1"'}
        admin_auth = request(jf, '/Users/AuthenticateByName', auth_headers, {'Username': 'qa-admin', 'Pw': password})
        admin = {'Authorization': 'MediaBrowser Token="' + admin_auth['AccessToken'] + '"'}
        viewer = request(jf, '/Users/New', admin, {'Name': 'qa-viewer'})
        viewer_auth = request(jf, '/Users/AuthenticateByName', auth_headers, {'Username': 'qa-viewer', 'Pw': ''})
        viewer_headers = {'Authorization': 'MediaBrowser Token="' + viewer_auth['AccessToken'] + '"'}
        check(not viewer_auth['User']['Policy']['IsAdministrator'], 'Viewer is unexpectedly elevated')
        server = request(jf, '/Plugins/Federation/ExternalServers', admin,
                         {'Name': 'Real QA Plex', 'Url': source_url, 'Token': source_token})['server']
        server_id = server.get('Id') or server['id']
        settings = request(jf, '/Plugins/Federation/Configuration', admin)
        settings['ServerUrl'] = jf
        settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
        settings['LibraryMappings'] = [{'LocalLibraryName': name, 'MediaType': kind, 'RemoteLibrarySources':
                                      [{'ServerId': server_id, 'RemoteLibraryId': section}], 'Enabled': True, 'AutoProvision': True}
                                     for (section, _), name, kind in zip(sections, ('QA Movies', 'QA Shows'), ('Movie', 'Series'))]
        request(jf, '/Plugins/Federation/Configuration', admin, settings)
        request(jf, '/Plugins/Federation/ProvisionLibraries', admin, {})
        stage = 'Plex import into Jellyfin'

        def refresh():
            return request(jf, '/Plugins/Federation/Refresh', admin, {})
        def movies(headers=admin):
            return request(jf, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams,ProviderIds', headers)['Items']
        def imported():
            refresh()
            result = movies()
            return result if len(result) == 2 else None
        jf_movies = wait_for(imported, 'Plex movies never appeared in Jellyfin')
        item = next(i for i in jf_movies if 'Tracks' in i['Name']); item_id = item['Id']
        for headers in (admin, viewer_headers):
            items = movies(headers)
            check(len(items) == 2, 'Admin/viewer catalog differs')
            for i in items:
                expected_file = mkv if 'Tracks' in i['Name'] else mp4
                source = i['MediaSources'][0]
                check(source['Size'] == expected_file.stat().st_size, 'Persisted file size differs from actual bytes')
                check(source['Container'] == ('mkv' if expected_file == mkv else 'mp4'), 'Persisted container differs from file')
                check(next(s for s in source['MediaStreams'] if s['Type'] == 'Video')['Codec'] == 'h264', 'Persisted video codec differs from file')
                audio = next(s for s in source['MediaStreams'] if s['Type'] == 'Audio')
                check(audio['Codec'] == 'aac' and audio['Index'] == (2 if expected_file == mkv else 1), 'Audio codec/index differs from actual file')
                check(token not in json.dumps(i), 'Plex credential exposed to client')
        episodes = request(jf, '/Items?Recursive=true&IncludeItemTypes=Episode', viewer_headers)['Items']
        check(len(episodes) == 2 and {i['IndexNumber'] for i in episodes} == {1, 2}, 'Episode numbering or hierarchy is incorrect')
        print('LIVE PASS: admin/viewer file sizes, containers, video/audio codecs and actual track indexes', flush=True)

        def poster_color():
            _, data = request(jf, f'/Items/{item_id}/Images/Primary', viewer_headers, raw=True)
            local = work / 'jellyfin-poster.jpg'; local.write_bytes(data)
            p = subprocess.run(FFMPEG_PREFIX + ['-v', 'error', '-i', str(local), '-vf', 'scale=1:1', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-'],
                               check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            return tuple(p.stdout[:3])
        color = wait_for(poster_color, 'Plex poster never copied')
        check(color[0] > color[2] + 100, 'First Plex poster was not the selected red image')
        request(plex, f'/library/metadata/{rating}/posters', {**plex_headers, 'Content-Type': 'image/jpeg'}, posters[1].read_bytes(), raw=True)
        refresh()
        color = poster_color()
        check(color[2] > color[0] + 100, 'Changed Plex poster did not replace the cached image')
        print('LIVE PASS: exact selected Plex poster copied and replaced after owner changes it', flush=True)

        stage = 'raw playback, ranges and concurrent streams'
        detail = request(jf, '/Items/' + item_id, viewer_headers)
        raw_path = detail['MediaSources'][0]['Path']
        check(raw_path.startswith('http://127.0.0.1:8096/'), 'Static source does not use local relay')
        stream_path = urllib.parse.urlsplit(raw_path).path + '?' + urllib.parse.urlsplit(raw_path).query
        payload = mkv.read_bytes()
        for headers in (admin, viewer_headers):
            info = request(jf, f'/Items/{item_id}/PlaybackInfo', headers)
            check(info.get('MediaSources'), 'PlaybackInfo has no playable sources')
            check(info['MediaSources'][0]['Size'] == len(payload), 'PlaybackInfo reports incorrect size')
            check(next(s for s in info['MediaSources'][0]['MediaStreams'] if s['Type'] == 'Audio')['Index'] == 2, 'PlaybackInfo reports incorrect audio index')
            _, data = request(jf, stream_path, headers, raw=True)
            check(data == payload, 'Federated media relay changed the source bytes')
        head, body = request(jf, stream_path, viewer_headers, method='HEAD', raw=True)
        check(not body and int(head['Content-Length']) == len(payload), 'HEAD reports incorrect size/body')
        suffix_headers, suffix = request(jf, stream_path, {**viewer_headers, 'Range': 'bytes=-512'}, expected=206, raw=True)
        check(suffix == payload[-512:] and suffix_headers['Content-Range'] == f'bytes {len(payload)-512}-{len(payload)-1}/{len(payload)}', 'Suffix Range is incorrect')
        def ranged(n):
            a = n * 137 % (len(payload) - 512)
            incoming, body = request(jf, stream_path, {**viewer_headers, 'Range': f'bytes={a}-{a+511}'}, expected=206, raw=True)
            check(incoming['Content-Range'] == f'bytes {a}-{a+511}/{len(payload)}', 'Concurrent Range returned incorrect Content-Range')
            check(body == payload[a:a+512], 'Concurrent Range returned incorrect bytes')
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            list(pool.map(ranged, range(80)))
        served = work / 'served.mkv'; served.write_bytes(request(jf, stream_path, viewer_headers, raw=True)[1])
        run(['ffmpeg', '-v', 'error', '-i', served, '-map', '0:v', '-map', '0:a', '-f', 'null', '-'])
        print('LIVE PASS: actual federated media decoded; 80 Range requests at concurrency 8 matched the file', flush=True)

        stage = 'Jellyfin ffmpeg transcoding, audio selection and seek'
        transcode_query = urllib.parse.urlencode({'Static': 'false', 'MediaSourceId': detail['MediaSources'][0]['Id'],
            'VideoCodec': 'h264', 'AudioCodec': 'aac', 'AudioStreamIndex': 2, 'VideoBitrate': 250000,
            'AudioBitrate': 64000, 'MaxWidth': 160, 'MaxHeight': 90, 'StartTimeTicks': 10000000})
        _, transcoded = request(jf, f'/Videos/{item_id}/stream.mp4?' + transcode_query, viewer_headers, raw=True)
        served_mp4 = work / 'transcoded-seek.mp4'; served_mp4.write_bytes(transcoded)
        run(['ffmpeg', '-v', 'error', '-i', served_mp4, '-map', '0:v', '-map', '0:a', '-f', 'null', '-'])
        check(transcoded != payload, 'Transcode unexpectedly returned the original bytes')
        print('LIVE PASS: Jellyfin transcoded the Plex relay using audio track 2 and a one-second seek; result decoded', flush=True)

        stage = 'outage/recovery and cache preservation'
        cache_candidates = list(config.rglob('federation-cache.json'))
        check(cache_candidates, 'Catalog cache missing')
        before_cache = json.loads(cache_candidates[0].read_text())
        run(['podman', 'stop', '-t', '2', plex_name])
        for _ in range(2):
            request(jf, '/Plugins/Federation/Availability/Rescan', admin, {})
        check(not movies() and not movies(viewer_headers), 'Offline media still visible')
        refresh()
        check(json.loads(cache_candidates[0].read_text()) == before_cache, 'Outage erased or changed cached catalog')
        run(['podman', 'start', plex_name])
        wait_for(lambda: request(plex, '/identity', plex_headers), 'Plex restart failed')
        request(jf, '/Plugins/Federation/Availability/Rescan', admin, {})
        recovered = movies()
        check(len(recovered) == 2 and any(i['Id'] == item_id for i in recovered), 'Recovery lost titles or stable identity')
        check(request(jf, stream_path, viewer_headers, raw=True)[1] == payload, 'Recovery playback bytes differ')
        print('LIVE PASS: offline titles hidden for admin/viewer, catalog retained, stable IDs and playback restored', flush=True)
        if file_fixture: file_fixture.verify(plex_movie)
        print('LIVE SUCCESS: real Plex -> Jellyfin sandbox complete' + (' with Companion original-file relay' if companion_files else ''), flush=True)
    except Exception as e:
        import traceback
        trace_path = work / 'failure.private.log'
        trace_path.write_text(traceback.format_exc())
        trace_path.chmod(0o600)
        for name in created:
            logs = subprocess.run(['podman', 'logs', name], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            log_path = work / (name + '.private.log')
            log_path.write_bytes(logs.stdout)
            log_path.chmod(0o600)
        if isinstance(e, subprocess.CalledProcessError):
            error_log = work / 'private-command-error.log'
            error_log.write_bytes(e.stderr or b'')
            error_log.chmod(0o600)
        detail = str(e) if isinstance(e, RuntimeError) else type(e).__name__
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish runtime logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman', 'stop', '-t', '2', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman', 'rm', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman', 'pod', 'rm', network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('LIVE: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--companion-files', action='store_true', help='Prove Companion video playback works with Plex video APIs deliberately blocked')
    smoke(parser.parse_args().companion_files)
