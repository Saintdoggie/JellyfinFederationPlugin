#!/usr/bin/env python3
"""Disposable three-source Jellyfin web handoff fixture. Requires ADAPTIVE_BROWSER_RUNNER."""
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
    work = Path(tempfile.mkdtemp(prefix='federation-adaptive-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5); network = 'fed-adaptive-' + suffix
    names = ['fed-adaptive-' + str(n) + '-' + suffix for n in range(4)]
    created = []
    try:
        qa.run(['podman', 'network', 'create', network])
        servers = []
        for number, name in enumerate(names):
            media = work / ('media' + str(number)); media.mkdir()
            if number < 3:
                subtitle = media / 'subtitles.srt'; subtitle.write_text('1\n00:00:00,000 --> 00:01:30,000\nMatching subtitle\n')
                # Same 90-second timeline; subtitle insertion shifts the audio indexes on copies 2 and 3.
                movie = media / 'QA Adaptive (2026).mkv'
                args = ['podman','run','--rm','--security-opt','label=disable','-v',f'{work}:{work}',
                    '--entrypoint','/usr/lib/jellyfin-ffmpeg/ffmpeg',qa.JELLYFIN_IMAGE,
                    '-v','error','-f','lavfi','-i', 'testsrc2=size=' + ['320x180','640x360','960x540'][number] + ':rate=24',
                    '-f','lavfi','-i','sine=frequency=440:sample_rate=48000',
                    '-f','lavfi','-i','sine=frequency=880:sample_rate=48000','-i',str(subtitle),
                    '-map','0:v']
                if number: args += ['-map','3:s','-map','1:a','-map','2:a']
                else: args += ['-map','1:a','-map','2:a','-map','3:s']
                args += ['-t','90','-c:v','libx264','-preset','ultrafast','-crf','28','-g','48','-c:a','aac','-c:s','srt',
                    '-metadata:s:a:0','language=eng','-metadata:s:a:0','title=Main',
                    '-metadata:s:a:1','language=eng','-metadata:s:a:1','title=Commentary',
                    '-metadata:s:s:0','language=eng','-y',str(movie)]
                qa.run(args)
            config = work / name; plugin = config / 'plugins/Federation'; plugin.mkdir(parents=True)
            shutil.copy(qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
            created.append(name)
            qa.run(['podman','run','-d','--name',name,'--network',network,'--security-opt','label=disable',
                '-p','127.0.0.1::8096','-v',f'{config}:/config','-v',f'{media}:/media:ro',qa.JELLYFIN_IMAGE])
            base = 'http://' + qa.run(['podman','port',name,'8096/tcp'])
            qa.wait_for(lambda: qa.request(base, '/Startup/User'), 'Jellyfin startup failed')
            qa.request(base, '/Startup/Configuration', data={'UICulture':'en-US','MetadataCountryCode':'US','PreferredMetadataLanguage':'en'}, expected=204)
            password = secrets.token_urlsafe(32)
            qa.request(base, '/Startup/User', data={'Name':'qa-admin','Password':password}, expected=204)
            qa.request(base, '/Startup/RemoteAccess', data={'EnableRemoteAccess':True,'EnableAutomaticPortMapping':False}, expected=204)
            qa.request(base, '/Startup/Complete', data={}, expected=204)
            header = {'Authorization':'MediaBrowser Client="Adaptive QA", Device="Sandbox", DeviceId="adaptive-qa", Version="1"'}
            auth = qa.request(base, '/Users/AuthenticateByName', header, {'Username':'qa-admin','Pw':password})
            admin = {'Authorization': 'MediaBrowser Token="' + auth['AccessToken'] + '"'}
            qa.request(base, '/Users/New', admin, {'Name':'qa-viewer'})
            viewer = qa.request(base, '/Users/AuthenticateByName', header, {'Username':'qa-viewer','Pw':''})
            playback_policy = qa.request(base, '/System/Configuration', admin)
            playback_policy['MinResumeDurationSeconds'] = 0
            qa.request(base, '/System/Configuration', admin, playback_policy, expected=204)
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'; settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            settings['EnableAdaptivePlayback'] = number == 3
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            servers.append({'base':base,'admin':admin,'viewer':viewer,'config':str(config),'name':name})
            if number < 3:
                qa.request(base, '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', admin,
                    {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
                native = qa.wait_for(lambda: qa.request(base, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams', admin)['Items'], 'Source scan failed')[0]
                # Explicit stable provider identity gives dedup one logical movie.
                native = qa.request(base, '/Items/' + native['Id'], admin)
                native['ProviderIds'] = {'Imdb':'tt0816692'}
                native['LockData'] = True
                qa.request(base, '/Items/' + native['Id'], admin, native, expected=204)
                qa.check(qa.request(base, '/Items/' + native['Id'], admin).get('ProviderIds', {}).get('Imdb') == 'tt0816692', 'Native provider update failed')
                servers[-1]['native'] = native['Id']
        receiver = servers[3]; base = receiver['base']; admin = receiver['admin']
        mappings = []
        for source in servers[:3]:
            sent = qa.request(base, '/Plugins/Federation/Friends/Send', admin, {'Url':f'http://{source["name"]}:8096'})
            qa.check(sent['success'], 'Friend send failed')
            pending = qa.request(source['base'], '/Plugins/Federation/Friends', source['admin'])['incoming'][0]['Id']
            qa.check(qa.request(source['base'], f'/Plugins/Federation/Friends/{pending}/Accept', source['admin'], {})['success'], 'Friend accept failed')
            source_friend = qa.request(source['base'], '/Plugins/Federation/Configuration', source['admin'])['RemoteServers'][0]['Id']
            qa.request(source['base'], f'/Plugins/Federation/Friends/{source_friend}/Sharing', source['admin'], {'ShareAll':True})
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            friend = next(s for s in settings['RemoteServers'] if s['Url'] == f'http://{source["name"]}:8096')
            friend['Name'] = 'Server ' + str(len(mappings)+1); friend['StreamingMode'] = 'Proxy'; friend['WanCapMode'] = 'Off'
            source['friend'] = friend['Id']; source['sourceFriend'] = source_friend
            library = qa.request(source['base'], '/Library/VirtualFolders', source['admin'])[0]['ItemId']
            mappings.append({'ServerId':friend['Id'],'RemoteLibraryId':library})
            settings['LibraryMappings'] = [{'LocalLibraryName':'QA Federated','MediaType':'Movie','Enabled':True,'AutoProvision':True,'RemoteLibrarySources':mappings}]
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(base, '/Plugins/Federation/ProvisionLibraries', admin, {})
        def refresh():
            qa.request(base, '/Plugins/Federation/Refresh', admin, {})
            return qa.request(base, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams', admin)['Items']
        imported = qa.wait_for(refresh, 'Dedup import failed')
        qa.check(len(imported) == 1, 'Expected one deduplicated movie')
        receiver['item'] = imported[0]['Id']
        headers = {'Authorization': 'MediaBrowser Token="' + receiver['viewer']['AccessToken'] + '"'}
        candidates = qa.request(base, '/Plugins/Federation/Adaptive/' + receiver['item'] + '/Sources', headers)['Sources']
        qa.check(len(candidates) == 3, 'Missing authorized adaptive sources')
        qa.check(len(set(s['TimelineName'] for s in candidates)) == 1, 'Source timelines differ')
        qa.check(len(set(s['RunTimeTicks'] for s in candidates)) == 1, 'Source durations differ')
        for source in servers[:3]: source['candidate'] = next(s for s in candidates if s['Name'] == 'Server ' + str(servers.index(source)+1))
        state = work / 'browser-state.json'; state.write_text(json.dumps({'work':str(work),'servers':servers})); state.chmod(0o600)
        print('ADAPTIVE PASS: three real peers, one stable movie, source-bound metadata and shifted track indexes', flush=True)
        runner = os.environ.get('ADAPTIVE_BROWSER_RUNNER')
        if not runner: raise RuntimeError('Set ADAPTIVE_BROWSER_RUNNER to the Chromium runner')
        subprocess.run([os.environ.get('NODE_BINARY','node'),runner,str(state)],check=True)
    except Exception as e:
        detail = str(e) if isinstance(e, RuntimeError) else type(e).__name__
        raise RuntimeError(f'{detail}. Private fixture retained at {work}; never publish state/logs.') from None
    finally:
        if os.environ.get('ADAPTIVE_KEEP_FIXTURE') == '1':
            print('Retained only this disposable fixture for debugging:', work, flush=True)
        else:
            for name in reversed(created):
                subprocess.run(['podman','rm','-f',name],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            subprocess.run(['podman','network','rm',network],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)

if __name__ == '__main__': smoke()
