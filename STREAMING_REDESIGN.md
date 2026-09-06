# Jellyfin federation streaming redesign

Status: investigation and initial regression work, 2026-09-06. Not released.

## Scope and reported baseline

The user reports **30–60 seconds or longer from Play to playback**, varying with
quality and codec. This is a reported baseline, not yet a controlled measurement.
This project phase covers **Jellyfin-to-Jellyfin only**, including watch parties.
Plex and Companion streaming architecture changes are out of scope. Shared relay
changes must still pass their existing regressions.

Support friends watching through one receiving Jellyfin server first; extend to
friends using separate Jellyfin servers after validating media identity and
cross-server authorization. Neither party invitations nor an intermediate server
may grant access to content owned by a third party.

## Current path, mapped from the repository

```mermaid
sequenceDiagram
    participant C as Jellyfin client
    participant R as Receiving Jellyfin
    participant P as Federation source provider
    participant O as Owning Jellyfin
    participant F as Receiving ffmpeg
    C->>R: PlaybackInfo for federated item
    R->>P: Resolve additional/current media sources
    Note over R,P: Stored primary Path can bypass provider metadata fetch
    P->>O: Peer/PlaybackInfo on cache miss
    O->>O: Check current sharing, call native PlaybackInfo over loopback
    O-->>P: Media information
    Note over P,O: Direct-mode token resolution runs alongside metadata
    P-->>R: Source URLs, streams, container
    R-->>C: Device-specific playback choice
    alt Raw playback through receiver
        C->>R: Request media/range
        R->>O: Token resolution then DirectStream
    else Receiving server transcodes/remuxes
        R->>F: Start ffmpeg
        F->>R: Read local federation gateway
        R->>O: Token resolution then DirectStream
    else Compatible client uses remote source
        C->>O: Authorized DirectStream
    end
    O->>O: Recheck sharing/token; relay native static stream over loopback
    O-->>R: Source bytes (receiver paths)
    R-->>C: Raw bytes or generated segments
    Note over C,R: First body write is NOT first rendered frame
```

Entry points and responsibilities:

| Area | Current implementation | Consequence to investigate |
| --- | --- | --- |
| Persisted source | `FederationLibraryManager.BuildStaticPath`, `TryPersistMediaStreams` | Existing DB metadata and paths may differ from newly synced entries; migration must be explicit. |
| Playback negotiation | `FederationMediaSourceProvider.GetMediaSources` | Concurrent metadata/path tasks still wait for all candidates, with a 20-second batch timeout. An offline alternate can hold up a healthy primary. |
| Remote metadata | `RemoteServerClient.GetPlaybackInfoAsync` | 15-minute cache; cold misses call owner, then owner calls native PlaybackInfo. Original cache lacked viewer/credential separation and concurrent miss coordination. |
| Access/tokens | `FederationPeerAccessService`, playback/session token services | Current access must remain authoritative at stream time; cached metadata is never proof of access. |
| Transport | `FederationStreamHandler`, controller `DirectStream` | Extra local HTTP hops, range retries and bandwidth pacing; do not remove correctness checks to reduce hop count. |
| Codec choice | Jellyfin device-profile negotiation and ffmpeg | Unsupported audio/video/subtitles can trigger expensive work. Need actual decision and timing evidence. |
| Existing social state | `FederationNowWatchingService` | Dashboard observation only; not a party coordinator. |
| Synchronization foundation | Jellyfin 10.11.6 controller SyncPlay contracts | Group join, ready, buffering, ping and playback commands exist. Client behavior and federated-item access need live verification. |

## Architecture rules

Separate the control path (identity, access, source selection, media description,
party commands) from byte transport. Make startup decisions explicit and measurable.
Avoid a single replacement controller containing all concerns.

- Proposed playback preparation service: resolve acting viewer, eligible source,
  media version, complete metadata and chosen delivery mode under a bounded deadline.
