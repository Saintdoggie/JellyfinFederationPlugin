# Jellyfin Federation — remaining work

Completed work is removed from this file. Git history and GitHub releases keep the validation record.

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

- [ ] Add a playback preflight diagnostic showing the chosen source, friend reachability, version compatibility, authorization result, metadata result, and final media-source viability.
- [ ] Test physical Xbox hardware and record the Jellyfin client version, media format, direct-play/transcode decision, and failure stage.
- [ ] Expand the playback matrix for subtitles, audio switching, resume/seek, fallback sources, Plex, Companion imports, and mixed plugin versions.
- [ ] Preserve watch progress when a federated item must be deleted and recreated.
- [ ] Reduce the extra local relay hop used by Direct mode while retaining scoped authorization and never exposing a long-lived credential.

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
