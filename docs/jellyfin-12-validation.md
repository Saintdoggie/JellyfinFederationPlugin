# Federation 0.0.136 — Jellyfin 12 compatibility

Validated on 2026-09-08 using disposable local containers and synthetic H.264/AAC
media. Production servers and their plugin/configuration directories were not modified.

The plugin targets .NET 10 and Jellyfin.Controller/Model 12.0.0, with catalog ABI
12.0.0.0. The previous 0.0.135 archive and every older catalog entry remain available
for Jellyfin 10.11. Companion source and its rolling release are unchanged.

## Changes

- Use Jellyfin 12's `IItemPersistenceService` for deletion of obsolete, unreadable
  federation rows. Existing materialization, IDs, configuration and sharing rules
  are unchanged.
- Authenticate native JSON, image and media requests with `Authorization:
  MediaBrowser Token="…"`. Internal relay credentials stay in request headers.
- Use the same supported header in the embedded settings page and injected web UI.
- Update build, test and packaging paths for .NET 10. Companion tests retain their
  .NET 9 runtime. Fix the README catalog URL to the existing source repository.

## Verification

- Clean Release build; 424 plugin tests, 76 Companion tests and 31 JavaScript tests
  passed twice. Five Python validation-tool tests passed. Focused gateway tests
  cover GET/HEAD, ranges and separation of viewer/internal credentials.
- Jellyfin 12.0 on both peers: native authentication with legacy auth disabled,
  mutual friend handshake, library selection, sync, persisted items and artwork.
- Direct and Proxy modes, in both directions, for administrator and ordinary-user
  sessions: native PlaybackInfo and Jellyfin stream endpoints, full bodies, HEAD,
  ordinary/suffix ranges and actual FFmpeg transcode/decode.
- Scoped peer credentials cannot authenticate to native administrative endpoints.
  User blocking, item consent removal and server disablement deny previously
  issued playback URLs immediately.
- Jellyfin 12.0 / Federation 0.0.136 paired with Jellyfin 10.11.11 / released
  Federation 0.0.135: handshake, sync and Direct/Proxy playback passed both ways
  for administrator and ordinary-user sessions.
- An actual upgrade of that 10.11.11 fixture to 12.0 / 0.0.136 retained every friend
  ID, saved credentials, users/sessions and federated item ID. Sync and playback
  worked after the upgrade without re-friending. Existing 12.0 state also survived
  a plugin replacement and restart.
- Chromium on Jellyfin 12's default Modern layout: settings load/save at 390,
  1440 and 1920px without horizontal overflow; home cloud badges and detail source
  labels for administrator and viewer, with no failed federation HTTP requests.
- The full existing gate also checks Windows Companion cross-compilation and a
  disposable Jellyfin → Companion → rclone → Plex import, mount restart, range
  reads, sharing revocation, outage preservation and Plex-served media decode.

Run the automated/full gates with `scripts/test.sh` / `scripts/test.sh --all` using
.NET SDK 10 and the .NET 9 runtime. Private live fixture state and screenshots are
excluded from source and release archives.

This establishes compatibility with Jellyfin 10.11.11 peers, not every historical
10.x version. Physical Xbox/TV hardware, Windows/Funnel deployments and the broader
subtitle/audio-switching matrix remain the existing follow-up work in `TODO.md`.
