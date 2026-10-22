# Paired friends: friends without a reachable address (design spec)

Status: design v2, 2026-10-22. Experimental, off by default. Builds on `docs/DIRECT-LINK.md`.
v2 folds in the first security review (see "Review changes" at the end).

## Why

Today, becoming friends needs at least one server with an address the other can reach
over https (a forwarded port, a VPS tunnel or Tailscale). Some people can't do any of
that. The direct link already gets through two home routers without port forwarding, but
it needs the https friend channel to swap keys and addresses first.

Pairing replaces that channel with **two codes** the admins swap themselves, plus a
**safety number** they compare by voice. After that, everything between the two servers
goes over the punched link.

## Goals and non-goals

- Two servers with no reachable address become friends with no account, no Tailscale and
  no port forwarding.
- Nobody in between (including a helping friend) can read, change or fake the traffic.
  Swapping the codes is caught by the safety number, which is **required**.
- A paired friend can do exactly what a normal friend can, nothing more.
- Everything is off by default, on the new **Beta** tab, with plain explanations.
- Not in v1: symmetric NAT on both sides, IPv6, Windows (comes with the Windows
  direct-link work), Plex/Companion. A paired friend sees your home address, and the
  code contains it. The UI says so.

## Threat model

1. **On the path:** sees and changes packets between the homes.
2. **Sees a code, or can change it** (group chat, spoofed SMS), before or after use.
3. **Any internet host** sending packets to the pairing port: scans, floods, spoofed
   sources, replays.
4. **A helping friend** that is curious or hostile.
5. **A paired friend that turns hostile:** limited to normal-friend permissions.
6. **A missed code path** in this plugin that sends a paired friend's request somewhere
   other than the link.

## Keys: fresh for every pairing

Nothing long-term is shared between friends, so friends can't link your pairings
together, and there is no single key file to steal.

- **Per pairing**, each side makes a fresh ECDH P-256 key (memory only, discarded once
  the pairing finishes or expires) and a fresh TLS certificate (ECDSA P-256, stored
  as `pairing/<friendId>.pfx`, mode 0600, never replaced automatically. An unreadable
  file shows "pair again"; it is never silently remade).
- `K = HKDF-SHA256(ikm = ECDH(eA, eB) ‖ secretA ‖ secretB, salt = offerHash,
  info = "jf-fed-pair/1" ‖ fpA ‖ fpB)`. The ECDH private keys exist only during pairing,
  so **someone with both codes can't compute K**, and no later theft recovers it.
- `K` is stored per friend with the same protection at rest as `ApiKey`. Sub-keys:
  `K_punch = Expand(K, "punch")`, `K_proof`, `K_mail_AtoB`, `K_mail_BtoA`,
  `pairTag = Expand(K, "pair-tag")[0..16]`.
- Link traffic is QUIC TLS 1.3 with ECDHE, so it stays forward-secret even if `K` or a
  certificate key is stolen later.

## The codes

Binary, Crockford base32, groups of 5. The tag is keyed with secrets that are in the
codes, so it is a **typo check, not proof of who made the code**. That proof is the
safety number.

Offer (A): `version | kind=1 | createdUnix | expiresUnix | fpA(32) | eA(65) | secretA(32)
| endpoints(1 + ≤3×6) | name(1 + ≤40) | tag(16) = HMAC(secretA, …)`

Answer (B): `version | kind=2 | createdUnix | expiresUnix | offerHash(16) | fpB | eB |
secretB | endpoints | name | tag = HMAC(secretA ‖ secretB, …)`

- `offerHash = SHA-256(offer bytes)[0..16]`. A accepts an answer only for one of its own
  pending offers (at most 3, 15 minutes each **by A's clock**, forgotten on use, expiry
  or cancel). B rejects offers more than 15 minutes old by its clock, allowing 10
  minutes of clock difference.
- **Endpoints in a code are checked:** public IPv4 only (no loopback, link-local,
  multicast, broadcast, CGNAT-shared, documentation or reserved ranges), or a private
  address in the same subnet as one of this server's own interfaces. Port ≥ 1024. A
  hostile code can't make a server send packets to its LAN, loopback or a well-known
  service.
