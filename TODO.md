# Jellyfin Federation — remaining work

## 0.0.199 — friends' addresses out of logs (2026-10-22)

- [x] Public addresses in direct-link and pairing logs, status details and error text shown as a per-run tag; private/LAN/Tailscale kept.
- [x] Real friend names, home paths and the owner's domain removed from tests, docs and comments (public repo).

## 0.0.198 — pairing codes (beta) and link hardening (2026-10-22)

- [x] Pairing codes, Beta tab, QUIC windows; security review + adversarial pass, all findings fixed (Tests/Adversarial).
- [x] Direct link: header allowlist; read-ahead playlist cap.
- [ ] Mutual-friend (mailbox) reconnect; relay tickets for remote viewers of paired friends.
- [ ] Smaller receive windows on paired links; Windows MsQuic.

## 0.0.197 — relay read-ahead (2026-10-22)

- [x] Direct-link relay fetches the player's next 3 segments over the link (one download to friendflix stopped near 110 Mbps; four at once 178).
- [ ] Live: one-at-a-time playback speed to friendflix with read-ahead.
- [ ] Read-ahead for the Proxy streaming path.

## 0.0.196 — direct links released as beta, opt-in (2026-10-22)

- [x] Opt-in (`DirectLinkOptIn`, default off; 0.0.195's saved `EnableDirectLink` ignored), Beta badge, switch at the top of the Friends tab.
- [x] Dial back as soon as a friend links in (live: friendflix linked 14:21, our side waited until 14:34).
- [x] Live on 0.0.195 between this server and friendflix: both directions up, 115 Mbps, both routers cone.
- [ ] Live: a home viewer play over the link ("comes over the direct link to this home viewer" in the log).
- [ ] Optional private relay through a friend's own VPS (UDP forward) for friends who keep their home address hidden.
- [ ] Hide friends' home addresses from logs and the status page.

## 0.0.195 (test build, branch `test-branch-udp-connection`) — direct links (2026-10-22)

- [x] Friend-to-friend direct link: STUN + UDP hole punching, QUIC with pinned mutual TLS + session proof,
      HTTP/1.1 per QUIC stream; only Peer/Play, DirectStream and Peer/BitrateTest carried (`docs/DIRECT-LINK.md`).
- [x] QUIC runs in a private AssemblyLoadContext with the bundled MsQuic 2.6.3 (Jellyfin caches "no QUIC" before plugins load).
- [x] Home-network viewers get handed-off video through `DirectLink/{serverId}/...` on this server; others go to the friend.
- [x] Library downloads from a friend over the link; Friends tab status/mode/retry; Advanced switch; Playback Info row.
- [x] Sonnet security review + ultra review: all findings fixed (`edeaf92`, `0d4d16b`).
- [x] Two-home NAT harness `scripts/federation_directlink_smoke.py` (cone connects ~1.2–1.4 Gbps; symmetric both sides falls back).
- [ ] Live: install on this server and friendflix (both need 0.0.195); check Friends tab "Connected · N Mbps" and Playback Info.
- [ ] Live: does rootless Podman (pasta) keep UDP ports for punching here? Is friendflix's VPS a full tunnel / are they behind CGNAT?
- [ ] Windows servers (MsQuic ships with .NET there; needs testing + firewall prompt). IPv6 candidates.
- [ ] Merge to master after a day or two of real use.

## 0.0.194 — data limits, Popular on friends' servers, direct downloads, review fixes (2026-10-21)

- [x] Per-friend monthly GB and simultaneous-stream limits on the owner, counted on DirectStream and the
      Peer/Play gateway (adapted from svoeth's PR #3; its activity card, user hiding and collections not taken).
- [x] "Popular on <friend>" Home rows: distinct local users' plays of the owner's own titles, last 30 days.
- [x] Device downloads redirect straight to the friend (https rebuilt for http-configured friends).
- [x] WAN speed test: cap never above the measured speed (live: "Slowpoke Jellyfin" measures 1–7 Mbps and was capped at 10).
- [x] Fixes from the 2026-10-21 review (`~/Documents/federation-review-2026-10-21/`): rating scales, corrupt-cache
      mass delete, `{}` library prune, disk full, batch image tokens, segment hot path, pool/discovery address checks,
      token never sent to another host on redirect, web rescans and stuck progress.
- [x] Gate twice (1215 plugin / 170 Companion / 139 web); two-server peer 9/9, handoff 9/9, party 8/8 on 12.2.
- [ ] Live checks: a cap reached mid-HLS and mid-download; an apex→www friend redirect; first Popular load on a big
      library; a client that omits PlaySessionId with a transcode limit set; friendflix's WAN classification (new Info line).
- [ ] PR #3: reply to svoeth (split into small PRs: end dates + preview, friends-of-friends control).

## 0.0.189 — watch parties with friends (2026-10-20)

- [x] SyncPlay groups across servers: "New group with friends" / "Invite friends" in the group menu;
      friends' viewers join from their normal group list; each server plays its own copy.
- [x] Play/pause/seek/title changes shared; loading or buffering anywhere pauses everyone (Wait/Go handshake).
- [x] Two-server test with scripted SyncPlay players: 8 stages incl. reversed roles, second joiner, title change.
- [ ] Real-world check with real apps (web, Android) once both friends run 0.0.189.
- [ ] Not covered: friends of friends (a party only reaches the host's direct friends), Plex/Companion sources.

## 0.0.188 — downloads to device and friends' favorites (2026-10-19)

- [x] Any viewer can download a friend's title with their app's own Download button (web, Android,
      Findroid, Swiftfin): CanDownload is reported for friends' files, `/Items/{id}/Download` sends the
      app straight to the friend (named file, viewer-bound permit) or relays when it must.
- [x] Fixed: the old web-only "Download to device" handed browsers this server's loopback address
      (never worked off the server itself) and refused every title once any per-user rule existed.
- [x] Friend download consent covers viewers (friend switch + per-viewer rule), with a per-viewer allowance.
- [x] Friends' favorites: "Favorites on (friend)" rows on the Favorites tab; titles only, per-viewer visibility.
- [ ] Real-world check once both friends run 0.0.188 (older friends: downloads are relayed, no favorites row).
- [ ] Not covered: Plex/Companion sources (no Download button), native-app favorites rows (web only).

## 0.0.187 — remote-transcode handoff and access that hides (2026-10-19)

Branch `feature/direct-play-and-access`.

- [x] Remote-transcode handoff: a viewer signed in here plays a friend's title that must be
  converted; the friend's server converts its own file and streams it straight to the player
  (`Peer/Play/{token}/...` gateway, `FederationRemoteTranscodeFilter`, session bridge for pause/stop).
  Falls back to converting here when the friend can't. Two-server proof: `scripts/federation_handoff_smoke.py`.
- [x] Friend per-user rules hide titles (native BlockedTags + per-user hide tag), not only refuse playback.
  Receiver pulls its rules from each friend every sync (`Peer/UserRules`).
- [x] Unsharing a library removes it from the friend at once (sharing notice skips spacing; revoked
  libraries prune without the glitch brakes). Proof: `scripts/federation_peer_smoke.py`.
- [x] Fixed: transcoding a friend title with an external subtitle on the receiving server failed
  (stream index shifted by the redacted sidecar path).
- [ ] Real-world check on the user's servers (both friends must run 0.0.187 for the handoff and the
  rule pull; the hiding of already-pushed rules works with older friends).
- [ ] Not covered yet: Plex/Companion sources (always converted here), adaptive web player, live TV.

## Top priority — major streaming redesign and federated watch parties

**Scope clarification: Jellyfin-to-Jellyfin only for this phase.** User reports
30–60+ seconds to start, depending on quality/codec.

- [x] Map current playback path and write the phased redesign/party plan.
- [x] Initial code: coordinate duplicate metadata requests, isolate cache by viewer/address/credential, add metadata and first-body-write debug timing. Four focused regressions pass; not yet a measured end-to-end speed improvement.
- [x] Initial two-Jellyfin HTTP benchmark passed (20 samples/path plus four concurrent requests, exact Range bytes). Local-network H.264/AAC first-byte p95 26.4 ms; does not reproduce the reported codec/WAN delay. See the plan for full evidence and limits.
- [x] Optional Chromium first-frame probe passed (three new browser instances per path): federated 58.1–67.7 ms on synthetic static MP4 over the local container network. This is not jellyfin-web codec negotiation.
- [x] Added/ran verified H.264 conversion and 1080p HEVC Main 10 SDR conversion benchmarks. Federated HEVC first frames 1.171–1.200 s versus local 1.170–1.206 s (three probes each); does not reproduce the reported WAN/client delay. HTTP/Range integrity passed, including concurrent reads. Added total source-preparation timing; 47 focused regressions passed.
- [x] Final implementation gate passed: clean builds, 425 plugin + 60 Companion + 29 JavaScript tests twice, plus Python gate tests. All disposable benchmark containers/networks were cleaned up. Final documentation records the result; push automation must validate the final committed snapshot.
- [ ] Next: controlled-WAN/cold-cache cases, actual jellyfin-web negotiation, offline-alternate timing, and a regression for importing while source media analysis is incomplete (fresh fixture reproduced a native stream extension error). Clean builds and 425 plugin + 60 Companion + 29 JS tests previously passed twice. Rerun the gate on frozen files before pushing. Current branch: `work/jellyfin-streaming-redesign`; no redesign release yet.

User request (2026-09-06): federated content takes too long to start. Treat this as a substantial architectural redesign, potentially rebuilding the streaming subsystem, rather than a small performance patch. Support watching federated media with multiple friends at the same time. This is delicate work: preserve working behavior while replacing the parts that measurements and design review show need rebuilding.

- [ ] Map the complete playback architecture and propose the replacement design: source selection, metadata, authorization, transport/relay, Plex media mounts, buffering, transcoding, session lifecycle and watch-party synchronization. Document migration, compatibility and rollback before implementation; unrelated plugin systems are outside this rebuild unless a concrete dependency requires changes.
- [ ] Measure Jellyfin-to-Jellyfin startup before changing the architecture: metadata lookup, authorization/token requests, source connection, first byte, client buffering and transcoding. Include controlled WAN conditions and the actual connection when available; Plex/Companion redesign is deferred.
- [ ] Remove measured startup bottlenecks and unnecessary requests/hops while preserving current sharing checks, credential isolation, Range/seek behavior, cancellation and source fallback. Record comparable before/after timings; do not assume larger buffers improve startup.
- [x] (0.0.189) Design and implement watch parties for federated content: assess Jellyfin SyncPlay integration first, then define supported clients and same-server versus cross-server participation, invitations, synchronized play/pause/seek, late joining and reconnects.
- [ ] Require each participant to have their own authorized access to the source media. Party membership must never grant access to another friend's imported library, expose credentials or bypass revoked sharing.
- [ ] Test concurrent viewers, slow connections, buffering, seeking, disconnects, source outages and access revocation with bounded resource use. Keep independent playback reliable when party members stall or leave.
- [ ] Keep this checklist updated with measured findings, implementation progress, validation evidence and remaining limitations so another model can take over safely. Do not publish streaming architecture changes before focused regressions and the applicable live playback matrix pass.

Completed work is removed from this file. Git history and GitHub releases keep the validation record.

## Fast federated playback start (target: 4-30 s from click)

Findings (measured 2026-10-03, live server): start time is roughly `6 s x file bitrate / link speed`.
Jellyfin serves the first HLS segment only once the next exists (2 x 3 s of source). plexpal (Plex, Proxy
mode) delivers ~8 Mbps whatever the connection count, so a 93 Mbps remux takes ~75 s. The transcode broker
already trims ffmpeg's probe to 1 s / 1 MB on the Windows GPU box, so the analysis-window change is harmless but
does not help this setup. Fewer bytes from the friend's side is the only fix for link-bound titles.

Done
- [x] Diagnose why it is slow (link-bound; model predicted 74 s, measured 75-85 s).
- [x] 0.0.171: bounded ffmpeg analysis window for federated sources (`FastStartProbing`, on by default). Verified in a throwaway Jellyfin 12; no gain behind the transcode broker.
- [x] Persist stream info when a source reports duplicate stream indexes (avoids Jellyfin's slow first-play probe).

In progress
- [x] Link-speed measurement: daily, on new server, on reconnect; Jellyfin peers and Plex (consented files only). Live: plexpal measured 7.1-7.6 Mbps automatically.

Loading experience
- [x] Start-time estimator and `GET /Plugins/Federation/StartEstimate/{itemId}` with tests. Live check: 93 Mbps title projected 78 s vs 75-85 s measured.
- [x] Fun-fact service: server-side call to a free fact API, short timeout, built-in fallback list, `LoadingFunFacts` toggle, tests.
- [x] Settings `LoadingOverlay` / `LoadingFunFacts` / `FastStartProbing`: on the admin settings page, saved, and `loadingOverlay` exposed through `ClientSettings`.
- [x] jellyfin-web overlay: simple bottom bar, projected countdown, reason text, rotating fun fact; only if still black after ~1.5 s; 15 jsdom tests. Served live; rendered in headless Firefox at desktop and phone widths and looks as intended.
- [x] Timing logs: media-source resolution time and slow upstream responses (`[Federation][timing]`). Still wanted: time to first segment.

Real speed-up (fewer bytes from the friend)
- [x] Probe whether plexpal's Plex allows transcoding (`Diagnostics/PlexTranscodeProbe/{itemId}`). Result 2026-10-03: HTTP 403 with an empty body from BOTH Plex's decision call and the transcode call. Cause: `Companion/PlexFederationRelay.cs` is an allowlist (sections, metadata, parts) and answers everything else 403, so the friend's consent scope blocks transcoding.
- [x] Prefer the faster source for titles on several servers when the first choice would exceed a 30 s start budget (hysteresis, measured sources only). Simulated on the real catalog: 26 of 104 multi-server movies would move to friendflix once it is online, saving 28-74 s each.
- [ ] Owner decision needed: Auto WAN cap floor is 10 Mbps (set at the owner's request) but Slowpoke measures 2.0 Mbps, so its capped stream is 5x too fast for the link. Consider a floor that follows the link.
- [ ] Capped Plex stream for link-bound titles. BLOCKED on friend consent: needs an opt-in Companion setting (default off) that lets the relay pass `/video/:/transcode/universal/{decision,start.mkv,stop}` for shared items only, the friend updating Companion and enabling it, then the plugin path (bitrate = min(client max, ~80% of measured link)), original-quality copy kept as a version, seek/resume, audio track and subtitles.
- [ ] Mid-stream sharpening: multi-level HLS playlist (for example 3 / 8 / original Mbps) so the player starts low and switches up; investigate jellyfin-web / hls.js behaviour and TV native players first.
- [ ] Optional: warm a title when its detail page opens or the next episode is queued (small capped LRU, no bulk caching).

Diagnosis and release
- [ ] Diagnose friendflix (gigabit peer, still slow) once it is online, using the new stage timings.
- [ ] Full gate before publishing: commit on `feature/fast-start`, run the suite twice, two-server ordinary-user/admin matrix, update `manifest.json` / `meta.json` / zip checksum, push, GitHub release (needs GitHub login on this machine).
- [ ] Rollback if needed: previous plugin folder is in `/config/plugin-backups/Jellyfin Federation_0.0.170` inside the `jellyfin` container.

## Companion desktop app — Windows + Linux

Companion now ships as a real background app: WinExe + tray on Windows, XDG
autostart on Linux, single-instance, rotating log, low-memory GC settings, and
an owner "Companion app" card.

## Active Plex repair — shipped as 0.0.133

The Plex ownership/playback repair from `fix/plex-federation-reliability` is
published in 0.0.133. See `Companion/TODO.md` for implementation details and
remaining live Windows/Funnel/client checks.

- [x] Fix onward sharing: Helluva Boss was third-party federated media, not the user's own. Do not renumber it. Peer catalog, metadata and playback authorization now exclude imported content; old playback tokens are rechecked. Companion and Downloads also filter older peers' forwarded items.
- [x] Fix Downloads source isolation: reject delayed responses from a previously selected server; bind selected rows and queued requests to their source. Recheck server state and item ownership when a queued download starts.
- [x] Replace native Plex `.strm` playback assumptions with authenticated read-only WebDAV media plus an rclone mount. Preserve source media containers/sizes, season/episode ranges and explicit library selection; show reasons for missing media information.
- [x] Implement managed mount start/restart, scoped source consent, current Plex part ownership checks, import cleanup that preserves unrelated files, and catalog preservation on failures.
- [x] Final automated runs passed twice: **404 plugin + 59 Companion + 26 JavaScript tests = 489**. Release build/publish and Windows x64 self-contained preview compilation passed. Diff whitespace check clean.
- [x] Live disposable Jellyfin/Companion/rclone/Plex checks passed: real H.264/AAC metadata and Plex-served media decode, HEAD, normal/suffix byte ranges, ownership revocation including old tokens, removal/reselect, source outage, and managed mount restart. Browser checks passed at 390/1440/1920px.
- [ ] Validate the friend's actual **Windows + WinFsp + Plex + Tailscale Funnel** setup, real Plex client playback/transcoding, and the complete two-server ordinary-user/admin release matrix. Linux tests and Windows compilation do not substitute for this.
- [ ] Review very large catalogs, multi-part video source selection and alternate episode-order matching before claiming universal playback support.
- [x] Published as 0.0.133. Remaining live Windows/Funnel/client checks are post-release validation, not a catalog gate. Preview artifacts stay under ignored `artifacts/plex-repair-preview/`.

## Repeatable checks and preview pushes

- `scripts/test.sh --all`: clean builds, automated suites twice, Windows cross-build, and a fresh disposable Plex/Jellyfin/rclone smoke fixture.
- `scripts/push.sh`: require a committed review branch, run/reuse a matching full gate, push the exact tested commit without force or release side effects.
- `scripts/release-preview.sh`: explicit preview publication with Windows and plugin ZIPs/checksums; never replaces companion-latest or changes the manifest.
- Validation receipts expire after 24 hours and are invalidated by changed files/toolchains/images. See `scripts/README.md`; Windows/Funnel/client release checks remain explicit.

## Usability and interface

- [ ] Split the large embedded settings page into maintainable assets or bounded modules without breaking Jellyfin's plugin page loader.
- [ ] Add guided repair actions for offline friends: retry, edit address, replace token where applicable, and explain which side needs attention.
- [ ] Make status, loading, empty, retry, disabled, success, and error states consistent across every settings tab.
- [ ] Finish responsive and accessibility review for Friends, Pools, Companion, Discovery, Libraries, Browse, Catalog, Storage, and Advanced at mobile, laptop, TV, zoomed, and high-contrast layouts.
- [ ] Add stable diagnostic codes to user-facing failures so an administrator can match a friendly message to the server log.
- [ ] Replace manual paging in Browse, Catalog, and Downloads with lazy loading and off-screen image loading.

## Playback and reliability

- [ ] Preserve each user's watch progress when deleting a local movie or series in favor of a higher-quality copy on another federated server. Carry resume positions and watched/unwatched status over to the matching remote movie or episodes so switching copies does not reset progress. Requested for planning only; do not implement yet.
- [ ] Add a playback preflight diagnostic showing the chosen source, friend reachability, version compatibility, authorization result, metadata result, and final media-source viability.
- [ ] Test physical Xbox hardware and record the Jellyfin client version, media format, direct-play/transcode decision, and failure stage.
- [ ] Expand the playback matrix for subtitles, audio switching, resume/seek, fallback sources, Plex, Companion imports, and mixed plugin versions.
- [ ] Preserve watch progress when a federated item must be deleted and recreated.
- [ ] Reduce the extra local relay hop used by Direct mode while retaining scoped authorization and never exposing a long-lived credential.

## Adaptive source ranking and prepared handoff — requested after reliability fixes

- [ ] Design continuous ranking for a movie available on two, three or more authorized servers. Combine recent uptime/failure history, sustainable throughput, latency, buffer health, this movie's bitrate/quality, client codec/HDR compatibility and transcode capacity. Keep lower-quality copies eligible when they offer more reliable playback; do not select on resolution alone.
- [ ] Reuse passive playback measurements and shared cached health snapshots. Bound probe concurrency, add jitter, timeouts, failure backoff and minimum refresh intervals, and avoid one uptime probe per viewer. Measure the overhead with large peer/catalog counts.
- [ ] Use score hysteresis, minimum dwell time and switching cooldowns to prevent oscillation. React quickly to actual source failure; upgrade to a newly online or better-performing source only when the improvement is sustained.
- [ ] Verify equivalent movie edition, timeline, duration and selected audio/subtitles before handing off. Provider-ID equality alone does not establish compatible cuts or seek positions.
- [ ] Prepare one replacement source before a voluntary switch: authorize it, seek to the projected presentation time, start any required transcode and fill a bounded buffer. Keep the current source playing until the candidate is ready; cap speculative bandwidth/CPU and dispose abandoned work.
- [ ] Implement a client/session-aware handoff at a compatible keyframe/segment boundary, preserving audio/subtitles, resume position and watch progress. Never splice different source-file bytes into one HTTP Range response. Determine which Jellyfin clients can support seamless switching; retain a truthful fallback for incompatible/native clients and sudden failures without enough buffered media.
- [ ] Roll back failed preparations and handle source disappearance, server disablement, access revocation and cancellation during handoff. Aim for minimal interruption; measure stall time rather than promising zero buffering under all network conditions.
- [ ] Show a brief, accessible top-corner notice such as “Switched to Server 2” only after a successful handoff. Keep secrets and internal addresses out of the notice; provide an optional explanation/disable control.
- [ ] Stress-test 2/3/N servers, competing viewers, bandwidth changes, flapping availability, mixed containers/codecs/editions, subtitle/audio switching, transcode exhaustion and cancellation. Record continuity, quality, startup/seek latency, speculative traffic and probe load.

## Companion sharing and public portability

- [x] Implement a preview of an integrated free sharing option using Companion's authenticated local-media relay rather than depending on Plex Remote Access or a Plex-facing Funnel. Support a private network when public ingress is unavailable; discover the runtime listener, installed network helper and machine address automatically.
- [x] Research September 2026 enforcement extending Plex paid remote playback to third-party apps. Do not promise that changing a tunnel provider removes Plex's remote-video requirements; see the Companion guide for sources.
- [x] Implement opt-in original-file sharing on Windows/Linux: discover Plex library folders, support explicit container/drive mappings, recheck the exact part and consent, preserve HEAD/Range seeking, and let receiving Jellyfin transcode. Never fall back to Plex video APIs on a file error; reject imported media and links below approved roots.
- [ ] Validate original-file mode with a claimed free Plex account and on Windows, including ordinary-user file permissions, mapped drives and cancellation. The blocked-video-API sandbox proves independence from Plex streaming, not actual account entitlement or private-network reachability.
- [ ] Prefer configurable/discovered paths, ports and addresses across Windows/Linux/macOS and containers. Never bake a developer's username, home directory, server address or credentials into shipped code or setup instructions.

- [ ] Before promoting private sharing to stable, verify two separately approved Tailscale server computers: real HTTPS certificates, cross-account access, catalog/HEAD/Range/decoded media, access revocation, restart, direct/DERP behavior and receiving-side imports. Current sandbox uses a simulated network CLI; do not claim real private-network playback yet.

## Federation state and security

- [ ] Make leaving a pool durable so a later stale pool notice cannot silently rejoin it.
- [ ] Remove stale federation access rules and session records when a Jellyfin user is deleted.
- [ ] Reconcile items and mappings immediately when a disabled friend is removed or its libraries are no longer shared.
- [ ] Continue endpoint authorization, SSRF, path traversal, header injection, secret handling, cancellation, and resource-exhaustion review.

## Future — Radarr and Sonarr requests

- [ ] Design administrator-only Radarr and Sonarr connections with encrypted-at-rest API keys that never reach clients, peers, URLs, or logs.
- [ ] Add an optional Request action that confirms target server, root folder, monitor policy, and quality profile before submission.
- [ ] Keep request permission separate from federation sharing and download permission, with rate limits and untrusted-metadata validation.
- [ ] Deduplicate against local libraries and existing Radarr/Sonarr queues; show owned, requested, downloading, imported, rejected, and failed states.
- [ ] Add root-folder allow lists and SSRF, secret-storage, rate-limit, and destructive-action tests before enabling integrations.

## Deferred design

- [ ] Design optional friend ratings and comments for a completed movie, episode, or season. Do not implement until scheduled by the project owner.
