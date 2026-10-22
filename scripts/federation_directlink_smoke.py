#!/usr/bin/env python3
"""Two-home NAT harness for the Federation direct link (see docs/DIRECT-LINK.md).

Topology (rootless podman; everything is prefixed fdl- and removed at the end):

  fdl-wan 10.231.0.0/24 ("internet")   stun .5, router-a .10, router-b .11, viewer-w .20
  fdl-lan-a 10.231.1.0/24 (internal)   router-a .2, jf-a .10, viewer-a .20
  fdl-lan-b 10.231.2.0/24 (internal)   router-b .2, jf-b .10, viewer-b .20

The LAN networks are `--internal` with `--route 0.0.0.0/0,<router lan ip>`, so
netavark gives every LAN container a default route through its router and the
host never NATs them. Routers MASQUERADE out of the WAN, accept WAN->LAN only for
ESTABLISHED,RELATED, and DNAT TCP 8096 to their Jellyfin (the stand-in for the
VPS tunnel: HTTP reachable, UDP not). The host cannot reach the internal
networks, so every HTTP call is made with `podman exec` inside a viewer container.
"""
import argparse
import base64
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.parse
from pathlib import Path

import federation_metadata_smoke as qa

HERE = Path(__file__).resolve().parent
IMAGE_TOOLS = 'localhost/fdl-tools:1'
ALPINE = 'docker.io/library/alpine:3.22'
NETS = {'wan': '10.231.0.0/24', 'lan-a': '10.231.1.0/24', 'lan-b': '10.231.2.0/24'}
IP = {'stun': '10.231.0.5', 'router-a-wan': '10.231.0.10', 'router-b-wan': '10.231.0.11', 'viewer-w': '10.231.0.20',
      'router-a-lan': '10.231.1.2', 'jf-a': '10.231.1.10', 'viewer-a': '10.231.1.20',
      'router-b-lan': '10.231.2.2', 'jf-b': '10.231.2.10', 'viewer-b': '10.231.2.20'}
PUBLIC = {'a': f"http://{IP['router-a-wan']}:8096", 'b': f"http://{IP['router-b-wan']}:8096"}
LAN = {'a': f"http://{IP['jf-a']}:8096", 'b': f"http://{IP['jf-b']}:8096"}
CREATED_CONTAINERS, CREATED_NETS = [], []
RESULTS = []


def pm(*args, check=True, input=None):
    p = subprocess.run(['podman', *[str(a) for a in args]], capture_output=True, text=True, input=input)
    if check and p.returncode:
        raise RuntimeError(f'podman {" ".join(map(str, args[:3]))} failed: {p.stderr.strip()[:400]}')
    return p.stdout.strip() if check else p


def log(msg):
    print(msg, flush=True)


def result(name, ok, detail=''):
    RESULTS.append((name, ok))
    log(f'{"PASS" if ok else "FAIL"}: {name}' + (f' ({detail})' if detail else ''))
    return ok


def guard(name, fn):
    """Run one check; an exception is a FAIL, never an abort."""
    try:
        r = fn()
        if r is None:
            r = True
        if isinstance(r, tuple):
            return result(name, r[0], r[1])
        return result(name, bool(r))
    except Exception as e:  # noqa: BLE001
        return result(name, False, f'{type(e).__name__}: {e}'[:300])


# ---------------------------------------------------------------- topology

def build_tools_image():
    if pm('image', 'exists', IMAGE_TOOLS, check=False).returncode == 0:
        return
    ctx = Path(tempfile.mkdtemp(prefix='fdl-build-', dir=os.environ.get('TMPDIR', str(Path.home() / '.t'))))
    (ctx / 'Containerfile').write_text(f'FROM {ALPINE}\nRUN apk add --no-cache iptables python3 curl tcpdump\n')
    log('building fdl-tools image (alpine + iptables + python3 + curl)')
    pm('build', '-t', IMAGE_TOOLS, ctx)
    shutil.rmtree(ctx, ignore_errors=True)


def net_create(name, subnet, internal=False, route=None):
    args = ['network', 'create', '--subnet', subnet]
    if internal:
        args.append('--internal')
    if route:
        args += ['--route', f'0.0.0.0/0,{route}']
    pm(*args, name)
    CREATED_NETS.append(name)


def ctr(name, *args, image=IMAGE_TOOLS, entry=None, tail=('sleep', 'infinity')):
    cmd = ['run', '-d', '--name', name, '--security-opt', 'label=disable', *args]
    if entry:
        cmd += ['--entrypoint', entry]
    cmd += [image] + ([] if entry is None and tail is None else list(tail or []))
    pm(*cmd)
    CREATED_CONTAINERS.append(name)


FORWARD = {'on': True}


def router_rules(side, random_fully, forward=None):
    """Shell to (re)program a router. WAN/LAN interfaces are looked up by address.

    forward=False: no port forwarding at all (the paired scenario: neither home reachable)."""
    forward = FORWARD['on'] if forward is None else forward
    mq = '-j MASQUERADE' + (' --random-fully' if random_fully else '')
    fwd = (f"iptables -A FORWARD -i $WAN -o $LAN -p tcp -d {IP['jf-' + side]} --dport 8096 -j ACCEPT\n"
           f"iptables -t nat -A PREROUTING -i $WAN -p tcp --dport 8096 -j DNAT --to-destination {IP['jf-' + side]}:8096\n") if forward else ''
    return f'''set -e
WAN=$(ip -o -4 addr show | awk '/10\\.231\\.0\\./ {{print $2}}' | head -1)
LAN=$(ip -o -4 addr show | awk '/10\\.231\\.[12]\\./ {{print $2}}' | head -1)
iptables -t nat -F; iptables -F
iptables -P FORWARD DROP
iptables -A FORWARD -i $LAN -o $WAN -j ACCEPT
iptables -A FORWARD -i $WAN -o $LAN -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
{fwd}iptables -t nat -A POSTROUTING -o $WAN {mq}
iptables -A INPUT -i $WAN -p udp -j DROP
echo "wan=$WAN lan=$LAN"
'''


