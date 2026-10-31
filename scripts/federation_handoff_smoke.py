#!/usr/bin/env python3
"""Disposable two-Jellyfin check of the remote-transcode handoff.

A viewer signed in to the *receiving* server plays a friend's title that must be
converted. Expected: the receiver negotiates, the *owner* (source) runs ffmpeg,
the playlist the player gets points at the owner for every segment, the player
fetches segments straight from the owner without any credential of the receiver,
subtitles come from the owner, the receiver runs no transcode at all, a stop on
the receiver frees the owner's job immediately, revoking the viewer cuts the
owner's stream, and switching the owner's permission off falls back to the
receiver converting (old behaviour) instead of failing.

Only containers/network created here are touched; production is never used.
"""
import json
import re
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.parse
import urllib.request
import federation_metadata_smoke as qa


def transcode_logs(config):
    # This server's own conversions; copies of a friend's log of a play it
    # converted (they start with "Conversion log from friend") don't count.
    return sorted(p for p in (config / 'log').glob('FFmpeg.Transcode-*.log')
                  if not p.read_text(errors='replace').startswith('Conversion log from friend'))


def friend_logs(config):
    return sorted(p for p in (config / 'log').glob('FFmpeg.Transcode-*.log')
                  if p.read_text(errors='replace').startswith('Conversion log from friend'))


def container_log(name):
    return subprocess.run(['podman', 'logs', name], capture_output=True, text=True).stdout + \
        subprocess.run(['podman', 'logs', name], capture_output=True, text=True).stderr


