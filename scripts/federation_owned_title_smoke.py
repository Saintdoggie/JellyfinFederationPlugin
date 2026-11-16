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
import urllib.parse
import federation_metadata_smoke as qa
from federation_handoff_smoke import container_log
from federation_peer_smoke import no_redirect


def browser_video_picker(base, item_id, expected):
    """Exercise the actual jellyfin-web DOM when explicitly requested by the QA runner."""
    from playwright.sync_api import sync_playwright

    import federation_browser
    selected = expected[1]
    with sync_playwright() as playwright:
        browser = federation_browser.launch(playwright)
        diagnostic = os.environ.get('FEDERATION_QA_DIAGNOSTIC')
        phase = 'sign in'
        errors, item_responses, page = [], [], None
        try:
            page, _, errors = federation_browser.signed_in_page(browser, base, 'qa-viewer')
            page.on('response', lambda response: item_responses.append(
                {'status': response.status, 'path': response.url.split('?', 1)[0].replace(base, '')})
                if ('/Items/' in response.url or '/Users/' in response.url) and len(item_responses) < 40 else None)
            phase = 'open item details'
            federation_browser.go(page, base, '#/details?id=' + item_id)
            phase = 'wait for enriched Video options'
            page.wait_for_function("""expected => {
                const video = document.querySelector('.itemDetailPage .selectVideo');
                return video && video.dataset.federationCopyPicker === '1'
                    && Array.from(video.options).map(o => o.textContent).join('|') === expected.join('|');
            }""", arg=[source['Name'] for source in expected], timeout=45000)
            source_hidden = page.locator('.itemDetailPage .selectSourceContainer').evaluate(
                "element => getComputedStyle(element).display === 'none'")
            qa.check(source_hidden, 'The live browser still shows a separate Version row')
            qa.check(page.locator('.itemDetailPage .selectVideo').is_enabled(), 'The live Video dropdown is disabled')
            screenshot = os.environ.get('FEDERATION_QA_SCREENSHOT')
            if screenshot:
                page.screenshot(path=screenshot, full_page=True)
            page.locator('.itemDetailPage .selectVideo').select_option(selected['Id'])
            page.wait_for_function("id => document.querySelector('.itemDetailPage .selectSource').value === id",
                                   arg=selected['Id'], timeout=15000)
            page.wait_for_function("""expected => {
                const video = document.querySelector('.itemDetailPage .selectVideo');
                return video && !video.disabled && video.value === expected;
            }""", arg=selected['Id'], timeout=15000)
            print('OWNED PASS: real Chrome item page: one enabled Video picker, exact labels, native source selection', flush=True)
        except Exception:
            if diagnostic and page is not None:
                import json
                state = page.evaluate("""() => ({
                    url: location.href, readyState: document.readyState,
                    visiblePages: Array.from(document.querySelectorAll('.page:not(.hide)')).map(p => p.id || p.className),
                    sourceExists: !!document.querySelector('.selectSource'),
                    videoExists: !!document.querySelector('.selectVideo'),
                    sourceOptions: Array.from(document.querySelectorAll('.selectSource option')).map(o => o.textContent),
                    videoOptions: Array.from(document.querySelectorAll('.selectVideo option')).map(o => o.textContent)
                })""")
                Path(diagnostic + '.json').write_text(json.dumps({'phase': phase, 'page': state, 'errors': errors, 'itemResponses': item_responses}, indent=2) + '\n')
                page.screenshot(path=diagnostic, full_page=True)
            raise
        finally:
            browser.close()


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
        # A second title only the receiver has: it must stay out of the friend's tab.
        only_here = 'QA Only Here (2026) [imdbid-tt9912347].mkv'
        shutil.copy(work / ('media-' + names[1]) / title, work / ('media-' + names[1]) / only_here)
        qa.run(['podman', 'network', 'create', network])
        servers = []
        for name in names:
            config = work / name
            plugin = config / 'plugins/Federation'; plugin.mkdir(parents=True)
            shutil.copy(qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
            created.append(name)
            qa.run(['podman', 'run', '-d', '--name', name, '--network', network, '--security-opt', 'label=disable', '-e', 'FEDERATION_PLAYERS_REACH_PRIVATE=1',
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
        qa.wait_for(lambda: len([m for m in movies(base, admin) if m.get('ProviderIds', {}).get('Imdb')]) == 2 or None,
                    'The receiver did not scan both of its titles')
        own = next(m for m in movies(base, admin) if m['ProviderIds']['Imdb'] == 'tt9912346')

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
        qa.check(len(movies(base, admin)) == 2, 'The friend\'s copy of an owned title became another library title')
        print('OWNED PASS: the friend holds the title too, and the library still shows it once', flush=True)

        stage = 'a device that only plays H.264 gets the friend copy first, the own file as the one to convert'
        own_id = own['Id'].replace('-', '')
        first, second = sources
        qa.check(first['Id'].replace('-', '') != own_id and first['Protocol'] == 'Http', 'The friend\'s copy is not offered first')
        qa.check(first.get('SupportsDirectStream') or first.get('SupportsDirectPlay'), 'The friend\'s copy is not offered to play as stored')
        qa.check(not first.get('TranscodingUrl'), 'The friend\'s copy was offered for conversion')
        qa.check(second['Id'].replace('-', '') == own_id and second.get('TranscodingUrl'), 'The own file is not kept as the copy to convert')
        qa.check('is owned here; offering' in container_log(receiver['name']), 'The receiver did not log the offer')
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

        stage = 'the version list of the owned title names both servers, own file first'
        friend_name = qa.request(base, '/Plugins/Federation/Configuration', admin)['RemoteServers'][0]['Name']
        listed = next(m for m in movies(base, viewer) if m['Id'] == own['Id'])['MediaSources']
        qa.check(len(listed) == 2, f'The version list holds {len(listed)} entries, expected the own file and the friend copy')
        friend_words = friend_name.replace('-', ' ').replace('_', ' ').split()
        friend_abbreviation = (friend_name if len(friend_words) == 1 else ''.join(w[0] for w in friend_words)).upper()
        qa.check(listed[0]['Id'].replace('-', '') == own_id and listed[0]['Name'].endswith(' · LOCAL'),
                 f'The own file is not first with a LOCAL quality label: {listed[0]["Name"]}')
        qa.check(all('p · ' in source['Name'] and ' Mbps · ' in source['Name'] for source in listed),
                 f'A copy is missing resolution/bitrate: {[source["Name"] for source in listed]}')
        qa.check(listed[1]['Name'].endswith(' · ' + friend_abbreviation) and listed[1]['Protocol'] == 'Http',
                 f'The friend copy is not listed under its server: {listed[1]["Name"]}')
        pick = listed[1]['Id']
        print(f'OWNED PASS: version list: "{listed[0]["Name"]}", then "{listed[1]["Name"]}"', flush=True)

        stage = 'a copy picked in the version list plays from the friend when the device plays it as stored'
        def negotiate_pick(video_codecs):
            body = {'UserId': viewer_id, 'MaxStreamingBitrate': 120000000, 'EnableDirectPlay': True, 'EnableDirectStream': True,
                    'EnableTranscoding': True, 'MediaSourceId': pick, 'DeviceProfile': profile(video_codecs)}
            return qa.request(base, f'/Items/{own["Id"]}/PlaybackInfo?userId={viewer_id}', viewer, body)['MediaSources']
        picked = negotiate_pick('h264,hevc')
        qa.check(len(picked) == 2 and picked[0]['Id'] == pick and (picked[0].get('SupportsDirectStream') or picked[0].get('SupportsDirectPlay')),
                 f'The picked copy is not first and playable as stored: {[s["Id"] for s in picked]}')
        qa.check(picked[1]['Id'].replace('-', '') == own_id, 'The own file is not kept behind the picked copy')
        status, headers, _ = no_redirect(base, f'/Videos/{own["Id"]}/stream.mkv?static=true&mediaSourceId={pick}', viewer)
        target = headers.get('Location') or headers.get('location') or ''
        qa.check(status == 302 and target.startswith(friend['host'] + '/Plugins/Federation/DirectStream/'), f'The picked copy was not sent to the friend (HTTP {status})')
        print('OWNED PASS: picked copy: first in the answer, stream request redirected to the friend', flush=True)

        stage = 'a picked copy the device cannot play as stored falls back to the own file'
        fallback = negotiate_pick('hevc')
        qa.check(len(fallback) == 1 and fallback[0]['Id'].replace('-', '') == own_id and not fallback[0].get('TranscodingUrl'),
                 f'An HEVC-only device that picked the H.264 copy did not get the own file as stored: {[s["Id"] for s in fallback]}')
        capable = negotiate('h264,hevc')
        qa.check(len(capable) == 1 and capable[0]['Id'].replace('-', '') == own_id, 'After a pick, an ordinary play was still offered the friend copy')
        print('OWNED PASS: picked copy the device cannot play: own file plays; the next ordinary play gets the own file only', flush=True)

        stage = 'the friend\'s tab in the library lists the title owned here that the friend has too, and nothing else'
        def tab(origin, extra=''):
            path = '/Items?Recursive=true&IncludeItemTypes=Movie&SortBy=SortName&Limit=100' + extra
            if origin:
                path += '&federationOrigin=' + urllib.parse.quote(origin)
            return qa.request(base, path, viewer)
        everything = tab(None)
        qa.check(everything['TotalRecordCount'] == 2, f'The unfiltered library holds {everything["TotalRecordCount"]} titles, expected 2')
        native = next(l['ItemId'] for l in qa.request(base, '/Library/VirtualFolders', admin) if l['Name'] == 'QA Native')
        for extra in ('', '&ParentId=' + native, '&ParentId=' + native + '&StartIndex=100'):
            theirs_tab = tab(friend_name, extra)
            qa.check(theirs_tab['TotalRecordCount'] == 1 and [i['Id'] for i in theirs_tab['Items']] == [own['Id']],
                     f'The friend\'s tab ({extra or "whole server"}) shows {theirs_tab["TotalRecordCount"]} titles / {[i["Name"] for i in theirs_tab["Items"]]}')
        nobody = tab('No Such Server')
        qa.check(nobody['TotalRecordCount'] == 0 and not nobody['Items'], 'A tab for an unknown server showed titles')
        print('OWNED PASS: friend tab: 1 of 2 titles (the one owned here that the friend also has), full count, also from a stale page number', flush=True)

        stage = 'the support report counts the own file and the friend copy, and names what each player needs'
        qa.check(qa.request(base, '/Plugins/Federation/PlaySupport/Start', admin, {})['success'], 'The support check did not start')
        support = qa.wait_for(lambda: (lambda r: r if not r['running'] and r.get('report') else None)(
            qa.request(base, '/Plugins/Federation/PlaySupport', admin)), 'The support check did not finish', 60)
        report = support['report']
        labels = {f['label']: f for f in report['formats']}
        qa.check(report['filesChecked'] == 3 and report['undescribed'] == 0, f'The support check saw {report["filesChecked"]} files, expected the two own files and the friend copy')
        qa.check(set(labels) == {'HEVC, AAC, in MKV', 'H.264, AAC, in MKV'}, f'Unexpected file types: {sorted(labels)}')
        origins = {e.split(':', 1)[0] for f in report['formats'] for e in f['examples']}
        qa.check('This server' in origins and len(origins) == 2, f'The friend copy was not attributed to the friend: {sorted(origins)}')
        chrome = [p['id'] for p in report['players']].index('chrome')
        qa.check(labels['HEVC, AAC, in MKV']['players'][chrome] == 'no', 'Chrome was not flagged for the HEVC file')
        qa.check(labels['H.264, AAC, in MKV']['players'][chrome] == 'plays', 'Chrome was flagged for the H.264 MKV')
        print('OWNED PASS: support report: own HEVC file and friend H.264 copy, both checked against each player', flush=True)
        if os.environ.get('FEDERATION_QA_BROWSER'):
            stage = 'real Chrome: the owned title offers one enabled Video picker and changes the native source'
            browser_video_picker(base, own['Id'], listed)
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
