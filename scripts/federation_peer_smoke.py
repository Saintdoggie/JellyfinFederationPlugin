#!/usr/bin/env python3
"""Disposable two-Jellyfin admin/viewer playback and revocation matrix."""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import urllib.parse
import federation_metadata_smoke as qa


def smoke():
    work = Path(tempfile.mkdtemp(prefix='federation-peer-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-peer-' + suffix
    names = ['fed-peer-source-' + suffix, 'fed-peer-receiver-' + suffix]
    created = []
    stage = 'setup'
    try:
        media = work / 'media'; media.mkdir()
        movie = media / 'QA Peer (2026).mkv'
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000', '-t', '4', '-c:v', 'libx264', '-c:a', 'aac', '-y', movie])
        payload = movie.read_bytes()
        qa.run(['podman', 'network', 'create', network])
        servers = []
        for number, name in enumerate(names):
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
            auth_header = {'Authorization':'MediaBrowser Client="Federation peer QA", Device="Sandbox", DeviceId="peer-qa", Version="1"'}
            auth = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-admin','Pw':password})
            admin = {'Authorization':'MediaBrowser Token="'+auth['AccessToken']+'"'}
            qa.request(base, '/Users/New', admin, {'Name':'qa-viewer'})
            viewer = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-viewer','Pw':''})
            qa.check(not viewer['User']['Policy']['IsAdministrator'], 'Viewer is elevated')
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'
            settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            servers.append((base, admin, {'Authorization':'MediaBrowser Token="'+viewer['AccessToken']+'"'}, viewer['User']['Id'], config))
        source, receiver = servers
        source_base, source_admin, _, _, _ = source
        base, admin, viewer, viewer_id, config = receiver
        qa.request(source_base, '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', source_admin,
            {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
        def movies(url, headers):
            return qa.request(url, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams', headers)['Items']
        native = qa.wait_for(lambda: movies(source_base, source_admin), 'Source did not scan media')[0]
        stage = 'actual friendship and catalog'
        sent = qa.request(base, '/Plugins/Federation/Friends/Send', admin, {'Url':f'http://{names[0]}:8096'})
        qa.check(sent['success'], 'Friend request failed')
        pending = qa.request(source_base, '/Plugins/Federation/Friends', source_admin)['incoming'][0]['Id']
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{pending}/Accept', source_admin, {})['success'], 'Friend accept failed')
        source_settings = qa.request(source_base, '/Plugins/Federation/Configuration', source_admin)
        source_friend = source_settings['RemoteServers'][0]['Id']
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/Sharing', source_admin, {'ShareAll':True})['success'], 'Sharing failed')
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        friend = settings['RemoteServers'][0]['Id']
        libraries = qa.request(source_base, '/Library/VirtualFolders', source_admin)
        library_id = next(l['ItemId'] for l in libraries if l['Name']=='QA Native')
        settings['LibraryMappings'] = [{'LocalLibraryName':'QA Federated','MediaType':'Movie','Enabled':True,'AutoProvision':True,
            'RemoteLibrarySources':[{'ServerId':friend,'RemoteLibraryId':library_id}]}]
        settings['RemoteServers'][0]['WanCapMode'] = 'Off'
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(base, '/Plugins/Federation/ProvisionLibraries', admin, {})
        def refreshed():
            qa.request(base, '/Plugins/Federation/Refresh', admin, {})
            return movies(base, admin)
        imported = qa.wait_for(refreshed, 'Friend catalog did not import')[0]; item_id = imported['Id']
        stage = 'Direct/Proxy admin/viewer streams'
        def path_only(url):
            p = urllib.parse.urlsplit(url)
            return p.path + ('?' + p.query if p.query else '')
        for mode in ('Direct','Proxy'):
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['RemoteServers'][0]['StreamingMode'] = mode
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            refreshed()
            for headers in (admin, viewer):
                info = qa.request(base, f'/Items/{item_id}/PlaybackInfo', headers)
                playable = info['MediaSources'][0]
                qa.check(playable.get('Size')==native['MediaSources'][0].get('Size'), 'Playback size differs from native Jellyfin source')
                qa.check(playable['Container']==native['MediaSources'][0]['Container'], 'Playback container differs from native Jellyfin source')
                stream = path_only(playable['Path'])
                _, served = qa.request(base, stream, headers, raw=True)
                qa.check(served == payload, 'Peer stream bytes differ')
                head, body = qa.request(base, stream, headers, method='HEAD', raw=True)
                qa.check(not body and int(head['Content-Length'])==len(payload), 'HEAD differs')
                _, ranged = qa.request(base, stream, {**headers,'Range':'bytes=137-648'}, expected=206, raw=True)
                qa.check(ranged==payload[137:649], 'Seek range differs')
            old_path = stream
            print(f'PEER PASS: {mode} native metadata, admin/viewer full playback, HEAD and seek ranges', flush=True)
        stage = 'viewer rule and old-capability revocation'
        result = qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId':viewer_id,'RemoteUserName':'qa-viewer','Mode':'Blocked'})
        qa.check(result['success'], 'Viewer revocation failed')
        qa.request(base, old_path, viewer, expected=403, raw=True)
        info = qa.request(base, f'/Items/{item_id}/PlaybackInfo', viewer)
        for denied in info.get('MediaSources', []):
            if denied.get('Path'):
                qa.request(base, path_only(denied['Path']), viewer, expected=403, raw=True)
        info = qa.request(base, f'/Items/{item_id}/PlaybackInfo', admin)
        qa.check(qa.request(base, path_only(info['MediaSources'][0]['Path']), admin, raw=True)[1]==payload, 'Viewer revocation blocked admin')
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId':viewer_id,'Mode':'AllLibraries'})['success'], 'Viewer restore failed')
        stage = 'disable, outage, cache retention and stable recovery'
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        settings['RemoteServers'][0]['Enabled'] = False
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(base, old_path, viewer, expected=403, raw=True)
        refreshed()
        qa.check(not movies(base, admin) and not movies(base, viewer), 'Disabled source remains visible')
        settings['RemoteServers'][0]['Enabled'] = True
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.wait_for(refreshed, 'Reenabled source did not return')
        cache = list(config.rglob('federation-cache.json'))[0]
        snapshot = json.loads(cache.read_text())
        qa.run(['podman','stop','-t','2',names[0]])
        for _ in range(2): qa.request(base, '/Plugins/Federation/Availability/Rescan', admin, {})
        qa.check(not movies(base, admin) and not movies(base, viewer), 'Offline source remains visible')
        refreshed()
        qa.check(json.loads(cache.read_text())==snapshot, 'Outage erased cached catalog')
        qa.run(['podman','start',names[0]])
        qa.wait_for(lambda: qa.request(source_base, '/System/Info/Public'), 'Source restart failed')
        qa.request(base, '/Plugins/Federation/Availability/Rescan', admin, {})
        recovered = qa.wait_for(lambda: movies(base, viewer), 'Source titles did not return')
        qa.check(recovered[0]['Id']==item_id, 'Recovery changed identity')
        info = qa.request(base, f'/Items/{item_id}/PlaybackInfo', viewer)
        qa.check(qa.request(base, path_only(info['MediaSources'][0]['Path']), viewer, raw=True)[1]==payload, 'Recovery playback failed')
        print('PEER PASS: viewer-only revocation, old URL denial, disabled/offline hiding, catalog retention and stable-ID recovery', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError)) else type(e).__name__
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('PEER: isolated containers cleaned up; production untouched', flush=True)

if __name__ == '__main__':
    smoke()
