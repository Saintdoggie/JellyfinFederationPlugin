# Federation Companion — active Plex repair handoff

Updated 2026-09-06. Source: `/var/home/cranky/Documents/JellyfinFederationPlugin-master`. The Plex ownership/playback repair is published in plugin 0.0.133. Read root `AGENTS.md` and `TODO.md` before continuing.

## User clarification and root causes

Friend runs Windows. The Helluva Boss titles were third-party media federated to the user, and should never have reached the friend's Plex. Downloads also showed items from the wrong selected server. Prior screenshots showed posters but Video/Audio None and playback errors.

The peer authorization service lacked a FederationKey exclusion before allow-all; Downloads accepted stale responses after server changes; older peer exports needed receiving-side filtering. Native Plex scanned `.strm` text as unplayable metadata. Do not describe successful catalog import as successful playback or renumber third-party shows.

## Implemented locally

- [x] Authoritative outgoing ownership gate, including already-issued playback tokens; hide wholly federated virtual libraries while retaining mixed local libraries. Resolve actual top library for series/season queries. Case-insensitive FederationKey checks.
- [x] `PeerCatalogPage` filters older peers while preserving the raw cursor; Downloads response epochs, origin-bound selection and queued-server/remote-item preflight prevent wrong-source downloads.
- [x] `MediaMount` exposes authenticated read-only WebDAV real media files from committed imports; per-read upstream authorization, HEAD and Range relays. Optional media sources in Peer/Items supply actual container/size.
- [x] `LocalMediaMountService` owns its rclone process, starts without a shell, verifies an instance-specific marker and restores the mount after restart. Companion downloads or ships a pinned rclone binary; WinFsp/FUSE remains a one-time OS driver install. Owner UI shows current mount status and setup guidance.
- [x] Source catalog diagnostics preserve source season/episode/combined-episode numbering and explain missing size/container/numbering. Windows reserved device names and trailing dots are handled.
- [x] Explicit preview/library selection and editing; new source libraries are not silently added to new connections. Imported Plex sections are ineligible for sharing. Switching Plex servers resets section consent/attachment IDs; account picker shows owned servers only.
- [x] LAN-first bounded Plex connection probes; owner-triggered real video-byte diagnostics; separate public Companion relay. Funnel claims have no raw Plex-token fallback; legacy unsigned import-stream route returns 410; Add-to-Plex response is sanitized.
- [x] Approved Plex parts are revalidated against current metadata and sharing at read time, including moves to private sections.
- [x] Failed/malformed catalog fetches preserve the last successful catalog. Successful removal/revocation reconciles entries. Source media changes trigger Plex refresh; failed refreshes remain pending for retry.
- [x] Legacy `.strm` cleanup tracks ownership atomically, rejects traversal/links, adopts only this peer's old URLs, preserves unrelated files even when titles collide, and serializes removal with sync.
- [x] Documentation and UI distinguish catalog sync, mount readiness, Plex attachment and playback. Root README points to Companion setup; KNOWN_ISSUES records current limits.

## Validation completed on the final source

- [x] Full automated suites passed twice: **404 plugin xUnit, 59 Companion xUnit, 26 JavaScript tests**. Tests cover stale Downloads responses, forwarded-item pagination/download denial, malformed catalogs, current Plex authorization, explicit UI selection, error recovery, safe rendering, episode ranges, Windows names and foreign-file preservation.
- [x] Release build/publish passed; Windows x64 self-contained single-file publish passed. No warnings in the recorded plugin Release build. `git diff --check` passed.
- [x] Real disposable Jellyfin 10.11.11 → Companion → rclone 1.75.1 → Plex test: actual mounted MKV, H.264 video, AAC audio, and successful ffmpeg decode of media served through Plex. This is server-side decode, not a Plex client UI/transcode test.
- [x] Final live Companion build: UI served from executable directory; catalog import and real mount reads; HEAD length 83882/body 0; ordinary bytes=10-29 and suffix bytes=-20 each 206 with 20 bytes.
- [x] Live outgoing gate: temporarily stamp only the disposable test movie as third-party; outgoing list excludes it, metadata 404, new token 403, pre-issued token HEAD 403, next Companion sync removes its mounted entry. Original test metadata restored.
- [x] Deselect/reselect removes/restores import; source outage preserves catalog and reports sync failure; clean Companion shutdown removes its managed mount and startup restores it automatically.
- [x] Actual Chromium Companion page checks at 390/1440/1920px: owner unlock, imports, source catalog interaction, no page errors and no horizontal overflow.