def build_topology(forward=True):
    FORWARD['on'] = forward
    build_tools_image()
    net_create('fdl-wan', NETS['wan'])
    net_create('fdl-lan-a', NETS['lan-a'], internal=True, route=IP['router-a-lan'])
    net_create('fdl-lan-b', NETS['lan-b'], internal=True, route=IP['router-b-lan'])
    scripts = f'{HERE}:/fdl:ro'
    # STUN on the internet network
    ctr('fdl-stun', '--network', f"fdl-wan:ip={IP['stun']}", '-v', scripts, tail=('python3', '/fdl/fdl_stun_server.py', '3478', '3479'))
    for s in 'ab':
        # router: starts on the WAN, then joins its LAN with a fixed address
        ctr(f'fdl-router-{s}', '--network', f"fdl-wan:ip={IP['router-' + s + '-wan']}", '--cap-add', 'NET_ADMIN',
            '--sysctl', 'net.ipv4.ip_forward=1')
        pm('network', 'connect', '--ip', IP[f'router-{s}-lan'], f'fdl-lan-{s}', f'fdl-router-{s}')
        pm('exec', f'fdl-router-{s}', 'sh', '-c', router_rules(s, False, forward))
        ctr(f'fdl-viewer-{s}', '--network', f"fdl-lan-{s}:ip={IP['viewer-' + s]}", '-v', scripts)
    ctr('fdl-viewer-w', '--network', f"fdl-wan:ip={IP['viewer-w']}", '-v', scripts)


def set_nat(random_fully):
    for s in 'ab':
        pm('exec', f'fdl-router-{s}', 'sh', '-c', router_rules(s, random_fully))
        # drop tracked UDP mappings so the next link attempt gets fresh ones
        pm('exec', f'fdl-router-{s}', 'sh', '-c', 'echo 0 > /proc/sys/net/netfilter/nf_conntrack_udp_timeout 2>/dev/null; true', check=False)


def teardown():
    for c in reversed(CREATED_CONTAINERS):
        pm('rm', '-f', '-t', '0', c, check=False)
    for n in reversed(CREATED_NETS):
        pm('network', 'rm', '-f', n, check=False)


# ---------------------------------------------------------------- http (via exec)

def http(where, url, path='', headers=None, data=None, method=None, timeout=45, max_bytes=64 * 1024 * 1024):
    """Return (status, headers, body_bytes) from a request made inside fdl-viewer-<where>."""
    headers = dict(headers or {})
    body = None
    if data is not None:
        if isinstance(data, bytes):
            body = data
        else:
            body = json.dumps(data).encode()
            headers['Content-Type'] = 'application/json'
    req = {'url': url + path, 'method': method, 'headers': headers, 'timeout': timeout, 'max_bytes': max_bytes,
           'body_b64': base64.b64encode(body).decode() if body else ''}
    p = subprocess.run(['podman', 'exec', '-i', f'fdl-viewer-{where}', 'python3', '/fdl/fdl_http.py'],
                       input=json.dumps(req), capture_output=True, text=True, timeout=timeout + 30)
    if p.returncode:
        raise RuntimeError(f'exec failed: {p.stderr.strip()[:300]}')
    out = json.loads(p.stdout)
    if out['error']:
        raise RuntimeError(f'{url}{path[:60]}: {out["error"]}')
    return out['status'], out['headers'], base64.b64decode(out['body_b64'])


def api(where, base, path, headers=None, data=None, method=None, expected=200, raw=False):
    status, hdrs, body = http(where, base, path, headers, data, method)
    if status != expected:
        raise RuntimeError(f'{path.split("?")[0]}: HTTP {status}, expected {expected} ({body[:120]!r})')
    if raw:
        return hdrs, body
    return json.loads(body) if body else None


def wait_for(fn, label, seconds=60, every=1.0):
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        try:
            r = fn()
            if r:
                return r
        except (OSError, ValueError, RuntimeError, KeyError) as e:
            last = e
        time.sleep(every)
    raise RuntimeError(f'{label}' + (f' (last error: {last})' if last else ''))


# ---------------------------------------------------------------- Jellyfin servers

