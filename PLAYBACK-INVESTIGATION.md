# Playback investigation — 2026-09-17

## Baseline and boundaries

- User requested investigation, fixes, codec/device coverage and stress testing; subsequently requested notes sufficient for review and rollback.
- Source: `/var/home/cranky/Documents/JellyfinFederationPlugin-master`, branch `master`.
- Baseline commit: `7a104359a8a899268fbe660ae21af56d336ed5c3`. Initial `git status --short` was empty.
- No deployment, production configuration changes, commits, pushes or releases authorized/performed.
- Physical Android/Opera GX testing is not available yet. Synthetic profiles and in-process relay benchmarks cannot establish hardware decoder performance.

## Investigation activity

1. Located source and other working copies; read source `AGENTS.md`, `TODO.md`, `README.md`, project/package definitions and relevant tests.
2. Ran `git status --short`, `git branch --show-current`, `git rev-parse HEAD` (read-only baseline checks).
3. Two read-only reviews traced media-source/codec negotiation and relay behavior/test tooling.
4. Verified parent directories before creating this requested log.

## Preliminary findings (not a reproduced diagnosis of the reported phone)

- Capped gateways request H.264/AAC MP4 while provider/static metadata can still describe original HEVC/AV1 MKV. Local Jellyfin makes client compatibility decisions using that metadata.
- Auto static paths may select an alternate encoding after source metadata has already been advertised.
- Transcode builders budget the entire WAN cap for video, then add 256 kbps audio; the relay throttles all bytes at the original cap, leaving no audio/mux headroom.
- WAN Auto currently removes caps above 50 Mbps and imposes a 10 Mbps floor below that: neither proves the selected source fits the connection.
- Relay resume handling needs scrutiny for ignored Range requests, truncated responses, representation continuity and finite byte budgets.
- Relay allocates a 256 KiB buffer per request; no dedicated stress harness existed in the reviewed fixture.

## Changes and validation

- `PLAYBACK-INVESTIGATION.md`: this user-requested activity, evidence and rollback log.
- `Services/FederationLibraryManager.cs`: two expression changes reserve 256,000 bps audio plus 5% mux/rate-variation headroom from the total cap. At 12 Mbps, video becomes 11,144,000 bps instead of 12,000,000. This fixes budget accounting, not every source of buffering; no codec/Auto routing behavior is changed.
- `Tests/FederationStreamPathTests.cs`: seven parameterized cap-budget cases (1/2/10/12/17/100/int.MaxValue Mbps) check both builders; existing exact URL expectations updated. A temporary edit to the test's URL array was immediately reversed before execution (no resulting difference).
- `Services/FederationStreamHandler.cs`: lazy ArrayPool rental replaces fresh 256 KiB allocation per relay; return with clearing in finally, retain 256 KiB read bound and existing routing/retry behavior.
- `Tests/FederationStreamHandlerTests.cs`: six bounded stress cases (1/4/8 concurrent relays, completion/cancellation), generated request-specific bytes and verifying/counting sinks, no whole-media accumulation; synchronized test logger writes. Completion uses two rounds of 8 MiB + 123 bytes per stream; cancellation occurs after all streams write 256 KiB. Deadline 30 seconds, wait bound 35 seconds.
- `Tests/RemoteServerClientPlaybackTests.cs`: four metadata roundtrip cases for H.264 8-bit, HEVC Main10/HDR10, AV1 10-bit, VP9; container/profile/level/frame rate/color/audio-channel preservation. This does not test actual device decoding or playback decisions.

### Commands/results so far

All commands run from the source directory; `dotnet` below means `/var/home/cranky/.dotnet/dotnet`. Build outputs are generated locally only.

