#!/usr/bin/env python3
"""Disposable three-Jellyfin check of linked servers.

Server A is friends with C (which shares a movie) and with B. A and B are
linked by clicking Link on both and typing each server's six-digit code on the
other, exactly as an admin does. Expected:

* a wrong code is refused and counted; the link is made only after both codes;
* B then uses C as its own friend, through A, with nothing done on C: C's
  friends list and incoming requests are unchanged;
* B's viewer plays C's movie, and the stream request goes straight to C;
* the friends list of A is refused to a caller without the link key;
* removing C on A removes it from B; unlinking clears the link on both.

Only containers/network created here are touched; production is never used.
"""
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
    work = Path(tempfile.mkdtemp(prefix='federation-linked-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-linked-' + suffix
    roles = ('a', 'b', 'c')
    names = {role: f'fed-linked-{role}-{suffix}' for role in roles}
    created = []
    stage = 'setup'
    try:
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        media = work / 'media-c'; media.mkdir()
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=24', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000', '-t', '20', '-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p',
            '-c:a', 'aac', '-metadata:s:a:0', 'language=eng', '-y', media / 'QA Linked (2026).mp4'])
        for role in roles:
            (work / f'media-{role}').mkdir(exist_ok=True)
        # A has a title of its own too: the link's direction decides whether B shows it.
        shutil.copy(media / 'QA Linked (2026).mp4', work / 'media-a' / 'QA OwnA (2026).mp4')
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
            auth_header = {'Authorization':'MediaBrowser Client="Federation linked QA", Device="Sandbox", DeviceId="linked-qa", Version="1"'}
            auth = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-admin','Pw':password})
            admin = {'Authorization':'MediaBrowser Token="'+auth['AccessToken']+'"'}
            qa.request(base, '/Users/New', admin, {'Name':'qa-viewer'})
            viewer = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-viewer','Pw':''})
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'
            settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            if role in ('a', 'c'):
                qa.request(base, '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', admin,
                    {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
            servers[role] = {'name': name, 'base': base, 'admin': admin, 'host': f'http://{name}:8096',
                             'viewer': {'Authorization':'MediaBrowser Token="'+viewer['AccessToken']+'"'}, 'viewer_id': viewer['User']['Id']}
        a, b, c = servers['a'], servers['b'], servers['c']

        def friends_of(server):
            return qa.request(server['base'], '/Plugins/Federation/Configuration', server['admin'])['RemoteServers']

        def row_for(server, other):
            return next(s for s in friends_of(server) if s['Url'].rstrip('/') == other['host'])

        def befriend(sender, receiver, share_all=True):
            qa.check(qa.request(sender['base'], '/Plugins/Federation/Friends/Send', sender['admin'], {'Url': receiver['host']})['success'], 'Friend request failed')
            pending = qa.request(receiver['base'], '/Plugins/Federation/Friends', receiver['admin'])['incoming'][0]['Id']
            qa.check(qa.request(receiver['base'], f'/Plugins/Federation/Friends/{pending}/Accept', receiver['admin'], {})['success'], 'Friend accept failed')
            if share_all:
                theirs = row_for(receiver, sender)['Id']
                qa.check(qa.request(receiver['base'], f'/Plugins/Federation/Friends/{theirs}/Sharing', receiver['admin'], {'ShareAll': True})['success'], 'Sharing failed')

        stage = 'A is friends with C (which shares a movie) and with B'
        qa.wait_for(lambda: qa.request(c['base'], '/Items?Recursive=true&IncludeItemTypes=Movie', c['admin'])['TotalRecordCount'] == 1, 'C did not scan its movie')
        befriend(a, c)
        befriend(b, a)
        library_id = next(l['ItemId'] for l in qa.request(c['base'], '/Library/VirtualFolders', c['admin']) if l['Name'] == 'QA Native')
        settings = qa.request(a['base'], '/Plugins/Federation/Configuration', a['admin'])
        c_on_a = next(s for s in settings['RemoteServers'] if s['Url'].rstrip('/') == c['host'])
        settings['LibraryMappings'] = [{'LocalLibraryName':'Movies','MediaType':'Movie','Enabled':True,'AutoProvision':True,'AutoManaged':True,
            'RemoteServerIds':[c_on_a['Id']],
            'RemoteLibrarySources':[{'ServerId':c_on_a['Id'],'RemoteLibraryId':library_id,'RemoteLibraryName':'QA Native'}]}]
        qa.request(a['base'], '/Plugins/Federation/Configuration', a['admin'], settings)
        c_friends_before = [s['Url'] for s in friends_of(c)]
        qa.check(c_friends_before == [a['host']], f'C should only know A, knows {c_friends_before}')
        print('LINKED PASS: A is friends with C and B; C knows only A', flush=True)

        stage = 'linking needs both codes; a wrong code is refused'
        b_on_a, a_on_b = row_for(a, b)['Id'], row_for(b, a)['Id']
        early = qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Code', b['admin'], {'Code': '123456'})
        qa.check(not early['success'], 'A code was accepted before Link was clicked here')
        qa.check(qa.request(a['base'], f'/Plugins/Federation/Link/{b_on_a}/Start', a['admin'], {})['success'], 'Link did not start on A')
        asked = qa.request(b['base'], '/Plugins/Federation/Link/Status', b['admin'])
        qa.check(any(s['friendId'] == a_on_b and s['peerAsked'] for s in asked), 'B was not told that A wants to link')
        qa.check(qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Start', b['admin'], {})['success'], 'Link did not start on B')

        def code_on(server, row):
            return next(s for s in qa.request(server['base'], '/Plugins/Federation/Link/Status', server['admin']) if s['friendId'] == row)

        a_code = code_on(a, b_on_a)['code']
        wrong = '000000' if a_code != '000000' else '111111'
        bad = qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Code', b['admin'], {'Code': wrong})
        qa.check(not bad['success'] and 'not the code' in bad['message'], f'A wrong code was not refused: {bad}')
        qa.check(code_on(a, b_on_a)['attemptsLeft'] == 4, 'The wrong code was not counted')
        if code_on(a, b_on_a)['codeSecondsLeft'] < 3:
            time.sleep(4)
        good = qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Code', b['admin'], {'Code': code_on(a, b_on_a)['code']})
        qa.check(good['success'], f'The right code was refused: {good}')
        qa.check(not row_for(a, b).get('Linked') and not row_for(b, a).get('Linked'), 'Linked after one code only')
        if code_on(b, a_on_b)['codeSecondsLeft'] < 3:
            time.sleep(4)
        done = qa.request(a['base'], f'/Plugins/Federation/Link/{b_on_a}/Code', a['admin'], {'Code': code_on(b, a_on_b)['code']})
        qa.check(done['success'], f'The second code was refused: {done}')
        qa.wait_for(lambda: row_for(a, b).get('Linked') and row_for(b, a).get('Linked'), 'The link was not made on both servers', 20)
        print('LINKED PASS: early and wrong codes refused and counted; linked only after both codes', flush=True)

        stage = 'B uses C through A, with nothing done on C'
        through = qa.wait_for(lambda: [s for s in friends_of(b) if s.get('ViaLinkedServerId') == a_on_b], 'B did not get C through the link', 60)
        qa.check(len(through) == 1 and through[0]['Url'].rstrip('/') == c['host'], f'B got the wrong friends through the link: {[s["Url"] for s in through]}')
        qa.check(not through[0].get('HasIssuedApiKey'), 'A friend through the link must never be able to call B')
        qa.check([s['Url'] for s in friends_of(c)] == c_friends_before, 'C\'s friends changed')
        qa.check(not qa.request(c['base'], '/Plugins/Federation/Friends', c['admin'])['incoming'], 'C got a friend request')
        status, _, _ = no_redirect(a['base'], '/Plugins/Federation/Peer/Linked/Friends', {})
        qa.check(status == 401, f'A listed its friends to an anonymous caller (HTTP {status})')
        print('LINKED PASS: B uses C through A; C unchanged; A\'s friends list refused without the link key', flush=True)

        stage = 'B\'s viewer plays C\'s movie, straight from C'
        def b_movies():
            qa.request(b['base'], '/Plugins/Federation/Refresh', b['admin'], {})
            items = qa.request(b['base'], '/Items?Recursive=true&IncludeItemTypes=Movie', b['viewer'])['Items']
            return items or None
        def b_titles():
            return sorted(m['Name'] for m in (b_movies() or []))
        movie = qa.wait_for(lambda: next((m for m in (b_movies() or []) if m['Name'] == 'QA Linked'), None), 'C\'s movie did not appear on B', 120)
        body = {'UserId': b['viewer_id'], 'MaxStreamingBitrate': 120000000, 'EnableDirectPlay': True, 'EnableDirectStream': True, 'EnableTranscoding': True,
                'DeviceProfile': {'MaxStreamingBitrate': 120000000,
                    'DirectPlayProfiles': [{'Type':'Video','Container':'mp4,mov','VideoCodec':'h264','AudioCodec':'aac'}],
                    'TranscodingProfiles': [], 'SubtitleProfiles': [], 'CodecProfiles': [], 'ContainerProfiles': []}}
        source = qa.request(b['base'], f'/Items/{movie["Id"]}/PlaybackInfo?userId={b["viewer_id"]}', b['viewer'], body)['MediaSources'][0]
        status, headers, _ = no_redirect(b['base'], f'/Videos/{movie["Id"]}/stream.mp4?static=true&mediaSourceId={source["Id"]}', b['viewer'])
        target = headers.get('Location') or headers.get('location') or ''
        qa.check(status == 302 and target.startswith(c['host'] + '/Plugins/Federation/DirectStream/'), f'The stream was not sent to C (HTTP {status}, {target[:60]})')
        got, head = qa.request(c['base'] + target[len(c['host']):], '', {'Range': 'bytes=0-65535'}, expected=206, raw=True)
        qa.check(head[4:8] == b'ftyp', 'C did not send the MP4 file')
        print('LINKED PASS: B\'s viewer plays C\'s movie; the stream goes straight to C', flush=True)

        stage = 'link direction: B stops showing A\'s own titles; friends used through the link stay'
        own_library = next(l['ItemId'] for l in qa.request(a['base'], '/Library/VirtualFolders', a['admin']) if l['Name'] == 'QA Native')
        settings = qa.request(b['base'], '/Plugins/Federation/Configuration', b['admin'])
        if not any(src.get('ServerId') == a_on_b for m in settings.get('LibraryMappings') or [] for src in m.get('RemoteLibrarySources') or []):
            target = next((m for m in settings.get('LibraryMappings') or [] if m.get('MediaType') == 'Movie'), None)
            if target is None:
                target = {'LocalLibraryName':'Movies','MediaType':'Movie','Enabled':True,'AutoProvision':True,'RemoteServerIds':[],'RemoteLibrarySources':[]}
                settings.setdefault('LibraryMappings', []).append(target)
            target.setdefault('RemoteServerIds', []).append(a_on_b)
            target.setdefault('RemoteLibrarySources', []).append({'ServerId':a_on_b,'RemoteLibraryId':own_library,'RemoteLibraryName':'QA Native'})
            qa.request(b['base'], '/Plugins/Federation/Configuration', b['admin'], settings)
            qa.request(b['base'], '/Plugins/Federation/ProvisionLibraries', b['admin'], {})
        qa.wait_for(lambda: b_titles() == ['QA Linked', 'QA OwnA'] or None, 'B did not show A\'s own title beside C\'s', 120)
        both = code_on(b, a_on_b)
        qa.check(both['direction'] == 0 and both['receivesHere'] and both['sendsThere'], f'A new link is not both ways: {both}')
        refused = qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Direction', b['admin'], {'Direction': 7})
        qa.check(not refused['success'], 'An unknown direction was accepted')
        changed = qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}/Direction', b['admin'], {'Direction': 1})
        qa.check(changed['success'], f'B could not set the direction: {changed}')
        qa.wait_for(lambda: b_titles() == ['QA Linked'] or None, 'A\'s own title stayed on B, or C\'s title went with it', 120)
        on_a = code_on(a, b_on_a)
        qa.check(on_a['direction'] == 2 and on_a['receivesHere'] and not on_a['sendsThere'], f'A does not show the same direction from its side: {on_a}')
        qa.check([s for s in friends_of(b) if s.get('ViaLinkedServerId') == a_on_b], 'The friend used through the link was dropped with the titles')
        status, _, _ = no_redirect(a['base'], '/Plugins/Federation/Peer/Linked/Direction', {})
        qa.check(status in (401, 405), f'The peer direction route answered an anonymous caller (HTTP {status})')
        # Either server's admin can change it: A sets it back.
        back = qa.request(a['base'], f'/Plugins/Federation/Link/{b_on_a}/Direction', a['admin'], {'Direction': 0})
        qa.check(back['success'], f'A could not set the direction back: {back}')
        qa.wait_for(lambda: b_titles() == ['QA Linked', 'QA OwnA'] or None, 'A\'s own title did not come back on B', 120)
        qa.check(code_on(b, a_on_b)['direction'] == 0, 'B did not learn that A set the link back to both ways')
        print('LINKED PASS: direction set on B hides A\'s own title there (C\'s stays), A shows it from its side, A sets it back and the title returns', flush=True)

        stage = 'removing C on A removes it from B; unlinking clears both'
        refused = qa.request(b['base'], f'/Plugins/Federation/Servers/{through[0]["Id"]}', b['admin'], method='DELETE', expected=409)
        qa.check('linked server' in str(refused), 'B could remove a friend that comes from A')
        qa.request(a['base'], f'/Plugins/Federation/Servers/{c_on_a["Id"]}', a['admin'], method='DELETE')
        qa.wait_for(lambda: not [s for s in friends_of(b) if s.get('ViaLinkedServerId')], 'C stayed on B after A removed it', 30)
        unlinked = qa.request(b['base'], f'/Plugins/Federation/Link/{a_on_b}', b['admin'], method='DELETE')
        qa.check(unlinked['success'], f'Unlink failed: {unlinked}')
        qa.wait_for(lambda: not row_for(a, b).get('Linked') and not row_for(b, a).get('Linked'), 'The link stayed on one side', 20)
        qa.check(row_for(a, b) and row_for(b, a), 'Unlinking removed the friendship itself')
        print('LINKED PASS: C removed on A leaves B; unlink clears both sides and keeps the friendship', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError, StopIteration)) else f'{type(e).__name__}: {e}'
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print('LINKED: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    smoke()