- **Names in a code are untrusted.** The UI shows them as "calls itself …". The admin
  names the friend.
- Unknown versions are refused, with no fallback.

## The safety number (required)

`SHA-256("jf-fed-safety/1" ‖ offerHash ‖ fpA ‖ fpB ‖ eA ‖ eB)`, shown as 6 groups of
5 digits on both admin pages once the link is up. It covers this pairing's fresh
material, so it differs every time.

- Each admin must tick **"I compared this number with my friend by voice or in person,
  and it matches"** before anything is shared. Until both have ticked, the link carries
  only the pairing handshake (below), and each side shares nothing.
- If someone swapped a code, the two admins see different numbers. The page says to
  cancel, and nothing has been shared.
- This answers the review's main finding (H1): a person who can see and replace codes
  can't get past two humans comparing digits on a separate channel.

## The pairing ports

Each pairing has **its own fixed UDP port**, picked at random from a range (default
47950–47999, changeable) when the code is made, carried in the code and kept for that
friend. The QUIC library runs one connection per port, and a steady port per friend
keeps each router's mapping steady. The range is closed entirely while pairing is off.

- While a friend's link is down, its port answers **only punch packets**, and only valid
  ones: exact length, magic, this pairing's `pairTag`, a valid tag, a sender role that
  isn't ours, a time within 120 s and a nonce not seen in 5 minutes. An ack is the same
  size as the probe. Nothing else gets a reply.
- **No QUIC listener stays open.** After a valid probe, the QUIC server side opens a
  listener on that port for 10 s. It refuses any other address before TLS starts, and
  accepts only the pinned certificate and a valid proof. Then it closes.
- ALPN `jf-fed-pair/1`, distinct from direct links. The link's gate profile is fixed
  when the link is made, never chosen per request.
- Rate limits: a per-port budget of 50 packets/s, plus a global budget of 2,000
  packets/s across all pairing ports.

## Pairing flow

1. **A** clicks "Make a pairing code": binds the port, asks both STUN servers for its
   outside address, builds the offer and keeps the NAT mapping alive with a STUN request
   every 20 s while the offer is pending. A doesn't send to anyone else yet.
2. **B** pastes A's code: checks it and the endpoints, binds its port, asks STUN, builds
   the answer and **sends probes to A's endpoints** every 500 ms, then every 2 s, for
   at most the offer's remaining life, stopping on success. These probes are keyed with
   `K`, which B can already compute, so nobody who only saw the codes can make or reflect
   them. **B sends no acks and learns no path until a probe from A arrives.**
3. **A** pastes B's answer: computes `K` and probes B's endpoints. B's router is open for
   A because B has been sending. B's probe arrives at A, A's arrives at B, each acks
   the other, and the first two-way path is used.
4. **Roles:** lower `fp` is the QUIC client. The server opens its 10 s listener on its
   first valid probe. Before **any** stream is accepted, the control-stream proof
   `HMAC(K_proof, role ‖ fpA ‖ fpB ‖ nonceA ‖ nonceB)` must verify in both directions.
5. **Safety number:** both pages show it, and both admins tick "it matches".
6. **Pairing handshake** over the link (`POST /Plugins/Federation/Pairing/Peer/Hello`,
   identity taken from the link, never from the body): each side sends its server id,
   federation id, the friend token it minted for the other (`IssuedApiKey`) and its
   plugin version. Each admin then chooses what to share, as for any new friend. The
   friend is stored as a `RemoteServer` with `Paired = true`.

## One link, both directions

A paired friend has one QUIC connection used both ways. Either side may open request
streams, and each side gates the streams it accepts. At most 32 open streams per friend
and 256 across all paired friends. Per friend, at most 20 new requests per second.
Keepalive 10 s, idle timeout 45 s.

## Routing every friend request over the link

