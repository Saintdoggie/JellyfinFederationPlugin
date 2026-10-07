# Jellyfin Federation — remaining work

Completed work is removed from this file. Git history and GitHub releases keep the validation record.

## Fast federated playback start (target: 4-30 s from click)

Findings (measured 2026-10-03, live server): start time is roughly `6 s x file bitrate / link speed`.
Jellyfin serves the first HLS segment only once the next exists (2 x 3 s of source). freakbob (Plex, Proxy
mode) delivers ~8 Mbps whatever the connection count, so a 93 Mbps remux takes ~75 s. The transcode broker
already trims ffmpeg's probe to 1 s / 1 MB on the Windows GPU box, so the analysis-window change is harmless but
does not help this setup. Fewer bytes from the friend's side is the only fix for link-bound titles.

Done
- [x] Diagnose why it is slow (link-bound; model predicted 74 s, measured 75-85 s).
- [x] 0.0.171: bounded ffmpeg analysis window for federated sources (`FastStartProbing`, on by default). Verified in a throwaway Jellyfin 12; no gain behind the transcode broker.
- [x] Persist stream info when a source reports duplicate stream indexes (avoids Jellyfin's slow first-play probe).

In progress
- [x] Link-speed measurement: daily, on new server, on reconnect; Jellyfin peers and Plex (consented files only). Live: freakbob measured 7.1-7.6 Mbps automatically.

Loading experience
- [x] Start-time estimator and `GET /Plugins/Federation/StartEstimate/{itemId}` with tests. Live check: 93 Mbps title projected 78 s vs 75-85 s measured.
- [x] Fun-fact service: server-side call to a free fact API, short timeout, built-in fallback list, `LoadingFunFacts` toggle, tests.
- [x] Settings `LoadingOverlay` / `LoadingFunFacts` / `FastStartProbing`: on the admin settings page, saved, and `loadingOverlay` exposed through `ClientSettings`.
- [x] jellyfin-web overlay: simple bottom bar, projected countdown, reason text, rotating fun fact; only if still black after ~1.5 s; 15 jsdom tests. Served live; rendered in headless Firefox at desktop and phone widths and looks as intended.
- [x] Timing logs: media-source resolution time and slow upstream responses (`[Federation][timing]`). Still wanted: time to first segment.

Real speed-up (fewer bytes from the friend)
- [x] Probe whether freakbob's Plex allows transcoding (`Diagnostics/PlexTranscodeProbe/{itemId}`). Result 2026-10-03: HTTP 403 with an empty body from BOTH Plex's decision call and the transcode call. Cause: `Companion/PlexFederationRelay.cs` is an allowlist (sections, metadata, parts) and answers everything else 403, so the friend's consent scope blocks transcoding.
- [x] Prefer the faster source for titles on several servers when the first choice would exceed a 30 s start budget (hysteresis, measured sources only). Simulated on the real catalog: 26 of 104 multi-server movies would move to netflix++ once it is online, saving 28-74 s each.
- [ ] Owner decision needed: Auto WAN cap floor is 10 Mbps (set at the owner's request) but Inspired measures 2.0 Mbps, so its capped stream is 5x too fast for the link. Consider a floor that follows the link.
- [ ] Capped Plex stream for link-bound titles. BLOCKED on friend consent: needs an opt-in Companion setting (default off) that lets the relay pass `/video/:/transcode/universal/{decision,start.mkv,stop}` for shared items only, the friend updating Companion and enabling it, then the plugin path (bitrate = min(client max, ~80% of measured link)), original-quality copy kept as a version, seek/resume, audio track and subtitles.
- [ ] Mid-stream sharpening: multi-level HLS playlist (for example 3 / 8 / original Mbps) so the player starts low and switches up; investigate jellyfin-web / hls.js behaviour and TV native players first.
- [ ] Optional: warm a title when its detail page opens or the next episode is queued (small capped LRU, no bulk caching).

Diagnosis and release
- [ ] Diagnose netflix++ (gigabit peer, still slow) once it is online, using the new stage timings.
- [ ] Full gate before publishing: commit on `feature/fast-start`, run the suite twice, two-server ordinary-user/admin matrix, update `manifest.json` / `meta.json` / zip checksum, push, GitHub release (needs GitHub login on this machine).
- [ ] Rollback if needed: previous plugin folder is in `/config/plugin-backups/Jellyfin Federation_0.0.170` inside the `jellyfin` container.

## Companion desktop app — Windows + Linux

Companion now ships as a real background app: WinExe + tray on Windows, XDG
autostart on Linux, single-instance, rotating log, low-memory GC settings, and
an owner "Companion app" card. Implementation details, remaining platform
validation, and the exact test commands live in
[`Companion/DESKTOP-APP-TODO.md`](Companion/DESKTOP-APP-TODO.md). Read that file
before changing Companion startup, tray, autostart, or packaging.

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