- Proposed media-description cache: viewer/credential-scoped until a clearly
  sanitized, access-independent descriptor contract exists; coordinate identical
  cold requests, bound entries, expire and invalidate predictably. Cache data must
  not share mutable per-request playback URLs or track selections.
- Proposed transport interface: preserve authenticated current access, HTTP status,
  HEAD, ranges, cancellation and retry integrity for the existing gateway first.
  Evaluate a shorter route only after benchmarking the baseline.
- Reuse Jellyfin's transcoding/session lifecycle initially. Do not build a new
  transcoder, disable probing blindly, or force direct play for unsupported devices.
- Party coordination carries playback state and content identity, never media
  bytes or reusable source credentials. Each participant opens an independently
  authorized stream. Independent user seeks/track choices must not share a stream
  cursor or accidentally share a transcode session.

## Phase 1 — produce an attributable baseline

1. Build a disposable two-Jellyfin fixture with synthetic media and ordinary/admin
   users, fixed software versions and recorded network conditions.
2. Record monotonic durations for client Play → PlaybackInfo response → first media
   request → first source byte → first segment → first rendered frame. Add source
   selection, metadata, token, ffprobe and ffmpeg-start subspans. Cross-machine
   clocks must not be subtracted without offset correction.
3. Use random trace IDs, finite stage/outcome labels and sanitized diagnostic
   exports. Never log media URLs, token queries, API keys or raw ffmpeg commands.
4. Collect at least 20 starts per representative case and publish p50/p95, failures,
   CPU, bandwidth and bytes fetched before playback. Separate cold and warm caches.
5. Matrix: H.264/AAC MP4 direct play; MKV remux; HEVC supported/unsupported client;
   audio conversion; subtitle off/text/burn-in; low/high bitrate; resume and seek.
   Include MP4 metadata at beginning/end to detect range/probe dependence.
6. Compare local-source control, federated direct and proxy paths at baseline LAN,
   controlled WAN latency/loss/throughput, and the real user connection when available.
   Never alter production traffic shaping or install a replacement on production.

Exit: attribute the reported delay to stages with reproducible traces. A faster
HTTP first byte alone is insufficient evidence that Play became faster.

## Phase 2 — make source preparation predictable

1. Coordinate identical metadata cache misses across client instances, isolate
   viewers and changed credentials, and test cancellation/failure recovery.
2. Bound cache memory; audit all token caches for credential scope, invalidation
   races, expired entries and concurrent startup behavior. Do not expand token
   lifetimes to hide latency.
3. Design primary-first resolution: a healthy chosen source must not wait for an
   offline alternate. Establish whether Jellyfin permits lazy alternate discovery;
   if not, use a bounded alternate budget without silently removing version choice.
   Explicitly selected alternates must remain selectable and fail intelligibly.
4. Persist verified stream metadata so existing and new items negotiate consistently.
   Audit invalidation/recreation and watch-progress preservation before migration.
5. Test all cold/warm, changed-credential, changed-source, revoked-access, missing
   metadata and older-peer paths before enabling new preparation behavior.

Exit: healthy selected-source startup is independent of an unrelated offline peer;
no redundant same-identity metadata requests and no cross-viewer cache results.

## Phase 3 — improve transport and codec startup

1. Use Phase 1 results to rank loopback hops, remote probe reads, token resolution,
   connection setup, remote read stalls and segment generation by actual cost.
2. Compare safe route alternatives under identical authorization and Range tests.
   Browser direct delivery needs reachability, HTTPS/mixed-content and client
   support checks; never expose a long-lived key as an optimization.
3. Establish a bounded startup deadline distinct from ongoing idle/read deadlines;
   cancellation must stop unused requests/transcodes. Avoid retries stacked across
   layers multiplying a 20-second timeout into a minute-long spinner.
4. Optimize metadata/probing/remux decisions before changing ffmpeg buffers.
   Decide any source-versus-receiver transcoding change separately, based on device
   profile, hardware capability, bandwidth, authorization and measurable benefit.
