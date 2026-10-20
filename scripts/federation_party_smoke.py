#!/usr/bin/env python3
"""Disposable two-Jellyfin watch-party test: a viewer on the source server starts a
party with friends, a viewer on the friend's server finds it in the normal SyncPlay
group list and joins it, and play/pause/seek/buffering follow both ways with each
side playing its own server's copy. Scripted SyncPlay players stand in for apps."""
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import federation_metadata_smoke as qa
from syncplay_player import Player, TICKS

CLIENT = 'Federation party QA'


def wait(fn, label, seconds=40):
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        try:
            last = fn()
            if last:
                return last
        except (OSError, ValueError, RuntimeError) as e:
            last = e
        time.sleep(0.25)
    raise RuntimeError(f'{label} (last: {last})')


def smoke():
    work = Path(tempfile.mkdtemp(prefix='federation-party-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-party-' + suffix
    names = ['fed-party-source-' + suffix, 'fed-party-friend-' + suffix]
    created, players = [], []
    stage = 'setup'
    try:
        media = work / 'media'; media.mkdir()
        movie = media / 'QA Party (2026).mkv'
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=10', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000', '-t', '180', '-c:v', 'libx264', '-preset', 'ultrafast', '-c:a', 'aac', '-y', movie])
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10', '-f', 'lavfi', '-i',
            'sine=frequency=660:sample_rate=48000', '-t', '120', '-c:v', 'libx264', '-preset', 'ultrafast', '-c:a', 'aac', '-y', media / 'QA Party Two (2026).mkv'])
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
            def login(user, pw, device, base=base):
                header = {'Authorization': f'MediaBrowser Client="{CLIENT}", Device="{device}", DeviceId="{device}", Version="1"'}
                return qa.request(base, '/Users/AuthenticateByName', header, {'Username': user, 'Pw': pw})
            auth = login('qa-admin', password, 'setup-' + name)
            admin = {'Authorization': 'MediaBrowser Token="' + auth['AccessToken'] + '"'}
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'
            settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            servers.append({'base': base, 'admin': admin, 'password': password, 'login': login, 'name': name})
        source, friend = servers
        qa.request(source['base'], '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', source['admin'],
            {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
        def movies(base, headers):
            return qa.request(base, '/Items?Recursive=true&IncludeItemTypes=Movie', headers)['Items']
        natives = qa.wait_for(lambda: (lambda m: m if len(m) == 2 else None)(movies(source['base'], source['admin'])), 'Source did not scan media')
        native = next(m for m in natives if m['Name'].startswith('QA Party') and 'Two' not in m['Name'])
        native_two = next(m for m in natives if 'Two' in m['Name'])

        stage = 'friendship and catalog'
        for user in ('qa-bob', 'qa-carol', 'qa-dave'):
            qa.request(friend['base'], '/Users/New', friend['admin'], {'Name': user})
        sent = qa.request(friend['base'], '/Plugins/Federation/Friends/Send', friend['admin'], {'Url': f'http://{names[0]}:8096'})
        qa.check(sent['success'], 'Friend request failed')
        pending = qa.request(source['base'], '/Plugins/Federation/Friends', source['admin'])['incoming'][0]['Id']
        qa.check(qa.request(source['base'], f'/Plugins/Federation/Friends/{pending}/Accept', source['admin'], {})['success'], 'Friend accept failed')
        source_settings = qa.request(source['base'], '/Plugins/Federation/Configuration', source['admin'])
        source_friend = source_settings['RemoteServers'][0]['Id']
        qa.check(qa.request(source['base'], f'/Plugins/Federation/Friends/{source_friend}/Sharing', source['admin'], {'ShareAll':True})['success'], 'Sharing failed')
        settings = qa.request(friend['base'], '/Plugins/Federation/Configuration', friend['admin'])
        friend_id = settings['RemoteServers'][0]['Id']
        source_name = settings['RemoteServers'][0]['Name']
        library_id = next(l['ItemId'] for l in qa.request(source['base'], '/Library/VirtualFolders', source['admin']) if l['Name']=='QA Native')
        settings['LibraryMappings'] = [{'LocalLibraryName':'QA Federated','MediaType':'Movie','Enabled':True,'AutoProvision':True,
            'RemoteLibrarySources':[{'ServerId':friend_id,'RemoteLibraryId':library_id}]}]
        settings['RemoteServers'][0]['WanCapMode'] = 'Off'
        qa.request(friend['base'], '/Plugins/Federation/Configuration', friend['admin'], settings)
        qa.request(friend['base'], '/Plugins/Federation/ProvisionLibraries', friend['admin'], {})
        def refreshed():
            qa.request(friend['base'], '/Plugins/Federation/Refresh', friend['admin'], {})
            return movies(friend['base'], friend['admin'])
        imported_all = qa.wait_for(lambda: (lambda m: m if len(m) == 2 else None)(refreshed()), 'Friend catalog did not import')
        imported_id = next(m for m in imported_all if 'Two' not in m['Name'])['Id'].replace('-', '').lower()
        imported_two = next(m for m in imported_all if 'Two' in m['Name'])['Id'].replace('-', '').lower()
        users = {u['Name']: u['Id'] for u in qa.request(friend['base'], '/Users', friend['admin'])}
        qa.check(qa.request(source['base'], f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source['admin'],
            {'RemoteUserId': users['qa-carol'], 'RemoteUserName': 'qa-carol', 'Mode': 'AllLibraries', 'BlockedItemIds': [native['Id']]})['success'], 'Carol rule failed')
        carol_headers = {'Authorization': 'MediaBrowser Token="' + friend['login']('qa-carol', '', 'carol-check')['AccessToken'] + '"'}
        qa.wait_for(lambda: all('Two' in m['Name'] for m in movies(friend['base'], carol_headers)) or None, 'Blocked viewer still sees the title')

        stage = 'viewers open their apps'
        def player(server, user, pw, device):
            token = server['login'](user, pw, device)['AccessToken']
            p = Player(server['base'], token, CLIENT, device, user)
            players.append(p)
            return p
        alice = player(source, 'qa-admin', source['password'], 'alice-player')
        bob = player(friend, 'qa-bob', '', 'bob-player')
        carol = player(friend, 'qa-carol', '', 'carol-player')
        dave = player(friend, 'qa-dave', '', 'dave-player')
        time.sleep(1.5)

        stage = 'host starts a party with friends'
        created_party = alice.request('/Plugins/Federation/Party', {})
        qa.check(created_party and created_party.get('success'), f'Party not created: {created_party}')
        wait(lambda: alice.group_id, 'Host player was not told it joined the group')
        alice.set_queue([native['Id']])
        wait(lambda: alice.snapshot()['playing'], 'Host group did not start playing')

        stage = 'friend sees the party in the normal group list'
        def listed(p):
            return [g for g in p.groups() if g['GroupName'].endswith(' · ' + source_name)]
        party = wait(lambda: [g for g in listed(bob) if g['State'] == 'Playing'], 'Party not in the friend viewer\'s group list')[0]
        qa.check(any('qa-admin' in n for n in party['Participants']), 'Host participants not shown')
        qa.check(not listed(carol), 'A viewer blocked from the title sees the party')
        print('PARTY PASS: started from the group menu; listed for the friend\'s viewer (named, with who is watching), hidden from a viewer who can\'t open the title', flush=True)

        stage = 'friend joins and both play in step'
        bob.join(party['GroupId'])
        def in_step(playing=True, tolerance=2.0):
            a, b = alice.snapshot(), bob.snapshot()
            return (a['playing'] == playing and b['playing'] == playing
                    and b['item'] == imported_id and abs(a['position'] - b['position']) <= tolerance) and (a, b)
        def in_step_item(friend_item, playing=True, tolerance=2.0):
            a, b = alice.snapshot(), bob.snapshot()
            return (a['playing'] == playing and b['playing'] == playing
                    and b['item'] == friend_item and abs(a['position'] - b['position']) <= tolerance) and (a, b)
        a, b = wait(lambda: in_step(), 'Joined viewer did not play in step with the host', 60)
        qa.check(bob.group_id and bob.group_id != alice.group_id, 'Friend viewer is not in their own server\'s group')
        print(f'PARTY PASS: joined natively; friend plays its own copy, both playing {abs(a["position"]-b["position"]):.2f}s apart', flush=True)

        stage = 'pause, seek and resume follow both ways'
        bob.pause()
        wait(lambda: in_step(playing=False, tolerance=1.5), 'Friend\'s pause did not pause the host')
        alice.seek(90 * TICKS)
        wait(lambda: abs(bob.snapshot()['position'] - 90) < 2 and abs(alice.snapshot()['position'] - 90) < 2, 'Host seek did not reach the friend')
        alice.unpause()
        wait(lambda: in_step(), 'Host resume did not resume the friend')
        print('PARTY PASS: friend pause -> host paused; host seek -> friend at same spot; host resume -> both playing in step', flush=True)

        stage = 'a second friend viewer joins the running party'
        mirror = wait(lambda: [g for g in listed(dave) if g['GroupId'].replace('-', '') == bob.group_id.replace('-', '')], 'Friend\'s copy of the party not in the native list')[0]
        dave.join(mirror['GroupId'])
        wait(lambda: in_step() and dave.snapshot()['playing'] and dave.snapshot()['item'] == imported_id
             and abs(dave.snapshot()['position'] - alice.snapshot()['position']) <= 2, 'Second friend viewer not in step', 60)
        dave.leave()
        print('PARTY PASS: a second viewer on the friend\'s server joins the running party from the normal list and plays in step', flush=True)

        stage = 'the friend\'s viewer changes the title'
        bob.set_queue([imported_two])
        wait(lambda: alice.snapshot()['item'] == native_two['Id'].replace('-', '').lower() and in_step_item(imported_two), 'Host did not follow the friend\'s new title', 60)
        bob.set_queue([imported_id], 0, 90 * TICKS)
        wait(lambda: in_step() and alice.snapshot()['item'] == native['Id'].replace('-', '').lower(), 'Host did not follow back to the first title', 60)
        print('PARTY PASS: a title change on the friend\'s server switches the host to its own copy, and back', flush=True)

        stage = 'buffering anywhere waits for everyone'
        bob.buffering()
        wait(lambda: not alice.snapshot()['playing'], 'Host kept playing while the friend was buffering')
        bob.ready()
        wait(lambda: in_step(), 'Party did not resume after the friend finished buffering')
        print('PARTY PASS: a buffering viewer on the friend\'s server pauses the host until ready, then both resume together', flush=True)

        stage = 'leaving and ending'
        host_group = alice.group_id
        def friend_in_host_group():
            info = alice.request(f'/SyncPlay/{host_group}')
            return any(source_name not in n and '(' in n for n in info['Participants'])
        qa.check(friend_in_host_group(), 'Friend not shown in the host\'s group')
        bob.leave()
        wait(lambda: not friend_in_host_group(), 'Friend still in the host group after leaving', 30)
        wait(lambda: listed(bob), 'Party no longer listed for the friend after leaving')
        alice.leave()
        wait(lambda: not listed(bob), 'Ended party still listed on the friend', 40)
        print('PARTY PASS: friend leaving removes them from the host group; host leaving ends the party for friends', flush=True)

        stage = 'the friend hosts, this side joins with its own original'
        def listed_on_source(p):
            return [g for g in p.groups() if ' · ' in g['GroupName']]
        hosted = bob.request('/Plugins/Federation/Party', {})
        qa.check(hosted and hosted.get('success'), f'Friend could not host: {hosted}')
        wait(lambda: bob.group_id, 'Friend host not in its group')
        bob.set_queue([imported_id])
        wait(lambda: bob.snapshot()['playing'], 'Friend-hosted group did not play')
        reverse = wait(lambda: [g for g in listed_on_source(alice) if g['State'] == 'Playing'], 'Friend\'s party not listed on the source')[0]
        alice.join(reverse['GroupId'])
        wait(lambda: in_step() and alice.snapshot()['item'] == native['Id'].replace('-', '').lower(), 'Source viewer not in step with the friend-hosted party', 60)
        alice.pause()
        wait(lambda: in_step(playing=False, tolerance=1.5), 'Pause on the joining side did not reach the friend host')
        alice.leave()
        bob.leave()
        wait(lambda: not [g for g in alice.groups() if g['GroupId'] == reverse['GroupId']], 'Ended friend party still listed', 40)
        print('PARTY PASS: reversed roles: the friend hosts its federated copy, this server joins with its own original, pause follows back', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError)) else type(e).__name__ + ': ' + str(e)
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for p in players:
            p.close()
        for name in reversed(created):
            if os.environ.get('PARTY_KEEP_LOGS'):
                subprocess.run(['podman', 'logs', name], stdout=open(work / (name + '.log'), 'w'), stderr=subprocess.STDOUT)
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('PARTY: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    sys.path.insert(0, str(Path(__file__).parent))
    smoke()
