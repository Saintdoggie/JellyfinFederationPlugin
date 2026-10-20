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
  friends (Slowpoke, friendflix), and no log lines for rule saves. So the user's
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