- `dotnet build JellyfinFederationPlugin.csproj --no-restore`: passed, zero warnings/errors.
- `dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj --no-restore --filter FullyQualifiedName~FederationStreamHandlerTests --logger "console;verbosity=detailed"`, same command without logger, and `--filter FederationStreamHandlerTests --logger "console;verbosity=detailed"`: each passed 22/22.
- Final Debug stress completion round-two observations: concurrency 1/4/8 approximately 46.87/174.50/334.53 MiB/s aggregate; process-wide allocations 15,896/58,560/383,840 bytes. Synthetic generation/verification/scheduling and shared pool warmup dominate these numbers. No before/after comparison or real network/decoder benchmark is claimed.
- `dotnet build Tests/Jellyfin.Plugin.Federation.Tests.csproj --no-restore`: passed, zero warnings/errors.
- Metadata test initial hand-assembled JSON draft failed four cases; switched the fixture to serializer-generated JSON. Then `dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj --no-restore --filter FullyQualifiedName~RemoteServerClientPlaybackTests`: 30/30 passed. The initial focused filter was `FullyQualifiedName~GetPlaybackInfoAsync_RoundTripsDeviceCompatibilityMetadata`.
- `timeout --kill-after=10s 120s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj --no-restore --filter FullyQualifiedName~CappedTranscodeBuilders`: before production edit, all seven cases failed (12 Mbps video + 256 kbps audio exceeds budget); after the two-expression fix, all seven passed.
- `git diff --check`: passed during delegated reviews and final combined review.
- `timeout --kill-after=10s 300s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj -c Release --no-restore && dotnet build JellyfinFederationPlugin.csproj -c Release --no-restore`: 595/595 plugin tests passed; Release build zero warnings/errors.
- `timeout --kill-after=10s 120s npm test`: 46/46 UI tests passed. No packages installed or upgraded.
- `timeout --kill-after=10s 120s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~FederationStreamPathTests' --logger 'console;verbosity=detailed'`: 63/63 passed, explicitly including seven budget cases and existing uncapped/audio path cases.
- `timeout --kill-after=10s 300s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj -c Release --no-build --no-restore && git diff --check`: repeated complete plugin suite 595/595 passed; whitespace check passed.
- Reviewed `git diff --stat`, source/test diffs and `git status --short`: exactly five tracked source/test files changed plus this untracked log. No release metadata or deployed files changed.
- No dedicated lint/typecheck script found. C# Release compilation provides type checking; an additional repository-specific lint command must be supplied if required. Companion backend tests and disposable two-server release gate were not run because this pass changes neither Companion backend nor deployed services.

### Arithmetic behind updated expectations

The previous fixed query expectations represented the entire cap as video. New video targets are `capMbps * 950,000 - 256,000`: 10 Mbps -> 9,244,000 bps; 12 -> 11,144,000; 17 -> 15,894,000. Audio remains 256,000 bps, so video plus audio equals 95% of relay capacity. These are derived from the budget policy, not tuned to make tests pass. This is encoder target accounting; 5% is not a measured guarantee against every VBR peak or server bottleneck.

### Intentionally deferred

Representation/metadata binding, Auto source changes, WAN quality-floor policy and relay range/timeout findings require separate targeted reproduction and design. Changing all of them together would obscure causality and make rollback riskier. The present patch is not a verified fix for Opera GX. Physical-device/client-profile and two-server integration coverage are still outstanding.

## Second research pass

### Scope and environment checks

- User requested more research. No additional production edits in this pass; added negotiation tests to the existing `Tests/RemoteServerClientPlaybackTests.cs` only, plus this log.
- `command -v adb; command -v emulator; command -v waydroid; command -v chromium; command -v google-chrome`: adb and Waydroid available, no listed emulator/desktop browser command found.
- `adb devices -l && waydroid status`: no attached devices; Waydroid session STOPPED. ADB automatically started its local daemon on port 5037. `adb kill-server` subsequently stopped that daemon successfully. Waydroid was not started, no browser installed, no phone data accessed.
- `git status --short` confirmed earlier five-file patch and this log were preserved. One delegated representation-review tool execution aborted and supplied no result; controller path was read directly instead. Other read-only reviews inspected local dependency XML, bandwidth logic and exact upstream sources.

### New evidence

1. Actual Jellyfin 12 StreamBuilder, not a mocked codec selector, correctly chooses H.264 fallback when synthetic profiles exclude HEVC/AV1. It allows supported codecs at low bitrate and rejects 20 Mbps direct play with an 8 Mbps client budget. This narrows suspicion toward real client capability reporting, inaccurate representation metadata, route bandwidth, or encoding/decoding performance—not a general failure to recognize those codec names.
2. The initial synthetic matrix enabled a direct-stream branch disabled by the real Jellyfin API. Exact package provenance is Jellyfin Model 12.0.0, upstream commit `6c073e19ddf604b2369c638716164fdab4c952dc`. At that commit, `MediaInfoHelper.SetDeviceSpecificData` disables `EnableDirectStream` while retaining stream-copy permissions. Tests were corrected accordingly. Unsupported MKV now selects MP4 through the transcode pipeline with `ContainerNotSupported`; `PlayMethod.Transcode` does not prove video re-encoding, and equal input/output codec names do not prove ffmpeg stream copy.
3. `Services/RemoteServerClient.cs:1216` samples approximately 5 MB from peer to local server. It does NOT measure the local-server-to-phone route. A fast server-to-server link says nothing about a poor mobile/Wi-Fi connection.
4. `Services/FederationStreamHandler.cs:658` maintains pacing counters per HTTP relay; `Services/WanBandwidthMonitor.cs:85` stores one peer cap without reserving capacity for concurrent sessions. Four requests capped at 12 Mbps can collectively approach 48 Mbps. No aggregate capacity fix made.
5. `Configuration/FederationPluginController.cs:2761` applies video caps and calls the H.264/AAC MP4 gateway builder, while `Services/FederationMediaSourceProvider.cs:363` retains original metadata. This is a confirmed mismatch between advertised source description and requested gateway representation. Actual emitted bytes, double transcoding, HDR tone mapping, subtitle/track-index effects and the reported phone failure still require an integration capture; no claim of actual ffmpeg reproduction is made.
6. Browser codec support is not the same as smooth or hardware-accelerated playback. MediaCapabilities reports `supported`, `smooth`, `powerEfficient`, but even the latter may be optimistic until playback statistics exist. Do not force an Opera user-agent codec blacklist based on these tests.

