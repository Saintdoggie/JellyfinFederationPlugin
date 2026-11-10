#!/usr/bin/env python3
"""Disposable three-Jellyfin check of the standby server for converted plays.

Two friends hold the same title; a viewer on the receiving server plays it with a
profile that must be converted. Expected:

* the master playlist lists the second friend as a redundant variant, and that
  friend converts nothing until a player asks it;
* a player that asks the standby for its playlist gets segments from it, and a
  stop on the receiver reaches both friends;
* when the friend picked for a new play is down, the play starts on the other
  friend instead of failing, and the receiver still converts nothing itself;
* with Playwright and hls.js available, a real hls.js player in headless
  Chromium keeps playing after the server it plays from is stopped mid-play.

Only containers/network created here are touched; production is never used.
"""
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import json
import tempfile
import time
import urllib.parse
import federation_metadata_smoke as qa
from federation_handoff_smoke import transcode_logs, container_log

RELAY = '/Plugins/Federation/HandoffPlaylist?u='


def hls_version():
    return json.loads((qa.REPO / 'node_modules/hls.js/package.json').read_text())['version']


def smoke():
    work = Path(tempfile.mkdtemp(prefix='federation-standby-qa-')); os.chmod(work, 0o700)
    suffix = secrets.token_hex(5)
    network = 'fed-standby-' + suffix
    names = [f'fed-standby-{role}-{suffix}' for role in ('one', 'two', 'receiver')]
    created = []
    stage = 'setup'
    try:
        media = work / 'media'; media.mkdir()
        # The id in the name is what the receiver merges the two friends' copies on.
        movie = media / 'QA Standby (2026) [imdbid-tt9912345].mkv'
        qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--security-opt', 'label=disable', '-v', f'{work}:{work}',
            '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
        qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=24', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000', '-t', '90', '-c:v', 'libx264', '-preset', 'veryfast', '-b:v', '6M',
            '-c:a', 'aac', '-metadata:s:a:0', 'language=eng', '-y', movie])
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
            auth_header = {'Authorization':'MediaBrowser Client="Federation standby QA", Device="Sandbox", DeviceId="standby-qa", Version="1"'}
            auth = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-admin','Pw':password})
            admin = {'Authorization':'MediaBrowser Token="'+auth['AccessToken']+'"'}
            qa.request(base, '/Users/New', admin, {'Name':'qa-viewer'})
            viewer = qa.request(base, '/Users/AuthenticateByName', auth_header, {'Username':'qa-viewer','Pw':''})
            settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
            settings['ServerUrl'] = f'http://{name}:8096'
            settings['InternalServerUrl'] = 'http://127.0.0.1:8096'
            qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
            servers.append({'name': name, 'base': base, 'admin': admin, 'config': config, 'host': f'http://{name}:8096',
                            'viewer': {'Authorization':'MediaBrowser Token="'+viewer['AccessToken']+'"'}, 'viewer_id': viewer['User']['Id']})
        friends, receiver = servers[:2], servers[2]
        base, admin, viewer, viewer_id, config = receiver['base'], receiver['admin'], receiver['viewer'], receiver['viewer_id'], receiver['config']

        def movies(url, headers):
            return qa.request(url, '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams', headers)['Items']
        for friend in friends:
            qa.request(friend['base'], '/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true', friend['admin'],
                {'LibraryOptions':{'PathInfos':[{'Path':'/media'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
        for friend in friends:
            qa.wait_for(lambda: movies(friend['base'], friend['admin']), 'A friend did not scan its media')

        stage = 'friendship with both friends, one library from the two of them'
        sources = []
        for friend in friends:
            qa.check(qa.request(base, '/Plugins/Federation/Friends/Send', admin, {'Url': friend['host']})['success'], 'Friend request failed')
            pending = qa.request(friend['base'], '/Plugins/Federation/Friends', friend['admin'])['incoming'][0]['Id']
            qa.check(qa.request(friend['base'], f'/Plugins/Federation/Friends/{pending}/Accept', friend['admin'], {})['success'], 'Friend accept failed')
            theirs = qa.request(friend['base'], '/Plugins/Federation/Configuration', friend['admin'])['RemoteServers'][0]['Id']
            qa.check(qa.request(friend['base'], f'/Plugins/Federation/Friends/{theirs}/Sharing', friend['admin'], {'ShareAll':True})['success'], 'Sharing failed')
            friend['library'] = next(l['ItemId'] for l in qa.request(friend['base'], '/Library/VirtualFolders', friend['admin']) if l['Name'] == 'QA Native')
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        qa.check(len(settings['RemoteServers']) == 2, 'The receiver does not have two friends')
        for remote in settings['RemoteServers']:
            friend = next(f for f in friends if f['host'] in (remote.get('Url') or '') or f['name'] in (remote.get('Url') or ''))
            friend['id'] = remote['Id']
            remote['WanCapMode'] = 'Off'
            remote['StreamingMode'] = 'Direct'
            sources.append({'ServerId': remote['Id'], 'RemoteLibraryId': friend['library']})
        settings['LibraryMappings'] = [{'LocalLibraryName':'QA Federated','MediaType':'Movie','Enabled':True,'AutoProvision':True,
            'RemoteLibrarySources': sources}]
        settings['SmartSourceSelection'] = True
        settings['EnableDedup'] = True
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(base, '/Plugins/Federation/ProvisionLibraries', admin, {})
        def merged():
            qa.request(base, '/Plugins/Federation/Refresh', admin, {})
            found = movies(base, admin)
            return found if found and len(found[0].get('MediaSources', [])) >= 2 else None
        found = qa.wait_for(merged, 'The two friends\' copies did not merge into one title with two copies')
        qa.check(len(found) == 1, f'Expected one merged title, got {len(found)}')
        item_id = found[0]['Id']

        profile = {
            'MaxStreamingBitrate': 120000000,
            'DirectPlayProfiles': [],
            'TranscodingProfiles': [{'Container':'ts','Type':'Video','VideoCodec':'h264','AudioCodec':'aac','Protocol':'hls',
                                     'Context':'Streaming','MaxAudioChannels':'2','MinSegments':1,'BreakOnNonKeyFrames':True}],
            'SubtitleProfiles': [], 'CodecProfiles': [], 'ContainerProfiles': [],
        }
        def negotiate(bitrate=3000000):
            body = {'UserId': viewer_id, 'MaxStreamingBitrate': bitrate, 'EnableDirectPlay': False, 'EnableDirectStream': False,
                    'EnableTranscoding': True, 'AllowVideoStreamCopy': False, 'AllowAudioStreamCopy': False, 'DeviceProfile': profile}
            info = qa.request(base, f'/Items/{item_id}/PlaybackInfo?userId={viewer_id}', viewer, body)
            source = next(s for s in info['MediaSources'] if s['Id'].replace('-', '') == item_id.replace('-', ''))
            qa.check(source.get('TranscodingUrl'), 'Receiver offered no transcode for a must-convert profile')
            return info['PlaySessionId'], source
        def variants(master):
            return [l for l in master.splitlines() if l and not l.startswith('#')]
        def friend_of(variant_url):
            qa.check(variant_url.startswith(RELAY), 'A variant does not go through the receiver relay')
            target = urllib.parse.unquote(variant_url[len(RELAY):])
            return next(f for f in friends if target.startswith(f['host'] + '/Plugins/Federation/Peer/Play/'))
        def on_host(friend, url):
            for prefix in (friend['host'], friend['base']):
                if url.startswith(prefix):
                    return friend['base'] + url[len(prefix):]
            raise RuntimeError(f'Expected a URL on {friend["name"]}, got {url[:60]}')

        stage = 'the second friend is listed as a standby and converts nothing'
        before = len(transcode_logs(config))
        play_session, source = negotiate()
        _, master = qa.request(base, source['TranscodingUrl'], viewer, raw=True)
        master = master.decode()
        listed = variants(master)
        qa.check(len(listed) == 2, f'Expected the first server and one standby in the master playlist, got {len(listed)} variants')
        first, standby = friend_of(listed[0]), friend_of(listed[1])
        qa.check(first is not standby, 'The standby is the same server as the first')
        infs = [l for l in master.splitlines() if l.startswith('#EXT-X-STREAM-INF:')]
        qa.check(len(infs) == 2 and infs[0] == infs[1], 'The standby variant does not repeat the first variant\'s attributes')
        qa.check('stands by for' in container_log(receiver['name']), 'The receiver did not log the standby')
        qa.wait_for(lambda: len(transcode_logs(first['config'])) >= 1 or None, 'The first server did not start converting', seconds=20)
        qa.check(len(transcode_logs(standby['config'])) == 0, 'The standby started converting before a player asked it')
        _, variant = qa.request(base, listed[0], None, raw=True)
        _, segment = qa.request(on_host(first, variants(variant.decode())[0]), '', None, raw=True)
        qa.check(len(segment) > 10000 and segment[0] == 0x47, 'First server segment is not MPEG-TS video')
        print(f'STANDBY PASS: master lists {first["name"]} and, as a redundant variant, {standby["name"]}; only the first converts', flush=True)

        stage = 'a player that moves to the standby gets the same timeline from it'
        _, standby_variant = qa.request(base, listed[1], None, raw=True)
        first_segments, standby_segments = variants(variant.decode()), variants(standby_variant.decode())
        qa.check(len(first_segments) == len(standby_segments) and len(standby_segments) > 3, 'The two servers\' playlists have different segment counts')
        durations = lambda text: [l for l in text.splitlines() if l.startswith('#EXTINF')]
        qa.check(durations(variant.decode()) == durations(standby_variant.decode()), 'The two servers cut the title differently')
        _, moved_segment = qa.request(on_host(standby, standby_segments[2]), '', None, raw=True)
        qa.check(len(moved_segment) > 10000 and moved_segment[0] == 0x47, 'Standby segment is not MPEG-TS video')
        qa.check(len(transcode_logs(standby['config'])) == 1, 'The standby did not convert when the player asked it')
        qa.check('its standby server' in container_log(receiver['name']), 'The receiver did not notice the move')
        qa.check(len(transcode_logs(config)) == before, 'The receiving server ran a transcode itself')
        print('STANDBY PASS: the standby serves a later segment of the same play on request; the receiver converts nothing', flush=True)

        stage = 'stop on the receiver reaches both friends'
        played = {'ItemId': item_id, 'PlaySessionId': play_session, 'MediaSourceId': source['Id']}
        qa.request(base, '/Sessions/Playing', viewer, dict(played, PlayMethod='Transcode'), expected=204)
        qa.request(base, '/Sessions/Playing/Stopped', viewer, dict(played, PositionTicks=100000000), expected=204)
        for friend in friends:
            qa.wait_for(lambda: 'Deleting partial stream file' in container_log(friend['name']) or None, f'{friend["name"]} was not told to stop', seconds=30)
            qa.check('kill timer stopped' not in container_log(friend['name']), f'{friend["name"]} only ended by its idle timer')
        print('STANDBY PASS: the stop frees the conversion on both friends at once', flush=True)

        stage = 'a new play starts on the other friend when the picked one is down'
        play_session, source = negotiate(bitrate=2800000)
        _, probe = qa.request(base, source['TranscodingUrl'], viewer, raw=True)
        picked = friend_of(variants(probe.decode())[0])
        other = next(f for f in friends if f is not picked)
        qa.request(base, '/Sessions/Playing/Stopped', viewer, {'ItemId': item_id, 'PlaySessionId': play_session, 'MediaSourceId': source['Id'], 'PositionTicks': 0}, expected=204)
        other_before = len(transcode_logs(other['config']))
        play_session, source = negotiate(bitrate=2600000)
        qa.run(['podman', 'stop', '-t', '2', picked['name']])
        _, rescued = qa.request(base, source['TranscodingUrl'], viewer, raw=True)
        rescued = variants(rescued.decode())
        qa.check(friend_of(rescued[0]) is other, 'The play was not started on the other friend')
        _, rescued_variant = qa.request(base, rescued[0], None, raw=True)
        _, rescued_segment = qa.request(on_host(other, variants(rescued_variant.decode())[0]), '', None, raw=True)
        qa.check(len(rescued_segment) > 10000 and rescued_segment[0] == 0x47, 'Rescued play segment is not MPEG-TS video')
        qa.check(len(transcode_logs(other['config'])) > other_before, 'The other friend did not convert the rescued play')
        qa.check('starting it on' in container_log(receiver['name']), 'The receiver did not log the move at start')
        qa.check(len(transcode_logs(config)) == before, 'The receiving server ran a transcode itself')
        print(f'STANDBY PASS: with {picked["name"]} down, the play started on {other["name"]}; the receiver converts nothing', flush=True)

        stage = 'a real hls.js player keeps playing when its server goes away mid-play'
        try:
            from playwright.sync_api import sync_playwright
            hls_js = (qa.REPO / 'node_modules/hls.js/dist/hls.min.js').read_text()
        except (ImportError, OSError):
            print('STANDBY SKIP: no Playwright/hls.js here; the browser player stage did not run', flush=True)
            return
        qa.run(['podman', 'start', picked['name']])
        qa.wait_for(lambda: qa.request(picked['base'], '/System/Info/Public'), 'The stopped friend did not come back')
        qa.request(base, '/Sessions/Playing/Stopped', viewer, {'ItemId': item_id, 'PlaySessionId': play_session, 'MediaSourceId': source['Id'], 'PositionTicks': 0}, expected=204)
        def both_listed():
            # The friend that was down is skipped for a minute, then offered again.
            session, offered = negotiate(bitrate=2400000)
            _, text = qa.request(base, offered['TranscodingUrl'], viewer, raw=True)
            if len(variants(text.decode())) == 2:
                return session, offered
            qa.request(base, '/Sessions/Playing/Stopped', viewer, {'ItemId': item_id, 'PlaySessionId': session, 'MediaSourceId': offered['Id'], 'PositionTicks': 0}, expected=204)
            return None
        play_session, source = qa.wait_for(both_listed, 'The restarted friend was not offered again', seconds=180)
        qa.request(base, '/Sessions/Playing/Stopped', viewer, {'ItemId': item_id, 'PlaySessionId': play_session, 'MediaSourceId': source['Id'], 'PositionTicks': 0}, expected=204)
        play_session, source = negotiate(bitrate=2200000)
        play_url = base + source['TranscodingUrl']
        qa.check('apikey=' in play_url.lower() or 'api_key=' in play_url.lower(), 'The transcoding URL carries no key for a browser player')
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True, args=['--autoplay-policy=no-user-gesture-required'])
            page = browser.new_page()
            def reroute(route):
                # Friends' addresses are container names; send them to the mapped ports.
                url = route.request.url
                for friend in friends:
                    if url.startswith(friend['host']):
                        return route.continue_(url=friend['base'] + url[len(friend['host']):])
                return route.continue_()
            page.route('**/*', reroute)
            page.goto(base + '/System/Info/Public')
            page.add_script_tag(content=hls_js)
            page.evaluate("""url => {
                window.fed = { frags: [], errors: [] };
                const video = document.createElement('video');
                video.muted = true;
                document.body.appendChild(video);
                const hls = new Hls();
                hls.on(Hls.Events.FRAG_LOADED, (e, d) => fed.frags.push({ host: new URL(d.frag.url).host, sn: d.frag.sn }));
                hls.on(Hls.Events.ERROR, (e, d) => fed.errors.push({ details: d.details, fatal: d.fatal }));
                hls.loadSource(url);
                hls.attachMedia(video);
                video.play();
                window.fedVideo = video;
            }""", play_url)
            def state():
                return page.evaluate("""() => ({ time: fedVideo.currentTime,
                    end: fedVideo.buffered.length ? fedVideo.buffered.end(fedVideo.buffered.length - 1) : 0,
                    hosts: [...new Set(fed.frags.map(f => f.host))], frags: fed.frags.length,
                    fatal: fed.errors.filter(e => e.fatal).map(e => e.details), errors: fed.errors.length })""")
            qa.wait_for(lambda: state()['time'] > 4 or None, 'The browser player did not start playing', seconds=90)
            at_kill = state()
            qa.check(len(at_kill['hosts']) == 1, f'The player used both servers before anything failed: {at_kill["hosts"]}')
            playing_from = next(f for f in friends if urllib.parse.urlsplit(f['host']).netloc == at_kill['hosts'][0])
            moving_to = next(f for f in friends if f is not playing_from)
            qa.run(['podman', 'stop', '-t', '1', playing_from['name']])
            killed = time.monotonic()
            target = min(at_kill['end'] + 8, 84)
            moved_after = None; longest_pause = 0.0; last_time = at_kill['time']; last_change = killed
            while time.monotonic() - killed < 240:
                now = state()
                qa.check(not now['fatal'], f'The player gave up: {now["fatal"]}')
                if now['time'] > last_time + 0.05:
                    last_time = now['time']; last_change = time.monotonic()
                else:
                    longest_pause = max(longest_pause, time.monotonic() - last_change)
                if moved_after is None and urllib.parse.urlsplit(moving_to['host']).netloc in now['hosts']:
                    moved_after = time.monotonic() - killed
                if moved_after is not None and now['time'] >= target:
                    break
                time.sleep(0.5)
            final = state()
            browser.close()
        qa.check(moved_after is not None, f'hls.js never asked the standby for video ({final["errors"]} errors, at {final["time"]:.0f}s of {at_kill["end"]:.0f}s buffered)')
        qa.check(final['time'] >= target, f'Playback did not continue past what was buffered when the server went away ({final["time"]:.0f}s, needed {target:.0f}s)')
        qa.check(len(transcode_logs(config)) == before, 'The receiving server ran a transcode itself')
        print(f'STANDBY PASS: hls.js {hls_version()} lost {playing_from["name"]} at {at_kill["time"]:.0f}s with {at_kill["end"]:.0f}s buffered, '
              f'asked {moving_to["name"]} after {moved_after:.0f}s and played on to {final["time"]:.0f}s; longest pause {longest_pause:.1f}s', flush=True)
    except Exception as e:
        detail = str(e) if isinstance(e, (RuntimeError, KeyError, StopIteration)) else f'{type(e).__name__}: {e}'
        raise RuntimeError(f'{stage}: {detail}. Private fixture retained at {work}; do not publish logs.') from None
    finally:
        for name in reversed(created):
            subprocess.run(['podman','stop','-t','2',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(['podman','rm',name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['podman','network','rm',network], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print('STANDBY: isolated containers cleaned up; production untouched', flush=True)


if __name__ == '__main__':
    smoke()
