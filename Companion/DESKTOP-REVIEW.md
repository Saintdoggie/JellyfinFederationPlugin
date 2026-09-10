# Companion desktop and recent security review — 2026-09-10

Local review of release `666f61c` and the existing uncommitted desktop changes. These changes are not a published release. The watch-progress migration requested by the owner remains planning-only in the root TODO.

## Changes from the review

- Removed forced blocking full collections and finalizer waits after startup, sync and mount actions. RSS includes native memory and mapped assemblies; it is not a useful trigger for repeated full GC. Kept workstation GC, enabled concurrent GC, and explicitly emitted `System.GC.ConserveMemory=5` in runtime configuration.
- Used the slim web host and pooled outbound clients. Media has its own connection pool so long-running streams cannot consume the eight control-plane connections. Helper memory buffering is 4 MB per open file; disk-cache cleanup targets 2 GB and one-hour age. Open files may exceed the disk target.
- Dashboard status polling is 30 seconds, skips hidden pages and prevents overlapping requests. Tray refresh is 30 seconds and when opening its menu; sign-in startup does not show a balloon.
- Fresh installs do not download or launch media helpers automatically. The dashboard describes optional rclone/WinFsp installation before the owner enables it. Existing installs with saved auto-mount enabled continue to restore their mounts.
- Stopping the media folder persists `AutoStartMediaMount=false`, preventing the worker or next launch from reversing the owner's choice. Starting it explicitly restores the preference on success.
- Credential/config files are restricted before writing: current-user Windows ACL or Unix 0600. This is access control, not encryption. Same-user processes and administrators remain trusted.
- Second-launch IPC requires the same OS user and has a read deadline. A persistent per-user lock avoids TMPDIR/login-session mismatches and is not unlinked during disposal. Browser opening uses argument lists and drains redirected output.
- Mount cleanup validates PID, process start time and executable together. A bare numeric marker from an older install is insufficient to kill an orphan; the owner may need to stop that old helper manually once. The installer stops only its own Companion executable and a verified helper; updater scripts leave helper shutdown to the host.
- Update scripts use unique filenames, quote Unix paths, reject batch-expansion paths on Windows, stop on copy failure, and restart quietly. Installers verify release SHA256SUMS before extraction. Signing/authenticated update metadata is still a separate release concern.
- Linux installer adds an XDG application launcher, supports headless setup, validates desktop-entry quoting, and refuses unsupported architectures. Desktop autostart and headless systemd instructions are documented. No Linux tray or ARM build is claimed.
- Storage has compact comparison cards, visible selected states, clearer space totals, accessible selection buttons and a useful empty state. Existing show/season grouping and exact-ID deletion confirmation remain. The UI makes the outstanding watch-progress limitation visible.

## Recent security implementations inspected

| Area | Assessment and evidence |
| --- | --- |
| A1 inbound friend-request SSRF | Incomplete in the previous implementation: a public-looking hostname could resolve to a private IP. Callback transport now resolves and validates addresses, connects directly to those validated IPs, disables proxies and redirects, and reads headers without buffering an arbitrary response body. PublicCallbackConnection tests cover private, metadata, mapped, multicast, unspecified, NAT64/6to4 and mixed answers. Owner-initiated LAN friendship is unchanged. |
| A2 peer identity on notices | Caller identity comes from the authenticated peer and is checked against claimed federation IDs; pool and remote-user-access regression suites pass. |
| A3 download purpose and download permission | v2 stream MAC includes download purpose; download service/controller checks ordinary-user downloading policy. Stream/download tests cover turning a play URL into a download and denied users. |
| A4 client credential exposure | Per-user playback and image flows use scoped capability tokens rather than forwarding session tokens to media clients; playback-token, stream-path, image-provider and remote-client tests pass. |
| A5 downloaded/imported ownership | Download origin markers and peer/catalog/quality filters prevent onward sharing of imported titles. Existing download, ownership, catalog and export tests pass; real fixture checks are recorded below. |
| A6 catalog maps | Federation ID endpoints require authenticated access; catalog/UI tests pass. |
| A7 proxy capability expiry | New v2 signatures expire and bind user/item/media/download purpose. **Legacy v1 play signatures remain accepted without expiry for compatibility.** This is not a complete expiry migration. They still require current enabled-peer credentials and stream authorization. Retiring v1 needs a planned persisted-path/client migration. |
| A8 bounded inbound state | Existing pending-request/icon limits and their tests remain. These are not a general rate limiter or a whole-server resource-exhaustion guarantee. |
| B4 / series blocks | Per-user blocked IDs round-trip and ancestor series/season blocks apply through the cache at stream time; remote-access and stream-path tests pass. |
| Windows process/secret changes | Earlier numeric-PID-only cleanup and inherited credential permissions were incomplete. Fixed as described above. Actual Windows ACL, UAC, tray, reinstall and Defender behavior still require Windows runtime testing. |

## Validation

- Clean plugin Release build: zero warnings/errors. Plugin suite: 556 passed. Companion suite: 106 passed. Browser JavaScript suite: 46 passed. Installer/QA Python suite: 8 passed. The clean automated gate passed: plugin, Companion and JavaScript suites each passed twice (708 tests per pass), plus eight installer/QA Python checks.
- Self-contained Linux x64 and Windows x64 publishes succeeded; Windows output is PE GUI subsystem, not console. Cross-compilation does not validate native Windows operation.
- Chromium fixtures at 390, 1366 and 1920 px: Storage selection/totals, no horizontal overflow, and permission disclosure usable before owner unlock. Screenshots are local ignored artifacts under `artifacts/desktop-review/`.
- Isolated Linux process: missing/wrong owner keys rejected; fresh install creates no mount config/helper; state is 0600; autostart toggles; a second launch exits; stop persists; owner key absent from redirected output; API shutdown exits 0.
- Linux empty-catalog idle working set: 113–116 MB in six samples over 60 seconds, with the media helper disabled. This is a short container smoke measurement, not a Windows result, a before/after benchmark, or a 24-hour catalog/playback soak.
- Disposable live Jellyfin → Companion → rclone → Plex checks passed: real H.264/AAC scan and Plex-served decode, HEAD/byte ranges, mount restart, old-token revocation, onward-sharing denial, removal/reselect and outage preservation. Only the new disposable fixture was modified.
- Native Windows tray/DPI/UAC/ACL/Defender, GNOME/KDE login, separate Plex-user FUSE permissions, long-running memory/CPU and real remote clients remain platform checks. No claim of a complete independent security audit or universal playback support is made.

## Release coordination

The revised installers require `SHA256SUMS`. Publish matching archives and sums before advertising these installer revisions; the old rolling release does not necessarily contain sums. Checksums from the same release verify consistency, not publisher identity. Do not turn an unsigned-app reputation warning into a blanket antivirus exception. See the [permissions guide](README.md#windows-app-tray-and-permissions) and [Microsoft's guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
