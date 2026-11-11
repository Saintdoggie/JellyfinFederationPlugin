#!/usr/bin/env python3
"""Disposable two-Jellyfin check of sound help.

Server A shares two movies with server B; A and B are linked by codes. B is set
never to convert and does not use friends' converters, so nothing on B or A
would play a file a browser can't take as stored. A browser-like device (MP4,
H.264, AAC/MP3 only) asks B to play:

* a Matroska movie with Dolby Digital 5.1 sound: B copies the picture, sends
  only the sound to A, and plays MP4 with AAC stereo; the sound starts with the
  picture, also after a seek; the loading bar's estimate says why it waits;
* a Matroska movie with AAC sound: B only changes the wrapper, A is not used;
* without the link, the Dolby Digital movie is not offered this way;
* the play address is refused for unknown ids, A's sound route without the link key.

Only containers/network created here are touched; production is never used.
"""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import time
import federation_metadata_smoke as qa
from federation_peer_smoke import no_redirect


def smoke():
    work = Path(tempfile.mkdtemp(prefix='federation-sound-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-sound-' + suffix
    roles = ('a', 'b')
    names = {role: f'fed-sound-{role}-{suffix}' for role in roles}
    created = []
    stage = 'setup'
    try:
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        probe_prefix = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffprobe', qa.JELLYFIN_IMAGE]
        media = work / 'media-a'; media.mkdir()
        (work / 'media-b').mkdir()
        # One keyframe a second, so a seek lands on one and the sound can be timed exactly.
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=24', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000', '-t', '20', '-c:v', 'libx264', '-preset', 'veryfast', '-g', '24', '-pix_fmt', 'yuv420p',
            '-c:a', 'ac3', '-ac', '6', '-b:a', '384k', '-metadata:s:a:0', 'language=eng', '-y', media / 'QA Sound (2026).mkv'])
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=24', '-f', 'lavfi', '-i',
            'sine=frequency=660:sample_rate=48000', '-t', '12', '-c:v', 'libx264', '-preset', 'veryfast', '-g', '24', '-pix_fmt', 'yuv420p',
            '-c:a', 'aac', '-metadata:s:a:0', 'language=eng', '-y', media / 'QA Wrapper (2026).mkv'])
        qa.run(['podman', 'network', 'create', network])
        servers = {}
        for role in roles:
            name = names[role]
            config = work / name
            plugin = config / 'plugins/Federation'; plugin.mkdir(parents=True)
            shutil.copy(qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
            created.append(name)
            qa.run(['podman', 'run', '-d', '--name', name, '--network', network, '--security-opt', 'label=disable',
                '-p', '127.0.0.1::8096', '-v', f'{config}:/config', '-v', f'{work / ("media-" + role)}:/media:ro', qa.JELLYFIN_IMAGE])
            base = 'http://' + qa.run(['podman', 'port', name, '8096/tcp'])
            qa.wait_for(lambda: qa.request(base, '/Startup/User'), 'Jellyfin did not start')
            qa.request(base, '/Startup/Configuration', data={'UICulture':'en-US','MetadataCountryCode':'US','PreferredMetadataLanguage':'en'}, expected=204)
            password = secrets.token_urlsafe(32)
            qa.request(base, '/Startup/User', data={'Name':'qa-admin','Password':password}, expected=204)
            qa.request(base, '/Startup/RemoteAccess', data={'EnableRemoteAccess':True,'EnableAutomaticPortMapping':False}, expected=204)
            qa.request(base, '/Startup/Complete', data={}, expected=204)
            auth_header = {'Authorization':'MediaBrowser Client="Federation sound QA", Device="Sandbox", DeviceId="sound-qa", Version="1"'}
            auth = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-admin','Pw':password})
            admin = {'Authorization':'MediaBrowser Token="'+auth['AccessToken']+'"'}
            qa.request(base, '/Users/New', admin, {'Name':'qa-viewer'})
            viewer = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-viewer','Pw':''})
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'
            settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            if role == 'b':
                qa.check(qa.request(base, '/Plugins/Federation/Settings/RemoteTranscode', admin,
                    {'LocalTranscodeRestriction': 'BlockAll', 'UseFriendsTranscoders': False})['success'], 'B could not be set never to convert')
            if role == 'a':
                qa.request(base, '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', admin,
                    {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
            servers[role] = {'name': name, 'base': base, 'admin': admin, 'host': f'http://{name}:8096', 'config': config,
                             'viewer': {'Authorization':'MediaBrowser Token="'+viewer['AccessToken']+'"'}, 'viewer_id': viewer['User']['Id']}
        a, b = servers['a'], servers['b']
        restriction = qa.request(b['base'], '/Plugins/Federation/Settings/RemoteTranscode', b['admin'])['LocalTranscodeRestriction']
        qa.check(restriction in ('BlockAll', 2), f'B is not set never to convert ({restriction})')

        def friends_of(server):
            return qa.request(server['base'], '/Plugins/Federation/Configuration', server['admin'])['RemoteServers']

        def row_for(server, other):
            return next(s for s in friends_of(server) if s['Url'].rstrip('/') == other['host'])

        stage = 'A shares its movies with B'
        qa.wait_for(lambda: qa.request(a['base'], '/Items?Recursive=true&IncludeItemTypes=Movie', a['admin'])['TotalRecordCount'] == 2, 'A did not scan its movies')
        qa.check(qa.request(b['base'], '/Plugins/Federation/Friends/Send', b['admin'], {'Url': a['host']})['success'], 'Friend request failed')
        pending = qa.request(a['base'], '/Plugins/Federation/Friends', a['admin'])['incoming'][0]['Id']
        qa.check(qa.request(a['base'], f'/Plugins/Federation/Friends/{pending}/Accept', a['admin'], {})['success'], 'Friend accept failed')
        qa.check(qa.request(a['base'], f'/Plugins/Federation/Friends/{row_for(a, b)["Id"]}/Sharing', a['admin'], {'ShareAll': True})['success'], 'Sharing failed')
        library_id = next(l['ItemId'] for l in qa.request(a['base'], '/Library/VirtualFolders', a['admin']) if l['Name'] == 'QA Native')
        a_on_b, b_on_a = row_for(b, a)['Id'], row_for(a, b)['Id']
        settings = qa.request(b['base'], '/Plugins/Federation/Configuration', b['admin'])
        settings['LibraryMappings'] = [{'LocalLibraryName':'Movies','MediaType':'Movie','Enabled':True,'AutoProvision':True,'AutoManaged':True,
            'RemoteServerIds':[a_on_b],
            'RemoteLibrarySources':[{'ServerId':a_on_b,'RemoteLibraryId':library_id,'RemoteLibraryName':'QA Native'}]}]
        qa.request(b['base'], '/Plugins/Federation/Configuration', b['admin'], settings)

        def b_movies():
            qa.request(b['base'], '/Plugins/Federation/Refresh', b['admin'], {})
            items = qa.request(b['base'], '/Items?Recursive=true&IncludeItemTypes=Movie', b['viewer'])['Items']
            return items if len(items) == 2 else None
        movies = {m['Name']: m for m in qa.wait_for(b_movies, 'A\'s movies did not appear on B', 120)}
        sound_movie, wrapper_movie = movies['QA Sound'], movies['QA Wrapper']

        browser = {'MaxStreamingBitrate': 120000000,
            'DirectPlayProfiles': [{'Type':'Video','Container':'mp4,m4v','VideoCodec':'h264','AudioCodec':'aac,mp3'}],
            'TranscodingProfiles': [{'Type':'Video','Container':'ts','VideoCodec':'h264','AudioCodec':'aac','Protocol':'hls','Context':'Streaming'}],
            'SubtitleProfiles': [], 'CodecProfiles': [], 'ContainerProfiles': []}

        def negotiate(movie, start_ticks=None):
            body = {'UserId': b['viewer_id'], 'MaxStreamingBitrate': 120000000, 'EnableDirectPlay': True, 'EnableDirectStream': True,
                    'EnableTranscoding': True, 'DeviceProfile': browser}
            if start_ticks:
                body['StartTimeTicks'] = start_ticks
            return qa.request(b['base'], f'/Items/{movie["Id"]}/PlaybackInfo?userId={b["viewer_id"]}', b['viewer'], body)['MediaSources'][0]

        stage = 'without a link the Dolby Digital movie is not offered with sound help'
        plain = negotiate(sound_movie)
        qa.check('/SoundHelp/' not in (plain.get('TranscodingUrl') or ''), 'Sound help was offered without a linked server')
        print('SOUND PASS: without a linked server, nothing is sent anywhere', flush=True)

        stage = 'link A and B'
        qa.check(qa.request(a['base'], f'/Plugins/Federation/Link/{b_on_a}/Start', a['admin'], {})['success'], 'Link did not start on A')
        qa.check(qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Start', b['admin'], {})['success'], 'Link did not start on B')

        def code_on(server, row):
            entry = next(s for s in qa.request(server['base'], '/Plugins/Federation/Link/Status', server['admin']) if s['friendId'] == row)
            if entry['codeSecondsLeft'] < 3:
                time.sleep(4)
                return code_on(server, row)
            return entry['code']

        qa.check(qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Code', b['admin'], {'Code': code_on(a, b_on_a)})['success'], 'A\'s code was refused')
        qa.check(qa.request(a['base'], f'/Plugins/Federation/Link/{b_on_a}/Code', a['admin'], {'Code': code_on(b, a_on_b)})['success'], 'B\'s code was refused')
        qa.wait_for(lambda: row_for(a, b).get('Linked') and row_for(b, a).get('Linked'), 'The link was not made on both servers', 20)

        def probe(path):
            out = subprocess.run(probe_prefix + ['-v', 'error', '-count_packets', '-show_entries', 'stream=codec_type,codec_name,channels,start_time,sample_rate,nb_read_packets:format=duration',
                '-of', 'json', str(path)], check=True, stdout=subprocess.PIPE).stdout
            return json.loads(out)

        def fetch(url, name):
            started = time.monotonic()
            head, body = qa.request(b['base'], url, {}, raw=True)
            path = work / name
            path.write_bytes(body)
            return head, path, time.monotonic() - started

        def check_sync(info, expected_duration, label):
            streams = {s['codec_type']: s for s in info['streams']}
            qa.check(streams.get('video', {}).get('codec_name') == 'h264', f'{label}: the picture is not the original H.264: {streams}')
            duration = float(info['format']['duration'])
            qa.check(abs(duration - expected_duration) < 1.5, f'{label}: {duration:.1f} s long, expected about {expected_duration}')
            gap = abs(float(streams['video']['start_time']) - float(streams['audio']['start_time']))
            qa.check(gap < 0.1, f'{label}: the sound starts {gap:.3f} s away from the picture')
            if streams['audio'].get('codec_name') == 'aac':
                heard = int(streams['audio']['nb_read_packets']) * 1024 / int(streams['audio']['sample_rate'])
                qa.check(abs(heard - expected_duration) < 1.5, f'{label}: {heard:.1f} s of sound, expected about {expected_duration}')
            return streams

        stage = 'Dolby Digital movie: picture copied on B, sound converted on A'
        source = negotiate(sound_movie)
        url = source.get('TranscodingUrl') or ''
        qa.check('/Plugins/Federation/SoundHelp/' in url and source.get('TranscodingContainer') == 'mp4'
            and str(source.get('TranscodingSubProtocol')).lower() == 'http' and not source.get('SupportsDirectStream'),
            f'B did not offer sound help: {url[:80]} {source.get("TranscodingContainer")} {source.get("TranscodingSubProtocol")} '
            f'transcoding={source.get("SupportsTranscoding")} stream={source.get("SupportsDirectStream")} play={source.get("SupportsDirectPlay")} '
            f'protocol={source.get("Protocol")} reasons={source.get("TranscodeReasons")} linked={row_for(b, a).get("Linked")} '
            + ' | '.join(l for l in subprocess.run(['podman', 'logs', b['name']], stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode(errors='replace').splitlines() if 'ound' in l or 'Federation' in l and 'ERR' in l)[-1500:])
        qa.check('api_key' not in url.lower() and a['host'] not in url, 'The play address carries a key or an address')
        estimate = qa.request(b['base'], f'/Plugins/Federation/StartEstimate/{sound_movie["Id"]}', b['viewer'])
        qa.check(estimate.get('soundHelp') and 'converting only the sound' in estimate.get('reason', '') and estimate.get('seconds', 0) >= 1,
            f'The loading bar is not told why it waits: {estimate}')
        head, path, took = fetch(url, 'sound.mp4')
        qa.check(head.get('Content-Type', '').startswith('video/mp4'), f'Wrong type {head.get("Content-Type")}')
        streams = check_sync(probe(path), 20, 'from the start')
        qa.check(streams['audio']['codec_name'] == 'aac' and streams['audio']['channels'] == 2, f'The sound is not AAC stereo: {streams["audio"]}')
        after = qa.request(b['base'], f'/Plugins/Federation/StartEstimate/{sound_movie["Id"]}', b['viewer'])
        qa.check(not after.get('soundHelp'), 'The loading bar still waits after the play started')
        logs_a = subprocess.run(['podman', 'logs', a['name']], stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode(errors='replace')
        qa.check('Converting sound only for a play on a linked server' in logs_a, 'A did not convert the sound')
        print(f'SOUND PASS: Dolby Digital 5.1 in a browser: picture copied on B, sound converted on A to AAC stereo, in sync; whole 20 s film in {took:.1f} s; the loading bar says why', flush=True)

        stage = 'a seek starts a new play at the new place, still in sync'
        sought = negotiate(sound_movie, start_ticks=10 * 10_000_000)
        qa.check('StartTimeTicks=100000000' in (sought.get('TranscodingUrl') or ''), 'The seek did not make a new play')
        _, path, _ = fetch(sought['TranscodingUrl'], 'sought.mp4')
        check_sync(probe(path), 10, 'after a seek to 10 s')
        print('SOUND PASS: a seek to 10 s plays the last 10 s, sound with the picture', flush=True)

        stage = 'AAC movie in Matroska: wrapper changed on B only'
        before = subprocess.run(['podman', 'logs', a['name']], stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode(errors='replace').count('Converting sound only')
        wrapped = negotiate(wrapper_movie)
        qa.check('/Plugins/Federation/SoundHelp/' in (wrapped.get('TranscodingUrl') or ''), 'B did not repackage the Matroska file')
        _, path, _ = fetch(wrapped['TranscodingUrl'], 'wrapper.mp4')
        streams = check_sync(probe(path), 12, 'wrapper only')
        qa.check(streams['audio']['codec_name'] == 'aac', 'The AAC sound was changed')
        logs_a = subprocess.run(['podman', 'logs', a['name']], stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode(errors='replace')
        qa.check(logs_a.count('Converting sound only') == before, 'A was asked to convert sound that plays as it is')
        print('SOUND PASS: AAC in Matroska: only the wrapper changes, on B; nothing goes to A', flush=True)

        stage = 'refusals'
        status, _, _ = no_redirect(b['base'], '/Plugins/Federation/SoundHelp/' + secrets.token_hex(16) + '/stream.mp4', {})
        qa.check(status == 404, f'An unknown play id was answered (HTTP {status})')
        status, _, _ = no_redirect(a['base'], '/Plugins/Federation/Peer/Linked/Sound', {})
        qa.check(status == 401, f'A answered the sound route without the link key (HTTP {status})')
        logs_b = subprocess.run(['podman', 'logs', b['name']], stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode(errors='replace')
        qa.check('sig=' not in logs_b and 'api_key' not in logs_b.lower(), 'B logged a stream address or key')
        print('SOUND PASS: unknown play ids and calls without the link key are refused; no addresses in the logs', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError, StopIteration)) else f'{type(e).__name__}: {e}'
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print('SOUND: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    smoke()
