#!/usr/bin/env python3
"""A scripted SyncPlay player for tests: speaks Jellyfin's websocket and SyncPlay
HTTP API the way jellyfin-web does (reports Ready after "loading", follows
Unpause/Pause/Seek/Stop, answers keep-alives), and tracks its own position.
Standard library only; plain ws:// to a local test container."""
import base64
import datetime
import json
import os
import socket
import struct
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

TICKS = 10_000_000


def utcnow():
    return datetime.datetime.now(datetime.timezone.utc)


def iso(dt):
    return dt.strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3] + 'Z'


def parse_time(value):
    """Jellyfin's UTC timestamps (up to 7 fractional digits, optional Z/offset)."""
    if not value:
        return utcnow()
    text = value.strip().replace('Z', '+00:00')
    main, offset = text, ''
    for sign in ('+', '-'):
        at = text.rfind(sign)
        if at > 10:
            main, offset = text[:at], text[at:]
            break
    if '.' in main:
        head, frac = main.split('.', 1)
        main = head + '.' + frac[:6].ljust(6, '0')
    dt = datetime.datetime.fromisoformat(main + offset)
    return dt if dt.tzinfo else dt.replace(tzinfo=datetime.timezone.utc)


class WebSocket:
    """Minimal RFC 6455 client: text frames, ping/pong, close."""

    def __init__(self, url):
        parts = urllib.parse.urlsplit(url)
        self.sock = socket.create_connection((parts.hostname, parts.port or 80), timeout=30)
        key = base64.b64encode(os.urandom(16)).decode()
        path = parts.path + ('?' + parts.query if parts.query else '')
        self.sock.sendall((f'GET {path} HTTP/1.1\r\nHost: {parts.netloc}\r\nUpgrade: websocket\r\n'
                           f'Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n').encode())
        head = b''
        while b'\r\n\r\n' not in head:
            chunk = self.sock.recv(1)
            if not chunk:
                raise RuntimeError('websocket handshake closed')
            head += chunk
        if b' 101 ' not in head.split(b'\r\n', 1)[0]:
            raise RuntimeError('websocket handshake refused: ' + head.split(b'\r\n', 1)[0].decode(errors='replace'))
        self.sock.settimeout(None)
        self.lock = threading.Lock()

    def _read(self, n):
        data = b''
        while len(data) < n:
            chunk = self.sock.recv(n - len(data))
            if not chunk:
                raise EOFError('websocket closed')
            data += chunk
        return data

    def _frame(self, opcode, payload):
        mask = os.urandom(4)
        header = bytes([0x80 | opcode])
        n = len(payload)
        if n < 126:
            header += bytes([0x80 | n])
        elif n < 65536:
            header += bytes([0x80 | 126]) + struct.pack('>H', n)
        else:
            header += bytes([0x80 | 127]) + struct.pack('>Q', n)
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        with self.lock:
            self.sock.sendall(header + mask + masked)

    def send(self, text):
        self._frame(0x1, text.encode())

    def recv(self):
        message = b''
        while True:
            b1, b2 = self._read(2)
            fin, opcode, n = b1 & 0x80, b1 & 0x0F, b2 & 0x7F
            if n == 126:
                n = struct.unpack('>H', self._read(2))[0]
            elif n == 127:
                n = struct.unpack('>Q', self._read(8))[0]
            mask = self._read(4) if b2 & 0x80 else None
            payload = self._read(n)
            if mask:
                payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
            if opcode == 0x8:
                raise EOFError('websocket closed by server')
            if opcode == 0x9:
                self._frame(0xA, payload)
                continue
            if opcode in (0x0, 0x1, 0x2):
                message += payload
                if fin:
                    return message.decode()

    def close(self):
        try:
            self._frame(0x8, b'')
        except OSError:
            pass
        self.sock.close()


