# Notes — direct remote transcode + access hiding (0.0.187 work)

Started 2026-10-19 on branch `feature/direct-play-and-access` from `origin/master` (0.0.186).
Task list: `.todo`. This file holds the reasoning and evidence.

## What the user asked for (in their words, summarised)

1. Federated playback must go **client → owner server directly**, owner transcodes
   (it has the GPU and the file), while the user stays logged in to their own server
   and all state (progress, sessions, history) lives there. The receiving server's
   network must not carry the video.
2. Revoking library access stops playback but titles stay visible. They must disappear.
3. Blocking a specific user on a friend's server from specific media does not work.
   Admin should see the friend's users and selectively block content per user.

User confirmed via browser devtools that federated playback is going through their
own server (transcoding there).

## Root causes found

### 1. Playback path
- `FederationMediaSourceProvider` (Direct mode) gives the client the owner's
  `DirectStream` URL for direct play. When the client can't direct play,
  Jellyfin on the receiver builds a `TranscodingUrl` (`/videos/{id}/master.m3u8`)
  on **itself**; its ffmpeg reads the owner's file over the network and the
  receiver re-uploads segments. That is the slow path the user sees.
- The WAN-cap path asks the owner for one fixed H.264/AAC progressive MP4
  (`capMbps`), which ignores the device profile, seeks badly and still goes
  through the receiver when that MP4 itself can't be direct played.
- `FederationDirectStreamRedirectFilter` already 302s static direct-play
  requests to the owner. Nothing does the same for transcodes.

### 2/3. Visibility
- `RemoteAccessControlService.IsAllowed` is only consulted at playback
  (`FederationMediaSourceProvider`, `FederationStreamHandler`,
  `AdaptivePlaybackController`). No browse/list/search filtering exists, so
  blocked titles stay visible.
- Rules are pushed once (`SetRemoteUserAccessRuleAsync` → `Friends/RemoteUserRules`).
  A failed push is never retried; the receiver keeps its old copy indefinitely.
- Live config (`fedora-jellyfin-state/plugins/configurations/Jellyfin.Plugin.Federation.xml`)
  has **zero** `RemoteUserAccessRules` and zero `FriendUserAccessRules` for both
  friends (Inspired, netflix++), and no log lines for rule saves. So the user's
  attempt never persisted; need to reproduce the save path in the fixture.
- Owner sharing change (`UpdateFriendSharingAsync`) only saves config. The friend
  is not told; its next sync gets a filtered empty list for that library.

## Design decisions

### Remote transcode handoff
- **Receiver still negotiates.** Its StreamBuilder runs against the owner's media
  streams (indexes come from the owner's PlaybackInfo, so audio/subtitle indexes
  line up). The resulting native transcode URL is then executed **on the owner**
  through a token-gated gateway: `Plugins/Federation/Peer/Play/{token}/{nativePath}`.
- **Token reuse:** the item-scoped Playback token the receiver already mints for
  Direct sources. It is friend-bound, remote-user-bound and re-checked against
  current access on every request, so revoke/unfriend/disable apply mid-stream.
  The token response gains a `remoteTranscode` flag, which doubles as the
  capability probe (old owners don't send it, so the receiver falls back).
- **Master playlist is fetched by the receiver** (tiny text, one round trip),
  rewritten to absolute owner URLs and returned. That keeps a real fallback: if
  the owner refuses or is unreachable, the receiver serves its own transcode as
  before. Variant playlists and segments then go client → owner only.
- Progressive transcodes (non-HLS) are 302-redirected (bulk bytes, no fallback
  after redirect).
- Subtitles/attachments are fetched from the owner by the receiver: they're small,
  and extracting them on the receiver would read the whole remote file.
- Owner relays to its own native endpoints over loopback with the internal relay
  key in the **header** (so Jellyfin never copies it into playlist URLs); the
  rewrite strips any key-like params anyway.
- Session bridge on the receiver: `ISessionManager` progress/stop events carry
  `PlaySessionId`; pings keep the owner's HLS job alive while paused (owner kills
  idle HLS jobs after 60 s), stop kills it so the GPU frees immediately.