class Server:
    def __init__(self, side, work, dll):
        self.side, self.work, self.dll = side, work, dll
        self.name = f'fdl-jf-{side}'
        self.lan, self.public = LAN[side], PUBLIC[side]
        self.config = work / self.name
        self.admin = self.viewer = self.viewer_id = None

    def run_container(self, media):
        plugin = self.config / 'plugins/Federation'
        plugin.mkdir(parents=True, exist_ok=True)
        for f in self.dll.iterdir():
            if (f.suffix in ('.dll', '.json', '.so') or f.name.endswith('.deps.json')) and 'QuicTransport' not in f.name:
                shutil.copy(f, plugin)
        if (self.dll / 'native').is_dir():
            # The bundled MsQuic ships in the release zip under native/<rid>/.
            shutil.copytree(self.dll / 'native', plugin / 'native', dirs_exist_ok=True)
        ctr(self.name, '--network', f"fdl-lan-{self.side}:ip={IP['jf-' + self.side]}", '-e', 'FEDERATION_DIRECTLINK_TEST_ALLOW_PRIVATE=1',
            '-v', f'{self.config}:/config', '-v', f'{media}:/media:ro', image=qa.JELLYFIN_IMAGE, tail=None)

    def call(self, path, headers=None, data=None, **kw):
        return api(self.side, self.lan, path, headers or self.admin, data, **kw)

    def start_wizard(self):
        s = self.side
        wait_for(lambda: api(s, self.lan, '/Startup/User'), f'{self.name} did not start', seconds=180)
        api(s, self.lan, '/Startup/Configuration', data={'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'}, expected=204)
        pw = secrets.token_urlsafe(24)
        api(s, self.lan, '/Startup/User', data={'Name': 'qa-admin', 'Password': pw}, expected=204)
        api(s, self.lan, '/Startup/RemoteAccess', data={'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False}, expected=204)
        api(s, self.lan, '/Startup/Complete', data={}, expected=204)
        hdr = {'Authorization': 'MediaBrowser Client="Direct link QA", Device="Sandbox", DeviceId="fdl-qa", Version="1"'}
        auth = api(s, self.lan, '/Users/AuthenticateByName', hdr, {'Username': 'qa-admin', 'Pw': pw})
        self.admin = {'Authorization': f'MediaBrowser Token="{auth["AccessToken"]}"'}
        api(s, self.lan, '/Users/New', self.admin, {'Name': 'qa-viewer'})
        v = api(s, self.lan, '/Users/AuthenticateByName', hdr, {'Username': 'qa-viewer', 'Pw': ''})
        self.viewer = {'Authorization': f'MediaBrowser Token="{v["AccessToken"]}"'}
        self.viewer_id = v['User']['Id']
        # Only this home's own LAN is "local"; the WAN network must look remote.
        net = self.call('/System/Configuration/network')
        net['LocalNetworkSubnets'] = [NETS['lan-' + s]]
        self.call('/System/Configuration/network', data=net, expected=204)

    def configure_plugin(self):
        cfg = self.call('/Plugins/Federation/Configuration')
        cfg['ServerUrl'] = self.public
        cfg['InternalServerUrl'] = 'http://127.0.0.1:8096'
        self.call('/Plugins/Federation/Configuration', data=cfg)
        # Direct link settings have their own route (the main save keeps them as they were).
        self.call('/Plugins/Federation/Settings/DirectLink', data={'EnableDirectLink': True, 'DirectLinkStunServers': [f"{IP['stun']}:3478", f"{IP['stun']}:3479"]})

    def status(self):
        return self.call('/Plugins/Federation/DirectLink/Status')

    def entry(self, friend_id=None):
        st = self.status()
        rows = st.get('Friends') or st.get('Entries') or st.get('Peers') or []
        if not rows:
            rows = next((v for v in st.values() if isinstance(v, list) and v and isinstance(v[0], dict) and 'State' in v[0]), [])
        return next((r for r in rows if friend_id in (None, r.get('ServerId'))), None)


def make_media(work):
    media = work / 'media'
    media.mkdir()
    movie = media / 'QA Link (2026).mkv'
    qa.FFMPEG_PREFIX = ['podman', 'run', '--rm', '--name', 'fdl-ffmpeg-' + secrets.token_hex(3), '--security-opt', 'label=disable', '-v', f'{work}:{work}',
                        '--entrypoint', '/usr/lib/jellyfin-ffmpeg/ffmpeg', qa.JELLYFIN_IMAGE]
    qa.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=24', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000',
            '-t', '90', '-c:v', 'libx264', '-preset', 'veryfast', '-b:v', '6M', '-c:a', 'aac', '-y', movie])
    return media


def befriend(a, b):
    """B owns the media and shares everything; A (the receiver / initiator) consumes it."""
    api('a', a.lan, '/Plugins/Federation/Friends/Send', a.admin, {'Url': b.public})
    pending = wait_for(lambda: b.call('/Plugins/Federation/Friends')['incoming'][0]['Id'], 'friend request did not arrive')
    qa.check(b.call(f'/Plugins/Federation/Friends/{pending}/Accept', data={})['success'], 'accept failed')
    b_friend = b.call('/Plugins/Federation/Configuration')['RemoteServers'][0]['Id']
    qa.check(b.call(f'/Plugins/Federation/Friends/{b_friend}/Sharing', data={'ShareAll': True})['success'], 'sharing failed')
    b.call('/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true',
           data={'LibraryOptions': {'PathInfos': [{'Path': '/media'}], 'EnableRealtimeMonitor': False, 'EnableInternetProviders': False}}, expected=204)
    wait_for(lambda: b.call('/Items?Recursive=true&IncludeItemTypes=Movie')['Items'], 'B did not scan media', seconds=120)
    cfg = a.call('/Plugins/Federation/Configuration')
    friend = cfg['RemoteServers'][0]['Id']
    lib = next(l['ItemId'] for l in b.call('/Library/VirtualFolders') if l['Name'] == 'QA Native')
    cfg['LibraryMappings'] = [{'LocalLibraryName': 'QA Federated', 'MediaType': 'Movie', 'Enabled': True, 'AutoProvision': True,
                               'RemoteLibrarySources': [{'ServerId': friend, 'RemoteLibraryId': lib}]}]
    cfg['RemoteServers'][0]['WanCapMode'] = 'Off'
    cfg['RemoteServers'][0]['StreamingMode'] = 'Direct'
    a.call('/Plugins/Federation/Configuration', data=cfg)
    a.call('/Plugins/Federation/ProvisionLibraries', data={})

    def imported():
        a.call('/Plugins/Federation/Refresh', data={})
        return a.call('/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams')['Items']
    item = wait_for(imported, 'catalog did not import', seconds=150)[0]
    # The id the direct-link status and relay route use for B, as A knows it.
    a_settings = a.call('/Plugins/Federation/Configuration')['RemoteServers'][0]
    return item['Id'], friend, a_settings