### External references reviewed

- https://jellyfin.org/docs/general/clients/codec-support/ — video support depends on client/device/version; HDR, audio, containers and subtitles can independently require conversion. The Android column is not proof of Opera GX support.
- https://developer.mozilla.org/en-US/docs/Web/API/MediaCapabilities/decodingInfo — separate support/smoothness/power-efficiency signals and their limitations.
- https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/MediaBrowser.Model/Dlna/StreamBuilder.cs — lines 26, 633–642, 804–818, 881–887, 1389–1398 explain direct-stream selection and retained source container.
- https://github.com/jellyfin/jellyfin/blob/6c073e19ddf604b2369c638716164fdab4c952dc/Jellyfin.Api/Helpers/MediaInfoHelper.cs — normal API direct-stream option handling.

### Test additions and complete command history

`Tests/RemoteServerClientPlaybackTests.cs:135`: ten cases covering two synthetic capability profiles × three codecs, two unsupported-container controls and two bitrate controls. Real remote metadata parser and real StreamBuilder; only ITranscoderSupport mocked. This is decision testing, not an attached device, actual Opera profile, ffmpeg run or new reproduced production bug.

All commands from source directory, `dotnet` expanded as in the earlier section:

- Twice: `timeout --kill-after=10s 120s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj --no-restore --filter FullyQualifiedName~GetPlaybackInfoAsync_StreamBuilderNegotiatesSyntheticCapabilities --logger 'console;verbosity=detailed'`. Initial compile failed (source list needed array; target codec properties are lists). Corrected draft: 8 pass, 2 container assertion failures. Capturing the observed retained MKV yielded a passing characterization but did not match normal API behavior; superseded below rather than claiming a remux fix.
- `timeout --kill-after=10s 120s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj --no-restore --filter FullyQualifiedName~RemoteServerClientPlaybackTests --logger 'console;verbosity=detailed'`: 40/40 passed before normal-API option correction.
- `git diff --check && git diff --stat && git status --short && git diff -- Tests/RemoteServerClientPlaybackTests.cs`: clean whitespace and reviewed changes.
- `timeout --kill-after=10s 300s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj -c Release --no-restore --logger 'console;verbosity=normal'`: 605/605 passed before normal-API option correction.
- After exact-source review, set EnableDirectStream=false while retaining copy permissions; MKV expectations now Transcode/MP4. No production change.
- `timeout --kill-after=10s 300s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj -c Release --no-restore && dotnet build JellyfinFederationPlugin.csproj -c Release --no-restore && git diff --check`: 605/605 passed, build zero warnings/errors, clean whitespace.
- `timeout --kill-after=10s 300s dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj -c Release --no-restore`: repeated after final test edit, 605/605 passed.
- No additional lint command has been provided; compilation validates C# types. UI source untouched; earlier 46-test UI result remains the previous pass's result, not a rerun here.
- Repository gate `timeout --kill-after=15s 900s ./scripts/test.sh` (automated scope, no live services): npm ci installed 38 packages, 8 script tests OK, clean Release builds, both .NET suites twice (605 plugin + 106 Companion each), UI 46 twice; PASS receipt written to `.git/federation-qa.json`. All local; no deploy or push.

### Recommended next experiment (not performed)

