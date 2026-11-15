#!/usr/bin/env python3
"""Direct link between a real Windows Jellyfin and a Linux one (see docs/DIRECT-LINK.md).

What it proves: the plugin loads on Windows, QUIC is available there, the two
servers punch a link in both directions, and a title plays over it each way.
What it does not prove: NAT behaviour (both machines sit on one LAN, with the
harness's own STUN responder) and the Windows firewall prompt.

Needs:
  - this machine: podman, a build made with -p:FederationTestHooks=true
  - a Windows machine on the same LAN reachable with ssh (key login, PowerShell),
    with a portable Jellyfin unpacked (the folder holding jellyfin.exe)

Nothing on the Windows machine is deleted; the server is stopped at the end.

The Windows firewall usually blocks the test server's web port on the LAN, and
this script never changes firewall rules. Give --win-host an address the web
port answers on (a VPN address works); the link itself is still punched over
the LAN, through the firewall, which is the point.

  scripts/federation_windows_smoke.py --ssh user@winhost --win-host 198.51.100.10 \\
      --win-lan-ip 192.0.2.10 --linux-ip 192.0.2.11 --win-jellyfin 'C:\\jf-test\\server\\jellyfin' --win-root 'C:\\jf-test'
"""
import argparse
import base64
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

import federation_metadata_smoke as qa
from federation_directlink_smoke import PROFILE, segments_ok

HERE = Path(__file__).resolve().parent
TEST_ENV = 'FEDERATION_DIRECTLINK_TEST_ALLOW_PRIVATE'
ONLY_HOST_ENV = 'FEDERATION_DIRECTLINK_TEST_ONLY_HOST'
RESULTS = []
NETWORK_XML = ('<?xml version="1.0" encoding="utf-8"?>\n<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
               'xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n  <InternalHttpPort>{port}</InternalHttpPort>\n'
               '  <PublicHttpPort>{port}</PublicHttpPort>\n</NetworkConfiguration>\n')


def log(msg):
    print(msg, flush=True)


def result(name, ok, detail=''):
    RESULTS.append((name, ok))
    log(f'{"PASS" if ok else "FAIL"}: {name}' + (f' ({detail})' if detail else ''))
    return ok


def guard(name, fn):
    try:
        r = fn()
        if r is None:
            r = True
        if isinstance(r, tuple):
            return result(name, r[0], r[1])
        return result(name, bool(r))
    except Exception as e:  # noqa: BLE001
        return result(name, False, f'{type(e).__name__}: {e}'[:400])


def wait_for(fn, label, seconds=60, every=1.0):
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        try:
            r = fn()
            if r:
                return r
        except (OSError, ValueError, RuntimeError, KeyError, IndexError) as e:
            last = e
        time.sleep(every)
    raise RuntimeError(label + (f' (last error: {last})' if last else ''))