PROFILE = {'MaxStreamingBitrate': 120000000, 'DirectPlayProfiles': [],
           'TranscodingProfiles': [{'Container': 'ts', 'Type': 'Video', 'VideoCodec': 'h264', 'AudioCodec': 'aac', 'Protocol': 'hls',
                                    'Context': 'Streaming', 'MaxAudioChannels': '2', 'MinSegments': 1, 'BreakOnNonKeyFrames': True}],
           'SubtitleProfiles': [{'Format': 'vtt', 'Method': 'External'}], 'CodecProfiles': [], 'ContainerProfiles': []}


def play(a, item_id, where, base, segments=2, bitrate=3000000):
    """Act as a player at `where` using A at `base`: playlist -> variant -> segments.

    Returns dict(master, variant_url, segment_urls, sizes)."""
    body = {'UserId': a.viewer_id, 'MaxStreamingBitrate': bitrate, 'EnableDirectPlay': False, 'EnableDirectStream': False, 'EnableTranscoding': True,
            'AllowVideoStreamCopy': False, 'AllowAudioStreamCopy': False, 'DeviceProfile': PROFILE}
    info = api(where, base, f'/Items/{item_id}/PlaybackInfo?userId={a.viewer_id}', a.viewer, body)
    src = info['MediaSources'][0]
    qa.check(src.get('TranscodingUrl'), 'no TranscodingUrl offered')
    _, master = api(where, base, src['TranscodingUrl'], a.viewer, raw=True)
    master = master.decode()
    variant_rel = next(l for l in master.splitlines() if l and not l.startswith('#'))
    variant_url = urllib.parse.urljoin(base + src['TranscodingUrl'], variant_rel)
    status, _, playlist = http(where, variant_url, '', {})
    if status in (401, 403):  # relay variant may want the viewer token
        status, _, playlist = http(where, variant_url, '', a.viewer)
    qa.check(status == 200, f'variant playlist HTTP {status}')
    segs = [urllib.parse.urljoin(variant_url, l) for l in playlist.decode().splitlines() if l and not l.startswith('#')]
    qa.check(len(segs) >= segments, f'only {len(segs)} segments listed')
    sizes = []
    for u in segs[:segments]:
        st, _, data = http(where, u, '', {})
        sizes.append((st, len(data), data[:1]))
    return {'master': master, 'variant_url': variant_url, 'segment_urls': segs[:segments], 'sizes': sizes, 'item_info': src}


def segments_ok(res):
    return all(st == 200 and n > 10000 and first == b'G' for st, n, first in res['sizes'])


# ---------------------------------------------------------------- topology self-test

def stun_query(container, port=3478, local_port=0):
    code = ('import socket,struct,os\n'
            f's=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.settimeout(3);s.bind(("0.0.0.0",{local_port}))\n'
            'tx=os.urandom(12);s.sendto(struct.pack("!HHI",1,0,0x2112A442)+tx,("%s",%d))\n' % (IP['stun'], port) +
            'd,_=s.recvfrom(2048);p=struct.unpack("!H",d[26:28])[0]^0x2112;ip=bytes(a^b for a,b in zip(d[28:32],struct.pack("!I",0x2112A442)))\n'
            'print(".".join(map(str,ip)),p)')
    out = pm('exec', container, 'python3', '-c', code)
    ip, port_ = out.split()
    return ip, int(port_)


def topology_checks():
    """Prove the NAT plumbing independently of the plugin."""
    for s in 'ab':
        def stun(s=s):
            ip, p = stun_query(f'fdl-viewer-{s}')
            return ip == IP[f'router-{s}-wan'], f'STUN sees {ip}:{p}, expected router-{s} wan {IP["router-" + s + "-wan"]}'
        guard(f'topology: lan-{s} UDP egress is NATed to router-{s} wan address', stun)

        def route(s=s):
            out = pm('exec', f'fdl-viewer-{s}', 'ip', 'route')
            return f"default via {IP['router-' + s + '-lan']}" in out, out.splitlines()[0]
        guard(f'topology: lan-{s} default route is router-{s}', route)

        def inbound(s=s):
            # A WAN host sends UDP straight at the LAN address and at the router's WAN address.
            code = ('import socket\ns=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.settimeout(2)\n'
                    'for t in ("%s","%s"):\n s.sendto(b"x",(t,40000))\n' % (IP['jf-' + s], IP['router-' + s + '-wan']) +
                    'print("sent")')
            # Listener on the LAN side catches anything that gets through.
            listener = subprocess.Popen(['podman', 'exec', f'fdl-viewer-{s}', 'python3', '-c',
                                         'import socket\ns=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.bind(("0.0.0.0",40000));s.settimeout(4)\n'
                                         'try:\n print("GOT",s.recvfrom(10))\nexcept socket.timeout:\n print("NONE")'],
                                        stdout=subprocess.PIPE, text=True)
            time.sleep(1)
            pm('exec', 'fdl-viewer-w', 'python3', '-c', code)
            out = listener.communicate(timeout=15)[0]
            return 'NONE' in out, out.strip()
        guard(f'topology: inbound UDP from the wan never reaches lan-{s}', inbound)


    def mapping(random_fully):
        set_nat(random_fully)
        ports = []
        for _ in range(3):
            # one local socket per attempt: same source port to both STUN ports
            code = ('import socket,struct,os\ns=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.settimeout(3);s.bind(("0.0.0.0",0))\nr=[]\n'
                    'for p in (3478,3479):\n s.sendto(struct.pack("!HHI",1,0,0x2112A442)+os.urandom(12),("%s",p));d,_=s.recvfrom(2048);r.append(struct.unpack("!H",d[26:28])[0]^0x2112)\n' % IP['stun'] +
                    'print(*r)')
            ports.append(tuple(map(int, pm('exec', 'fdl-viewer-a', 'python3', '-c', code).split())))
        return ports
    guard('topology: default NAT is cone-like (same mapped port for both STUN servers)',
          lambda: (lambda m: (all(x == y for x, y in m), f'mapped={m}'))(mapping(False)))
    guard('topology: --random-fully NAT is symmetric-like (mapped port differs per STUN server)',
          lambda: (lambda m: (all(x != y for x, y in m), f'mapped={m}'))(mapping(True)))
    set_nat(False)
    # lan-a -> lan-b is only reachable through the routers: no direct path.
    def isolated():
        p = subprocess.run(['podman', 'exec', 'fdl-viewer-a', 'curl', '-s', '-m', '3', '-o', '/dev/null', f"http://{IP['jf-b']}:8096/"],
                           capture_output=True, text=True)
        return p.returncode != 0, f'curl rc={p.returncode} (non-zero = unreachable, good)'
    guard('topology: lan-a cannot reach lan-b directly', isolated)


