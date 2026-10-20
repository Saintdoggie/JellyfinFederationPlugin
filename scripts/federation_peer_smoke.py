#!/usr/bin/env python3
"""Disposable two-Jellyfin admin/viewer playback and revocation matrix."""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import federation_metadata_smoke as qa


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def no_redirect(base, path, headers):
    """GET without following redirects: (status, headers, body)."""
    opener = urllib.request.build_opener(_NoRedirect)
    try:
        with opener.open(urllib.request.Request(base + path, headers=headers), timeout=45) as r:
            return r.status, dict(r.headers), r.read()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


OLD_SOURCE = os.environ.get('FEDERATION_SOURCE_DLL')


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
            # FEDERATION_SOURCE_DLL runs the source on an older release, to check a
            # current receiver against a friend that hasn't updated yet.
            shutil.copy(OLD_SOURCE if (number == 0 and OLD_SOURCE) else qa.REPO / 'bin/Release/net10.0/Jellyfin.Plugin.Federation.dll', plugin)
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
        def target(url):
            # Direct-mode sources point at the source server itself (the bytes go
            # source -> client, never through the receiver); reach it on its
            # host-mapped port. Everything else is a receiver path.
            host = urllib.parse.urlsplit(url).netloc
            return (source_base if host == f'{names[0]}:8096' else base), path_only(url)
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
                host, stream = target(playable['Path'])
                qa.check(mode == 'Proxy' or host == source_base, 'Direct source is relayed through the receiver')
                _, served = qa.request(host, stream, headers, raw=True)
                qa.check(served == payload, 'Peer stream bytes differ')
                head, body = qa.request(host, stream, headers, method='HEAD', raw=True)
                qa.check(not body and int(head['Content-Length'])==len(payload), 'HEAD differs')
                _, ranged = qa.request(host, stream, {**headers,'Range':'bytes=137-648'}, expected=206, raw=True)
                qa.check(ranged==payload[137:649], 'Seek range differs')
            old_host, old_path = host, stream
            print(f'PEER PASS: {mode} native metadata, admin/viewer full playback, HEAD and seek ranges', flush=True)
        stage = 'viewer rule and old-capability revocation'
        result = qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId':viewer_id,'RemoteUserName':'qa-viewer','Mode':'Blocked'})
        qa.check(result['success'], 'Viewer revocation failed')
        qa.request(old_host, old_path, viewer, expected=403, raw=True)
        info = qa.request(base, f'/Items/{item_id}/PlaybackInfo', admin)
        qa.check(qa.request(*target(info['MediaSources'][0]['Path']), admin, raw=True)[1]==payload, 'Viewer revocation blocked admin')
        stage = 'viewer rule hides titles, not just playback'
        def viewer_sees():
            listed = len(movies(base, viewer))
            searched = len(qa.request(base, '/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=QA%20Peer', viewer)['Items'])
            latest = len(qa.request(base, '/Items/Latest?IncludeItemTypes=Movie', viewer))
            return listed, searched, latest
        qa.wait_for(lambda: viewer_sees() == (0, 0, 0) or None, 'Blocked viewer still sees the friend title')
        qa.request(base, f'/Items/{item_id}?userId={viewer_id}', viewer, expected=404)
        qa.request(base, f'/Items/{item_id}/PlaybackInfo', viewer, expected=404)
        qa.check(len(movies(base, admin)) == 1, 'Blocking the viewer hid the title from the admin too')
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId':viewer_id,'Mode':'AllLibraries'})['success'], 'Viewer restore failed')
        qa.wait_for(lambda: viewer_sees() == (1, 1, 1) or None, 'Unblocked viewer did not get the title back')
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId':viewer_id,'Mode':'AllLibraries','BlockedItemIds':[native['Id']]})['success'], 'Per-item block failed')
        qa.wait_for(lambda: viewer_sees() == (0, 0, 0) or None, 'Per-item block did not hide the title')
        qa.check(len(movies(base, admin)) == 1, 'Per-item block hid the title from the admin too')
        policy = qa.request(base, f'/Users/{viewer_id}', admin)['Policy']
        qa.check(any(t.startswith('federation-hidden-') for t in policy.get('BlockedTags') or []), 'Viewer policy has no hide tag')
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
            {'RemoteUserId':viewer_id,'Mode':'AllLibraries'})['success'], 'Viewer restore failed')
        qa.wait_for(lambda: viewer_sees() == (1, 1, 1) or None, 'Unblocked viewer did not get the title back')
        policy = qa.request(base, f'/Users/{viewer_id}', admin)['Policy']
        qa.check(not any(t.startswith('federation-hidden-') for t in policy.get('BlockedTags') or []), 'Hide tag left on an unrestricted viewer')
        print('PEER PASS: friend per-user block/per-item block hide the title from that user only (list, search, Latest, direct id) and restore', flush=True)
        stage = 'friend unshares the library'
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/Sharing', source_admin, {'ShareAll':False,'FolderIds':[]})['success'], 'Unshare failed')
        qa.wait_for(lambda: (not movies(base, admin) and not movies(base, viewer)) or None, 'Unshared library titles are still listed on the friend')
        qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/Sharing', source_admin, {'ShareAll':True})['success'], 'Reshare failed')
        qa.wait_for(refreshed, 'Reshared library did not come back')
        print('PEER PASS: unsharing a library removes its titles from the friend; resharing restores them', flush=True)
        stage = 'viewers download friends\' titles to their devices'
        item_id = movies(base, viewer)[0]['Id']
        def can_download(headers, user_id):
            return qa.request(base, f'/Items/{item_id}?userId={user_id}', headers).get('CanDownload')
        admin_id = qa.request(base, '/Users/Me', admin)['Id']
        qa.wait_for(lambda: can_download(viewer, viewer_id), 'Download is not offered on the friend title')
        if OLD_SOURCE:
            # A friend before 0.0.188 doesn't name the file itself, so this server
            # relays it; 0.0.188 does, so the viewer is sent straight there.
            status, relayed_headers, relayed = no_redirect(base, f'/Items/{item_id}/Download', viewer)
            if status == 302:
                location = relayed_headers.get('Location') or relayed_headers.get('location')
                relayed_headers, relayed = qa.request(source_base, path_only(location), {}, raw=True)
            qa.check(status in (200, 302) and relayed == payload, f'Download from an older friend failed (HTTP {status})')
            qa.check('QA Peer (2026).mkv' in (relayed_headers.get('Content-Disposition') or ''), 'Downloaded file is not named')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/DownloadAccess', source_admin,
                {'AllowDownloads':False,'AllowBulkDownloads':False})['success'], 'Friend download switch failed')
            status, _, body = no_redirect(base, f'/Items/{item_id}/Download', viewer)
            qa.check(status == 403 and b'error' in body, f'Older friend refusal not passed on (HTTP {status})')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/DownloadAccess', source_admin,
                {'AllowDownloads':True,'AllowBulkDownloads':False})['success'], 'Friend download restore failed')
            qa.check(no_redirect(base, f'/Items/{item_id}/Download', viewer)[0] in (200, 302), 'Download did not come back')
            qa.check(not qa.request(base, '/Plugins/Federation/FriendFavorites', viewer)['sections'], 'Older friend produced a favorites row')
            print('PEER PASS (older friend): downloads work (named, relayed or direct by the friend\'s version), the friend\'s refusal passed on; no favorites row, no errors', flush=True)
        else:
            status, located, _ = no_redirect(base, f'/Items/{item_id}/Download', viewer)
            qa.check(status == 302, f'Device download answered HTTP {status}, expected a redirect to the friend')
            location = located.get('Location') or located.get('location')
            parts = urllib.parse.urlsplit(location)
            qa.check(parts.netloc == f'{names[0]}:8096' and '/Plugins/Federation/DirectStream/' in parts.path
                and 'download=true' in parts.query and 'token=' in parts.query, 'Device download is not sent to the friend')
            secret = viewer['Authorization'].split('"')[1]
            qa.check(secret not in location and 'api_key' not in location.lower(), 'Device download URL carries a credential')
            got_headers, got = qa.request(source_base, path_only(location), {}, raw=True)
            qa.check(got == payload, 'File from the friend differs')
            disposition = got_headers.get('Content-Disposition') or ''
            qa.check(disposition.startswith('attachment') and 'QA Peer (2026).mkv' in disposition, 'Friend does not name the downloaded file')
            relayed_headers, relayed = qa.request(base, f'/Items/{item_id}/Download', {**viewer, 'X-Forwarded-Proto': 'https'}, raw=True)
            qa.check(relayed == payload, 'Relayed download differs')
            qa.check('QA Peer (2026).mkv' in (relayed_headers.get('Content-Disposition') or ''), 'Relayed download is not named')
            _, ranged = qa.request(base, f'/Items/{item_id}/Download', {**viewer, 'X-Forwarded-Proto': 'https', 'Range': 'bytes=137-648'}, expected=206, raw=True)
            qa.check(ranged == payload[137:649], 'Relayed download cannot resume')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
                {'RemoteUserId':viewer_id,'Mode':'AllLibraries','AllowDownload':False})['success'], 'Viewer download rule failed')
            qa.wait_for(lambda: can_download(viewer, viewer_id) is False or None, 'Download still offered after the friend turned it off for the viewer')
            status, _, body = no_redirect(base, f'/Items/{item_id}/Download', viewer)
            qa.check(status == 403 and b'error' in body, f'Viewer download not refused with a reason (HTTP {status})')
            qa.check(can_download(admin, admin_id), 'A per-viewer download rule removed Download for the admin too')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
                {'RemoteUserId':viewer_id,'Mode':'AllLibraries'})['success'], 'Viewer download restore failed')
            qa.wait_for(lambda: can_download(viewer, viewer_id), 'Download did not come back for the viewer')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/DownloadAccess', source_admin,
                {'AllowDownloads':False,'AllowBulkDownloads':False})['success'], 'Friend download switch failed')
            refreshed()
            qa.wait_for(lambda: (refreshed() and can_download(admin, admin_id) is False) or None, 'Download still offered after the friend turned downloads off')
            qa.check(no_redirect(base, f'/Items/{item_id}/Download', admin)[0] == 403, 'Download not refused after the friend turned downloads off')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/DownloadAccess', source_admin,
                {'AllowDownloads':True,'AllowBulkDownloads':False})['success'], 'Friend download restore failed')
            qa.wait_for(lambda: (refreshed() and can_download(viewer, viewer_id)) or None, 'Download did not come back')
            qa.request(source_base, f'/Plugins/Federation/DirectStream/{native["Id"]}?token=forged&download=true', {}, expected=403, raw=True)
            print('PEER PASS: any viewer downloads a friend title in their app: straight from the friend (named file, no credential), relayed when the scheme differs (resumable), refused with a reason when the friend turns it off per viewer or for everyone', flush=True)
            stage = 'friends\' favorites'
            source_admin_id = qa.request(source_base, '/Users/Me', source_admin)['Id']
            qa.request(source_base, f'/UserFavoriteItems/{native["Id"]}?userId={source_admin_id}', source_admin, {})
            qa.request(source_base, '/Plugins/Federation/Peer/Favorites', {}, expected=401, raw=True)
            def favorite_ids(headers):
                data = qa.request(base, '/Plugins/Federation/FriendFavorites', headers)
                return [i for section in data.get('sections') or [] for i in section.get('itemIds') or []]
            local = item_id.replace('-', '').lower()
            qa.wait_for(lambda: local in favorite_ids(viewer) or None, 'Friend favorite not shown to the viewer')
            data = qa.request(base, '/Plugins/Federation/FriendFavorites', viewer)
            qa.check(data['sections'][0]['name'] == settings['RemoteServers'][0]['Name'], 'Favorites section is not named after the friend')
            qa.check('qa-admin' not in json.dumps(data), 'Favorites reveal who favorited')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
                {'RemoteUserId':viewer_id,'Mode':'AllLibraries','BlockedItemIds':[native['Id']]})['success'], 'Per-item block failed')
            qa.wait_for(lambda: viewer_sees() == (0, 0, 0) or None, 'Per-item block did not hide the title')
            qa.check(local not in favorite_ids(viewer), 'A hidden title is shown in the viewer\'s friends\' favorites')
            qa.check(local in favorite_ids(admin), 'Hiding from the viewer removed the favorite for the admin')
            # A title only some viewers may see has no shared path (Jellyfin calls it Virtual).
            qa.check(can_download(admin, admin_id), 'Download not offered on a title other viewers are blocked from')
            qa.check(no_redirect(base, f'/Items/{item_id}/Download', admin)[0] == 302, 'Admin download of a per-viewer title failed')
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
                {'RemoteUserId':viewer_id,'Mode':'AllLibraries'})['success'], 'Viewer restore failed')
            qa.wait_for(lambda: viewer_sees() == (1, 1, 1) or None, 'Unblocked viewer did not get the title back')
            qa.request(source_base, '/Plugins/Federation/Settings/Favorites', source_admin, {'ShareFavoritesWithFriends':False,'ShowFriendsFavorites':True})
            qa.wait_for(lambda: (refreshed() and not favorite_ids(admin)) or None, 'Friend favorites still shown after the friend stopped sharing them')
            qa.request(source_base, '/Plugins/Federation/Settings/Favorites', source_admin, {'ShareFavoritesWithFriends':True,'ShowFriendsFavorites':True})
            qa.wait_for(lambda: (refreshed() and local in favorite_ids(admin)) or None, 'Friend favorites did not come back')
            qa.request(base, '/Plugins/Federation/Settings/Favorites', admin, {'ShareFavoritesWithFriends':True,'ShowFriendsFavorites':False})
            qa.check(not favorite_ids(viewer), 'Friends\' favorites shown after this server turned them off')
            qa.request(base, '/Plugins/Federation/Settings/Favorites', admin, {'ShareFavoritesWithFriends':True,'ShowFriendsFavorites':True})
            print('PEER PASS: friends\' favorites: titles only, per viewer visibility, owner and receiver switches', flush=True)
            stage = 'friends\' viewers on the owner\'s dashboard'
            receiver_name = source_settings['RemoteServers'][0]['Name']
            native_id = native['Id'].replace('-', '').lower()
            def owner_view():
                sessions = qa.request(source_base, '/Sessions', source_admin)
                return next((s for s in sessions if (s.get('NowPlayingItem') or {}).get('Id', '').replace('-', '').lower() == native_id), None)
            ticks = 10_000_000
            play = {'ItemId': item_id, 'MediaSourceId': item_id, 'PlaySessionId': 'qa-play-1', 'PositionTicks': 30 * ticks,
                'PlayMethod': 'DirectPlay', 'CanSeek': True, 'IsPaused': False}
            qa.request(base, '/Sessions/Playing', viewer, play, expected=204)
            shown = qa.wait_for(owner_view, 'Friend\'s viewer not shown on the owner\'s dashboard')
            qa.check(shown['UserName'] == f'qa-viewer ({receiver_name})', f'Friend viewer labelled {shown.get("UserName")!r}')
            qa.check(shown.get('UserId') in (None, '', '00000000000000000000000000000000', '00000000-0000-0000-0000-000000000000'), 'Friend viewer was tied to a local user')
            qa.check(receiver_name in (shown.get('DeviceName') or ''), 'Device line does not say which friend')
            qa.check(abs(shown['PlayState']['PositionTicks'] - 30 * ticks) < 15 * ticks, 'Owner shows the wrong position')
            qa.check(shown['PlayState'].get('PlayMethod') in ('DirectPlay', 'DirectStream'), 'Owner shows the wrong play method')
            qa.request(base, '/Sessions/Playing/Progress', viewer, {**play, 'PositionTicks': 95 * ticks, 'IsPaused': True}, expected=204)
            qa.wait_for(lambda: (owner_view() or {}).get('PlayState', {}).get('IsPaused') or None, 'Pause not shown on the owner\'s dashboard')
            qa.check(abs(owner_view()['PlayState']['PositionTicks'] - 95 * ticks) < 2 * ticks, 'Seek not shown on the owner\'s dashboard')
            connection = qa.request(base, '/Plugins/Federation/Playback/Connection', viewer)
            qa.check(connection.get('federated') and connection.get('friend') == settings['RemoteServers'][0]['Name'], f'Playback Info connection wrong: {connection}')
            qa.check(connection.get('route') in ('direct', 'relay') and connection.get('routeText'), 'Playback Info has no route')
            qa.check(connection.get('friendHardware'), 'Playback Info lacks the friend\'s shared hardware')
            qa.request(base, f'/Plugins/Federation/Playback/Connection?itemId={native_id}', viewer, expected=404, raw=True)
            qa.request(base, '/Sessions/Playing/Stopped', viewer, {**play, 'PositionTicks': 96 * ticks}, expected=204)
            qa.wait_for(lambda: owner_view() is None or None, 'Friend viewer still shown after stopping')
            qa.request(source_base, '/Plugins/Federation/Peer/Viewers', {}, {'Key': 'x', 'ItemId': native['Id']}, expected=401, raw=True)
            qa.request(source_base, '/Plugins/Federation/Peer/Hardware', {}, expected=401, raw=True)
            hardware = qa.request(base, '/Plugins/Federation/Settings/Hardware', admin)
            qa.check(hardware['friends'] and hardware['friends'][0]['hardware'], 'Friend hardware not listed for the admin')
            # A viewer the owner blocked from the title is never shown.
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
                {'RemoteUserId':viewer_id,'Mode':'AllLibraries','BlockedItemIds':[native['Id']]})['success'], 'Per-item block failed')
            qa.wait_for(lambda: viewer_sees() == (0, 0, 0) or None, 'Per-item block did not hide the title')
            try:
                qa.request(base, '/Sessions/Playing', viewer, {**play, 'PlaySessionId': 'qa-play-blocked'}, expected=204)
            except RuntimeError:
                pass  # Jellyfin may refuse the report itself; either way nothing may reach the owner.
            time.sleep(4)
            qa.check(owner_view() is None, 'A viewer the owner blocked from the title is shown on its dashboard')
            qa.request(base, '/Sessions/Playing', admin, {**play, 'PlaySessionId': 'qa-play-2'}, expected=204)
            qa.wait_for(lambda: (owner_view() or {}).get('UserName', '').startswith('qa-admin') or None, 'Admin viewer not shown on the owner\'s dashboard')
            qa.request(base, '/Sessions/Playing/Stopped', admin, {**play, 'PlaySessionId': 'qa-play-2'}, expected=204)
            qa.check(qa.request(source_base, f'/Plugins/Federation/Friends/{source_friend}/RemoteUserRule', source_admin,
                {'RemoteUserId':viewer_id,'Mode':'AllLibraries'})['success'], 'Viewer restore failed')
            qa.wait_for(lambda: viewer_sees() == (1, 1, 1) or None, 'Unblocked viewer did not get the title back')
            qa.request(source_base, '/Plugins/Federation/Settings/Viewers', source_admin, {'ShareViewersWithFriends': True, 'ShowFriendsViewers': False})
            qa.request(base, '/Sessions/Playing', viewer, {**play, 'PlaySessionId': 'qa-play-3'}, expected=204)
            qa.wait_for(lambda: (qa.request(base, '/Plugins/Federation/Playback/Connection', viewer).get('federated')) or None, 'Viewer not playing')
            time.sleep(4)
            qa.check(owner_view() is None, 'Friend viewer shown although the owner turned it off')
            qa.request(base, '/Sessions/Playing/Stopped', viewer, {**play, 'PlaySessionId': 'qa-play-3'}, expected=204)
            qa.request(source_base, '/Plugins/Federation/Settings/Viewers', source_admin, {'ShareViewersWithFriends': True, 'ShowFriendsViewers': True})
            print('PEER PASS: friends\' viewers show on the owner\'s dashboard (name (friend), position, pause, stop), not for blocked viewers or with the owner\'s switch off; Playback Info connection; hardware shared', flush=True)
        stage = 'disable, outage, cache retention and stable recovery'
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        settings['RemoteServers'][0]['Enabled'] = False
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.request(old_host, old_path, viewer, expected=403, raw=True)
        refreshed()
        qa.check(not movies(base, admin) and not movies(base, viewer), 'Disabled source remains visible')
        settings['RemoteServers'][0]['Enabled'] = True
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
        qa.wait_for(refreshed, 'Reenabled source did not return')
        cache = list(config.rglob('federation-cache.json'))[0]
        snapshot = json.loads(cache.read_text())
        qa.run(['podman','stop','-t','2',names[0]])
        for _ in range(2): qa.request(base, '/Plugins/Federation/Availability/Rescan', admin, {})
        # Since 0.0.184 titles already in the library stay listed while their
        # server is offline (they come back without churn); playback fails fast.
        qa.check(movies(base, admin) and movies(base, viewer), 'Offline source titles were dropped from the library')
        refreshed()
        qa.check(json.loads(cache.read_text())==snapshot, 'Outage erased cached catalog')
        qa.run(['podman','start',names[0]])
        qa.wait_for(lambda: qa.request(source_base, '/System/Info/Public'), 'Source restart failed')
        qa.request(base, '/Plugins/Federation/Availability/Rescan', admin, {})
        recovered = qa.wait_for(lambda: movies(base, viewer), 'Source titles did not return')
        qa.check(recovered[0]['Id']==item_id, 'Recovery changed identity')
        # Titles stay listed while offline (0.0.184), so wait for a playable source.
        def playable_again():
            qa.request(base, '/Plugins/Federation/Availability/Rescan', admin, {})
            return qa.request(base, f'/Items/{item_id}/PlaybackInfo', viewer).get('MediaSources') or None
        sources = qa.wait_for(playable_again, 'Recovered source never became playable')
        qa.check(qa.request(*target(sources[0]['Path']), viewer, raw=True)[1]==payload, 'Recovery playback failed')
        print('PEER PASS: viewer-only revocation, old URL denial, disabled/offline hiding, catalog retention and stable-ID recovery', flush=True)
        if not OLD_SOURCE:
            # Last: a server download replaces the friend's title with the local file.
            stage = 'server downloads land in the chosen library'
            (config / 'qa-movies').mkdir()
            qa.request(base, '/Library/VirtualFolders?name=QA%20Movies&collectionType=movies&refreshLibrary=false', admin,
                {'LibraryOptions':{'PathInfos':[{'Path':'/config/qa-movies'}],'EnableRealtimeMonitor':False,'EnableInternetProviders':False}}, expected=204)
            destinations = qa.request(base, '/Plugins/Federation/Download/Destinations', admin)
            qa.check(destinations['defaultDestination'] == 'auto', 'Default destination is not the matching library')
            qa.check(any(o['id'] == 'library:QA Movies' and o['path'] == '/config/qa-movies' for o in destinations['options']), 'QA Movies not offered')
            qa.check(not any('/plugins/' in o['path'].lower() for o in destinations['options']), 'The plugin\'s own folders are offered')
            qa.request(base, '/Plugins/Federation/Download/Destinations', viewer, expected=403, raw=True)
            qa.request(base, '/Plugins/Federation/Settings/DownloadDestination', admin, {'Destination': 'library:Nope'}, expected=400, raw=True)
            def finished(operation):
                def check():
                    progress = qa.request(base, f'/Plugins/Federation/Download/Progress/{operation}', admin)
                    return progress if progress.get('isComplete') else None
                done = qa.wait_for(check, 'Server download did not finish')
                qa.check(done.get('success'), f'Server download failed: {done.get("status")}')
            started = qa.request(base, '/Plugins/Federation/Download', admin, {'ItemId': item_id})
            finished(started['operationId'])
            landed = config / 'qa-movies' / 'QA Peer (2026)' / 'QA Peer (2026).mkv'
            qa.check(landed.exists() and landed.read_bytes() == payload, 'Download did not land as Movies/Name (Year)/Name (Year).mkv')
            qa.check(not list((config / 'qa-movies').rglob('*.partial')), 'A partial file was left behind')
            started = qa.request(base, '/Plugins/Federation/Browse/Download', admin, {'ServerId': friend, 'ItemId': native['Id'],
                'Name': 'QA Peer', 'Type': 'Movie', 'Year': 2026, 'Destination': 'folder:QA Friends'})
            finished(started['operationId'])
            fresh = config / 'QA Friends' / 'QA Peer (2026)' / 'QA Peer (2026).mkv'
            qa.check(fresh.exists() and fresh.read_bytes() == payload, 'New-folder download did not land beside the Movies folder')
            folders = qa.request(base, '/Library/VirtualFolders', admin)
            qa.check(any(f['Name'] == 'QA Friends' and '/config/QA Friends' in (f.get('Locations') or []) for f in folders), 'The new folder did not become a library')
            started = qa.request(base, '/Plugins/Federation/Browse/Download', admin, {'ServerId': friend, 'ItemId': native['Id'],
                'Name': 'QA Peer', 'Type': 'Movie', 'Year': 2026, 'Destination': 'library:QA Movies'})
            finished(started['operationId'])
            qa.check((config / 'qa-movies' / 'QA Peer (2026)' / f'QA Peer (2026) - {settings["RemoteServers"][0]["Name"]}.mkv').exists(),
                'A second copy was not saved as a version')
            refused = qa.request(base, '/Plugins/Federation/Browse/Download', admin, {'ServerId': friend, 'ItemId': native['Id'],
                'Name': 'QA Peer', 'Destination': 'folder:../escape'}, expected=400)
            qa.check(not refused.get('success'), 'An escaping folder was accepted')
            print('PEER PASS: server downloads go to the matching library as Name (Year)/Name (Year).ext, to a new folder made into a library, a second copy as a version; bad destinations refused', flush=True)
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