def fetch(url, headers=None, timeout=45):
    req = urllib.request.Request(url, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


class Windows:
    """The Windows machine, driven over ssh with PowerShell."""

    def __init__(self, target, key):
        self.base = ['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10']
        if key:
            self.base += ['-i', key]
        self.target = target

    def ps(self, script, check=True, timeout=300):
        encoded = base64.b64encode(("$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " + script).encode('utf-16-le')).decode()
        p = subprocess.run(self.base + [self.target, 'powershell', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
                           capture_output=True, text=True, timeout=timeout)
        if check and p.returncode:
            raise RuntimeError(f'powershell failed: {(p.stderr or p.stdout).strip()[-400:]}')
        return p.stdout.strip()

    def put(self, local, remote):
        cmd = ['scp', '-q', '-o', 'BatchMode=yes'] + (self.base[self.base.index('-i'):][:2] if '-i' in self.base else [])
        subprocess.run(cmd + ['-r', str(local), f'{self.target}:{remote}'], check=True, capture_output=True, timeout=600)

    def get(self, remote, local):
        cmd = ['scp', '-q', '-o', 'BatchMode=yes'] + (self.base[self.base.index('-i'):][:2] if '-i' in self.base else [])
        subprocess.run(cmd + [f'{self.target}:{remote}', str(local)], check=True, capture_output=True, timeout=600)


class Server:
    def __init__(self, name, url):
        self.name, self.url = name, url
        self.admin = self.viewer = self.viewer_id = None

    def call(self, path, headers=None, data=None, **kw):
        return qa.request(self.url, path, headers or self.admin, data, **kw)

    def start_wizard(self):
        wait_for(lambda: qa.request(self.url, '/Startup/User'), f'{self.name} did not start', seconds=240)
        qa.request(self.url, '/Startup/Configuration', data={'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, expected=204)
        pw = secrets.token_urlsafe(24)
        qa.request(self.url, '/Startup/User', data={'Name': 'qa-admin', 'Password': pw}, expected=204)
        qa.request(self.url, '/Startup/RemoteAccess', data={'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False}, expected=204)
        qa.request(self.url, '/Startup/Complete', data={}, expected=204)
        hdr = {'Authorization': f'MediaBrowser Client="Windows link QA", Device="Sandbox", DeviceId="fwl-{self.name}", Version="1"'}
        auth = qa.request(self.url, '/Users/AuthenticateByName', hdr, {'Username': 'qa-admin', 'Pw': pw})
        self.admin = {'Authorization': f'MediaBrowser Token="{auth["AccessToken"]}"'}
        self.call('/Users/New', data={'Name': 'qa-viewer'})
        v = qa.request(self.url, '/Users/AuthenticateByName', hdr, {'Username': 'qa-viewer', 'Pw': ''})
        self.viewer = {'Authorization': f'MediaBrowser Token="{v["AccessToken"]}"'}
        self.viewer_id = v['User']['Id']

    def set_local_subnets(self, subnets):
        net = self.call('/System/Configuration/network')
        net['LocalNetworkSubnets'] = subnets
        self.call('/System/Configuration/network', data=net, expected=204)

    def configure_plugin(self, port, stun):
        cfg = self.call('/Plugins/Federation/Configuration')
        cfg['ServerUrl'] = self.url
        cfg['InternalServerUrl'] = f'http://127.0.0.1:{port}'
        self.call('/Plugins/Federation/Configuration', data=cfg)
        self.call('/Plugins/Federation/Settings/DirectLink', data={'EnableDirectLink': True, 'DirectLinkStunServers': stun})

    def status(self):
        return self.call('/Plugins/Federation/DirectLink/Status')

    def entry(self):
        st = self.status()
        rows = next((v for v in st.values() if isinstance(v, list) and v and isinstance(v[0], dict) and 'State' in v[0]), [])
        return rows[0] if rows else None

    def add_library(self, path):
        self.call('/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true',
                  data={'LibraryOptions': {'PathInfos': [{'Path': path}], 'EnableRealtimeMonitor': False, 'EnableInternetProviders': False}}, expected=204)
        wait_for(lambda: self.call('/Items?Recursive=true&IncludeItemTypes=Movie')['Items'], f'{self.name} did not scan its media', seconds=180)


def befriend(asker, owner):
    asker.call('/Plugins/Federation/Friends/Send', data={'Url': owner.url})
    pending = wait_for(lambda: owner.call('/Plugins/Federation/Friends')['incoming'][0]['Id'], 'friend request did not arrive')
    qa.check(owner.call(f'/Plugins/Federation/Friends/{pending}/Accept', data={})['success'], 'accept failed')
    wait_for(lambda: asker.call('/Plugins/Federation/Configuration')['RemoteServers'] and owner.call('/Plugins/Federation/Configuration')['RemoteServers'],
             'the friendship did not appear on both servers')


def share_and_import(owner, receiver, direct=True):
    """`owner` shares its library with its one friend; `receiver` imports it. Returns the imported item id."""
    friend_of_owner = owner.call('/Plugins/Federation/Configuration')['RemoteServers'][0]['Id']
    qa.check(owner.call(f'/Plugins/Federation/Friends/{friend_of_owner}/Sharing', data={'ShareAll': True})['success'], 'sharing failed')
    lib = next(l['ItemId'] for l in owner.call('/Library/VirtualFolders') if l['Name'] == 'QA Native')
    cfg = receiver.call('/Plugins/Federation/Configuration')
    friend = cfg['RemoteServers'][0]['Id']
    cfg['LibraryMappings'] = [{'LocalLibraryName': 'QA Federated', 'MediaType': 'Movie', 'Enabled': True, 'AutoProvision': True,
                               'RemoteLibrarySources': [{'ServerId': friend, 'RemoteLibraryId': lib}]}]
    if direct:
        cfg['RemoteServers'][0]['WanCapMode'] = 'Off'
        cfg['RemoteServers'][0]['StreamingMode'] = 'Direct'
    receiver.call('/Plugins/Federation/Configuration', data=cfg)
    receiver.call('/Plugins/Federation/ProvisionLibraries', data={})
    own = {i['Id'] for i in receiver.call('/Items?Recursive=true&IncludeItemTypes=Movie')['Items']}

    def imported():
        receiver.call('/Plugins/Federation/Refresh', data={})
        return [i for i in receiver.call('/Items?Recursive=true&IncludeItemTypes=Movie')['Items'] if i['Id'] not in own]
    return wait_for(imported, f'{receiver.name} did not import the catalogue', seconds=180, every=3)[0]['Id'], friend


def play(server, item_id, segments=2):
    body = {'UserId': server.viewer_id, 'MaxStreamingBitrate': 3000000, 'EnableDirectPlay': False, 'EnableDirectStream': False, 'EnableTranscoding': True,
            'AllowVideoStreamCopy': False, 'AllowAudioStreamCopy': False, 'DeviceProfile': PROFILE}
    info = server.call(f'/Items/{item_id}/PlaybackInfo?userId={server.viewer_id}', server.viewer, body)
    src = info['MediaSources'][0]
    qa.check(src.get('TranscodingUrl'), 'no TranscodingUrl offered')
    _, master = server.call(src['TranscodingUrl'], server.viewer, raw=True)
    variant_rel = next(l for l in master.decode().splitlines() if l and not l.startswith('#'))
    variant_url = urllib.parse.urljoin(server.url + src['TranscodingUrl'], variant_rel)
    status, playlist = fetch(variant_url)
    if status in (401, 403):
        status, playlist = fetch(variant_url, server.viewer)
    qa.check(status == 200, f'variant playlist HTTP {status}')
    segs = [urllib.parse.urljoin(variant_url, l) for l in playlist.decode().splitlines() if l and not l.startswith('#')]
    qa.check(len(segs) >= segments, f'only {len(segs)} segments listed')
    sizes = []
    for u in segs[:segments]:
        st, data = fetch(u)
        sizes.append((st, len(data), data[:1]))
    return {'segment_urls': segs[:segments], 'sizes': sizes}


def link_checks(viewer_side, owner_side, item_id, friend_id):
    tag = f'{viewer_side.name} -> {owner_side.name}'

    def connected():
        # The speed is measured just after the link comes up; wait for both.
        e = wait_for(lambda: (lambda e: e if e and e.get('State') == 'connected' and (e.get('Mbps') or 0) > 0 else None)(viewer_side.entry()),
                     'never reached connected with a measured speed', seconds=90)
        return True, f"Mbps={e.get('Mbps')} Remote={e.get('RemoteEndpoint')} Nat={e.get('NatLocal')}/{e.get('NatRemote')}"
    if not guard(f'[{tag}] link connected with Mbps > 0', connected):
        e = viewer_side.entry() or {}
        log(f"    state={e.get('State')} detail={e.get('Detail')!r} error={e.get('LastError')!r}")
        return
    before = (viewer_side.entry() or {}).get('BytesIn') or 0

    def played():
        r = play(viewer_side, item_id)
        through = all('/DirectLink/' in urllib.parse.unquote(u) for u in r['segment_urls'])
        return through and segments_ok(r), f"sizes={[(s, n) for s, n, _ in r['sizes']]} through-link={through}"
    guard(f'[{tag}] a title plays through the link', played)
    guard(f'[{tag}] link BytesIn increased', lambda: ((viewer_side.entry() or {}).get('BytesIn', 0) > before, f"{before} -> {(viewer_side.entry() or {}).get('BytesIn')}"))


def admin_post(server, path, data):
    """Like Server.call for an admin POST, but a refusal's message is in the error."""
    req = urllib.request.Request(server.url + path, data=json.dumps(data).encode(), headers={**server.admin, 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=45) as r:
            return json.loads(r.read() or b'{}')
    except urllib.error.HTTPError as e:
        raise RuntimeError(f'{path.split("?")[0]}: HTTP {e.code} {e.read()[:300]!r}') from None


def paired_status(server):
    rows = server.call('/Plugins/Federation/Pairing/Status')['Friends']
    return rows[0] if rows else None


def paired_checks(offerer, answerer, w, l, windows_role):
    """Friends with no reachable address: two codes, then everything over the punched link."""
    for s in (w, l):
        s.call('/Plugins/Federation/Settings/Pairing', data={'EnablePairing': True, 'PortFirst': 47950, 'PortLast': 47959})

    def paired():
        offer = admin_post(offerer, '/Plugins/Federation/Pairing/Offer', {})
        qa.check(offer.get('success') and offer.get('code'), f'no offer: {offer}')
        answer = admin_post(answerer, '/Plugins/Federation/Pairing/Answer', {'Code': offer['code']})
        qa.check(answer.get('success'), f'answer failed: {answer}')
        done = admin_post(offerer, '/Plugins/Federation/Pairing/Complete', {'Code': answer['code']})
        qa.check(done.get('success'), f'complete failed: {done}')
        up = lambda s: (lambda f: f if f and f['State'] == 'connected' else None)(paired_status(s))
        st_w = wait_for(lambda: up(w), 'Windows never connected', seconds=90)
        st_l = wait_for(lambda: up(l), 'Linux never connected', seconds=90)
        return st_w['SafetyNumber'] == st_l['SafetyNumber'] and len(st_w['SafetyNumber']) == 35, f"Windows {windows_role()}; {st_w['Detail']}"
    if not guard(f'[paired, {offerer.name} offers] two codes connect both servers with one safety number', paired):
        for s in (w, l):
            f = paired_status(s) or {}
            log(f"    {s.name}: state={f.get('State')} detail={f.get('Detail')!r}")
        return

    def confirmed():
        for s in (w, l):
            admin_post(s, f"/Plugins/Federation/Pairing/{paired_status(s)['ServerId']}/Confirm", {'SafetyNumber': paired_status(s)['SafetyNumber']})
        for s in (w, l):
            wait_for(lambda s=s: paired_status(s)['Ready'] or None, f'{s.name} never ready', seconds=90)
    if not guard('[paired] after both confirm, the friendship is set up', confirmed):
        return
    for viewer_side, owner_side in ((w, l), (l, w)):
        holder = {}

        def synced():
            holder['item'], _ = share_and_import(owner_side, viewer_side, direct=False)
        if guard(f'[paired] {viewer_side.name} gets {owner_side.name}\'s catalogue over the link', synced):
            def played():
                r = play(viewer_side, holder['item'])
                return segments_ok(r), f"sizes={[(s, n) for s, n, _ in r['sizes']]}"
            guard(f'[paired] a viewer on {viewer_side.name} plays {owner_side.name}\'s title over the link', played)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--ssh', required=True, help='user@host of the Windows machine')
    ap.add_argument('--ssh-key', default=os.environ.get('FWL_SSH_KEY'))
    ap.add_argument('--win-host', required=True, help='address the Windows test server answers http on, from here and from the Linux server')
    ap.add_argument('--local-subnets', default='10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10',
                    help='what both servers treat as their own network, so this script counts as a viewer at home')
    ap.add_argument('--linux-ip', required=True, help="this machine's LAN address")
    ap.add_argument('--win-lan-ip', required=True, help="the Windows machine's address on the same LAN; the link is punched between the two LAN addresses only")
    ap.add_argument('--win-jellyfin', required=True, help='folder on Windows holding jellyfin.exe')
    ap.add_argument('--win-root', required=True, help='folder on Windows for this test (data, media, log)')
    ap.add_argument('--win-port', type=int, default=18096)
    ap.add_argument('--linux-port', type=int, default=18196)
    ap.add_argument('--stun-ports', default='13478,13479')
    ap.add_argument('--plugin-dir', required=True, help='plugin build made with -p:FederationTestHooks=true')
    ap.add_argument('--scenario', choices=['direct', 'paired'], default='direct',
                    help='direct: friends by address plus the direct link. paired: friends by pairing codes only; which side accepts is random per pairing (it is printed), so run it until both have been seen')
    ap.add_argument('--offerer', choices=['windows', 'linux'], default='windows', help='paired: which server makes the first code')
    ap.add_argument('--keep', action='store_true', help='leave both servers running')
    args = ap.parse_args()

    dll = Path(args.plugin_dir)
    qa.check((dll / 'Jellyfin.Plugin.Federation.dll').exists(), f'plugin build not found in {dll}')
    win = Windows(args.ssh, args.ssh_key)
    run_id = secrets.token_hex(3)
    root = args.win_root.rstrip('\\') + '\\run-' + run_id
    os.environ.setdefault('TMPDIR', str(Path.home() / '.t'))
    base_tmp = Path(os.environ['TMPDIR']) / 'fwl'
    base_tmp.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='run-', dir=base_tmp))
    container = 'fwl-jf-linux-' + run_id
    stun_ports = [int(p) for p in args.stun_ports.split(',')]
    stun = None
    stage = 'windows setup'
    w = Server('windows', f'http://{args.win_host}:{args.win_port}')
    l = Server('linux', f'http://{args.linux_ip}:{args.linux_port}')
    try:
        # ---- Windows: folders, plugin, media (made there), server
        win.ps(f"New-Item -ItemType Directory -Force '{root}\\data\\plugins\\Federation','{root}\\config','{root}\\media-win','{root}\\media-linux' | Out-Null")
        stagedir = work / 'plugin'
        stagedir.mkdir()
        for f in dll.iterdir():
            if (f.suffix in ('.dll', '.json') or f.name.endswith('.deps.json')) and 'QuicTransport' not in f.name:
                shutil.copy(f, stagedir)
        for f in stagedir.iterdir():
            win.put(f, f'{root}\\data\\plugins\\Federation\\'.replace('\\', '/'))
        (work / 'network.xml').write_text(NETWORK_XML.format(port=args.win_port))
        win.put(work / 'network.xml', f'{root}\\config\\'.replace('\\', '/'))
        ff = f"& '{args.win_jellyfin}\\ffmpeg.exe' -v error -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 60 -c:v libx264 -preset veryfast -b:v 6M -c:a aac -y"
        win.ps(f"{ff} '{root}\\media-win\\QA Windows (2026).mkv'; {ff} '{root}\\media-linux\\QA Linux (2026).mkv'")
        media = work / 'media'
        media.mkdir()
        win.get(f'{root}\\media-linux\\QA Linux (2026).mkv'.replace('\\', '/'), media / 'QA Linux (2026).mkv')
        # Started through WMI so the server outlives this ssh session.
        command = (f'cmd /c "set {TEST_ENV}=1&& set {ONLY_HOST_ENV}={args.win_lan_ip}&& "{args.win_jellyfin}\\jellyfin.exe" --datadir "{root}\\data" --configdir "{root}\\config" '
                   f'--cachedir "{root}\\cache" --logdir "{root}\\log" > "{root}\\console.log" 2>&1"')
        win.ps("$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='" + command.replace("'", "''") + "'}; if ($r.ReturnValue -ne 0) { throw \"start failed: $($r.ReturnValue)\" }")

        # ---- Linux: STUN responder, server on the host network
        stage = 'linux setup'
        stun = subprocess.Popen([sys.executable, str(HERE / 'fdl_stun_server.py'), *map(str, stun_ports)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        config = work / 'jf-linux'
        plugin = config / 'plugins/Federation'
        plugin.mkdir(parents=True)
        for f in stagedir.iterdir():
            shutil.copy(f, plugin)
        shutil.copytree(dll / 'native', plugin / 'native')
        (config / 'config').mkdir()
        (config / 'config/network.xml').write_text(NETWORK_XML.format(port=args.linux_port))
        subprocess.run(['podman', 'run', '-d', '--name', container, '--network', 'host', '--security-opt', 'label=disable', '-e', f'{TEST_ENV}=1', '-e', f'{ONLY_HOST_ENV}={args.linux_ip}',
                        '-v', f'{config}:/config', '-v', f'{media}:/media:ro', qa.JELLYFIN_IMAGE], check=True, capture_output=True)

        stage = 'jellyfin startup'
        stun_servers = [f'{args.linux_ip}:{p}' for p in stun_ports]
        for s, port in ((w, args.win_port), (l, args.linux_port)):
            s.start_wizard()
            s.set_local_subnets(args.local_subnets.split(','))
            s.configure_plugin(port, stun_servers)
        guard('Windows: plugin loaded and the direct link is available', lambda: (w.status().get('Available') is True, f"Reason={w.status().get('Reason')!r}"))
        guard('Linux: the direct link is available', lambda: (l.status().get('Available') is True, f"Reason={l.status().get('Reason')!r}"))

        stage = 'libraries'
        w.add_library(f'{root}\\media-win')
        l.add_library('/media')
        if args.scenario == 'paired':
            stage = 'pairing'
            def windows_role():
                # Which side accepts is decided by the two certificates, so it is random per pairing.
                line = win.ps(f"(Select-String -Path '{root}\\console.log' -Pattern 'Paired link to .* is up' | Select-Object -Last 1).Line", check=False)
                return 'accepted on its punched socket' if 'accepted on the punched socket' in line else 'dialled' if 'dialled' in line else 'role unknown'
            paired_checks(*((w, l) if args.offerer == 'windows' else (l, w)), w, l, windows_role)
        else:
            stage = 'befriending'
            befriend(w, l)
            item_on_w, friend_on_w = share_and_import(l, w)
            item_on_l, friend_on_l = share_and_import(w, l)
            log('befriended; each server imported the other\'s title')
            stage = 'link'
            link_checks(w, l, item_on_w, friend_on_w)
            link_checks(l, w, item_on_l, friend_on_l)
    except Exception as e:  # noqa: BLE001
        result(f'harness stage "{stage}"', False, f'{type(e).__name__}: {e}'[:500])
    finally:
        if args.keep:
            log(f'--keep: servers left running. Windows folder {root}; Linux container {container}; work dir {work}')
        else:
            subprocess.run(['podman', 'rm', '-f', '-t', '20', container], capture_output=True)
            try:
                win.ps(f"Get-Process jellyfin -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{args.win_jellyfin}\\*' }} | Stop-Process -Force", check=False)
            except Exception:  # noqa: BLE001
                pass
            log(f'servers stopped. Left in place: Windows folder {root}; work dir {work}')
        if stun:
            stun.terminate()
    failed = [n for n, ok in RESULTS if not ok]
    log(f'\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed' + (f'; FAILED: {len(failed)}' if failed else ''))
    sys.exit(1 if failed or not RESULTS else 0)


if __name__ == '__main__':
    main()
