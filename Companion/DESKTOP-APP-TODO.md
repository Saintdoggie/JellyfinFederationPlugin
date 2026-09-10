# Companion desktop app — handoff TODO

Status as of 2026-09-10. Source: `/var/home/cranky/Documents/JellyfinFederationPlugin-master`.
Read root `AGENTS.md`, `TODO.md`, and `Companion/TODO.md` first. This file tracks the
"real app" work: tray/background behavior, RAM tuning, autostart, and platform support.

## Review update — published as 0.0.159

See [DESKTOP-REVIEW.md](DESKTOP-REVIEW.md) for the review, security findings,
resource measurements and platform limits. The update supersedes older details
below: concurrent workstation GC is enabled (no forced collections); status
polling is 30 seconds and skips hidden pages; new installs require explicit
media-helper setup; Stop persists; credential ACLs and process-identity checks
are implemented; Linux installs have an application launcher. Native Windows
runtime and long soak checks remain open. Installers require a coordinated
release with SHA256SUMS.

Validation: clean automated gate passed (556 plugin + 106 Companion + 46 UI
tests, each suite twice; eight Python installer/QA checks). Windows GUI
cross-publish, Linux lifecycle smoke, responsive browser fixtures, and the
disposable Jellyfin/rclone/Plex media and authorization gate passed. See the
review for scope and remaining native-platform checks.

## Goal

The Companion download should behave like a normal desktop app on Windows and Linux:

- Start in the background, stay resident, and be manageable without a terminal.
- Windows: tray icon with status + controls, no console window, optional start-at-login.
- Linux: console/server process plus XDG autostart; browser dashboard is the UI.
- Low idle memory (workstation GC, no server GC heaps).
- One process per user; a second launch opens the running dashboard instead of duplicating.

## Landed in code (needs platform runtime validation)

- [x] `Companion/FederationCompanion.csproj` multi-targets `net9.0` and `net9.0-windows`.
  Windows target is `WinExe` + `UseWindowsForms` + `companion.ico`; RAM knobs are
  `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`,
  `RetainVMGarbageCollection=false`, `GCConserveMemory=5`.
- [x] `Companion/WindowsCompanionTray.cs` (`#if WINDOWS`): `NotifyIcon` menu with
  Open dashboard, Copy owner key, live Plex/media-folder/friends status,
  Start/Stop media folder, Start with Windows toggle, Open log folder,
  Open install folder, Exit. Double-click opens the dashboard.
- [x] `Companion/WindowsAutostartRegistration.cs` writes only
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no elevation, per-user).
- [x] `Companion/AutostartRegistration.cs` adds `LinuxAutostartRegistration`
  (XDG autostart `.desktop` in `~/.config/autostart`) and the shared
  `AutostartCommand.Build` (`"exe" --tray`).
- [x] `Companion/SingleInstance.cs`: per-user lock file plus a named-pipe
  "open dashboard" request. A second launch exits and pops the first dashboard.
- [x] `Companion/AppLog.cs`: 2 MB rotating `companion.log` beside the exe. Never
  logs credentials; owner key stays console/tray-only.
- [x] `Companion/Program.cs`: `StartAsync` + tray thread + `WaitForShutdownAsync`;
  `--tray`/`--background` quiet start, `--open`/`--no-browser`; Kestrel
  `AddServerHeader=false`; new endpoints:
  - `GET  /api/app/info` (version, port, uptime, working set, paths, autostart state)
  - `POST /api/app/autostart` `{enabled}`
  - `POST /api/app/open-dashboard`
  - `POST /api/app/exit`
  - `POST /api/media-mount/stop`
- [x] `LocalMediaMountService.StopMountAsync`/`HasOwnedProcess`; stopping also
  cleans up an rclone left by a previous Companion run via the PID marker.
- [x] `Companion/wwwroot/index.html`: "Companion app" card with live version /
  memory / uptime, sign-in toggle, open dashboard, stop media folder, exit.
- [x] `Companion/install.ps1`: starts the WinExe detached with `--open`, creates
  Start Menu + Desktop shortcuts, prints the SmartScreen/permissions notice.
- [x] Release pipeline: `.github/workflows/companion-release.yml` publishes
  `-f net9.0-windows` for win-x64 and `-f net9.0` for linux/osx; `scripts/qa.py`
  publishes the matching TFM for the Linux live gate and the Windows cross-build.
- [x] Tests: `Companion.Tests/CompanionAppShellTests.cs` (launch args, autostart
  command/XDG entry, runtime URL, log format, single-instance pipe round trip,
  unsupported-autostart behavior) and 3 new `Tests/companion-ui.test.js` cases.