- Mixed content: never hand an http owner URL to a client that reached the
  receiver over https (same scheme rule the static redirect uses).

### Hiding (2/3)
- Use Jellyfin's native **BlockedTags** user policy. Jellyfin 12 filters blocked
  tags against the item, its series, all parents and top parent in the DB query
  (`BaseItemRepository.TranslateQuery`), so it applies everywhere: library grids,
  search, Latest, Next Up, resume.
- One tag per local user (`federation-hidden-<hash>`), stamped only on items
  hidden from that user; the user's policy gets that one tag. Rule change →
  diff and restamp. Item tags already update in place in
  `FederationItemPersistenceService` (watch progress preserved).
- Receiver **pulls** its rules from each friend every sync (`Peer/UserRules`),
  push stays as the fast path. No more lost rules.
- Owner sharing change notifies the friend (`Peer/CatalogChanged`), so the
  friend resyncs and prunes immediately.

## Reference facts (Jellyfin 12.0 source)
- `StreamInfo.ToUrl`: HLS → `/videos/{ItemId}/master.m3u8?...&PlaySessionId=..&ApiKey=..`,
  progressive → `/videos/{ItemId}/stream.{container}?...`.
- jellyfin-web plays `apiClient.getUrl(mediaSource.TranscodingUrl)` through hls.js;
  relative playlist URIs resolve against the final (post-redirect) URL.
- `TranscodeManager`: HLS job killed 60 s after last ping/segment request;
  `Sessions/Playing/Ping?playSessionId` and segment requests reset it.

## Log
- 2026-10-19: baseline 960/960 plugin tests pass on 0.0.186.
- 2026-10-19: `federation_peer_smoke.py` was stale against 0.0.186 in two places (not product bugs):
  Direct-mode Paths now point at the source server (fetched from the receiver → 403 by design),
  and since 0.0.184 offline titles stay listed. Test updated for both.
- 2026-10-19: **Reproduced problem 3 in the two-server fixture on stock 0.0.186:** owner saves
  a Blocked rule for the receiver's viewer → "Saved.", receiver stores 1 rule, viewer's playback
  is refused, but the viewer still sees the title (1 visible). So in the fixture the save works;
  the live config's zero rules must come from the UI path or a failed push. Still to check.
- 2026-10-19: live Jellyfin upgraded 12.0.0 → 12.2.0 by a helper agent (backup: fedora-jellyfin-state-backup-12.0.0-20261019, compose.yml.bak-jellyfin-12.0.0-20261019; old containers kept stopped as jellyfin-old-12.0.0*). Plugin 0.0.186 loads Active on 12.2. Daily check-only watchdog: scripts/jellyfin-update-watchdog.sh + user timer. Fixtures must also run on 12.2.
- 2026-10-19: **Problems 2/3 fixed and proven on 12.2 in the fixture** (`federation_peer_smoke.py`):
  per-user Blocked and per-item blocks hide the title from that viewer only (list, search, Latest,
  direct id → 404, PlaybackInfo → 404), admin unaffected, unblock restores; policy tag added/removed.
  Unshare → titles gone from the friend within seconds; reshare → back.
  - Unshare had two causes: (a) the friend's sync of a "catalog changed" notice is spaced 5 min
    after the previous notice — sharing notices now carry `?sharing=true` and skip/cut that wait;
    (b) pruning brakes (missing twice + large-removal admin hold) treated a revocation like a glitch.
    Now removal is immediate ("authoritative") when the friend's `Peer/Libraries` no longer lists the
    mapped library, or within 15 min of a sharing notice. Everything else keeps the brakes.
  - Smoke test had a third stale assumption (recovery: title listed ≠ playable since 0.0.184). Fixed.