Use the same failing item on the same phone/network in Opera GX and Chrome/Jellyfin app, first with subtitles off, then on; record phone/browser versions, actual Play Method/transcode reasons, selected source codec/profile/bit depth/HDR/audio, target codec/bitrate, encoder fps/speed and dropped frames/buffering. Compare Direct and Proxy one at a time and then 1/4 concurrent viewers only on a designated disposable/test instance. Collect only sanitized statistics, not tokens, raw playback URLs or unredacted logs. Bind media-source metadata to a negotiated immutable representation before changing routing; simply relabeling HEVC as H.264 would invent unknown dimensions/stream indexes and is not a sound fix. No deployment or restart has been done.

## Third pass — deterministic representation fix

### Hypothesis test (added first, observed failing)

`Tests/FederationStreamPathTests.cs` `GetMediaSources_CappedWan_MatchesAdvertisedMetadataToTheCappedRepresentation`: Direct-mode peer, Manual 12 Mbps/1080p cap, remote PlaybackInfo returning mkv/HEVC Main10/HDR10 59 Mbps + EAC3 5.1. Asserts the provider-emitted source advertises container `mp4`, `h264` video, total bitrate within the 12.256 Mbps budget, and `Size == null` (transcoded size unknowable). First two failures were fixture wiring (shared handler also answered the token POST; then a mid-edit truncation was reconstructed). The genuine observed failure: `Assert.Equal("mp4", ...)` received `"mkv"` — proving the advertisement described the original file, not the capped representation the path delivers.

### Production fix (provider only)

`Services/FederationMediaSourceProvider.cs`: the emitted source now comes from new `DescribeDeliveredSource(...)`. Proxy mode, audio, and Direct mode without `capMbps` keep the remote's live metadata unchanged. A capped Direct path advertises `mp4`, `h264` video at the same budget the loopback builder encodes (`cap × 950,000 − 256,000`), AAC audio capped to 256 kbps stereo, height/width clamped to `maxHeight` when known, and unknowns (bit depth, profile/level, pixel format, HDR color fields, Size) dropped rather than guessed. Jellyfin 12's `MediaStream.VideoRange`/`VideoRangeType` are get-only, so fresh stream objects are built; no attempt is made to invent values. Auto/fit ranking still uses the ORIGINAL metadata (unchanged behavior); only the client-facing description is bound to the delivered representation. Known limits: stamped static-path Auto selection may still swap encodings after negotiation (separate finding, deferred); actual peer gateway bytes still need an integration capture.

### Third-pass commands/results

- `dotnet test ... --filter 'FullyQualifiedName~GetMediaSources_CappedWan_MatchesAdvertisedMetadataToTheCappedRepresentation'` (detailed): failed as designed (first empty-list due to fixture handler, then `"mkv"` vs `"mp4"`), passed after the provider patch.
- First build of the patch failed with real API mismatches: `int?` vs `long` on `BitRate`/`Channels`/`Bitrate`, get-only `VideoRange`/`VideoRangeType`, nonexistent `DisplayTitle` (checked in the local Jellyfin.Model 12 XML), one duplicated `downscale` local from piecemeal edits, one duplicated `/// <inheritdoc />`. All corrected; final build 0 warnings/0 errors.
- `timeout --kill-after=15s 900s ./scripts/test.sh`: full automated gate passed twice — npm ci + 8 script tests, clean Release builds, plugin 606/606 twice, Companion 106/106 twice, UI 46/46 twice, `git diff --check` clean, PASS receipt `.git/federation-qa.json`.

### Third-pass limitations

- This fixes what PlaybackInfo ADVERTISES; it does not prove the peer's ffmpeg output matches, that the phone decodes the result smoothly, or that no double transcode occurs end to end.
- No deployment, restart, commit, push, or release. Test-only server handlers; no production or deployed configuration touched.

## Fourth pass — UI polish (badge + lazy loading), 2026-09-17, post-0.0.163

### Scope and requests

- User asked for: plugin UI screenshots, a "really good and crisp" federation badge with NO gradients, and Catalog/Downloads converted to lazy infinite scroll (no "Load more" button).
- Baseline: commit `a86e80d` (0.0.163). Dirty files at end of this pass: `Web/federation-badge.js`, `Configuration/configPage.html`, `Tests/federation-ui.test.js`. Nothing committed, pushed, or deployed.

### Badge changes (`Web/federation-badge.js` only, `.federation-badge-corner` rule)