The plugin talks to friends from about ten `HttpClient`s. A paired friend's `Url` is an
address that **can't go anywhere**: `http://[100::<64-bit pairing id>]`, in the IPv6
discard prefix `100::/64` (RFC 6666). No DNS is involved (an ISP can't hijack it), and
packets to it are dropped by design.

- One shared handler, `FederationHttp`, used by every friend-facing client:
  - an outermost guard that **refuses** any request to a `100::/64` host unless it is
    going out over that friend's link (so a missed call site throws instead of sending);
  - `UseProxy = false` (a proxy would never see the connect hook) and
    `AllowAutoRedirect = false` (redirects go through `TokenRedirect`, which refuses
    other hosts);
  - a `ConnectCallback` that opens a stream on the friend's link for its `100::` host,
    and a normal TCP connection otherwise.
- `TokenRedirect.FollowHandler` takes its inner handler from `FederationHttp`.
- **Audit test:** reflects over every `HttpClient` the plugin builds and fails if one that
  can carry `X-Federation-Token` isn't built on `FederationHttp`. A second test sends a
  request for a `100::` host through each of them with no link and asserts it throws
  before any socket opens.
- Untrusted input never accepts a `100::/64` URL. `ConfigValidator.IsValidServerUrl`
  refuses it, and so do friend requests, pool notices and discovery.

## What a paired friend may call (exact table)

The forwarder checks method, path pattern and auth class from one table, and refuses
anything else with 403. There are no wildcards: a new route stays unreachable until it
is added on purpose.

| Method | Path | Auth |
|---|---|---|
| GET | `/Plugins/Federation/Peer/SystemInfo` | header token |
| GET | `/Plugins/Federation/Peer/Libraries`, `/Peer/Items`, `/Peer/Items/{id}`, `/Peer/Users`, `/Peer/UserRules`, `/Peer/Popular`, `/Peer/Favorites`, `/Peer/PlaybackInfo/{id}`, `/Peer/BitrateTest` | header token |
| POST | `/Plugins/Federation/Peer/CatalogChanged`, `/Peer/PlayControl`, `/Peer/PlayLog`, `/Plugins/Federation/PlaybackToken`, `/Plugins/Federation/RegisterUserSession` | header token, JSON body ≤ 64 KB |
| GET/HEAD | `/Plugins/Federation/Peer/Play/{token}/{**path}`, `/Peer/Images/{id}/{type}`, `/Plugins/Federation/DirectStream/{**path}` | item token (as today) |
| POST | `/Plugins/Federation/Friends/{RemoteUserRules,Unfriend}`, `/Plugins/Federation/Pools/{InviteNotice,AcceptNotice,RejectNotice,Notice}` | header token, body ≤ 64 KB |
| POST | `/Plugins/Federation/Pairing/Peer/Hello`, `/Pairing/Peer/Confirmed` | link identity only (before and after the safety check) |

The exact list is fixed in code and in a test. Anything in it found to lack the auth it
claims is a bug the test must catch. Not reachable: the anonymous friend-request routes,
`Stream`, `HandoffPlaylist`, `System/Info/Public`, any Jellyfin core route, admin routes
and the web client.

Before the safety number is confirmed on this side, only the two `Pairing/Peer/*` rows
are open.

### Gate rules

- **Header-token routes:** `X-Federation-Token` must resolve to the link's own friend
  (`ResolveCaller(...).Id == link friend`), checked in the forwarder. A friend that
  learned another friend's token can't use it over its link.
- **Item-token routes:** the token is already bound to an item and its friend. The
  forwarder also checks that the token's friend is the link's friend.
- **Link identity:** the forwarder removes every incoming `X-Federation-*` header, then
  adds `X-Federation-Link: <per-process random secret>` and
  `X-Federation-Link-Friend: <friend id>`. The `Pairing/Peer/*` routes accept a caller
  only when the secret matches (constant-time). Nothing outside the forwarder knows the
  secret.
- **Real peer address:** the forwarder adds `X-Federation-Link-Peer: <peer ip>`, under
  the same secret. The plugin's `RemoteIp()` uses it for viewer and party records.
  `ShouldAllowServerAccess` (Jellyfin's remote-access and IP rules) runs on every
  connect, reconnect and path change, not only the first.
- **Bodies:** only on the POST rows, exactly one `Content-Length` (digits only, ≤ 64 KB),
  no `Transfer-Encoding`, no `Expect`. Exactly that many bytes are forwarded, and the
  stream is then cut. Duplicate or conflicting lengths and trailing bytes are refused
  (no request smuggling into the loopback socket).
- Everything else from DIRECT-LINK.md's head parser still applies: ASCII only, no folded
  headers, no `..`, no encoded slashes, size limits.
- Loopback: the allowed routes are all plugin routes that do their own auth. Core
  Jellyfin features that look at the caller's address (remote access, bitrate limits)
  never see these requests, because no core route is allowed.

## Viewers and paired friends

Players can't reach a paired friend, so this server relays the friend's player-facing
traffic: handoff playlists and segments, direct streams, images and device downloads.

- Home viewers: the existing `DirectLink/{serverId}/...` relay, as today.
- **Viewers outside the home** get relay URLs carrying a **relay ticket**:
  `HMAC(serverRelayKey, serverId ‖ path prefix ‖ expiry ‖ jellyfinUserId)`. It is minted
  only while a signed-in local user starts that play, lasts 6 hours and only covers that
  play's path prefix. Without a valid ticket the relay answers 404, the same as for an
  unknown server. So a leaked URL dies with its ticket and can't be stretched to other
  titles.
- Relayed paths go through the same route table (`Play`, `Images`, `DirectStream`
  only).
- Caps: per paired friend and overall, simultaneous relayed plays and Mbps (default:
  the friend's data limits, and 4 relayed plays overall). The relay uses this
  server's upload, as normal proxy mode does.

## Reconnecting

1. **Last known path:** both sides keep the port and try the last path right away, then
   every 15 s for 10 minutes, then every 2 minutes. Each side re-checks its outside
   address with STUN every 2 minutes. With a fixed port and an unchanged address this
   recovers by itself.
2. **Through a helping friend (optional, consent on all three servers):**
   - Helper M (Beta tab): "Let my friends pass reconnect messages through this server".
     Off by default. Needs an https address.
   - A and B, per friend and per helper: "Use M to find <friend> again". Off by default.
   - A posts `{ box, sealed }` to M's `POST /Plugins/Federation/Peer/PairMailbox` with
     its federation token. `box = HMAC(K_mail_AtoB, "box" ‖ day)` changes daily.
     `sealed = AES-256-GCM(K_mail_AtoB, nonce = 96-bit counter, aad = version ‖ "AtoB" ‖
     box, {endpoints, unixSeconds})`. The counter is persisted per direction, and B
     refuses any counter ≤ the last one it accepted. Each direction has its own key, so a
     message can't be served back to its sender.
   - B polls M for its box every 60 s while the link is down. M keeps at most 4
     messages per box for 30 minutes, 8 boxes per poster, and rate-limits each friend.
   - **What M learns (said in the UI):** which two of its friends use it, when they
     reconnect, and their IP addresses. It can't read the new addresses inside, fake a
     message, or replay one.
   - Endpoints from a mailbox go through the same endpoint checks as codes, and
     `ShouldAllowServerAccess` runs on the new path.
3. **Otherwise:** after 1 hour without a link and with no helper, the Friends tab says
   "Can't reach <friend>. Their or your home address probably changed. Make new pairing
   codes to reconnect."

### Re-pairing

- **Same friend, new address:** the admin picks "Re-pair <friend>" and both swap new
  codes. The new pairing is linked to the old friend only when the friend proves it
  holds the old `K` (an extra proof on the control stream), so the friend's settings
  carry over. Both friend tokens are rotated.
- **Without that proof** (a lost key, or someone else), it's a **new** friend with
  nothing shared, and the safety number check is required again. A code can never take
  over an existing friend's sharing.

## The Beta tab

A new tab, **Beta**, holds the new and sensitive switches. Each one says what it does,
what it shares and when to leave it off:

- **Direct links for video** (moved from the Friends tab; same `DirectLinkOptIn`).
- **Pairing codes for friends without a reachable address** (`PairingOptIn`, off): make
  a code, enter a code, pending codes, the safety number check, and the pairing port.
- **Help friends reconnect** (`PairMailboxHelperOptIn`, off), and per paired friend which
  helpers to use.
- **Other beta features:** adaptive movie playback and picking who converts by hardware,
  moved here unchanged.

The Friends tab keeps one line: "Direct links and pairing codes are on the Beta tab."

## Config

- `PairingOptIn` (false), `PairingPortRange` ("47950-47999"), `PairMailboxHelperOptIn` (false).
- `RemoteServer`: `Paired`, `PairingId` (64-bit, for its `100::` URL), `PairPeerFingerprint`,
  `PairKey` (protected), `PairLocalPort`, `PairLastEndpoints`, `PairHelperIds`, `PairSafetyConfirmed`,
  `PairMailCounterOut`, `PairMailCounterIn`, `PairedAtUtc`.
- Pending offers and answers live in memory only.

## Status

`GET /Plugins/Federation/Pairing/Status` (admin): pending codes (expiry only, no secrets),
paired friends with `waiting-for-check|connected|reconnecting|lost`, last path, time since
the last link, the safety number and the helpers in use.

## Testing

- **Unit:** code encode/decode and every rejection (including endpoint filtering);
  pair key on both sides, and not derivable from the codes; safety number; punch packets
  (MAC, role, time, replay, pair tag); listener open/close timing; the route table
  (every allowed row, every refused path, header-token-for-another-friend, item token for
  another friend, `X-Federation-*` stripping, body rules and smuggling cases); relay
  tickets (expiry, other prefix, other user, unknown server = same 404); mailbox (seal,
  open, wrong direction, replayed counter, day rotation); the two `HttpClient` audit tests.
- **Harness** (`federation_directlink_smoke.py --scenario paired`): two homes behind cone
  NAT with **no port forwarding** and no reachable URL, plus a third reachable server as
  a helper:
  1. pair with two codes and the safety check through the admin API;
  2. catalog sync, a home play, a remote play (relayed, with a ticket), an image, a device
     download;
  3. router restart with the same address: back without codes;
  4. address change: back through the helper when allowed, and "make new codes" without;
  5. attacks: unpaired packets get no reply, QUIC from a random host gets nothing, replayed
     probes, an expired code, a reused offer, a swapped answer (different safety numbers,
     nothing shared), a paired friend calling a core API, another friend's token, a
     smuggled body, a relay URL without a ticket or with another title.
- **Reviews:** a Sonnet security review of the code, and an adversarial agent that
  attacks the running harness.

## Review changes (v1 → v2)

- H1 code swap → required safety number from fresh per-pairing material; untrusted names.
- H2 route list → an exact table with auth classes; anonymous routes removed; a pairing
  handshake that takes identity from the link.
- H3 open QUIC port → listener only 10 s after a valid probe; own ALPN.
- H4 loopback → `X-Federation-*` stripped and set by the forwarder under a secret; real
  peer IP passed; `ShouldAllowServerAccess` on every connect and path change.
- H5 bodies → a single Content-Length ≤ 64 KB, exact forwarding, POST only on listed rows.
- H6 relay → relay tickets, uniform 404, caps.
- M1 `.invalid` → the unroutable `100::/64` literal, a guard handler, no proxy, no
  auto-redirect, `TokenRedirect` fixed, two audit tests.
- M2 untrusted `100::` URLs refused. M3 re-pair needs proof of the old key, and tokens
  rotate. M4 pair tag = one HMAC per packet, global and LRU limits. M5 no acks before
  `K`, endpoint filtering, probing capped. M6 mailbox per-direction keys, persisted
  counters, daily box ids, honest disclosure. M7 per-pairing ephemeral keys and TLS
  forward secrecy. M8 stream and request limits.