- 2026-10-19: Handoff built: `RemoteMediaPath` (allow-list shared by both sides), `HlsPlaylistRewriter`,
  `FederationRemoteTranscodeGateway` + `Peer/Play/{token}/{**path}` + `Peer/PlayControl` (owner),
  `PeerTranscodeCapability`, `PeerPlaybackGrants`, `FederationRemoteTranscodeFilter`,
  `RemoteTranscodeHandoffs` + `RemoteTranscodeSessionBridge` (receiver). Settings:
  `EnableRemoteTranscodeForFriends`, `MaxRemoteTranscodesPerFriend`, `UseFriendsTranscoders`,
  per-friend `AllowRemoteTranscode` (endpoints `Settings/RemoteTranscode`, `Friends/{id}/RemoteTranscode`;
  all preserved by the main Save). E2E: `scripts/federation_handoff_smoke.py`.
- 2026-10-19: Fixed a pre-existing bug found by the fallback test: friend titles with an external
  (sidecar) subtitle could not be transcoded on the receiving server. Jellyfin maps streams to ffmpeg
  input indexes by counting streams with an equal Path; nulling every Path made the sidecar count as
  part of the file (ffmpeg asked for 0:1/0:2 of a 2-stream file). Sidecars now get a distinct
  placeholder path (`FederationLibraryManager.RedactStreamPath`), regression test uses Jellyfin's own
  `EncodingHelper.FindIndex`.
- 2026-10-19: Self-review fixes before freeze: gateway no longer checks owner-side `Enabled` (that is
  "do I pull from them", not "do I share with them"); handoff sessions recorded only after the owner
  accepted (no pings for fallen-back sessions); rules naming non-existent local users ignored.
- 2026-10-19: **Final validation on frozen commit 72c5c61**: `scripts/test.sh` gate passed (1006 plugin,
  170 Companion, 116 UI tests, twice; receipt written). `federation_peer_smoke.py` 5/5 on Jellyfin 12.2.
  `federation_handoff_smoke.py` 6/6 on 12.2 and 12.0: owner converts, 1 MB first segment straight from
  owner with no receiver credential, receiver ran no ffmpeg, subtitle from owner, stop frees owner job in
  ~1-2 s (was 60 s idle timeout), block cuts in-flight URLs, opt-out and limit fall back to receiver.

---

# Notes — friends' favorites + downloads to device (0.0.188 work)

Started 2026-10-19 on branch `feature/favorites-and-downloads` from `origin/master` (0.0.187).

## What the user asked for

1. "Allow favorites to be shared across servers", not janky or obstructive, maybe per-server
   sections in the Favorites view. Asked which meaning; user picked **friends' favorites**: each
   friend shares which of its titles its users favorited (anonymous, titles already shared with you),
   your Favorites tab gets a "Favorites on <friend>" section; works both ways; admins can turn it off.
2. "Allow users to download from server to device federated items. Right now it's broken." Any user.
3. Make it proper; fix bugs found on the way.

## Root causes (downloads)

- Jellyfin only offers Download for local files: `Video.CanDownload() => IsFileProtocol`. A federated
  item's Path is an http URL, so `CanDownload` is false and every app (jellyfin-web context menu,
  Android, Findroid, Swiftfin) hides Download. `/Items/{id}/Download` would `PhysicalFile(<url>)` → 500.
