#!/usr/bin/env python3
"""Disposable two-Jellyfin check of version choice on one friend.

A friend holds one title as two versions: a bigger HEVC file and a smaller
H.264 one. Expected on the receiving server:

* a device that only plays H.264 gets the H.264 version under the title's own
  id, as stored, with the HEVC one as second choice;
* later requests of that play and the stream itself stay on that file;
* a device that plays HEVC gets the bigger version;
* a version the viewer picks is the one that plays.

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
    work = Path(tempfile.mkdtemp(prefix='federation-versions-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-versions-' + suffix
    names = [f'fed-versions-{role}-{suffix}' for role in ('friend', 'receiver')]
    created = []
    stage = 'setup'
    try:
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        # The friend holds one title as two versions in one folder: a bigger
        # HEVC file and a smaller H.264 one. The receiver has no copy of its own.
        folder = 'QA Versions (2026)'
        files = {'hevc': f'{folder} - 1080p HEVC.mkv', 'h264': f'{folder} - 720p H264.mkv'}
        for name in names:
            (work / ('media-' + name)).mkdir()
        target = work / ('media-' + names[0]) / folder; target.mkdir()
        for codec, size, rate, key in (('libx265', '1920x1080', '6M', 'hevc'), ('libx264', '1280x720', '3M', 'h264')):
            qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', f'testsrc2=size={size}:rate=24', '-f', 'lavfi', '-i',
                'sine=frequency=440:sample_rate=48000', '-t', '60', '-c:v', codec, '-preset', 'veryfast', '-b:v', rate,
                '-c:a', 'aac', '-metadata:s:a:0', 'language=eng', '-y', target / files[key]])
        sizes = {key: (target / files[key]).stat().st_size for key in files}
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
            auth_header = {'Authorization':'MediaBrowser Client="Federation versions QA", Device="Sandbox", DeviceId="owned-qa", Version="1"'}
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
        qa.wait_for(lambda: [m for m in movies(friend['base'], friend['admin']) if len(m.get('MediaSources') or []) == 2],
                    'The friend did not scan the title as one item with two versions')
        stage = 'friendship and a shared library'
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
            return qa.request(base, f'/Items/{title["Id"]}/PlaybackInfo?userId={viewer_id}', viewer, body)['MediaSources']
        def listed():
            qa.request(base, '/Plugins/Federation/Refresh', admin, {})
            found = movies(base, viewer)
            return found[0] if found else None
        title = qa.wait_for(listed, 'The friend\'s title did not appear on the receiver')
        title_id = title['Id'].replace('-', '')
        def codec(source):
            return next(s['Codec'] for s in source['MediaStreams'] if s['Type'] == 'Video')

        stage = 'a device that only plays H.264 gets the H.264 version as the title, as stored'
        sources = qa.wait_for(lambda: (lambda found: found if len(found) == 2 else None)(negotiate('h264')), 'Both versions were not offered')
        first, second = sources
        qa.check(codec(first) == 'h264' and first['Id'].replace('-', '') == title_id, f'The title did not play its H.264 version first (got {codec(first)})')
        qa.check(first.get('Size') == sizes['h264'], 'The first version is not the H.264 file')
        qa.check((first.get('SupportsDirectStream') or first.get('SupportsDirectPlay')) and not first.get('TranscodingUrl'), 'The H.264 version was not offered as stored')
        qa.check(codec(second) == 'hevc' and second['Id'].replace('-', '') != title_id, 'The HEVC version is not kept as the second choice')
        qa.check('plays on this device as it is' in container_log(receiver['name']), 'The receiver did not log why it chose the version')
        print('VERSIONS PASS: H.264-only device: the 720p H.264 version plays as stored, the bigger HEVC one is second', flush=True)

        stage = 'later requests of that play (sent without the device\'s abilities) get the same version'
        plain = qa.request(base, f'/Items/{title["Id"]}/PlaybackInfo?userId={viewer_id}', viewer)['MediaSources']
        kept = next(s for s in plain if s['Id'].replace('-', '') == title_id)
        qa.check(codec(kept) == 'h264', 'A follow-up request switched the title to the other version')
        status, headers, _ = no_redirect(base, f'/Videos/{title["Id"]}/stream.mkv?static=true&mediaSourceId={title["Id"]}', viewer)
        target = headers.get('Location') or headers.get('location') or ''
        qa.check(status == 302 and target.startswith(friend['host'] + '/Plugins/Federation/DirectStream/'), f'Stream request was not sent to the friend (HTTP {status})')
        got, _ = qa.request(friend['base'] + target[len(friend['host']):], '', {'Range': 'bytes=0-1023'}, expected=206, raw=True)
        qa.check(int(got.get('Content-Range', 'bytes 0-0/0').split('/')[-1]) == sizes['h264'], 'The stream of the title is not the H.264 file')
        print('VERSIONS PASS: follow-up request and the stream itself stay on the H.264 file', flush=True)

        stage = 'a device that plays HEVC gets the bigger version'
        capable = negotiate('h264,hevc')
        qa.check(len(capable) == 2 and codec(capable[0]) == 'hevc' and capable[0]['Id'].replace('-', '') == title_id, 'A device that plays HEVC did not get the bigger version')
        qa.check(capable[0].get('Size') == sizes['hevc'] and not capable[0].get('TranscodingUrl'), 'The HEVC version was not offered as stored')
        print('VERSIONS PASS: HEVC-capable device: the 1080p HEVC version plays as stored', flush=True)

        stage = 'a version the viewer picks is the one that plays'
        picked = second['Id']
        status, headers, _ = no_redirect(base, f'/Videos/{title["Id"]}/stream.mkv?static=true&mediaSourceId={picked}', viewer)
        target = headers.get('Location') or headers.get('location') or ''
        qa.check(status == 302, f'The picked version was not sent to the friend (HTTP {status})')
        got, _ = qa.request(friend['base'] + target[len(friend['host']):], '', {'Range': 'bytes=0-1023'}, expected=206, raw=True)
        qa.check(int(got.get('Content-Range', 'bytes 0-0/0').split('/')[-1]) == sizes['hevc'], 'The picked version is not the HEVC file')
        print('VERSIONS PASS: a version picked by the viewer plays that file', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError, StopIteration)) else f'{type(e).__name__}: {e}'
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('VERSIONS: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    smoke()
