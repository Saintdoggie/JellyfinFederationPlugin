# Direct link: friend-to-friend UDP connection (design spec)

Status: built on branch `test-branch-udp-connection` (0.0.195 test build). Linux first, Windows later.

> Implementation notes (what changed from the first draft below):
> - **QUIC runs in a private AssemblyLoadContext.** Jellyfin's web host asks
>   `QuicListener.IsSupported` before plugins load, and the runtime caches that "no"
>   for the process, so preloading `libmsquic` from the plugin is too late. The QUIC
>   code lives in `QuicTransport/` (embedded in the plugin dll) and
>   `QuicTransportHost` loads it with its own copy of `System.Net.Quic`, whose
>   native lookup is answered with the bundled `native/<rid>/libmsquic.so.2`. Only
>   BCL types cross (connections are opaque handles, streams are `Stream`s).
> - **Keys:** both 32-byte secrets form one session key,
>   `HMAC-SHA256(initiatorSecret || responderSecret, "jf-fed-dl/1|" + sessionId)`.
>   Punch packets use the sub-key `HMAC(sessionKey, "punch")` and carry the sender's
>   role, so reflected packets fail. The connect proofs are
>   `HMAC(HMAC(sessionKey, "proof"), role | sessionId | initiatorFp | responderFp)`.
>   The certificate pins are checked separately.
> - **No clock sync:** the responder starts punching as soon as it answers, and the
>   initiator when it gets the answer. Both send for up to 8 s.
>   `PunchStartUnixMs` was dropped.
> - **Responder-side https check:** not done. The initiator only sends its details
>   over an https or Tailscale channel (or in test mode), and the answer returns on
>   that same connection.
> - **Results (two-home NAT harness, `scripts/federation_directlink_smoke.py`):** the
>   cone NAT case connects in about 1 s at 1.2 to 1.4 Gbps. Home-network playback
>   comes over the link. Symmetric NAT on both sides reports "strict routers" and
>   falls back. Remote viewers keep going straight to the friend.

## Why

A friend whose server is only reachable through a forwarding VPS (e.g. friendflix:
home -> Hetzner Hillsboro -> everyone) streams at ~25 Mbps on one connection even
though both homes have 500 Mbps. The direct link opens an encrypted UDP (QUIC)
connection straight between the two Jellyfin servers, punched through both home
routers like Tailscale does, with no port forwarding. Only the small connection
details go over the existing (slow) route.

## What goes over the link

The link is a transport, not a VPN. It carries only these friend requests, which
already authenticate by token exactly as today:

- `GET/HEAD /Plugins/Federation/Peer/Play/{token}/{**path}` (handoff gateway: HLS
  playlists, segments, direct streams)
- `GET /Plugins/Federation/Peer/BitrateTest`
- the server-to-server download routes the receiver uses for library downloads
  (whatever `FederationDownloadService` / `DownloadTransfer` fetch)

Everything else (catalog sync, tokens, popular, etc.) keeps using the friend's
normal URL. Nothing on either LAN is reachable through the link.

## Who uses it

- **Viewers on the receiver's home network** (Jellyfin's `INetworkManager`
  local-network check on the player's IP): the receiver serves the friend's
  playlists and segments itself, fetched over the link. The player only talks to
  its own home server, so it rides the home download speed and never the home
  upload, and https pages have no mixed-content problem.
