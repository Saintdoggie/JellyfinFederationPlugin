#!/usr/bin/env python3
"""Two disposable Jellyfin servers with a small shared catalogue, for looking at
the web additions in a real browser (server tabs, themes, the plugin's pages).

`Lab()` is used by `federation_ui_smoke.py`. Run this file directly to keep the
servers up and print where they are:

    JELLYFIN_TEST_IMAGE=<image> python3 scripts/federation_ui_lab.py --hold

The addresses and the generated admin password go to a mode-0600 file in the
private fixture folder, never to the terminal. Stop with Ctrl-C (or delete the
`hold` file it names); only the containers and network it made are removed.
"""
import json
import os
from pathlib import Path
import secrets
import shutil
import sys
import tempfile
import time
import federation_metadata_smoke as qa

# Titles on the friend, titles only on this server, and titles both hold (the id in
# the name is what the two servers match on).
FRIEND_ONLY = [('QA Friend %02d (2026)' % n, 'tt99200%02d' % n) for n in range(1, 10)]
HERE_ONLY = [('QA Here %02d (2026)' % n, 'tt99300%02d' % n) for n in range(1, 5)]
SHARED = [('QA Shared %02d (2026)' % n, 'tt99400%02d' % n) for n in range(1, 4)]


class Lab:
    def __init__(self):
        self.work = Path(tempfile.mkdtemp(prefix='federation-ui-qa-')); os.chmod(self.work, 0o700)
        self.suffix = secrets.token_hex(5)
        self.network = 'fed-ui-' + self.suffix
        self.names = [f'fed-ui-{role}-{self.suffix}' for role in ('friend', 'receiver')]
        self.created = []
        self.servers = []

    def up(self, dedup=True):
        work, names = self.work, self.names
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        seed = work / 'seed.mkv'
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=24', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000', '-t', '8', '-c:v', 'libx264', '-preset', 'veryfast', '-c:a', 'aac',
            '-metadata:s:a:0', 'language=eng', '-y', seed])
        for name, titles in zip(names, (FRIEND_ONLY + SHARED, HERE_ONLY + SHARED)):
            media = work / ('media-' + name); media.mkdir()
            for title, imdb in titles:
                shutil.copy(seed, media / f'{title} [imdbid-{imdb}].mkv')
        qa.run(['podman', 'network', 'create', self.network])
        for name in names:
            config = work / name
            plugin = config / 'plugins/Federation'; plugin.mkdir(parents=True)
            shutil.copy(qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
            meta = qa.REPO / 'bin/Release/net10.0/meta.json'
            if meta.exists():
                shutil.copy(meta, plugin)
            self.created.append(name)
            qa.run(['podman', 'run', '-d', '--name', name, '--network', self.network, '--security-opt', 'label=disable',
                '-p', '127.0.0.1::8096', '-v', f'{config}:/config', '-v', f'{work / ("media-" + name)}:/media:ro', qa.JELLYFIN_IMAGE])
            base = 'http://' + qa.run(['podman', 'port', name, '8096/tcp'])
            qa.wait_for(lambda: qa.request(base, '/Startup/User'), 'Jellyfin did not start')
            qa.request(base, '/Startup/Configuration', data={'UICulture':'en-US','MetadataCountryCode':'US','PreferredMetadataLanguage':'en'}, expected=204)
            password = secrets.token_urlsafe(24)
            qa.request(base, '/Startup/User', data={'Name':'qa-admin','Password':password}, expected=204)
            qa.request(base, '/Startup/RemoteAccess', data={'EnableRemoteAccess':True,'EnableAutomaticPortMapping':False}, expected=204)
            qa.request(base, '/Startup/Complete', data={}, expected=204)
            auth_header = {'Authorization':'MediaBrowser Client="Federation UI QA", Device="Sandbox", DeviceId="ui-qa", Version="1"'}
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
            self.servers.append({'name': name, 'base': base, 'admin': admin, 'password': password, 'host': f'http://{name}:8096',
                'viewer': {'Authorization':'MediaBrowser Token="'+viewer['AccessToken']+'"'}, 'viewer_id': viewer['User']['Id']})
        friend, receiver = self.servers
        base, admin = receiver['base'], receiver['admin']
        for server, count in zip(self.servers, (len(FRIEND_ONLY + SHARED), len(HERE_ONLY + SHARED))):
            tries = [0]
            def scanned():
                tries[0] += 1
                if tries[0] % 45 == 0:
                    # The scan started with the library sometimes ends before it saw the files.
                    qa.request(server['base'], '/Library/Refresh', server['admin'], data={}, expected=204)
                return len([m for m in self.movies(server['base'], server['admin']) if m.get('ProviderIds', {}).get('Imdb')]) == count or None
            qa.wait_for(scanned, 'A server did not scan its titles', 300)
        qa.check(qa.request(base, '/Plugins/Federation/Friends/Send', admin, {'Url': friend['host']})['success'], 'Friend request failed')
        pending = qa.request(friend['base'], '/Plugins/Federation/Friends', friend['admin'])['incoming'][0]['Id']
        qa.check(qa.request(friend['base'], f'/Plugins/Federation/Friends/{pending}/Accept', friend['admin'], {})['success'], 'Friend accept failed')
        theirs = qa.request(friend['base'], '/Plugins/Federation/Configuration', friend['admin'])['RemoteServers'][0]['Id']
        qa.check(qa.request(friend['base'], f'/Plugins/Federation/Friends/{theirs}/Sharing', friend['admin'], {'ShareAll':True})['success'], 'Sharing failed')
        library_id = next(l['ItemId'] for l in qa.request(friend['base'], '/Library/VirtualFolders', friend['admin']) if l['Name'] == 'QA Native')
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        self.friend_id = settings['RemoteServers'][0]['Id']
        self.friend_name = settings['RemoteServers'][0]['Name']
        settings['LibraryMappings'] = [{'LocalLibraryName':'QA Native','MediaType':'Movie','Enabled':True,'AutoProvision':True,
            'RemoteLibrarySources':[{'ServerId':self.friend_id,'RemoteLibraryId':library_id}]}]
        settings['RemoteServers'][0]['WanCapMode'] = 'Off'
        settings['RemoteServers'][0]['StreamingMode'] = 'Direct'
        settings['EnableDedup'] = dedup
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(base, '/Plugins/Federation/ProvisionLibraries', admin, {})
        expected = len(HERE_ONLY + SHARED) + len(FRIEND_ONLY) + (0 if dedup else len(SHARED))
        def refreshed():
            qa.request(base, '/Plugins/Federation/Refresh', admin, {})
            return len(self.movies(base, admin)) == expected or None
        qa.wait_for(refreshed, 'The friend catalogue did not arrive', 240)
        return self

    def describe(self):
        """What a browser stage needs. Holds the generated admin passwords: keep it private."""
        friend, receiver = self.servers
        return {'friend': {k: friend[k] for k in ('base', 'password', 'name')},
            'receiver': {k: receiver[k] for k in ('base', 'password', 'name', 'viewer_id')},
            'friend_name': self.friend_name, 'friend_id': self.friend_id, 'viewer': 'qa-viewer', 'admin': 'qa-admin',
            'counts': {'friend_only': len(FRIEND_ONLY), 'here_only': len(HERE_ONLY), 'shared': len(SHARED)}}

    @staticmethod
    def movies(url, headers):
        return qa.request(url, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds,Tags', headers)['Items']

    def down(self):
        if os.environ.get('FEDERATION_QA_KEEP') == '1':
            print('LAB KEPT (FEDERATION_QA_KEEP=1):', ' '.join(self.created), self.network, flush=True)
            return
        for name in reversed(self.created):
            try:
                qa.run(['podman', 'rm', '-f', '-t', '2', name])
            except Exception:
                pass
        try:
            qa.run(['podman', 'network', 'rm', self.network])
        except Exception:
            pass


def main():
    lab = Lab()
    try:
        lab.up(dedup='--no-dedup' not in sys.argv)
        note = lab.work / 'lab.json'
        note.write_text(json.dumps(lab.describe(), indent=1))
        os.chmod(note, 0o600)
        print('LAB UP: details in', note, flush=True)
        if '--hold' in sys.argv:
            hold = lab.work / 'hold'; hold.write_text('delete this file to stop the lab\n')
            while hold.exists():
                time.sleep(2)
    finally:
        lab.down()


if __name__ == '__main__':
    main()
