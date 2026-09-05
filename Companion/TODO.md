# Federation Companion handoff

Updated 2026-09-04. This file is scoped to `Companion/`; the plugin-wide release checklist remains in the repository root `TODO.md`.

## Completed in the current Companion pass

- [x] Plex → Jellyfin: prefer and probe public direct HTTPS connections, then Plex Relay, before accepting a LAN-only address that a remote friend cannot reach.
- [x] Plex → Jellyfin: allow multi-server Plex accounts to select the intended server without exposing any connection token to the browser.
- [x] Plex → Jellyfin: exchange the one-time code server-to-server for a per-friend Companion relay token; never hand Jellyfin the real Plex token.
- [x] Plex → Jellyfin: filter section, metadata, artwork, and exact media-part access against the Plex owner's current shared-library toggles; revoking the peer invalidates its relay token.
- [x] Give the Plex owner separate per-friend individual-download and bulk-download consent controls; Jellyfin marks server-side saves so the Companion relay can enforce them.
- [x] Jellyfin → Plex: replace expiring playback-token URLs in `.strm` files with stable Companion relay URLs.
- [x] Bind each relay URL to one exact peer and item with HMAC-SHA256; keep the federation token and upstream playback token server-side.
- [x] Mint fresh Jellyfin playback authorization at play time and preserve `GET`/`HEAD`, `Range`, `If-Range`, `If-None-Match`, `If-Modified-Since`, `206`, `416`, content range/length/type, ETag, and Last-Modified semantics.
- [x] Refresh Plex whenever exported file content changes, not only when the item count changes.
- [x] Serialize overlapping scheduled/manual syncs per peer so concurrent pruning cannot race.
- [x] Reject malformed/insecure Jellyfin connect-code URLs and validate the remote catalog before storing a new peer.
- [x] Stop returning Jellyfin federation tokens and stream-signing secrets from import-peer browser APIs.
- [x] Require a separate per-install owner key on every browser/admin API while leaving only the one-time server claim public.
- [x] Force `companion-state.json` to Unix mode `0600` where supported.
- [x] Split and polish the UI into explicit Plex → Jellyfin and Jellyfin → Plex flows, with laptop/TV scaling, safer destructive confirmations, copy feedback, server selection, clearer sync health, and keyboard focus states.
- [x] Add a dedicated Companion xUnit project covering endpoint preference, item-bound stream capabilities, range relay behavior, URL-only export changes, shared-section filtering, unshared metadata denial, and exact media-part authorization.

## Validation completed

- [x] `dotnet build Companion/FederationCompanion.csproj --no-restore`: clean, zero warnings before the focused test project was added.
- [x] `dotnet test Companion.Tests/FederationCompanion.Tests.csproj`: 10/10 passing after the revocable Plex facade and malformed-secret fail-closed case were added.
- [x] Companion inline JavaScript parsed with Node (`new Function(...)`).
- [x] Disposable localhost HTTP smoke: ownerless `/api/status` returned 401; the correct owner header returned 200; a state write produced mode `0600`.
- [x] `git diff --check` clean.

## Final release checks

- [ ] Run the focused Companion tests again from the exact final tree twice with a clean Release publish.
- [x] Capture laptop (roughly 1440×900) and TV (1920×1080 at distance) screenshots from the final build and check overflow, target sizing, and the two explicit directions in a real Chromium engine.
- [ ] Decide whether to add an explicit bandwidth cap; it remains a known requirement and is not silently treated as complete here.

## Live-machine follow-up

- [ ] Run a real Plex client against a two-machine Jellyfin → Companion → Plex movie and episode, including initial play, seek, resume, and a request after restarting Companion.
- [ ] From a separate-network Jellyfin instance, verify Plex → Jellyfin catalog sync and Range playback against both a public-direct Plex connection and Plex Relay fallback.
