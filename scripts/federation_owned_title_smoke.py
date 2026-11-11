#!/usr/bin/env python3
"""Disposable two-Jellyfin check of the own-copy rule.

The receiving server owns a title as HEVC; a friend holds the same title as
H.264. Expected:

* the friend's copy never becomes a second library title;
* a device that plays HEVC gets only the own file;
* a device that only plays H.264 is offered the friend's copy first (it plays
  as stored) with the own file kept as the copy to convert, and the stream
  request for the friend's copy is sent straight to the friend, with seeking.

Only containers/network created here are touched; production is never used.
"""
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import federation_metadata_smoke as qa
from federation_handoff_smoke import container_log
from federation_peer_smoke import no_redirect


def smoke():
    work = Path(tempfile.mkdtemp(prefix='federation-owned-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-owned-' + suffix
    names = [f'fed-owned-{role}-{suffix}' for role in ('friend', 'receiver')]
    created = []
    stage = 'setup'
    try:
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        # The id in the name is what the receiver matches its own title and the friend's copy on.
        title = 'QA Owned (2026) [imdbid-tt9912346].mkv'
        for name, codec in zip(names, ('libx264', 'libx265')):
            media = work / ('media-' + name); media.mkdir()
            qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=24', '-f', 'lavfi', '-i',
                'sine=frequency=440:sample_rate=48000', '-t', '60', '-c:v', codec, '-preset', 'veryfast', '-b:v', '3M',
                '-c:a', 'aac', '-metadata:s:a:0', 'language=eng', '-y', media / title])
        qa.run(['podman', 'network', 'create', network])
        servers = []
        for name in names:
            config = work / name
            plugin = config / 'plugins/Federation'; plugin.mkdir(parents=True)
            shutil.copy(qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
            created.append(name)
            qa.run(['podman', 'run', '-d', '--name', name, '--network', network, '--security-opt', 'label=disable',
                '-p', '127.0.0.1::8096', '-v', f'{config}:/config', '-v', f'{work / ("media-" + name)}:/media:ro', qa.JELLYFIN_IMAGE])
            base = 'http://' + qa.run(['podman', 'port', name, '8096/tcp'])
            qa.wait_for(lambda: qa.request(base, '/Startup/User'), 'Jellyfin did not start')
            qa.request(base, '/Startup/Configuration', data={'UICulture':'en-US','MetadataCountryCode':'US','PreferredMetadataLanguage':'en'}, expected=204)
            password = secrets.token_urlsafe(32)
            qa.request(base, '/Startup/User', data={'Name':'qa-admin','Password':password}, expected=204)
            qa.request(base, '/Startup/RemoteAccess', data={'EnableRemoteAccess':True,'EnableAutomaticPortMapping':False}, expected=204)
            qa.request(base, '/Startup/Complete', data={}, expected=204)
            auth_header = {'Authorization':'MediaBrowser Client="Federation owned QA", Device="Sandbox", DeviceId="owned-qa", Version="1"'}
            auth = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-admin','Pw':password})
            admin = {'Authorization':'MediaBrowser Token="'+auth['AccessToken']+'"'}
            qa.request(base, '/Users/New', admin, {'Name':'qa-viewer'})
            viewer = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-viewer','Pw':''})
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'
            settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            qa.request(base, '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', admin,
                {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
            servers.append({'name': name, 'base': base, 'admin': admin, 'host': f'http://{name}:8096',
                            'viewer': {'Authorization':'MediaBrowser Token="'+viewer['AccessToken']+'"'}, 'viewer_id': viewer['User']['Id']})
        friend, receiver = servers
        base, admin, viewer, viewer_id = receiver['base'], receiver['admin'], receiver['viewer'], receiver['viewer_id']
        def movies(url, headers):
            return qa.request(url, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,ProviderIds', headers)['Items']
        for server in servers:
            qa.wait_for(lambda: [m for m in movies(server['base'], server['admin']) if m.get('ProviderIds', {}).get('Imdb')],
                        'A server did not scan its title with the id from the file name')
        own = movies(base, admin)[0]

        stage = 'friendship and a shared library; the friend copy of an owned title is not added'
        qa.check(qa.request(base, '/Plugins/Federation/Friends/Send', admin, {'Url': friend['host']})['success'], 'Friend request failed')
        pending = qa.request(friend['base'], '/Plugins/Federation/Friends', friend['admin'])['incoming'][0]['Id']
        qa.check(qa.request(friend['base'], f'/Plugins/Federation/Friends/{pending}/Accept', friend['admin'], {})['success'], 'Friend accept failed')
        theirs = qa.request(friend['base'], '/Plugins/Federation/Configuration', friend['admin'])['RemoteServers'][0]['Id']
        qa.check(qa.request(friend['base'], f'/Plugins/Federation/Friends/{theirs}/Sharing', friend['admin'], {'ShareAll':True})['success'], 'Sharing failed')
        library_id = next(l['ItemId'] for l in qa.request(friend['base'], '/Library/VirtualFolders', friend['admin']) if l['Name'] == 'QA Native')
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        settings['LibraryMappings'] = [{'LocalLibraryName':'QA Federated','MediaType':'Movie','Enabled':True,'AutoProvision':True,
            'RemoteLibrarySources':[{'ServerId':settings['RemoteServers'][0]['Id'],'RemoteLibraryId':library_id}]}]
        settings['RemoteServers'][0]['WanCapMode'] = 'Off'
        settings['RemoteServers'][0]['StreamingMode'] = 'Direct'
        settings['SmartSourceSelection'] = True
        settings['EnableDedup'] = True
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(base, '/Plugins/Federation/ProvisionLibraries', admin, {})

        def profile(video_codecs):
            return {
                'MaxStreamingBitrate': 120000000,
                'DirectPlayProfiles': [{'Type':'Video','Container':'mkv,mp4','VideoCodec':video_codecs,'AudioCodec':'aac'}],
                'TranscodingProfiles': [{'Container':'ts','Type':'Video','VideoCodec':'h264','AudioCodec':'aac','Protocol':'hls','Context':'Streaming'}],
                'SubtitleProfiles': [], 'CodecProfiles': [], 'ContainerProfiles': [],
            }
        def negotiate(video_codecs):
            body = {'UserId': viewer_id, 'MaxStreamingBitrate': 120000000, 'EnableDirectPlay': True, 'EnableDirectStream': True,
                    'EnableTranscoding': True, 'DeviceProfile': profile(video_codecs)}
            return qa.request(base, f'/Items/{own["Id"]}/PlaybackInfo?userId={viewer_id}', viewer, body)['MediaSources']
        def offered():
            qa.request(base, '/Plugins/Federation/Refresh', admin, {})
            sources = negotiate('h264')
            return sources if len(sources) == 2 else None
        sources = qa.wait_for(offered, 'A device that cannot play the own file was not offered the friend\'s copy')
        qa.check(len(movies(base, admin)) == 1, 'The friend\'s copy of an owned title became a second library title')
        print('OWNED PASS: the friend holds the title too, and the library still shows it once', flush=True)

        stage = 'a device that only plays H.264 gets the friend copy first, the own file as the one to convert'
        own_id = own['Id'].replace('-', '')
        first, second = sources
        qa.check(first['Id'].replace('-', '') != own_id and first['Protocol'] == 'Http', 'The friend\'s copy is not offered first')
        qa.check(first.get('SupportsDirectStream') or first.get('SupportsDirectPlay'), 'The friend\'s copy is not offered to play as stored')
        qa.check(not first.get('TranscodingUrl'), 'The friend\'s copy was offered for conversion')
        qa.check(second['Id'].replace('-', '') == own_id and second.get('TranscodingUrl'), 'The own file is not kept as the copy to convert')
        qa.check('is owned here but' in container_log(receiver['name']), 'The receiver did not log the offer')
        print('OWNED PASS: H.264-only device: friend copy first (plays as stored), own HEVC file second (to convert)', flush=True)

        stage = 'the stream request for the friend copy goes straight to the friend, with seeking'
        status, headers, _ = no_redirect(base, f'/Videos/{own["Id"]}/stream.mkv?static=true&mediaSourceId={first["Id"]}', viewer)
        target = headers.get('Location') or headers.get('location') or ''
        qa.check(status == 302 and target.startswith(friend['host'] + '/Plugins/Federation/DirectStream/'), f'Stream request was not sent to the friend (HTTP {status})')
        got, head = qa.request(friend['base'] + target[len(friend['host']):], '', {'Range': 'bytes=0-65535'}, expected=206, raw=True)
        qa.check(head[:4] == b'\x1a\x45\xdf\xa3', 'The friend did not send the Matroska file')
        size = int(got.get('Content-Range', 'bytes 0-0/0').split('/')[-1])
        _, tail = qa.request(friend['base'] + target[len(friend['host']):], '', {'Range': f'bytes={size - 1000}-'}, expected=206, raw=True)
        qa.check(len(tail) == 1000, 'Seeking to the end of the friend\'s file failed')
        print(f'OWNED PASS: stream request redirected to the friend; first bytes and a seek to the end of its {size} byte file served', flush=True)

        stage = 'a player that refuses to play as stored is not given the friend copy to convert'
        refused = qa.request(base, f'/Items/{own["Id"]}/PlaybackInfo?userId={viewer_id}', viewer,
            {'UserId': viewer_id, 'MaxStreamingBitrate': 120000000, 'EnableDirectPlay': False, 'EnableDirectStream': False,
             'EnableTranscoding': True, 'DeviceProfile': profile('h264')})['MediaSources']
        qa.check(len(refused) == 1 and refused[0]['Id'].replace('-', '') == own_id and refused[0].get('TranscodingUrl'),
                 'With direct play refused, something other than converting the own file was offered')
        print('OWNED PASS: direct play refused by the player: only the own file, converted here', flush=True)
        qa.wait_for(lambda: len(negotiate('h264')) == 2 or None, 'The offer did not come back for a normal request', seconds=30)

        stage = 'a device that plays the own file gets only the own file'
        capable = negotiate('h264,hevc')
        qa.check(len(capable) == 1 and capable[0]['Id'].replace('-', '') == own_id, 'A device that plays the own file was still offered a friend\'s copy')
        status, _, _ = no_redirect(base, f'/Videos/{own["Id"]}/stream.mkv?static=true&mediaSourceId={own["Id"]}', viewer)
        qa.check(status == 200, f'The own file did not play from this server (HTTP {status})')
        print('OWNED PASS: HEVC-capable device: own file only, served by this server', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError, StopIteration)) else f'{type(e).__name__}: {e}'
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('OWNED: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    smoke()
