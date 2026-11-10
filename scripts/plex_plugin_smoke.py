#!/usr/bin/env python3
"""Disposable check that the standalone "Jellyfin Federation Plex" plugin loads.

One throwaway Jellyfin gets the core plugin and the Plex plugin in two separate
plugin folders (no DLL copied beside the core). Expected: both plugins Active,
the Plex plugin reports Running, the add-in's controllers answer, its dashboard
page is served by the core, and the Plex providers are known to Jellyfin.
Only the container created here is touched.
"""
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import federation_metadata_smoke as qa


def smoke():
    work = Path(tempfile.mkdtemp(prefix='federation-plexplugin-qa-')); os.chmod(work, 0o700)
    name = 'fed-plexplugin-' + secrets.token_hex(5)
    stage = 'setup'
    created = False
    try:
        config = work / 'config'
        core = config / 'plugins/Federation'; core.mkdir(parents=True)
        plex = config / 'plugins/FederationPlex'; plex.mkdir(parents=True)
        shutil.copy(qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', core)
        shutil.copy(qa.REPO / 'PlexPlugin/bin/Release/net10.0/Jellyfin.Plugin.FederationPlex.dll', plex)
        qa.check(not (core / 'Jellyfin.Plugin.Federation.Plex.dll').exists(), 'Fixture must not contain a hand-copied add-in')
        qa.run(['podman', 'run', '-d', '--name', name, '--security-opt', 'label=disable', '-p', '127.0.0.1::8096',
                '-v', f'{config}:/config', qa.JELLYFIN_IMAGE])
        created = True
        base = 'http://' + qa.run(['podman', 'port', name, '8096/tcp'])
        qa.wait_for(lambda: qa.request(base, '/Startup/User'), 'Jellyfin did not start')
        qa.request(base, '/Startup/Configuration', data={'UICulture':'en-US','MetadataCountryCode':'US','PreferredMetadataLanguage':'en'}, expected=204)
        password = secrets.token_urlsafe(32)
        qa.request(base, '/Startup/User', data={'Name':'qa-admin','Password':password}, expected=204)
        qa.request(base, '/Startup/RemoteAccess', data={'EnableRemoteAccess':True,'EnableAutomaticPortMapping':False}, expected=204)
        qa.request(base, '/Startup/Complete', data={}, expected=204)
        header = {'Authorization':'MediaBrowser Client="Plex plugin QA", Device="Sandbox", DeviceId="plexplugin-qa", Version="1"'}
        auth = qa.request(base, '/Users/AuthenticateByName', header, {'Username':'qa-admin','Pw':password})
        admin = {'Authorization':'MediaBrowser Token="'+auth['AccessToken']+'"'}

        stage = 'both plugins are active'
        plugins = {p['Name']: p for p in qa.request(base, '/Plugins', admin)}
        for wanted in ('Jellyfin Federation', 'Jellyfin Federation Plex'):
            qa.check(wanted in plugins and plugins[wanted]['Status'] == 'Active', f'{wanted} is not Active: {plugins.get(wanted)}')
        print('PLEX PLUGIN PASS: core and Plex plugins both Active from separate folders', flush=True)

        stage = 'the Plex plugin reports the add-in running'
        status = qa.request(base, '/Plugins/FederationPlex/Status', admin)
        qa.check(status['running'] is True and status['state'] == 'Loaded', f'Add-in not running: {status}')
        qa.check(status['version'] == status['federationVersion'], f'Versions differ: {status}')
        qa.request(base, '/Plugins/FederationPlex/Status', None, expected=401)
        print(f"PLEX PLUGIN PASS: {status['message']}", flush=True)

        stage = 'the add-in controllers answer'
        setup = qa.request(base, '/Plugins/Federation/PlexSetup/Status', admin)
        qa.check(isinstance(setup, dict), 'PlexSetup/Status did not answer')
        print('PLEX PLUGIN PASS: add-in controller route PlexSetup/Status answers', flush=True)

        stage = 'the add-in page is served by the core'
        _, page = qa.request(base, '/Plugins/Federation/AddInPages/FederationPlex', admin, raw=True)
        qa.check(b'federationPlexPage' in page, 'Plex dashboard page was not served')
        pages = qa.request(base, '/web/ConfigurationPages', admin)
        names = {p.get('Name') for p in pages}
        qa.check('FederationPlex' in names and 'Jellyfin Federation Plex' in names, f'Dashboard pages missing: {sorted(n for n in names if n)}')
        print('PLEX PLUGIN PASS: Federation Plex page listed and served; status page registered', flush=True)

        stage = 'Plex providers are known to Jellyfin'
        options = qa.request(base, '/Libraries/AvailableOptions?libraryContentType=movies&isNewLibrary=true', admin)
        text = str(options)
        qa.check("'Plex'" in text, 'No provider named Plex among the library options')
        qa.check('Plex (not running)' not in text, 'The provider stand-ins did not find the add-in')
        print('PLEX PLUGIN PASS: Plex image/metadata providers are offered for libraries', flush=True)

        stage = 'log is clean'
        log = subprocess.run(['podman', 'logs', name], capture_output=True, text=True)
        combined = log.stdout + log.stderr
        qa.check('Malfunctioned' not in combined and 'Error registering plugin services' not in combined, 'Jellyfin reported a plugin load problem')
        print('PLEX PLUGIN PASS: no plugin load errors in the Jellyfin log', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError, StopIteration)) else f'{type(e).__name__}: {e}'
        if created:
            log = subprocess.run(['podman', 'logs', '--tail', '60', name], capture_output=True, text=True)
            print((log.stdout + log.stderr)[-6000:], flush=True)
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        if created:
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('PLEX PLUGIN: isolated container cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    smoke()