- **Viewers outside the home**: unchanged (handoff straight to the friend's URL), so
  the receiver's upload is never used for friend video.
- **Server-side fetches** (library downloads to the receiver's disk, bitrate test,
  any FetchedByReceiver handoff fetch): over the link when it is up.
- Link down or not possible: everything falls back to today's route, including
  mid-stream (the relay route redirects to the friend URL).

## Roles

- **Initiator** = the server that wants the link (the receiver of the friend's
  media). It is the QUIC client and opens one bidirectional stream per HTTP
  request.
- **Responder** = the friend that owns the media. It is the QUIC server and
  forwards each stream to its own Jellyfin over loopback after validating it.
- Each pair of friends can have a link in each direction; they are independent.

## Identity and keys

- Each server has a long-term ECDSA P-256 key and self-signed certificate (CN =
  federation id), created on first use and stored in the plugin data folder
  (`directlink-identity.pfx`, file mode 0600 on Linux). The SHA-256 of the cert
  (DER) is its **fingerprint**.
- Fingerprints are exchanged only inside the signaling request/response, which
  travels over the existing token-authenticated channel. **Signaling requires an
  https effective URL or a Tailscale address** (100.64.0.0/10, *.ts.net); a friend on
  plain http never gets a direct link (a MITM there could swap fingerprints).
- QUIC TLS 1.3 with **mutual authentication**: the responder requires a client
  certificate; each side accepts only the exact fingerprint learned in this
  session's signaling. Any other certificate closes the connection.
- **Session secret**: signaling also carries a random 32-byte secret from each side.
  After QUIC connects, the initiator opens a control stream and sends
  `HMAC-SHA256(key = responderSecret, "jf-fed-dl/1|" + sessionId + "|" + initiatorFp + "|" + responderFp)`.
  The responder answers with the HMAC under initiatorSecret. Wrong or missing
  within 5 s: close. That's defence in depth on top of the pinned certificates.
- ALPN `jf-fed-dl/1`.

## Signaling (over the existing friend channel)

New endpoint on the responder: `POST /Plugins/Federation/Peer/DirectLink/Offer`,
authenticated with `FederationTokenAuth.ResolveCaller` like every Peer route.

Request (initiator -> responder):
```json
{ "Version": 1, "SessionId": "<32 hex>", "Fingerprint": "<hex>", "Secret": "<base64 32B>",
  "Candidates": [ { "Kind": "srflx|host", "Address": "203.0.113.5", "Port": 41641 } ],
  "NatType": "cone|symmetric|unknown" }
```
Response (responder -> initiator), or 403 if the responder has the link off for
this friend, 409/503 if it can't (no QUIC, busy):
```json
{ "Version": 1, "Fingerprint": "<hex>", "Secret": "<base64 32B>",
  "Candidates": [ ... ], "NatType": "...", "PunchStartUnixMs": 1760000000000 }
```
`PunchStartUnixMs` = responder time + 300 ms; both sides start punching then, so
neither side's router drops the first packets as unsolicited. The initiator
adjusts for the request's round-trip time.

Capability: the responder advertises `DirectLink: 1` in `Peer/SystemInfo`
(`DirectLinkVersion`). The initiator only offers to friends that advertise it.

## Candidates and NAT

- Each session, each side binds a fresh UDP socket on a random port on 0.0.0.0
  (one port per link; no fixed port in v1).
- **STUN** (RFC 5389 Binding Request, XOR-MAPPED-ADDRESS) from that socket to two
  servers from `DirectLinkStunServers` (default `stun.cloudflare.com:3478`,
  `stun.l.google.com:19302`). Same mapping from both -> `cone`; different ->
  `symmetric` (still try: it works if the other side is cone, via port
  prediction of +-0..4 around the observed port).
- Host candidates: the socket's private IPv4 addresses (works for two friends on
  one LAN or one tailnet). IPv4 only for v1.
- Symmetric on both sides: skip punching and report "strict NAT on both sides".

## Punching

- Packet format (UDP payload): `"JFDL"` (4 bytes) + type (1 byte: 1 = probe,
  2 = ack) + sessionId (16 bytes) + 32-byte HMAC-SHA256 tag over the previous bytes,
  keyed with the **peer's** secret for probes we send. Anything else is ignored
  (never answered), so the socket can't be used for reflection or scanning.
- From `PunchStartUnixMs`: both sides send a probe to every candidate every 50 ms
  for up to 6 s. Received valid probe -> reply ack to that source endpoint and mark
  it working; received valid ack -> mark working. The first working endpoint is
  the path.
- Hand-over to QUIC (the QUIC stack owns its own socket, so the punch socket is
  closed and the same local port is reused; the NAT mapping is keyed on the local
  port and survives):
  - responder: on a working path, keep acking for 300 ms, close the punch socket,
    then start `QuicListener` on the same local port (accepts only the pinned
    client certificate, only for this session, 10 s to connect);
  - initiator: on a working path, wait 400 ms, close the socket, then
    `QuicConnection.ConnectAsync` with `LocalEndPoint` = same port and
    `RemoteEndPoint` = the working path. QUIC retransmits its Initial, which covers
    small timing races.
- One listener per session, on that session's port: it accepts exactly one
  connection with the pinned certificate, then is disposed (the accepted
  connection lives on). Never share listeners between sessions.

## Connection lifecycle

- `KeepAliveInterval` 15 s, `IdleTimeout` 60 s, `MaxInboundBidirectionalStreams` 128
  on the responder.
- The initiator establishes the link at startup (after first sync) for every friend
  that advertises it and has it on, and keeps it up. If it drops: reconnect with
  backoff 30 s, 2 m, 10 m, 30 m, 1 h (reset on success). The new session gets new
  secrets and candidates each time.
- After connecting, measure throughput with `Peer/BitrateTest` (8 MB) over the
  link, and record it.
- Responder caps: at most 1 active link per friend and 32 total, and at most 10
  offers per friend per 10 minutes (429 otherwise).

## HTTP over the link

- **Initiator:** an `HttpClient` built on `SocketsHttpHandler` with a
  `ConnectCallback` that opens a new bidirectional `QuicStream` on the link and
  returns it as the connection stream. A delegating handler sets
  `Connection: close` on every request, so each request uses its own stream. HTTP/1.1
  framing is done by .NET itself.
- **Responder:** for each accepted stream:
  1. read the request head (max 16 KB, 10 s), parse the request line and headers;
  2. allow only GET/HEAD to the allowlisted paths above (after normalising: no
     `..`, no encoded slashes, no absolute-form, no `Transfer-Encoding` on
     GET/HEAD); otherwise send `403` and close;
  3. drop hop-by-hop headers, force `Connection: close`, set `Host` to the local
     Jellyfin, and add `X-Federation-Direct-Link: 1`;
  4. connect TCP to the local Jellyfin (`http://127.0.0.1:{port}{BaseUrl}`, the
     same loopback base the plugin already uses), write the head plus any bytes
     already read, then pipe both directions until either side ends. Cancel on
     link close.