def smoke():
    work = Path(tempfile.mkdtemp(prefix='federation-handoff-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-handoff-' + suffix
    names = ['fed-handoff-source-' + suffix, 'fed-handoff-receiver-' + suffix]
    created = []
    stage = 'setup'
    try:
        media = work / 'media'; media.mkdir()
        movie = media / 'QA Handoff (2026).mkv'
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=24', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000', '-t', '90', '-c:v', 'libx264', '-preset', 'veryfast', '-b:v', '6M',
            '-c:a', 'aac', '-y', movie])
        (media / 'QA Handoff (2026).en.srt').write_text('1\n00:00:01,000 --> 00:00:04,000\nHandoff subtitle line\n\n')
        qa.run(['podman', 'network', 'create', network])
        servers = []
        for name in names:
            config = work / name
            plugin = config / 'plugins/Federation'; plugin.mkdir(parents=True)
            shutil.copy(qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
            created.append(name)
            qa.run(['podman', 'run', '-d', '--name', name, '--network', network, '--security-opt', 'label=disable',
                '-p', '127.0.0.1::8096', '-v', f'{config}:/config', '-v', f'{media}:/media:ro', qa.JELLYFIN_IMAGE])
            base = 'http://' + qa.run(['podman', 'port', name, '8096/tcp'])
            qa.wait_for(lambda: qa.request(base, '/Startup/User'), 'Jellyfin did not start')
            qa.request(base, '/Startup/Configuration', data={'UICulture':'en-US','MetadataCountryCode':'US','PreferredMetadataLanguage':'en'}, expected=204)
            password = secrets.token_urlsafe(32)
            qa.request(base, '/Startup/User', data={'Name':'qa-admin','Password':password}, expected=204)
            qa.request(base, '/Startup/RemoteAccess', data={'EnableRemoteAccess':True,'EnableAutomaticPortMapping':False}, expected=204)
            qa.request(base, '/Startup/Complete', data={}, expected=204)
            auth_header = {'Authorization':'MediaBrowser Client="Federation handoff QA", Device="Sandbox", DeviceId="handoff-qa", Version="1"'}
            auth = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-admin','Pw':password})
            admin = {'Authorization':'MediaBrowser Token="'+auth['AccessToken']+'"'}
            qa.request(base, '/Users/New', admin, {'Name':'qa-viewer'})
            viewer = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-viewer','Pw':''})
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'
            settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            servers.append((base, admin, {'Authorization':'MediaBrowser Token="'+viewer['AccessToken']+'"'}, viewer['User']['Id'], config, viewer['AccessToken']))
        (source_base, source_admin, _, _, source_config, _), (base, admin, viewer, viewer_id, config, viewer_token) = servers
        qa.request(source_base, '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', source_admin,
            {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
        def movies(url, headers):
            return qa.request(url, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams', headers)['Items']
        native = qa.wait_for(lambda: [m for m in movies(source_base, source_admin)
                                      if any(s.get('Type') == 'Subtitle' for s in m['MediaSources'][0].get('MediaStreams', []))],
                             'Source did not scan media with its subtitle')[0]

        stage = 'friendship, sharing and catalog'
        qa.check(qa.request(base, '/Plugins/Federation/Friends/Send', admin, {'Url':f'http://{names[0]}:8096'})['success'], 'Friend request failed')
        pending = qa.request(source_base, '/Plugins/Federation/Friends', source_admin)['incoming'][0]['Id']
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{pending}/Accept', source_admin, {})['success'], 'Friend accept failed')
        source_friend = qa.request(source_base, '/Plugins/Federation/Configuration', source_admin)['RemoteServers'][0]['Id']
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/Sharing', source_admin, {'ShareAll':True})['success'], 'Sharing failed')
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        friend = settings['RemoteServers'][0]['Id']
        library_id = next(l['ItemId'] for l in qa.request(source_base, '/Library/VirtualFolders', source_admin) if l['Name'] == 'QA Native')
        settings['LibraryMappings'] = [{'LocalLibraryName':'QA Federated','MediaType':'Movie','Enabled':True,'AutoProvision':True,
            'RemoteLibrarySources':[{'ServerId':friend,'RemoteLibraryId':library_id}]}]
        settings['RemoteServers'][0]['WanCapMode'] = 'Off'
        settings['RemoteServers'][0]['StreamingMode'] = 'Direct'
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(base, '/Plugins/Federation/ProvisionLibraries', admin, {})
        def refreshed():
            qa.request(base, '/Plugins/Federation/Refresh', admin, {})
            return movies(base, admin)
        item_id = qa.wait_for(refreshed, 'Friend catalog did not import')[0]['Id']

        profile = {
            'MaxStreamingBitrate': 120000000,
            'DirectPlayProfiles': [],
            'TranscodingProfiles': [{'Container':'ts','Type':'Video','VideoCodec':'h264','AudioCodec':'aac','Protocol':'hls',
                                     'Context':'Streaming','MaxAudioChannels':'2','MinSegments':1,'BreakOnNonKeyFrames':True}],
            'SubtitleProfiles': [{'Format':'vtt','Method':'External'}],
            'CodecProfiles': [], 'ContainerProfiles': [],
        }
        def negotiate(headers, bitrate=3000000):
            body = {'UserId': viewer_id, 'MaxStreamingBitrate': bitrate, 'EnableDirectPlay': False, 'EnableDirectStream': False,
                    'EnableTranscoding': True, 'AllowVideoStreamCopy': False, 'AllowAudioStreamCopy': False, 'DeviceProfile': profile,
                    'SubtitleStreamIndex': next(s['Index'] for s in native['MediaSources'][0]['MediaStreams'] if s['Type'] == 'Subtitle')}
            info = qa.request(base, f'/Items/{item_id}/PlaybackInfo?userId={viewer_id}', headers, body)
            source = info['MediaSources'][0]
            qa.check(source.get('TranscodingUrl'), 'Receiver offered no transcode for a must-convert profile')
            return info['PlaySessionId'], source
        source_host = f'http://{names[0]}:8096'
        def on_source(url):
            # The owner writes absolute URLs for the address the request reached it
            # on: the container name when the receiver fetched, the host-mapped port
            # when this test (playing the player) fetched directly.
            for prefix in (source_host, source_base):
                if url.startswith(prefix):
                    return source_base + url[len(prefix):]
            raise RuntimeError(f'Expected an owner URL, got {url[:60]}')

        stage = 'handoff: playlist from the owner, segments straight from the owner'
        before = len(transcode_logs(config))
        owner_before = len(transcode_logs(source_config))
        play_session, source = negotiate(viewer)
        _, master = qa.request(base, source['TranscodingUrl'], viewer, raw=True)
        # The receiver starts the owner's conversion as soon as the master
        # playlist went out, before the player asks for a segment.
        qa.wait_for(lambda: len(transcode_logs(source_config)) > owner_before or None,
                    'The owner did not start converting ahead of the player', seconds=20)
        master = master.decode()
        qa.check(viewer_token not in master and 'ApiKey' not in master and 'api_key' not in master, 'A credential reached the playlist')
        variant_url = next(l for l in master.splitlines() if l and not l.startswith('#'))
        # The variant playlist comes through the receiver's relay, which puts the
        # owner's addresses on the address the receiver uses for the owner (an
        # owner behind a proxy can write http ones an https page would refuse).
        relay = '/Plugins/Federation/HandoffPlaylist?u='
        qa.check(variant_url.startswith(relay), 'Master playlist does not send the variant through the receiver relay')
        qa.check(f'{source_host}/Plugins/Federation/Peer/Play/' in urllib.parse.unquote(variant_url[len(relay):]), 'Relayed variant is not the owner gateway')
        _, variant = qa.request(base, variant_url, None, raw=True)
        variant = variant.decode()
        segment_url = next(l for l in variant.splitlines() if l and not l.startswith('#'))
        _, segment = qa.request(on_source(segment_url), '', None, raw=True)
        qa.check(len(segment) > 10000 and segment[0] == 0x47, 'First segment is not MPEG-TS video')
        qa.check(len(transcode_logs(config)) == before, 'The receiving server ran a transcode itself')
        qa.check(len(transcode_logs(source_config)) == owner_before + 1, 'The player\'s first segment restarted the conversion the receiver had started')
        print(f'HANDOFF PASS: receiver negotiated, owner converted; {len(segment)} byte first segment fetched from the owner with no receiver credential; receiver ran no ffmpeg', flush=True)

        stage = 'seek: a retried segment joins the running conversion instead of restarting it'
        # A fresh play jumping straight to a far segment: nothing is converted
        # for it yet, so the owner must start there. The player gives up after
        # 0.3 s and asks again; that retry must join the running conversion, not
        # restart it (Jellyfin restarts whenever no segment exists yet).
        owner_before = len(transcode_logs(source_config))
        _, seek_source = negotiate(viewer, bitrate=2800000)
        _, seek_master = qa.request(base, seek_source['TranscodingUrl'], viewer, raw=True)
        seek_variant_url = next(l for l in seek_master.decode().splitlines() if l and not l.startswith('#'))
        _, seek_variant = qa.request(base, seek_variant_url, None, raw=True)
        segments = [l for l in seek_variant.decode().splitlines() if l and not l.startswith('#')]
        far = on_source(segments[-2])
        try:
            urllib.request.urlopen(far, timeout=0.3).read()
        except Exception:
            pass  # the player giving up before the segment is ready
        _, seeked = qa.request(far, '', None, raw=True)
        qa.check(len(seeked) > 10000 and seeked[0] == 0x47, 'Seeked segment is not MPEG-TS video')
        # Expected: the receiver's early start of segment 0, then one start for
        # the jump. A third would be the retry restarting it.
        restarts = len(transcode_logs(source_config)) - owner_before
        qa.check(restarts <= 2, f'The retried segment restarted the owner conversion again ({restarts} starts)')
        print(f'HANDOFF PASS: a seek starts the owner conversion {restarts} time(s); the player\'s retry of that segment joins it', flush=True)

        stage = 'subtitles come from the owner'
        subtitle = next((s for s in source.get('MediaStreams', []) if s.get('Type') == 'Subtitle' and s.get('DeliveryUrl')), None)
        qa.check(subtitle is not None, 'No external subtitle offered')
        _, vtt = qa.request(base, subtitle['DeliveryUrl'], viewer, raw=True)
        qa.check(b'Handoff subtitle line' in vtt, 'Subtitle text missing')
        print('HANDOFF PASS: external subtitle served from the owner through the receiver', flush=True)

        stage = 'universal audio uses the owner playback user'
        play_token = urllib.parse.urlsplit(on_source(segment_url)).path.split('/Plugins/Federation/Peer/Play/', 1)[1].split('/', 1)[0]
        universal = source_base + '/Plugins/Federation/Peer/Play/' + play_token + '/audio/' + native['Id'] + '/universal?AudioCodec=aac&UserId=00000000000000000000000000000000'
        audio_head, empty = qa.request(universal, '', None, method='HEAD', raw=True)
        _, audio = qa.request(universal, '', None, raw=True)
        qa.check(not empty and len(audio) > 10000 and audio_head.get('Content-Type','').startswith('audio/'), 'Universal-audio gateway did not serve audio')
        print(f'HANDOFF PASS: universal audio GET/HEAD served {len(audio)} bytes; client UserId could not replace the internal playback user', flush=True)

        stage = 'stop on the receiver frees the owner job at once'
        qa.request(base, '/Sessions/Playing', viewer, {'ItemId': item_id, 'PlaySessionId': play_session, 'MediaSourceId': source['Id'], 'PlayMethod': 'Transcode'}, expected=204)
        qa.request(base, '/Sessions/Playing/Progress', viewer, {'ItemId': item_id, 'PlaySessionId': play_session, 'MediaSourceId': source['Id'], 'IsPaused': True, 'PositionTicks': 300000000}, expected=204)
        qa.request(base, '/Sessions/Playing/Stopped', viewer, {'ItemId': item_id, 'PlaySessionId': play_session, 'MediaSourceId': source['Id'], 'PositionTicks': 300000000}, expected=204)
        started = time.monotonic()
        qa.wait_for(lambda: 'Deleting partial stream file' in container_log(names[0]) or None, 'Owner job was not stopped', seconds=30)
        qa.check('kill timer stopped' not in container_log(names[0]), 'Owner job only ended by its idle timer')
        resume = qa.request(base, f'/Items/{item_id}?userId={viewer_id}', viewer)
        # 90 s is under Jellyfin's minimum resume duration, so the receiver records
        # the play (LastPlayedDate) rather than a resume point.
        qa.check((resume.get('UserData') or {}).get('LastPlayedDate'), 'Playback was not recorded on the receiver')
        print(f'HANDOFF PASS: stop relayed, owner job ended after {time.monotonic() - started:.1f}s; playback recorded on the receiver', flush=True)

        stage = 'the owner\'s conversion log of the play lands in the receiver\'s logs'
        copied = qa.wait_for(lambda: friend_logs(config) or None, 'No friend conversion log in the receiver logs', seconds=30)
        text = copied[-1].read_text(errors='replace')
        qa.check('ffmpeg' in text.lower() and 'Stream mapping' in text, 'Copied log is not the owner ffmpeg log')
        qa.check(not re.search(r'(?i)(token|api_?key)=(?!REDACTED)[0-9a-z]{16,}', text), 'A credential reached the copied log')
        print(f'HANDOFF PASS: owner conversion log copied into the receiver logs ({copied[-1].name}), credentials blanked', flush=True)

        stage = 'revoking the viewer cuts the owner stream'
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId': viewer_id, 'RemoteUserName': 'qa-viewer', 'Mode': 'Blocked'})['success'], 'Block failed')
        qa.request(on_source(segment_url), '', None, expected=403, raw=True)
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId': viewer_id, 'Mode': 'AllLibraries'})['success'], 'Unblock failed')
        qa.wait_for(lambda: movies(base, viewer) or None, 'Viewer did not get the title back')
        print('HANDOFF PASS: owner refuses the viewer\'s in-flight stream URLs immediately after a block', flush=True)

        stage = 'owner switches the handoff off: receiver converts, nothing breaks'
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteTranscode', source_admin, {'Allow': False})['success'], 'Switch off failed')
        _, source = negotiate(viewer, bitrate=2500000)
        before = len(transcode_logs(config))
        _, master = qa.request(base, source['TranscodingUrl'], viewer, raw=True)
        master = master.decode()
        qa.check(source_host not in master and 'HandoffPlaylist' not in master, 'Receiver still handed off after the owner switched it off')
        variant = next(l for l in master.splitlines() if l and not l.startswith('#'))
        variant_path = urllib.parse.urljoin(source['TranscodingUrl'], variant)
        _, playlist = qa.request(base, variant_path, viewer, raw=True)
        segment = next(l for l in playlist.decode().splitlines() if l and not l.startswith('#'))
        _, data = qa.request(base, urllib.parse.urljoin(variant_path, segment), viewer, raw=True)
        qa.check(len(data) > 10000 and data[0] == 0x47, 'Fallback segment is not MPEG-TS video')
        qa.check(len(transcode_logs(config)) > before, 'Fallback did not transcode on the receiver')
        print('HANDOFF PASS: owner opt-out falls back to the receiver converting, playback still works', flush=True)

        stage = 'owner limit: a session over it is refused and converts on the receiver'
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteTranscode', source_admin, {'Allow': True})['success'], 'Switch on failed')
        qa.check(qa.request(source_base, '/Plugins/Federation/Settings/RemoteTranscode', source_admin,
            {'EnableRemoteTranscodeForFriends': True, 'MaxRemoteTranscodesPerFriend': 1, 'UseFriendsTranscoders': True})['success'], 'Limit failed')
        settings = qa.request(source_base, '/Plugins/Federation/Settings/RemoteTranscode', source_admin)
        qa.check(settings['MaxRemoteTranscodesPerFriend'] == 1, 'Limit not saved')
        # The receiver backs off a friend for two minutes after a refusal (the
        # opt-out above), then tries the handoff again.
        def handed_off():
            _, src = negotiate(viewer, bitrate=2000000)
            _, body = qa.request(base, src['TranscodingUrl'], viewer, raw=True)
            return (src, body.decode()) if 'HandoffPlaylist' in body.decode() else None
        first_source, first = qa.wait_for(handed_off, 'Handoff did not resume after switching it back on', seconds=200)
        first_variant = next(l for l in first.splitlines() if l and not l.startswith('#'))
        qa.request(base, first_variant, None, raw=True)
        # A friend's title plays only from the friend: over its limit, the
        # player gets the friend's refusal and the receiver converts nothing.
        before = len(transcode_logs(config))
        _, second_source = negotiate(viewer, bitrate=1500000)
        qa.request(base, second_source['TranscodingUrl'], viewer, expected=429, raw=True)
        qa.check(len(transcode_logs(config)) == before, 'The receiver converted a friend title itself')
        # The play that is running stays with the owner.
        qa.request(base, first_variant, None, raw=True)
        print('HANDOFF PASS: owner limit of 1 refuses a second concurrent session; the player gets the refusal, the receiver never converts it', flush=True)

        stage = 'owner restart: plays keep handing off without restarting the receiver'
        qa.check(qa.request(source_base, '/Plugins/Federation/Settings/RemoteTranscode', source_admin,
            {'EnableRemoteTranscodeForFriends': True, 'MaxRemoteTranscodesPerFriend': 0, 'UseFriendsTranscoders': True})['success'], 'Unlimit failed')
        qa.run(['podman', 'restart', '-t', '5', names[0]])
        qa.wait_for(lambda: qa.request(source_base, '/System/Info/Public'), 'Owner did not come back')
        time.sleep(3)
        _, after_restart = handed_off() or (None, '')
        qa.check('HandoffPlaylist' in after_restart, 'After the owner restarted, the receiver could not hand off with its stored tokens')
        print('HANDOFF PASS: after the owner restarts, the next play hands off at once (tokens kept / refreshed)', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError, StopIteration)) else f'{type(e).__name__}: {e}'
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('HANDOFF: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    smoke()