# ---------------------------------------------------------------- scenarios

def scenario_checks(a, b, item_id, friend_id, name, expect_connected):
    # 1
    guard(f'[{name}] 1a. server A reports DirectLink Available', lambda: (a.status().get('Available') is True, f"Reason={a.status().get('Reason')!r}"))
    guard(f'[{name}] 1b. server B reports DirectLink Available', lambda: (b.status().get('Available') is True, f"Reason={b.status().get('Reason')!r}"))
    if expect_connected:
        def connected():
            def poll():
                e = a.entry()
                return e if e and e.get('State') == 'connected' else None
            e = wait_for(poll, 'A never reached connected', seconds=60)
            return (e.get('Mbps') or 0) > 0, f"State={e['State']} Mbps={e.get('Mbps')} Remote={e.get('RemoteEndpoint')}"
        guard(f'[{name}] 2. A shows B connected with Mbps > 0 within 60 s', connected)
        try:
            before = (a.entry() or {}).get('BytesIn') or 0
        except RuntimeError:
            before = 0
        holder = {}

        def lan_play():
            r = play(a, item_id, 'a', a.lan)
            holder['r'] = r
            relay = f'/Plugins/Federation/DirectLink/{friend_id}/Plugins/Federation/Peer/Play/'
            urls = r['segment_urls']
            ok_urls = all(relay in urllib.parse.unquote(u) or '/DirectLink/' in u for u in urls)
            return ok_urls and segments_ok(r), f'sizes={[(s, n) for s, n, _ in r["sizes"]]} first={urls[0][:120]}'
        guard(f'[{name}] 3a. in-LAN viewer: segments come through A\'s DirectLink relay with 200 + bytes', lan_play)
        guard(f'[{name}] 3b. A link BytesIn increased', lambda: ((a.entry() or {}).get('BytesIn', 0) > before, f"BytesIn {before} -> {(a.entry() or {}).get('BytesIn')}"))

        def read_ahead():
            served = a.status().get('ReadAheadServed') or 0
            r = play(a, item_id, 'a', a.lan, segments=6)
            after = a.status()
            got = (after.get('ReadAheadServed') or 0) - served
            return segments_ok(r) and got >= 3, f"segments={[(s, n) for s, n, _ in r['sizes']]} served-ahead={got} missed={after.get('ReadAheadMissed')}"
        guard(f'[{name}] 3c. in-LAN viewer playing in order gets later segments from read-ahead', read_ahead)
    else:
        def failed():
            # The first attempt may only find B still starting; wait for the verdict on the NAT itself.
            def poll():
                e = a.entry()
                if not e or e.get('State') not in ('failed', 'fallback'):
                    return None
                text = ' '.join(str(e.get(k) or '') for k in ('Detail', 'LastError', 'NatLocal', 'NatRemote'))
                return e if re.search(r'strict|symmetric', text, re.I) else None
            try:
                e = wait_for(poll, 'A never reported a strict-NAT failure', seconds=150)
            except RuntimeError:
                e = a.entry() or {'State': None}
            text = ' '.join(str(e.get(k) or '') for k in ('Detail', 'LastError', 'NatLocal', 'NatRemote'))
            return bool(re.search(r'strict|symmetric', text, re.I)), f"State={e['State']} Detail={e.get('Detail')!r} NatLocal={e.get('NatLocal')} NatRemote={e.get('NatRemote')}"
        guard(f'[{name}] 5a. symmetric NAT: A ends failed/fallback with a strict-NAT detail', failed)

        def fallback():
            r = play(a, item_id, 'a', a.lan)
            return segments_ok(r) and not any('/DirectLink/' in u for u in r['segment_urls']), f'first={r["segment_urls"][0][:100]}'
        guard(f'[{name}] 5b. in-LAN playback still works via the friend\'s address', fallback)

    def wan_play():
        r = play(a, item_id, 'w', PUBLIC['a'])
        friend = [u for u in r['segment_urls'] if u.startswith(PUBLIC['b']) or f"{IP['router-b-wan']}" in u]
        return segments_ok(r) and not any('/DirectLink/' in u for u in r['segment_urls']), f'friend-direct={len(friend)}/{len(r["segment_urls"])} first={r["segment_urls"][0][:100]}'
    guard(f'[{name}] 4. remote viewer (via router-a wan) plays via the friend\'s URL, no relay', wan_play)