5. Bound any read-ahead cache by bytes/time; test seeks, eviction and cancellation.
   No shared mutable stream or unbounded whole-file prefetch for party viewers.

Exit: repeat the baseline matrix. Proposed initial targets under documented capable
hardware/network conditions: p95 ≤5 seconds for warm direct play, ≤10 seconds for
cold direct play/remux, and ≤15 seconds for supported hardware transcode. These are
engineering targets to validate, not promises for arbitrary media or connections.
Require no increased corruption, authorization failures or unsupported-codec errors.

## Phase 4 — watch parties on one receiving Jellyfin

1. Validate native SyncPlay using two real browser sessions and a federated item.
   Check user policy, library visibility and source-owner sharing for both viewers.
2. Add discoverable Create party / Join party entry points only for supported
   clients. Explain unsupported clients before playback starts.
3. Define owner controls, invites, member list, readiness, scheduled start, pause,
   seek, late join, reconnect and owner departure. Decide a bounded buffering policy
   so one disconnected participant cannot hold a room forever.
4. Let Jellyfin manage same-server group timing where supported. Test whether its
   behavior already satisfies each requirement before replacing a working part.
5. Test 2, 4 and 8 viewers with independent sessions and mixed delivery modes;
   report limits, aggregate bandwidth and transcode saturation. Resource admission
   must fail clearly rather than start an overloaded room.

Exit: play/pause/seek and late join work without ordinary playback regressions.
Proposed sync target: p95 position skew ≤500 ms after settling on stable connections;
measure client positions against a common monotonic reference, not just arrival of
HTTP commands. Record incompatible clients and codecs explicitly.

## Phase 5 — parties across separate Jellyfin servers

1. Resolve identity by owning server + native item + media version/edition/duration.
   A title/provider ID match alone cannot establish identical timelines. Refuse
   mismatched cuts/episode orders until a supported mapping exists.
2. Verify each participant server has a direct, authorized relationship to the
   owner. A friend who only imported the title cannot share it onward via a party.
3. Design a versioned peer party protocol with expiring invitations, authenticated
   members, room IDs, ordered command revisions, deduplication, stale-command
   rejection, bounded message sizes/rates and explicit protocol negotiation.
4. Start with one authoritative coordinator. Commands carry target position and
   scheduled execution time; estimate clock offset/RTT, handle jitter and reconcile
   drift without incessant seeks. Partition/reconnect must not replay stale seeks.
5. Keep local SyncPlay groups as adapters if their contracts support the bridge.
   Prove the adapter with a prototype before claiming cross-server compatibility.
6. Revalidate access at join and stream creation; test disable/unfriend/revoke while
   preparing and while watching. Define active-stream cancellation explicitly;
   never claim already delivered bytes can be revoked.

Exit: two independent servers, separate users, current owner consent, no onward
sharing, correct late-join/reconnect behavior and measured synchronization accuracy.

## Rollout, verification and handoff

- Keep work on a review branch. Preserve a default-compatible route and use explicit
  opt-in flags for experimental preparation/transport/party behavior.
- Land phases independently with meaningful regressions and benchmark evidence.
  Use `scripts/test.sh`; before a push use the existing full gate plus the new
  Jellyfin timing/party fixture. Existing Plex smoke tests are regression coverage,
  not evidence for Jellyfin client startup or party synchronization.
- Expand automation with a Jellyfin-only benchmark script and machine-readable
  stage distributions; preserve tool/image versions and sanitize exports.
- Require the two-server ordinary-user/admin matrix, range integrity, cancellation,
  source outages, concurrency and live client playback before streaming releases.
- No irreversible media changes. Version persisted changes, preserve progress and
  ensure rollback restores the old route without changing owner consent.
- Update the top of TODO with exact files, tests, metrics, unresolved decisions and
  next runnable step after every implementation stage.

## Work started in this session