- Removed `linear-gradient(145deg,rgba(20,25,34,.94),rgba(5,8,13,.82))` → solid `background:rgba(8,11,16,.92)`.
- Border `rgba(255,255,255,.18)` → `.28`; shadow `0 2px 8px rgba(0,0,0,.45)` → crisper `0 1px 4px rgba(0,0,0,.5)`; `backdrop-filter:blur(5px)` removed; svg 58%/`.98` → 56%/`1`.
- Earlier in the pass the `.fed-storage-intro` rule in `Configuration/configPage.html` was also changed from a gradient to solid `rgba(0,164,220,.09)`.
- New regression test "badge styling is solid and crisp, with no gradients" asserts no gradient/backdrop-filter, solid background and 1px border in the rule.
- Visual proof: headless Chromium fixture (`/tmp/opencode/ui-harness/badge-check.mjs` + `badge-fixture.html`, script injected via `addScriptTag`) — computed style confirmed `backgroundImage:none`, `backgroundColor:rgba(8,11,16,0.92)`, `1px solid rgba(255,255,255,0.28)`, `backdropFilter:none`; screenshots `/tmp/opencode/ui-shots/04-badge-polished.png`, `05-badge-on-card.png`. An earlier attempt failed because the fixture referenced a non-existent local `federation-badge.js` copy (`ERR_FILE_NOT_FOUND`); no product change involved.

### Lazy loading changes (`Configuration/configPage.html` + tests)

- Removed both "Load more" anchors and their `catalog-more`/`browse-more` case handlers and the stale `#fedCatalogMoreWrap` JS block; added `#fedCatalogSentinel` / `#fedBrowseSentinel` sentinel divs with `.fed-lazy-sentinel` styling.
- `armCatalogSentinel(hasMore)` / `armBrowseSentinel(armed)` manage IntersectionObservers (rootMargin 480px 0px), with disconnect/rearm on tab switches, series drill-down, exhaustion, and page lifecycle; scroll/resize fallback when IntersectionObserver is unavailable; request epochs reject stale responses; loading guards prevent duplicate pages; errors suspend auto-loading until explicit retry; Downloads follows forward cursors through short/empty filtered pages and stops on exhaustion/no-progress.
- Bounded 60-item pages. During the pass, defects found and fixed: a broken `case 'download-view':` switch label (accidental deletion), an unarmed catalog sentinel (`armCatalogSentinel()` called with no argument after signature change — would have silently disabled lazy loading; caught and covered by behavioral tests), zero-height sentinel, and reset/filtered-page races.
- Test coverage replaced brittle regex-only assertions with mocked-behavior harnesses (fake IntersectionObserver, deferred fetches): stale-response rejection, exhaustion, error-retry, tab/page pause-rearm, nested-scroll fallback cleanup, repeated-page dedup, forward-cursor Downloads paging, catalog sentinel arming/firing/in-flight dedup. UI suite: 46 → 65 tests, all passing.
- Mid-pass note: a delegated rework briefly reported "64 passed"; the final on-disk suite is 65/65 — trust the final gate, not intermediate counts.

### Commands/results (final gate, this pass)

- `npm test`: 65/65 UI tests.
- `./scripts/test.sh`: PASS — 8 script tests, clean Release builds, plugin + Companion suites twice, UI 65 twice, `git diff --check` clean; receipt `.git/federation-qa.json` (passed, automated scope).
- Live-server screenshots via `/tmp/opencode/ui-harness/config-tabs.mjs` (Playwright, login ai@127.0.0.1:8096): `10-catalog-tab.png`, `11-downloads-tab.png` — both tabs render with no Load more button. IMPORTANT: the live server still runs released 0.0.163; these screenshots show layout only, not the new lazy-load code, which is validated by the jsdom behavior tests until a deploy is approved. (Deploying to the container `fed-ui-preview` was attempted and reverted as unable to validate CSS/data-less rendering; container may still exist locally, harmless.)
- One unrelated pageerror ("Response") appeared in the live config page console; present independent of these changes, worth watching separately.

### Rollback procedure (this pass)

Nothing committed. `git diff Web/federation-badge.js` → badge rule revert; `git diff Configuration/configPage.html` + `git restore Tests/federation-ui.test.js`-style reversal for lazy loading (test file contains only additive tests; configPage changes are self-contained to sentinel markup/JS). Or `git restore --source=a86e80d -- Web/federation-badge.js Configuration/configPage.html Tests/federation-ui.test.js` since no other work depends on these hunks yet.

## Rollback procedure

No rollback commands have been executed. Review `git diff` and `git status --short` first. For each tracked source/test file listed here, use `git restore --source=7a104359a8a899268fbe660ae21af56d336ed5c3 -- <file>` only if it still contains exclusively this session's changes; otherwise reverse the relevant hunks manually to preserve later work. Copy this log elsewhere before removing it if the record should survive rollback. Build/test outputs are ignored generated artifacts, not deployed binaries.