## Remaining before release

- [ ] Friend's actual Windows/WinFsp/rclone/Plex account permissions and Funnel reachability, including high-bitrate media, seeking, subtitles and audio changes. No access to the friend's host was available. Do not claim Windows runtime validation from a successful cross-build.
- [ ] Real Plex client playback/transcoding and full two-server ordinary-user/admin release matrix. Plex → Jellyfin facade unit tests passed, but final real Plex → Jellyfin playback needs the live matrix.
- [ ] Multi-part source video handling and source replacement identity/size behavior; combined episodes are supported but multiple parts are not yet assembled.
- [ ] Large-catalog stress tests: compatibility paging on outgoing Peer/Items rescans earlier raw rows to honor visible offsets. Correctness is covered; very large import latency is not.
- [ ] Alternate Plex/TMDB/TVDB episode matching. Files preserve source numbering; the importer cannot repair incorrect source metadata or guarantee matching with a different Plex episode-order setting.
- [ ] Different-user Linux FUSE permissions, macOS mounting, and Docker mount propagation across restarts. Fixture PMS used container root to match the mount owner, so it does not prove normal Plex-user permissions.
- [ ] Legacy direct Plex-token connections have broader credential boundaries. Reconnect through the scoped Companion Funnel flow; bytes already downloaded/cached cannot be remotely erased.
- [x] Published as plugin 0.0.133. Repeat live Windows/Funnel/client checks after install; they remain post-release validation.

## Local preview artifacts

Ignored directory `artifacts/plex-repair-preview/` contains a Windows preview ZIP, SHA256SUMS, and a plugin preview DLL. The ZIP contains only published binaries/static assets and preview/setup notes; inspected to exclude private state/config. These are local test builds, not published release artifacts, and retain the existing assembly version. Do not upload as the current release.

## Disposable fixtures and reproduction

Only newly created repair fixtures were modified. Production and older sandbox containers were untouched.

- Jellyfin: `fed-plex-repair-jellyfin`, localhost8350, `/tmp/fed-plex-repair-jellyfin` config, synthetic media mounted read-only.
- Plex: `fed-plex-repair-pms`, localhost32402, `/tmp/fed-plex-repair-pms-config`; `/tmp/fed-plex-repair-app/plex-media` bound read-only as `/media`. Test PMS runs as container root solely to match the FUSE owner.
- Companion: latest published runtime `/tmp/fed-plex-repair-app`, localhost8351. Managed mount `/tmp/fed-plex-repair-app/plex-media`; old manual mount `/tmp/fed-plex-repair-mount` was stopped.
- Scripts: `/tmp/fed-plex-repair-verify.py`, `-ownership.py`, `-outage.py`, `-ui.cjs`. Read before rerunning; the ownership script restores test metadata in finally. These scripts read private credentials without printing them.
- Private test credentials/state/config are outside the repo in `/tmp`; never print or copy them into source/docs. Runtime log includes the generated owner key and must stay private.
- Check fixture process/container/mount status before reuse. Final shutdown status is recorded below. Cleanup must target only these named repair fixtures and preserve user data.

Final shutdown: both named repair containers stopped; the repair Companion process stopped and both old/manual and managed rclone mounts are unmounted. Fixture data and private test files are retained under /tmp for reproducibility. No production processes were stopped.

## Scripted handoff

Use `scripts/test.sh --all`, `scripts/push.sh`, and `scripts/release-preview.sh`.
The full gate now creates fresh synthetic movie/episode files and isolated ephemeral
Jellyfin/Plex/Companion/rclone instances, validates ownership, selection, outage,
restart and actual Plex decode, then cleans up only its own resources. The push
receipt hashes the actual source files and toolchain/images and expires after 24h.
A preview release includes Windows and plugin test ZIPs plus checksums; it does not
replace companion-latest or the plugin catalog. See `scripts/README.md` for dependencies.

Fresh-install automation also caught shared rclone cache reuse across installations. Managed mounts now use an installation-local media-cache directory; fresh-instance mount/start/restart passed after this fix.
