# Repeatable validation and pushes

## Jellyfin-to-Jellyfin startup benchmark

After a Release build, run `python3 scripts/jellyfin_startup.py --samples 20`.
This creates two disposable Jellyfin servers on a unique container network and
loopback host ports, imports synthetic H.264/AAC media, and compares local and
federated metadata/first-byte/range timings. Four simultaneous requests also verify
the returned bytes against the source. It requires ffmpeg and a locally cached
Jellyfin image; `--image` selects that image without pulling it.

The JSON report defaults to `artifacts/qa/jellyfin-startup.json` and contains no
credentials or private URLs. Private fixture state remains in a mode-0700 system
temporary directory. Only generated containers/network are removed on completion.
This measures HTTP startup on a local container network using an admin identity;
it does **not** measure first rendered frame, client codec negotiation, transcoding,
WAN conditions or watch-party synchronization. The first measured request is not
guaranteed cache-cold after library scanning. See `STREAMING_REDESIGN.md` for the
remaining benchmark matrix.

For a real Chromium video-frame callback measurement, also pass
`--playwright-module /absolute/path/to/node_modules/playwright` and optionally
`--chromium /absolute/path/to/chrome`. `--browser-samples` defaults to 3 per path.
This uses a plain video element and the native static stream route; it does not
substitute for jellyfin-web negotiation or transcode testing. Browser credentials
are passed to the subprocess through stdin; never publish private fixture logs.

Add `--transcode` to compare actual local and federated software conversion to
H.264/AAC MP4. The probe verifies a smaller decoded frame, rather than trusting a
successful HTTP response. `--codec hevc10 --height 1080 --transcode-width 1280`
generates a 10-bit HEVC MKV and tests conversion with a 1280-pixel width ceiling;
Jellyfin may choose smaller output for the specified bitrate. HEVC cases skip the
raw browser probe and measure converted playback. Use a separate `--output` path
for each case to retain comparable reports. This is forced conversion of synthetic
SDR media, not HDR tone mapping or automatic device-profile negotiation.

## Build and release gates

From the repository root:

```sh
./scripts/test.sh          # Clean Release builds + both .NET suites and UI tests twice
./scripts/test.sh --all    # Also cross-build Windows and run disposable real Plex/Jellyfin checks
./scripts/push.sh          # Validate/reuse an exact recent full pass, then push this review branch
```

Commit the intended files before running `push.sh`. It never stages files, force-pushes,
merges, tags, or publishes a release. It refuses detached HEAD, dirty trees, and
`master`/`main`: pushing the latter automatically publishes the rolling Companion
release. Use a review branch until the Windows/Funnel/client release checks are done.

`push.sh` reuses a full success only when every tracked and nonignored untracked file,
the toolchain, and the local container image IDs match, and the result is less than
24 hours old. It checks that neither the branch nor commit changed while testing and
pushes the exact tested commit. A failed rerun invalidates the receipt. To force a new
run, use `test.sh --all` without `--reuse`. Receipts live in Git's private metadata;
builds and private runtime fixtures live in ignored `artifacts/qa/`.

## Prerequisites

The normal gate needs Python 3, .NET SDK 10 (plus .NET 9 runtime for Companion tests), Node/npm and Git. `DOTNET` can select an
SDK executable; otherwise PATH and `$HOME/.dotnet/dotnet` are checked. The scripts run
`npm ci`, so dependencies match `package-lock.json`. CI runs the same automated gate.

The full live gate additionally needs **Linux with usable FUSE**, Podman (preferred)
or Docker, ffmpeg with H.264/AAC encoding, and rclone. Set `CONTAINER_ENGINE`,
`FFMPEG_BINARY` or `RCLONE_BINARY` when necessary. An rclone binary can also be placed
at `artifacts/qa/tools/rclone`. Install these once; the gate does not change host
permissions or silently install system software.

Load the test images once:

```sh
podman pull docker.io/jellyfin/jellyfin:12.0
podman pull docker.io/plexinc/pms-docker:latest
```

`JELLYFIN_TEST_IMAGE` and `PLEX_TEST_IMAGE` can select pinned tags/digests instead.
The gate resolves the installed images to immutable IDs and records them in the
receipt. It never implicitly pulls updated images or uses existing server data.

## What the live gate does

It generates one synthetic H.264/AAC movie and two numbered episodes, creates uniquely
named disposable Jellyfin and Plex containers on loopback ephemeral ports, publishes
and runs an isolated Companion, and starts its managed rclone mount. It checks:

- Explicit library import, actual media information, source episode numbering.
- Mount reads, HEAD, normal/suffix Range requests and automatic mount restart.
- Outgoing third-party catalog exclusion, old/new token denial and cleanup after sync.
- Library deselection/reselection and preservation of catalog during a source outage.
- Actual Plex video/audio analysis and decoding media served through Plex.
- Refusal to share the imported Plex section onward.

Cleanup stops/removes only the newly named containers and their Companion process.
Private fixture data is retained for debugging under a mode-0700 directory. Runtime
logs contain generated owner access information: **do not upload fixture directories
or logs**. No production server, user library, fixed port or existing configuration is
used. The fixture PMS runs as container root to match the FUSE owner; this does not
validate different-user mount permissions.

The full gate does not pretend to test a Windows machine, WinFsp, the friend's real
Funnel, physical Plex clients, or the full two-server ordinary-user/admin matrix.
Those remain explicit release checks in `Companion/TODO.md`. Windows cross-compilation
proves a build succeeds, not that its host permissions and playback work.

## Explicit preview publishing

`./scripts/release-preview.sh` runs/reuses the full gate, pushes the review branch,
and publishes a GitHub **prerelease** attached to that exact commit. It requires `gh`
authentication and includes Windows/Plugin preview ZIPs plus checksums. It does not
replace `companion-latest`, change the plugin manifest, or publish a stable release.
Only run it when preview publication is intended; `push.sh` alone never publishes.

## Plex metadata and two-Jellyfin regression fixtures

After building the plugin in Release mode, run:

```sh
python3 scripts/federation_metadata_smoke.py
python3 scripts/federation_peer_smoke.py
```

These additional fixtures require Podman and the installed Jellyfin 12/Plex test
images above. `JELLYFIN_TEST_IMAGE` and `PLEX_TEST_IMAGE` select pinned local images.
Encoding/decoding uses Jellyfin's bundled ffmpeg inside a disposable container.
The fixtures bind fresh loopback ports, generate temporary credentials, and clean
up only their own containers, pod or network. Private runtime data stays under
mode-0700 `/tmp/federation-*-qa-*` directories; never publish it.

The Plex fixture verifies real MKV/MP4 sizes, codecs, subtitle/audio indexes,
series/episode hierarchy, selected-poster changes, raw playback, HEAD, suffix and
80 concurrent Range requests with eight workers. It asks Jellyfin to transcode
with audio track 2 and a one-second seek and decodes the result. It also verifies
admin/viewer outage hiding, cached-catalog preservation and stable-ID recovery.

The peer fixture establishes a real friendship between two Jellyfin servers and
checks Direct/Proxy playback, HEAD and seek ranges as an admin and ordinary viewer.
It checks viewer-specific access revocation, old playback URL rejection, server
disablement, outage hiding, cache preservation and stable-ID playback recovery.
These are API/ffmpeg checks; physical TV/Xbox clients and Windows/Funnel remain
separate validation.

## Private sharing integration

The automated gate also runs `private_sharing_smoke.py` on Linux against the
published Companion. It injects an isolated CLI executable through the test
process's PATH, without accessing the host's Tailscale daemon. It checks HTTP
owner authorization, failed login, discovered port/address, concurrent setup,
restart rebinding, refusal to switch an active private listener to public, and
verified removal of only Companion's owned listener. Runtime state is private
and removed afterward. Unit and DOM tests cover malformed configuration and
error recovery. This is simulated network CLI integration; it does not establish
a real two-account tailnet, issue HTTPS certificates or measure NAT/DERP bandwidth.
Those are required before promoting the private-sharing preview to stable.

## Original-file relay integration

After publishing the current Companion to `artifacts/qa/companion` and building
the plugin in Release mode, run `python3 scripts/federation_metadata_smoke.py
--companion-files`. It adds a real Companion and a disposable Plex API proxy
to the metadata fixture. The proxy forwards real catalog/artwork but returns
HTTP 402 for all media/transcode requests. The fixture maps discovered Plex
folders to read-only original files and checks the same Jellyfin playback,
transcode, Range stress and outage matrix, followed by peer revocation.
No video API calls may occur after original-file mode is enabled.

`DOTNET` selects an SDK with the .NET 9 ASP.NET runtime. The Companion-file
fixture uses the Plex image's normal initialization; `pms-docker:public` can
download the current server binary at startup. Other fixtures require an image
with the server binary already installed. Record the actual PMS version when
testing the public image. No Plex sign-in or account claim is performed; this
test does not establish paid-playback policy for a claimed account.