- The only path that existed was a web-only injected "Download to device" entry calling
  `Plugins/Federation/DownloadUrl`, which returned this server's **loopback** proxy URL
  (`http://127.0.0.1:8096/Plugins/Federation/Stream?...`) — unreachable from any phone or other PC,
  despite its doc comment claiming an origin-relative URL. It also refused every title once any
  per-user rule existed (`IsAllowedForEveryConfiguredUser`), and the bytes were relayed through this
  server with a Download-less Playback path (owner's download consent never asked).
- Owner `DirectStream?download=true` never sent `Content-Disposition`, so even a direct link would
  play in the browser instead of saving.

## Design (downloads)

- Native route everywhere: `FederationCanDownloadFilter` (result filter) flips `CanDownload` to true on
  item DTOs that asked for the field, are Remote, not folders, Video/Audio, federated, and
  `FederationDeviceDownloadService.CanOffer` (viewer has EnableContentDownloading; some source friend
  is enabled Jellyfin, didn't report `allowDownloads=false`, its rule for this viewer has
  AllowDownload ≠ false and lets them see the title).
- `FederationDeviceDownloadFilter` answers `Library/GetDownload` for federated items (never falls
  through): visibility check as the viewer → viewer-bound **Download** token from the owner (sends
  X-Federation-Remote-User-Id) → 302 straight to `owner/Plugins/Federation/DirectStream/{id}?token&download=true`
  when the owner names the file (`deviceDownload` flag on the token response, new) and the scheme
  matches the request (browsers block https→http downloads; ExoPlayer refuses scheme-changing
  redirects); otherwise relay through this server with our own Content-Disposition (Range works).
  Permits cached per (server,item,viewer) until 2 min before expiry; refusals never cached;
  a relayed 401/403 drops the cached permit. Activity log entry once per viewer+title per hour.
- Owner: viewer-bound Download tokens live 12 h (resume window; still re-validated per request);
  server copies keep 15 min. Viewer allowance 30 distinct titles/viewer/hour and 120/friend/hour
  when the friend has no bulk permission; server copies keep the 3/hour friend-wide limit.
  Refusal messages distinguish "turned off for your account" from "not allowed for this friend".
- `Peer/UserRules` now also reports `allowDownloads` and `shareFavorites`; the receiver stores them
  as `PeerAllowsDownloads` / `PeerSharesFavorites` (null = older friend), preserved by the main Save.
- Removed: injected web "Download to device" entry and `FederationDownloadService.GetDownloadUrl`.
  `DownloadUrl/{id}` kept for old cached web scripts; it now returns the native relative path.

## Design (favorites)

- Owner `Peer/Favorites` (federation token + viewer header): favorites of enabled local users
  (`InternalItemsQuery(user){IsFavorite}` over Movie/Series/Season/Episode/Video/MusicVideo/
  MusicAlbum/Audio), never federated/downloaded copies, ranked by count then newest, filtered by
  `IsItemVisible(caller, viewer, id)` (sharing scope, excludes, per-viewer rules), max 60. Ids and
  counts only. Aggregate cached 5 min. Off → `{shared:false, items:[]}`. Setting
  `ShareFavoritesWithFriends` (default on).
- Receiver `FriendFavorites` (signed-in viewer): per friend (enabled, Jellyfin, online, not reported
  as not sharing, not 404-backed-off), fetch with 8 s timeout, cache 10 min per viewer, map remote id →
  local id via the cache index + `ComputeItemId`, keep only ids the viewer's own `GetItemIds` query
  returns (library access, parental, friend hide tags). Setting `ShowFriendsFavorites` (default on).
- Web: rows appended to `#favoritesTab` after jellyfin-web's own `.sections` (never touches them),
  jellyfin-web heading classes, solid-colour cards, lazy images, links `#/details?id=`. Reloads when
  the tab is visible and data is >5 min old or another user signed in.
- Admin: Advanced → "Friends' favorites" card (both switches + what each friend reports, incl. downloads).

## Log
- 2026-10-19: 1025 plugin tests, 93 web UI tests pass (new: DeviceDownloadTests, FriendFavoritesTests,
  FriendFavoritesRouteTests, 4 UI tests). Peer smoke extended with device-download and favorites stages.
- 2026-10-19: Found during review: cached download permits would hide a friend's later refusal
  (viewer got a bare 403); permits are now asked fresh per press (no allowance cost for the same
  title). Friend titles that only some viewers may see have no shared Path → LocationType Virtual;
  CanDownload filter now covers Virtual as well as Remote (smoke checks it).
- 2026-10-19: **Final validation on frozen commit 864d338**: gate passed (1025 plugin, 170 Companion,
  120 UI tests, twice; receipt). Peer smoke 7/7 on 12.2; peer smoke with the source on released
  0.0.187 passes (downloads relayed + named, refusal passed on, no favorites row); handoff smoke 6/6.
- 2026-10-19: Released 0.0.188 (master a622f8a, GitHub release asset md5 4c4e574dfe8a4c0b208635b99775ce4a
  verified). Deployed live: backup of 0.0.187 plugin + config at
  /var/mnt/kingfast/hosting-cutover-20260927/plugin-backup-federation-0.0.187-20261019; 0.0.188 loaded
  Active, startup sync refreshed both friends with 0 ERR.


---

# Notes — watch parties with friends (0.0.189 work)

Started 2026-10-20 on branch `feature/watch-parties` from master (0.0.188).

## What the user asked for
"Group watch party at the same time watching the same federated media… at the top you usually
create a group watch party, but with this you click on that but can make one with federation friends."

## Design
- Jellyfin SyncPlay is per server (in-memory groups, clients talk to their own server over the
  websocket). Bridging keeps every client working unchanged: each server has a normal group; a
  hidden "link" session (plugin ISessionController attached via SessionInfo.SessionControllers)
  is a member and receives every SyncPlay message; ISyncPlayManager.HandleRequest lets it act.
- Host = hub: the host's group holds one link per friend server + an "observer" link (label
  "Watching with friends") that models the group for announcements; each friend's copy of the
  party holds one link to the host. A friend's copy is created when its first viewer joins.