def dial_back_checks(a, b):
    """A friend that turns direct links on (or updates) dials in first; A must dial back
    right away, not after its 30-minute capability recheck."""
    def state(want):
        def poll():
            e = a.entry()
            return e if e and e.get('State') == want else None
        return poll

    stun = {'DirectLinkStunServers': [f"{IP['stun']}:3478", f"{IP['stun']}:3479"]}

    def off():
        b.call('/Plugins/Federation/Settings/DirectLink', data={'EnableDirectLink': False, **stun})
        e = wait_for(state('unsupported'), 'A never saw B switch direct links off', seconds=120)
        return True, f"State={e['State']} Detail={e.get('Detail')!r}"
    guard('[dial-back] 7a. B switches direct links off: A shows unsupported', off)

    def on():
        b.call('/Plugins/Federation/Settings/DirectLink', data={'EnableDirectLink': True, **stun})
        started = time.time()
        e = wait_for(state('connected'), 'A did not dial back after B dialled in', seconds=90)
        return True, f"State={e['State']} after {time.time() - started:.0f} s Remote={e.get('RemoteEndpoint')}"
    guard('[dial-back] 7b. B switches back on and dials A: A dials back within 90 s', on)


def pair(a, b):
    """Pair A and B with two codes and the safety check, through the admin APIs only."""
    for s in (a, b):
        s.call('/Plugins/Federation/Settings/Pairing', data={'EnablePairing': True, 'PortFirst': 47950, 'PortLast': 47959})
    offer = a.call('/Plugins/Federation/Pairing/Offer', data={})
    qa.check(offer.get('success') and offer.get('code'), f'no offer: {offer}')
    answer = b.call('/Plugins/Federation/Pairing/Answer', data={'Code': offer['code']})
    qa.check(answer.get('success'), f'answer failed: {answer}')
    done = a.call('/Plugins/Federation/Pairing/Complete', data={'Code': answer['code']})
    qa.check(done.get('success'), f'complete failed: {done}')
    return offer['code'], answer['code'], done['friendId'], answer['friendId']


def paired_status(server, friend_id):
    return next((f for f in server.call('/Plugins/Federation/Pairing/Status')['Friends'] if f['ServerId'] == friend_id), None)


def paired_checks(a, b):
    """Two homes, no port forwarding, no reachable URL: pair, sync, play, recover, refuse attacks."""
    def unreachable(url):
        try:
            return http('w', url, '/System/Info/Public', timeout=5)[0] == 0
        except RuntimeError:
            return True
    guard('[paired] 0. neither server is reachable from the internet', lambda: (unreachable(PUBLIC['a']) and unreachable(PUBLIC['b']), 'both unreachable'))
    holder = {}

    def paired():
        holder['offer'], holder['answer'], holder['b_on_a'], holder['a_on_b'] = pair(a, b)
        st_a = wait_for(lambda: (lambda f: f if f and f['State'] == 'connected' else None)(paired_status(a, holder['b_on_a'])), 'A never connected', seconds=60)
        st_b = wait_for(lambda: (lambda f: f if f and f['State'] == 'connected' else None)(paired_status(b, holder['a_on_b'])), 'B never connected', seconds=60)
        return st_a['SafetyNumber'] == st_b['SafetyNumber'] and len(st_a['SafetyNumber']) == 35, f"A {st_a['SafetyNumber']} / B {st_b['SafetyNumber']} via {st_a['Detail']}"
    guard('[paired] 1. two codes connect both servers, and both show the same safety number', paired)
    if 'b_on_a' not in holder:
        return

    def reused():
        status, _, body = http('a', LAN['a'], '/Plugins/Federation/Pairing/Complete', {**a.admin, 'Content-Type': 'application/json'}, json.dumps({'Code': holder['answer']}).encode())
        return status == 400, f'HTTP {status} {body[:120]!r}'
    guard('[paired] 2. a used answer is refused (codes are single use)', reused)

    def nothing_before_check():
        f = paired_status(a, holder['b_on_a'])
        cfg = a.call('/Plugins/Federation/Configuration')
        me = next(s for s in cfg['RemoteServers'] if s['Id'] == holder['b_on_a'])
        return (not f['Ready']) and not me['HasApiKey'], f"Ready={f['Ready']} HasApiKey={me['HasApiKey']}"
    guard('[paired] 3. before both admins confirm, no tokens are swapped', nothing_before_check)

    def confirmed():
        a.call(f"/Plugins/Federation/Pairing/{holder['b_on_a']}/Confirm", data={})
        b.call(f"/Plugins/Federation/Pairing/{holder['a_on_b']}/Confirm", data={})
        wait_for(lambda: paired_status(a, holder['b_on_a'])['Ready'] or None, 'A never ready', seconds=60)
        wait_for(lambda: paired_status(b, holder['a_on_b'])['Ready'] or None, 'B never ready', seconds=60)
        return True, 'ready on both'
    guard('[paired] 4. after both confirm, the friendship is set up', confirmed)

    def synced():
        qa.check(b.call(f"/Plugins/Federation/Friends/{holder['a_on_b']}/Sharing", data={'ShareAll': True})['success'], 'sharing failed')
        b.call('/Library/VirtualFolders?name=QA%20Native&collectionType=movies&refreshLibrary=true',
               data={'LibraryOptions': {'PathInfos': [{'Path': '/media'}], 'EnableRealtimeMonitor': False, 'EnableInternetProviders': False}}, expected=204)
        wait_for(lambda: b.call('/Items?Recursive=true&IncludeItemTypes=Movie')['Items'], 'B did not scan media', seconds=120)
        lib = next(l['ItemId'] for l in b.call('/Library/VirtualFolders') if l['Name'] == 'QA Native')
        cfg = a.call('/Plugins/Federation/Configuration')
        cfg['LibraryMappings'] = [{'LocalLibraryName': 'QA Federated', 'MediaType': 'Movie', 'Enabled': True, 'AutoProvision': True,
                                   'RemoteLibrarySources': [{'ServerId': holder['b_on_a'], 'RemoteLibraryId': lib}]}]
        a.call('/Plugins/Federation/Configuration', data=cfg)
        a.call('/Plugins/Federation/ProvisionLibraries', data={})

        def imported():
            a.call('/Plugins/Federation/Refresh', data={})
            return a.call('/Items?Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources,MediaStreams')['Items']
        holder['item'] = wait_for(imported, 'catalog did not import over the link', seconds=180)[0]['Id']
        return True, f"item {holder['item']}"
    guard('[paired] 5. the catalog syncs over the link', synced)
    if 'item' not in holder:
        return

    def played(where, base):
        def run():
            r = play(a, holder['item'], where, base)
            return segments_ok(r), f'sizes={[(s, n) for s, n, _ in r["sizes"]]} first={r["segment_urls"][0][:90]}'
        return run
    guard('[paired] 6a. a home viewer plays through A (video over the link)', played('a', LAN['a']))

    def attacks():
        out = []
        port = paired_status(a, holder['b_on_a'])['Port']
        for payload in ('junk', 'quic'):
            data = os.urandom(1200) if payload == 'quic' else b'hello'
            if payload == 'quic':
                data = bytes([0xC3, 0, 0, 0, 1]) + data[5:]
            res = pm('exec', 'fdl-viewer-a', 'python3', '/fdl/fdl_udp_probe.py', IP['jf-a'], str(port), data.hex(), check=False)
            out.append(res.stdout.strip() or res.stderr.strip()[:80])
        return all(o.endswith('silent') for o in out), f'replies={out}'
    guard('[paired] 7. junk and a QUIC Initial to the pairing port get no reply', attacks)

    def recovers():
        # Cut UDP between the homes long enough for QUIC to give up (idle 45 s),
        # then let it through again: the link must come back without new codes.
        block = 'iptables -I FORWARD 1 -p udp -j DROP'
        for s in 'ab':
            pm('exec', f'fdl-router-{s}', 'sh', '-c', block)
        wait_for(lambda: paired_status(a, holder['b_on_a'])['State'] != 'connected' or None, 'the link never noticed UDP being cut', seconds=120)
        for s in 'ab':
            pm('exec', f'fdl-router-{s}', 'sh', '-c', 'iptables -D FORWARD -p udp -j DROP')
        started = time.time()
        st = wait_for(lambda: (lambda f: f if f['State'] == 'connected' else None)(paired_status(a, holder['b_on_a'])), 'the link did not come back', seconds=180)
        return True, f"back {time.time() - started:.0f} s after UDP returned, via {st['Detail']}"
    guard('[paired] 8. after UDP is cut long enough to drop the link, it comes back without codes', recovers)
    guard('[paired] 9. and playback works again', played('a', LAN['a']))


