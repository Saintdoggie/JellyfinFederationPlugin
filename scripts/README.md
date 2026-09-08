# Repeatable validation and pushes

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