class Player:
    """One viewer's app in a SyncPlay group."""

    def __init__(self, base, token, client, device_id, name, load_seconds=0.4):
        self.base = base
        self.name = name
        self.load_seconds = load_seconds
        self.auth = {'Authorization': f'MediaBrowser Client="{client}", Device="{name}", DeviceId="{device_id}", Version="1", Token="{token}"'}
        self.lock = threading.Lock()
        self.events = []
        self.group_id = None
        self.state = None
        self.playlist = []
        self.index = 0
        self.playing = False
        self.position = 0
        self.at = utcnow()
        self.keepalive = None
        self.closed = False
        ws_url = base.replace('http://', 'ws://') + f'/socket?ApiKey={token}&deviceId={urllib.parse.quote(device_id)}'
        self.ws = WebSocket(ws_url)
        threading.Thread(target=self._loop, daemon=True).start()

    # -- HTTP --------------------------------------------------------------
    def request(self, path, data=None, method=None, expected=(200, 204)):
        headers = dict(self.auth)
        body = None
        if data is not None:
            headers['Content-Type'] = 'application/json'
            body = json.dumps(data).encode()
        req = urllib.request.Request(self.base + path, data=body, headers=headers, method=method or ('POST' if data is not None else 'GET'))
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                status, raw = r.status, r.read()
        except urllib.error.HTTPError as e:
            status, raw = e.code, e.read()
        if status not in expected:
            raise RuntimeError(f'{self.name}: {path} HTTP {status}: {raw[:200]!r}')
        return json.loads(raw) if raw else None

    def groups(self):
        return self.request('/SyncPlay/List')

    def join(self, group_id):
        return self.request('/SyncPlay/Join', {'GroupId': group_id})

    def leave(self):
        return self.request('/SyncPlay/Leave', {})

    def new_group(self, name):
        return self.request('/SyncPlay/New', {'GroupName': name})

    def set_queue(self, item_ids, index=0, start=0):
        return self.request('/SyncPlay/SetNewQueue', {'PlayingQueue': item_ids, 'PlayingItemPosition': index, 'StartPositionTicks': start})

    def pause(self):
        return self.request('/SyncPlay/Pause', {})

    def unpause(self):
        return self.request('/SyncPlay/Unpause', {})

    def seek(self, ticks):
        return self.request('/SyncPlay/Seek', {'PositionTicks': ticks})

    def buffering(self):
        with self.lock:
            pid, pos = self._playlist_item(), self.position_now()
        return self.request('/SyncPlay/Buffering', {'When': iso(utcnow()), 'PositionTicks': pos, 'IsPlaying': False, 'PlaylistItemId': pid})

    def ready(self):
        with self.lock:
            pid, pos = self._playlist_item(), self.position_now()
        return self.request('/SyncPlay/Ready', {'When': iso(utcnow()), 'PositionTicks': pos, 'IsPlaying': False, 'PlaylistItemId': pid})

    # -- state -------------------------------------------------------------
    def position_now(self):
        if self.playing:
            return self.position + max(0, int((utcnow() - self.at).total_seconds() * TICKS))
        return self.position

    def snapshot(self):
        with self.lock:
            item = self.playlist[self.index]['ItemId'] if 0 <= self.index < len(self.playlist) else None
            return {'group': self.group_id, 'state': self.state, 'playing': self.playing,
                    'position': self.position_now() / TICKS, 'item': (item or '').replace('-', '').lower() or None}

    def _playlist_item(self):
        if 0 <= self.index < len(self.playlist):
            return self.playlist[self.index]['PlaylistItemId']
        return '00000000-0000-0000-0000-000000000000'

    def _later(self, seconds, fn):
        def run():
            time.sleep(seconds)
            if not self.closed:
                try:
                    fn()
                except Exception as e:  # noqa: BLE001 - recorded for the test to see
                    self.events.append(('error', str(e)))
        threading.Thread(target=run, daemon=True).start()

    def _loop(self):
        try:
            while not self.closed:
                message = json.loads(self.ws.recv())
                kind, data = message.get('MessageType'), message.get('Data')
                if kind == 'ForceKeepAlive' and self.keepalive is None:
                    self.keepalive = max(5, int(data or 60) // 2)
                    threading.Thread(target=self._keepalive, daemon=True).start()
                elif kind == 'SyncPlayGroupUpdate':
                    self._group_update(data)
                elif kind == 'SyncPlayCommand':
                    self._command(data)
        except (EOFError, OSError):
            pass

    def _keepalive(self):
        while not self.closed:
            try:
                self.ws.send(json.dumps({'MessageType': 'KeepAlive'}))
            except OSError:
                return
            time.sleep(self.keepalive)

    def _group_update(self, update):
        kind, data = update.get('Type'), update.get('Data')
        with self.lock:
            self.events.append(('update', kind))
            if kind == 'GroupJoined':
                self.group_id = update.get('GroupId')
            elif kind in ('GroupLeft', 'NotInGroup'):
                self.group_id, self.playing = None, False
            elif kind == 'StateUpdate':
                self.state = data.get('State')
            elif kind == 'PlayQueue':
                self.playlist = data.get('Playlist') or []
                self.index = data.get('PlayingItemIndex', 0)
                self.position = data.get('StartPositionTicks', 0)
                self.at = utcnow()
                self.playing = False
                if data.get('Reason') in ('NewPlaylist', 'SetCurrentItem', 'NextItem', 'PreviousItem') and self.playlist:
                    # "Load" the title, then report ready like a real player.
                    self._later(self.load_seconds, self.ready)

    def _command(self, command):
        kind = command.get('Command')
        position = command.get('PositionTicks') or 0
        with self.lock:
            self.events.append(('command', kind))
            if kind == 'Unpause':
                self.position, self.at, self.playing = position, parse_time(command.get('When')), True
            elif kind == 'Pause':
                self.position, self.at, self.playing = position, utcnow(), False
            elif kind == 'Seek':
                self.position, self.at, self.playing = position, utcnow(), False
                self._later(self.load_seconds, self.ready)
            elif kind == 'Stop':
                self.playing, self.position = False, 0

    def close(self):
        self.closed = True
        self.ws.close()