def security_checks(a, b):
    def no_token():
        out = []
        for hdr in ({}, {'Authorization': 'Bearer not-a-token', 'X-Federation-Token': 'bogus'}):
            st, _, _ = http('w', PUBLIC['b'], '/Plugins/Federation/Peer/DirectLink/Offer', hdr, data={'Fingerprint': 'x'}, method='POST')
            out.append(st)
        return out == [401, 401], f'statuses={out}'
    guard('6a. Offer with no/invalid token -> 401', no_token)

    def junk():
        port = b.status().get('LocalPort')
        if not port:
            return False, f'status has no LocalPort ({port!r})'
        jfdl_forged = (b'JFDL' + b'\x01' + os.urandom(8) + os.urandom(32)).hex()
        payloads = [os.urandom(40).hex(), jfdl_forged, jfdl_forged, (b'JFDL' + b'\x01').hex()]
        # From inside lan-b (no firewall in between), so silence is the responder's own decision.
        out = pm('exec', 'fdl-viewer-b', 'python3', '/fdl/fdl_udp_probe.py', IP['jf-b'], port, *payloads)
        # And from the wan at the router (also expected silent).
        outw = pm('exec', 'fdl-viewer-w', 'python3', '/fdl/fdl_udp_probe.py', IP['router-b-wan'], port, *payloads)
        # The port is the live link's QUIC socket by now, so a QUIC stack may
        # answer junk with a small stateless reset. What must never come back:
        # a punch ack (forged probes accepted) or anything larger than what was
        # sent (amplification).
        def bad(text):
            for line in text.splitlines():
                parts = line.split()
                if len(parts) >= 3 and parts[1] == 'reply':
                    sent = bytes.fromhex(payloads[int(parts[0])])
                    reply = bytes.fromhex(parts[2])
                    if reply.startswith(b'JFDL') or len(reply) > len(sent):
                        return True
            return False
        return not bad(out) and not bad(outw), f'lan:{out.split()[1::2]} wan:{outw.split()[1::2]}'
    guard('6b. junk / forged / replayed JFDL probe datagrams get no reply', junk)