- [x] `Companion/README.md` documents the app, tray, and exactly what it does on
  the machine: localhost listener, read-only rclone mount, state/log files,
  HKCU autostart, one UAC prompt for WinFsp, SmartScreen/antivirus expectations.

## Verified by cross-build (not a Windows runtime test)

- [x] `dotnet publish -f net9.0-windows -r win-x64 --self-contained
  -p:PublishSingleFile=true` succeeds; the produced exe is PE subsystem 2
  (GUI/WinExe, no console window) with `companion.ico` embedded and is a single
  self-contained ~62 MB file.
- [x] Local gotcha: this workstation's NuGet cache symlinks
  `microsoft.netcore.app.host.win-x64/.../apphost.exe` and `singlefilehost.exe`
  to `/var/mnt/hdd1/...`. Container builds must mount that path or the publish
  fails with `MSB4018 / FileNotFoundException`. Host/CI restores are unaffected.

## Remaining — Windows

- [ ] Run the real Windows desktop build: tray icon renders (all DPI scales),
  menu actions work, `WinExe` has no console window, `Exit` stops Kestrel +
  rclone cleanly, `--tray` autostart entry starts hidden at sign-in.
- [ ] Verify the HKCU Run entry appears/disappears with the toggle and survives
  a Companion self-update (the updater restarts the exe; the Run value stores
  the exe path, so it must still point at the updated install).
- [ ] SmartScreen/Defender first-run experience: record the exact prompts for
  README accuracy. Consider code signing if the project owner wants to remove
  the "Windows protected your PC" step (cost/identity decision, not a code fix).
- [x] Implement Windows current-user ACLs on state/config before writing secrets.
  Actual Windows runtime verification remains required.
- [ ] Measure idle working set on Windows after 24 h with the mount running and
  record it in the validation log. Target: materially below the previous
  console build; `GCConserveMemory=5` should release memory between syncs.
- [ ] Confirm the tray survives Fast User Switching / RDP disconnect and that a
  second user gets their own instance (lock file is per-user temp).

## Remaining — Linux

- [x] `LinuxAutostartRegistration` verified against the published Linux apphost
  in a disposable container: `POST /api/app/autostart {enabled:true}` writes
  `$XDG_CONFIG_HOME/autostart/federation-companion.desktop` with
  `Exec=".../FederationCompanion" --tray`; disabling removes it; `app/info`
  reports `autostartSupported`/`autostartEnabled` correctly. Unit tests cover
  `XDG_CONFIG_HOME` and the entry contents. Desktop-environment (GNOME/KDE
  login) confirmation still outstanding.
- [x] Single-instance verified cross-process in the container: a second
  `FederationCompanion --open` exits in ~0.2 s, the first logs
  "Opening the dashboard for a second launch." and keeps the only listener.
- [x] `POST /api/app/exit` stops the host cleanly (exit 0) and the log records
  the stop.
- [ ] Decide whether Linux gets a tray icon too. Today only Windows has one;
  Linux is a console/server app with the browser dashboard. If a tray is wanted,
  it needs a StatusNotifier/AppIndicator implementation (extra dependency) —
  do not fake it with WinForms.
- [x] Linux ZIP installer creates a desktop launcher; README covers desktop
  autostart and headless systemd operation. Native desktop login remains unverified.
- [ ] Validate single-instance behavior across login sessions (the lock file is
  in `$TMPDIR`, so a systemd user service and an SSH shell must not both run).
- [ ] macOS: `IAutostartRegistration` reports unsupported. Add a launchd agent
  if macOS background autostart is wanted; otherwise keep the console behavior.

## Remaining — cross-platform

- [ ] `--open` on a headless Linux host logs an xdg-open failure to stdout; keep
  it non-fatal (already caught) but consider suppressing the browser attempt
  when no display is detected.
- [ ] App card uptime/memory poll is 15 s; confirm it does not keep the log file
  busy or cause churn on low-power hosts.
- [ ] Add a small integration test that starts the published binary, checks
  `/api/app/info`, exercises autostart on Linux (XDG file), and shuts down via
  `/api/app/exit`. Current tests cover the units, not the process lifecycle.

## Validation commands

```sh
./scripts/test.sh          # plugin + Companion + UI suites twice
./scripts/test.sh --all    # plus Windows cross-build and disposable live gate
```

Companion-only during iteration:

```sh
podman run --rm -v "$PWD":/src:z -v "$HOME/.nuget/packages":/root/.nuget/packages:z \
  -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet test Companion.Tests
npm test
```