- Peer routes see the request as coming from loopback. They must not treat that
  as more trusted: token checks stay the only authorization (audit for IP-based
  trust in the allowed routes).

## Receiver relay for home viewers

- Route on the receiver:
  `GET/HEAD /Plugins/Federation/DirectLink/{serverId}/Plugins/Federation/Peer/Play/{token}/{**path}`.
  This shape lets `HandoffPlaylistLinks.Rewrite` work unchanged: its `peerBase`
  becomes `{PathBase}/Plugins/Federation/DirectLink/{serverId}`.
- It only answers callers in Jellyfin's local network (403 otherwise; behind a
  known proxy, use the forwarded address Jellyfin resolved).
- It forwards over the link client with `ResponseHeadersRead`, streams the body
  back, and copies status, Content-Type, Content-Length, Content-Range, Accept-Ranges
  and Cache-Control. Range requests pass through. A client abort cancels.
- Link down: `307` to the same path on the friend's effective URL (today's
  handoff), and on https pages only when the friend URL is https.
- `FederationRemoteTranscodeFilter`: when the player is in-network and the link to
  that friend is up, build the handoff with the relay base as the player-facing
  base (playlists rewritten to it, direct-stream redirects to it), while the
  server-side fetch of the master/variant still goes to the friend over the link
  client.

## Native library

- `native/linux-x64/libmsquic.so.2` and `native/linux-arm64/libmsquic.so.2` ship in
  the plugin zip (same relative paths).
- `MsQuicLoader.EnsureLoaded()` runs once at plugin start, **before anything touches
  `System.Net.Quic`** (touching `IsSupported` first caches "unsupported" for the
  process). On Linux it calls `NativeLibrary.TryLoad(<plugin dir>/native/<rid>/libmsquic.so.2)`
  for the process architecture, then reads `QuicConnection.IsSupported`. Windows
  does no preload. Other OSes: unsupported.
- If unsupported, the feature reports "not available on this server: <reason>"
  and everything else works as before.
- Verified 2026-10-21: works in the official images b603f4b6dede (12.2) and
  3305a4c72c38 (12.0), with a pinned-certificate QUIC transfer at ~6 Gbps on loopback.

## Config (PluginConfiguration)

- `DirectLinkOptIn` (bool, default **false**). A direct link shows each friend's
  server this home's internet address, so it's opt-in. It replaced 0.0.195's
  `EnableDirectLink` (default true); a saved `EnableDirectLink` is ignored, so
  upgraded servers start off. `Settings/DirectLink` still takes and returns
  `EnableDirectLink`. While off, the server runs no STUN, sends no address and
  answers no offers.
- `DirectLinkStunServers` (string[], defaults above)
- per friend `DirectLinkMode`: `Auto` (default) | `Off`

## Status and UI

- Admin endpoint `GET /Plugins/Federation/DirectLink/Status` (requires
  elevation): per friend `{ ServerId, Name, State: off|unsupported|connecting|connected|failed|fallback,
  Detail, RemoteEndpoint, NatLocal, NatRemote, Mbps, ConnectedSinceUtc, LastError,
  NextRetryUtc, BytesIn, BytesOut, ActiveStreams }`, plus a top-level
  `{ Available, Reason, LocalPort, PublicEndpoint }`.
- Friends tab: per-friend line "Direct link: Connected · 240 Mbps" /
  "Connecting…" / "Couldn't connect: strict NAT on both sides · retry in 10 min" /
  "Friend needs 0.0.195" / "Off", plus a per-friend Auto/Off control and the global
  toggle in settings.
- Playback Info row: "Route: direct link" vs "Route: friend's address".
- Info logs on state changes only (no per-request logs).

## Testing

- Unit tests: STUN encode/decode (incl. XOR-MAPPED-ADDRESS, IPv4), probe packet
  MAC and rejection, offer validation, request-head parser and allowlist (fuzz-ish
  table of bad inputs), relay header copying, backoff schedule, and the
  HTTP-over-QUIC round trip in-process over loopback with mutual pinning (skip
  with a clear message when libmsquic can't load).
- `scripts/federation_directlink_smoke.py`: two Jellyfin containers, each behind its
  own NAT router container (rootless podman, alpine + iptables MASQUERADE, inbound
  new connections dropped), a STUN responder on the "internet" network, and TCP
  8096 forwarded only through the routers, standing in for the VPS. Checks:
  1. both report `Available`;
  2. the link reaches `connected`;
  3. a handed-off play from an in-network viewer is served over the link (relay
     route 200, bytes on the link);
  4. a symmetric NAT on both sides (`--random-fully`) ends as `failed` and playback
     still works over the fallback;
  5. a wrong fingerprint or replayed probe is refused.