def attack_checks(a, b, item_id, friend_id):
    """Attacks on the released direct link from the internet and from a home device (2026-10-22)."""
    r = play(a, item_id, 'a', a.lan, segments=3)
    segs = r['segment_urls']
    seg = segs[1]
    relay = f'{a.lan}/Plugins/Federation/DirectLink/{friend_id}'
    gateway = '/Plugins/Federation/Peer/Play/'
    token = seg.split(gateway, 1)[1].split('/', 1)[0]

    def from_internet():
        wan_seg = seg.replace(a.lan, PUBLIC['a'])
        st = http('w', wan_seg)[0]
        return st == 403, f'wan viewer on the relay -> {st}'
    guard('8a. a viewer on the internet is refused by the relay (it would use the home upload)', from_internet)

    def spoofed_proxy():
        out = {}
        for h in ({'X-Forwarded-For': '203.0.113.9'}, {'CF-Connecting-IP': '203.0.113.9'}, {'Forwarded': 'for=203.0.113.9'}, {'True-Client-IP': '203.0.113.9'}):
            out[next(iter(h))] = http('a', seg, '', h)[0]
        return all(v == 403 for v in out.values()), str(out)
    guard('8b. a request carrying proxy headers is treated as not-at-home (403)', spoofed_proxy)

    def traversal():
        out = {}
        for tail in ('../../../../System/Info', '%2e%2e/%2e%2e/%2e%2e/%2e%2e/System/Info', '..%2f..%2f..%2f..%2fSystem%2fInfo',
                     '..;/..;/..;/..;/System/Info', '%252e%252e/%252e%252e/System/Info', 'x/..%5c..%5c..%5cSystem/Info'):
            st, _, body = http('a', f'{relay}{gateway}{token}/{tail}')
            out[tail[:18]] = st
            if st == 200 and b'ServerName' in body:
                return False, f'{tail} reached System/Info: {body[:80]!r}'
        return all(v != 200 for v in out.values()), str(out)
    guard('8c. path tricks through the relay never reach the friend\'s other routes', traversal)

    def wrong_token():
        # Segment 2 is held by read-ahead now; a different token must not get it.
        forged = segs[2].replace(token, token[:-1] + ('A' if token[-1] != 'A' else 'B'))
        st = http('a', forged)[0]
        st_admin = http('a', f'{relay}/Plugins/Federation/Configuration')[0]
        return st != 200 and st_admin == 404, f'forged token -> {st}; admin route via relay -> {st_admin}'
    guard('8d. a forged play token gets nothing, even for a segment read-ahead holds', wrong_token)

    def link_routes_only():
        # B's own link gate: what a hostile A could ask over the link (checked from B's side via its status: nothing to see),
        # so instead prove B's link port answers nothing to a stranger's QUIC/HTTP attempts.
        port = b.status().get('LocalPort')
        if not port:
            return True, 'no inbound link port to probe (skipped)'
        probe = (b'\xc0\x00\x00\x00\x01' + os.urandom(1195)).hex()
        out = pm('exec', 'fdl-viewer-w', 'python3', '/fdl/fdl_udp_probe.py', IP['router-b-wan'], port, probe)
        big = [l for l in out.splitlines() if ' reply ' in f' {l} ' and len(bytes.fromhex(l.split()[2])) > 1200]
        return not big, f'stranger QUIC Initial -> {out.split()[1::2]}'
    guard('8e. a stranger\'s QUIC Initial to the link port gets no larger answer (no amplification)', link_routes_only)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--scenario', choices=['cone', 'symmetric', 'paired', 'all'], default='all')
    ap.add_argument('--keep', action='store_true', help='leave containers/networks up for debugging')
    ap.add_argument('--topology-only', action='store_true', help='only prove the NAT plumbing (no Jellyfin)')
    ap.add_argument('--plugin-dir', default=str(qa.REPO / 'bin/Release/net10.0'), help='directory holding the built plugin dll')
    args = ap.parse_args()
    os.environ.setdefault('TMPDIR', str(Path.home() / '.t'))
    base_tmp = Path(os.environ['TMPDIR']) / 'fdl'
    base_tmp.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='run-', dir=base_tmp))
    os.chmod(work, 0o700)
    stage = 'topology'
    try:
        build_topology(forward=args.scenario != 'paired')
        topology_checks()
        if args.topology_only:
            http_ok = None
        else:
            dll = Path(args.plugin_dir)
            qa.check((dll / 'Jellyfin.Plugin.Federation.dll').exists(), f'plugin build not found in {dll}')
            stage = 'media'
            media = make_media(work)
            stage = 'jellyfin startup'
            a, b = Server('a', work, dll), Server('b', work, dll)
            for s in (a, b):
                s.run_container(media)
            for s in (a, b):
                s.start_wizard()
                s.configure_plugin()
            if args.scenario == 'paired':
                stage = 'paired scenario'
                paired_checks(a, b)
            else:
                guard('topology: TCP 8096 DNAT reaches Jellyfin A from the wan', lambda: (http('w', PUBLIC['a'], '/System/Info/Public', timeout=10)[0] == 200, PUBLIC['a']))
                guard('topology: TCP 8096 DNAT reaches Jellyfin B from the wan', lambda: (http('w', PUBLIC['b'], '/System/Info/Public', timeout=10)[0] == 200, PUBLIC['b']))
                stage = 'befriending'
                item_id, friend_id, _ = befriend(a, b)
                log(f'befriended: A imports B\'s catalog (item {item_id})')
            if args.scenario in ('cone', 'all'):
                stage = 'cone scenario'
                scenario_checks(a, b, item_id, friend_id, 'cone', True)
                security_checks(a, b)
                stage = 'attacks'
                attack_checks(a, b, item_id, friend_id)
                stage = 'dial-back'
                dial_back_checks(a, b)
            if args.scenario in ('symmetric', 'all'):
                stage = 'symmetric scenario'
                set_nat(True)
                for s in (a, b):
                    pm('restart', '-t', '5', s.name)
                for s in (a, b):
                    wait_for(lambda s=s: api(s.side, s.lan, '/System/Info/Public'), f'{s.name} did not come back', seconds=120)
                time.sleep(5)
                scenario_checks(a, b, item_id, friend_id, 'symmetric', False)
    except Exception as e:  # noqa: BLE001
        result(f'harness stage "{stage}"', False, f'{type(e).__name__}: {e}'[:400])
    finally:
        if args.keep:
            log(f'--keep: topology left running (containers/networks prefixed fdl-); work dir {work}')
            log('  cleanup: podman rm -f ' + ' '.join(CREATED_CONTAINERS) + '; podman network rm ' + ' '.join(CREATED_NETS))
        else:
            teardown()
            shutil.rmtree(work, ignore_errors=True)
    failed = [n for n, ok in RESULTS if not ok]
    log(f'\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed' + (f'; FAILED: {len(failed)}' if failed else ''))
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