- Added active-request-only metadata coordination; each caller retains its own
  cancellation. Cache identity now separates viewer, address and credential.
- Added debug timing for remote metadata and relay first body write. These are
  initial measurement points, not complete end-to-end tracing.
- Four focused tests passed: 16 concurrent callers issue one remote metadata
  request; cancelled waiter isolation; viewer/credential isolation; failure retry.
- Clean builds and all automated suites passed twice: 425 plugin, 60 Companion
  and 29 JavaScript tests per pass. The script refused a validation receipt because
  this plan/TODO changed during the run; rerun on frozen files before a push.
  No live latency improvement has been measured. Work is isolated on
  `work/jellyfin-streaming-redesign`; nothing has been pushed or released.
- Next executable step: build the two-Jellyfin client startup benchmark and capture
  cold/warm traces before deciding whether probing, negotiation or transport needs
  the first architectural replacement.

### Two-server benchmark progress

- `scripts/jellyfin_startup.py` now creates a unique two-container Jellyfin network,
  explicitly imports the source library and compares local/federated PlaybackInfo,
  first-byte and 64-KiB Range timings. It verifies actual bytes, includes four
  simultaneous requests and records DLL/image identities in sanitized JSON.
- First successful run: 20 samples per path, synthetic H.264/AAC MP4, admin identity,
  local container network. Federated p50/p95 metadata: 16.7/19.6 ms; first byte:
  16.0/26.4 ms. First measured federated use: metadata 75 ms, first byte 94 ms.
  Four simultaneous ranges passed byte integrity. This does not reproduce the
  user's WAN/codec-dependent 30–60-second delay and is not a before/after speed claim.
- A subsequent fresh fixture exposed a scan timing issue: a movie became visible
  before source container/video information was ready; streaming then failed with
  "Failed to find an appropriate file extension". Benchmark setup now waits for
  source analysis before import. Production behavior for imports during scanning
  needs its own regression and repair; do not assume the setup wait fixes users.
- `scripts/jellyfin_video_probe.cjs` adds optional Chromium video-frame callback
  timing with credentials passed only over stdin. Live validation passed: 20 HTTP
  samples per path plus four concurrent ranges, and three new Chromium instances
  per path. Federated first-frame callbacks measured 58.1–67.7 ms versus local
  58.3–69.2 ms. This is warm synthetic static playback, not jellyfin-web negotiation.
  No real WAN or transcode claim follows from these results.

### Codec conversion baseline

- Added forced native H.264/AAC MP4 conversion and verified decoded output is
  smaller than the source. Each probe uses a fresh play-session ID and browser.
- H.264 640×360 source: federated converted first frames 168.2–196.2 ms; local
  control 158.3–186.9 ms, three probes each.
- HEVC Main 10 1920×1080 SDR MKV source: federated converted first frames
  1171.0–1199.5 ms; local control 1170.3–1205.9 ms, three probes each. Jellyfin
  selected 960×540 H.264 output under the 1280-pixel ceiling/500-kbit target.
  Twenty raw HTTP samples and four concurrent Range reads also passed integrity.
- Reports: ignored `artifacts/qa/jellyfin-startup.json` and
  `artifacts/qa/jellyfin-startup-hevc.json`; each records the tested DLL hash and
  immutable container image identity. These are fresh synthetic fixtures, not
  production or WAN timing. Browser starts are measured after HTTP warmup.
- Added overall media-source preparation debug duration, alongside metadata and
  relay first-write durations. 47 focused startup/streaming regressions passed.
- Next: controlled latency/throughput and cold-cache scenarios; actual jellyfin-web
  device-profile negotiation; missing source metadata regression; offline alternate
  source timing. Do not conclude that production startup is fixed from LAN results.
- Final automated implementation gate passed with clean builds and 425 plugin,
  60 Companion and 29 JavaScript tests twice, plus the Python gate tests. Benchmark
  containers and private networks were removed. Final documentation was updated
  afterward; run push automation against the final committed snapshot when needed.