- Discovery: host announces every 15 s (and on title change) to its friends; friends add
  virtual entries to /SyncPlay/List (only when the viewer can open the playing title here) and
  intercept /SyncPlay/Join for them (NewGroup with the viewer's session + link + Join to host).
- Titles: PartyItemRef = owner refs (fid + item id on the owning server) + provider ids
  (+ series ids/season/episode). Resolved to the own item, the federated copy, or a local file
  with the same provider ids. Own titles not shared with that friend are described by kind only.
- Relay rules (PartyLink, pure): real changes only (pause from playing, resume, seek, stop, queue),
  each confirmed against the group's actual state (ISyncPlayManager.GetGroup) when read; echoes
  recognised against the last agreed state. Loading: remote → host "Wait"/"Go" (host link
  Buffer/Ready with IgnoreWait toggled); host loading → friends pause ("Wait") and resume on the
  host's resume. Friends never make the host wait on the host, so no mutual waits.
- Jellyfin quirks found: Idle→Waiting→Paused (initial load) emits no message to any member;
  a Ready during Waiting is answered with "pause when ready" (CurrentSession); Unpause in Waiting
  with ResumePlaying=true forces unloaded viewers to start (we send Pause then Unpause instead);
  Jellyfin 12 websockets take `ApiKey=` (not `api_key=`) unless legacy auth is on.

## Log
- 2026-10-20: 1047 plugin tests (22 new WatchPartyTests), 97 web tests. federation_party_smoke.py
  8/8 stages, three consecutive runs, ~30-50 SyncPlay requests per server per run.
- 2026-10-20: Released 0.0.189 (master 153a588, release asset md5 050d90d62a385abd7371cbb3bef0fee3
  verified, manifest on master matches). Deployed live: backup of 0.0.188 plugin + config at
  /var/mnt/kingfast/hosting-cutover-20260927/plugin-backup-federation-0.0.188-20261020; 0.0.189
  loaded Active, startup sync refreshed both friends with 0 ERR.

---

# Notes — download destination, friends' viewers, Playback Info, hardware (0.0.190 work)

Started 2026-10-20 on branch `feature/download-dest-viewers-info` from master (0.0.189).

## What the user asked for
1. Server downloads of federated titles go to a folder you pick: the Movies folder or a new
   folder; default Movies.
2. When someone on a friend's server watches something of yours, you see it like a directly
   connected viewer: what, how far in, etc.
3. Optional: servers share hardware (GPU/CPU) so a program decides whether the friend or this
   server converts (e.g. a friend with an RX 550). Minimums per codec/quality, CPU minimum.
   User's caveat: their own conversions run on another PC, so auto-detected hardware is wrong
   for them. "Maybe this will add more problems than fixes."
4. Playback Info (player menu) shows connection info for federated titles.
5. Later, review only: the svoeth fork's releases (end of .todo).

## Design
- Downloads: `DownloadDestinations` (pure). Choices `auto` (library matching the title's type,
  first folder that isn't the plugin's own: live Movies = /media/movies + plugin federation
  folder), `library:<name>`, `folder:<name|/path>` (bare name made beside the matching library's
  folder; becomes its own library of the title's type; a path inside an existing library's
  folder just uses that library), `separate` (old Federation Downloads). Films land as
  "Name (Year)/Name (Year).ext", episodes "Series/Season NN/Series SNNEMM.ext", a second copy of
  a film as "Name (Year) - <friend>.ext" (Jellyfin's version naming). Partial file sits beside the
  final one (atomic move), write-probed first (a read-only folder fails clearly instead of
  pausing forever). Confinement: job paths must be inside the job's TargetRoot; partial deletes
  only *.partial inside the downloads root or TargetRoot. Old queued jobs keep the downloads root.
- Downloaded copies outside the downloads folder are never shared onward: `DownloadedFileRegistry`
  (JSON in plugin data) covers the gap before the scan stamps FederationDownloadedFrom.
- Friends' viewers: viewer side `FederationViewerReporter` listens to PlaybackStart/Progress/
  Stopped, maps the played media source to the friend's copy (item id = primary; else hash of
  friend+remote id), posts `Peer/Viewers` (start/stop at once, progress every 15 s or on pause,
  route change or a seek > 10 s; one ordered queue; 10 min backoff for friends without the route).
  Owner side `FederationRemoteViewerService` checks the friend may see the title for that viewer
  (per-user rules), creates a user-less session (LogSessionActivity with null user is supported
  in Jellyfin 12.2; key = client+device+empty user, so never merges with a real session) and
  drives OnPlaybackStart/Progress/Stopped, so the dashboard shows it natively. No user = no
  watch history or activity log entries on the owner. Device id = the viewer's own device id so
  a handoff conversion (which asks with that id) attaches its TranscodingInfo. 90 s silence ends it.
- Playback Info: `GET Playback/Connection` with no args finds the calling device's session;
  the play session id comes from the reporter (players don't expose it) for handoff lookup.
  Web re-adds a "Federation" block after jellyfin-web's 1 s re-render (scan() runs on mutations).
- Hardware: `TranscodeHardwareInfo` via `Peer/Hardware` (models + codec lists + running count +
  per-friend limit; share switch default on). Auto = Jellyfin's encoding settings (live server:
  vaapi on a Ryzen 5 5500 / RX 580, which is NOT what converts); manual = the admin describes the
  machine that converts (offloaded, GPU, CPU, codecs). Placement (default OFF): friend converts
  unless it falls short of the minimum for the source (4K: hardware decode+encode; 720-1440p:
  hardware encode; CPU with >= 16 threads counts as an encoder) or is at its limit, AND this
  server meets it and is better, AND the link carries the original (never pull an over-cap
  original). Otherwise unchanged behaviour. Converting here = no grant recorded (and a stale
  grant forgotten), so FederationRemoteTranscodeFilter leaves the transcode local.

## Log
- 2026-10-20: 1081 plugin tests (34 new in DownloadsViewersHardwareTests), 133 web tests (9 new), gate passes twice.
  Two-server on Jellyfin 12.2: peer smoke 9/9 first run (new stages: friend's viewer shown on the owner's
  /Sessions as "qa-viewer (friend)" with no user id, position/pause/stop, not for a blocked viewer, not with
  the owner's switch off; Playback/Connection; Peer/Hardware 401 without a friend key; downloads land as
  /config/qa-movies/QA Peer (2026)/QA Peer (2026).mkv, a new folder becomes library "QA Friends", a second
  copy is "QA Peer (2026) - <friend>.mkv", "folder:../escape" refused). Handoff 6/6, party 8/8, peer with
  the source on released 0.0.189 6/6 (no Peer/Viewers there: the reporter backs off quietly).
- 2026-10-20: Released 0.0.190 (master 77ab9c6, asset md5 9cb488c7c77baf8ba9cdd67702c399da verified against the
  manifest). Deployed live: backup of 0.0.189 plugin + config at
  /var/mnt/kingfast/hosting-cutover-20260927/plugin-backup-federation-0.0.189-20261020; 0.0.190 loaded Active,
  startup sync 888 items, 0 ERR; /Plugins/Federation/ClientScript serves the new picker and Playback Info code.
  Friends (netflix++, Inspired) need 0.0.190 for viewers/hardware; downloads and Playback Info work without them.
- 2026-10-20 (0.0.191): live playback errors on netflix++ titles. Log: at 14:41 netflix++ refused the handoff of The Wild Robot
  ("could not take HlsMaster"), which turned the handoff off for ALL netflix++ titles for 2 min; local fallback then failed too
  (the broker's input relay got an HTTP error from netflix++ DirectStream and dropped the connection: ffmpeg "End of file",
  exit 187). Wicked, started in that window, got a local playlist; after the 2 min its segments were 302'd to netflix++, which
  converted too (the user's screenshot: every segment twice). User decision: a friend's title plays only from the friend.
  Fix in FederationRemoteTranscodeFilter: segments follow the play's start (RemoteTranscodeHandoffs.HandedOffTo), a failed
  handoff returns the friend's status to the player (502 if unreachable), 403 = switched off -> PeerTranscodeCapability.Record(false)
  and that play converts here. RecordRefusal (2-min friend-wide backoff) is no longer called. A first attempt with per-title
  backoffs and retry detection was dropped at the user's request ("simplify it").

- 2026-10-20: Released 0.0.191 (master fab48c4, asset md5 701d9c450082a395706c74d8a050fbcf verified). Deployed live:
  0.0.190 plugin + config backed up to /var/mnt/kingfast/hosting-cutover-20260927/plugin-backup-federation-0.0.190-20261020;
  0.0.191 Active, startup sync 888 items, 0 ERR.
- 2026-10-20 (0.0.192): after 0.0.191 the handoff to netflix++ happened but failed in the browser ("its going through http not
  https"): netflix++'s gateway writes absolute playlist addresses from X-Forwarded-Proto/request scheme, which its proxy chain
  reports as http. Receiver now rewrites them (HandoffPlaylistLinks) and relays nested playlists through
  Plugins/Federation/HandoffPlaylist so segment addresses are fixed too. Segments themselves go straight to the friend over https.

- 2026-10-20: Released 0.0.192 (master d96c998, md5 8961d3a7807c989d2156f5b081313389 verified); deployed live, backup of 0.0.191 at /var/mnt/kingfast/hosting-cutover-20260927/plugin-backup-federation-0.0.191-20261020; startup sync 888 items, 0 ERR.
- 2026-10-20 (0.0.193): seeking/slow-start root cause is Jellyfin restarting ffmpeg for any segment requested before one exists, combined with
  hls.js's 20 s fragment timeout and retries. Owner gateway now coalesces identical segment requests (FederationRemoteTranscodeGateway
  GetSharedSegmentAsync). netflix++ restarting (15:52) invalidated its in-memory playback tokens; our server kept reusing cached ones -> every
  play refused until our Jellyfin was restarted at 15:55. Fixed both sides (token persistence as hashes; receiver re-mint on 403).
  Early start: receiver fetches variant + first segment right after serving the master (only for friends advertising sharedSegments).
  Handoff test needs TMPDIR=/home/cranky/.cache/jf-qa-tmp (tmpfs /tmp too small for Jellyfin's 2 GiB free-space check).

