# Jellyfin Federation — reliability, security, and polish

This is the release checklist for the post-0.0.122 quality pass. Items are
ordered by user impact and risk. A polished interface does not compensate for
an unreliable or weak streaming boundary, so playback and authorization ship
first.

## Requested next pass (post-1.0.0) — not started

Reported directly by the project owner after 1.0.0 shipped. Nothing in this
section has been investigated or fixed yet.

- [ ] Advanced tab checkboxes are effectively invisible - `input type="checkbox"
      class="emby-checkbox fed-check"` never gets a Jellyfin `is="emby-checkbox"`
      custom-element upgrade anywhere in `configPage.html`, and there's no fed-*
      CSS fallback for the box/checkmark either, so unlike every other injected
      control on this page nothing draws a visible check state. Likely every
      `.fed-check` checkbox on every tab has the same problem, not just Advanced.
- [ ] Browse/Catalog/Downloads show individual federated TV episodes as flat,
      effectively random list entries instead of grouping by series. Should
      show one card per TV show that expands/drills into that show's own
      episode list, matching how Jellyfin normally presents a series.
- [ ] Clicking Download gives no feedback (no toast, spinner, disabled state, or
      progress) - from the user's perspective nothing happens. Needs a real
      Downloads tab/section showing in-flight and completed
      device/server downloads with live progress and failure state, not just
      the per-item button.
- [ ] Clicking into a movie/show from the download flow should show a proper
      info card (poster, overview, year, genres, etc. - the normal Jellyfin
      item-detail treatment) instead of a bare download action with no context.
- [ ] Browse/Catalog/Downloads paging should become infinite scroll with real
      lazy-loading (fetch/render next page as the user nears the bottom, lazy
      image loading for off-screen cover art) instead of the current paging.

## Deferred — friend ratings/comments (design only, not scheduled)

Project owner wants a rough draft of the concept, not an implementation, and
said explicitly not to start building it yet:

- [ ] When a friend finishes a movie or an episode/season of a TV show, show a
      rating reminder prompting a star rating plus a free-text comment about
      that title, visible to federated friends.

## Requested product pass — complete before the next release

- [x] Add an administrator setting for the injected federated cloud badge.
      Default it off for new and upgraded installs; when off, do not inject a
      corner cloud into cards, while preserving playback and other explicitly
      enabled controls.
- [ ] Polish the injected jellyfin-web experience at laptop and TV widths,
      covering portrait cards, landscape cards, list rows, detail metadata,
      action sheets, focus visibility, couch-distance readability, and SPA
      navigation without duplicate controls.
- [x] Make Catalog an outgoing-sharing workspace containing local media only.
      Support Movies and TV shows, default newest-added-first sorting, search,
      paging, cover art, clear selected/excluded state, and selection of exact
      items that must not be federated.
- [x] Add a separate Downloads workspace containing federated media only.
      Support Movies and TV shows with newest-added-first sorting, search,
      paging, cover art, and download actions. Mark titles with an exclamation
      indicator when an equivalent local copy already exists, with accessible
      text explaining the warning.
- [x] Turn “prefer better federated copies” into an explicit staged workflow:
      enable suggestions first, then separately opt into seeing replacement
      actions. Never delete or download merely because either preference is on.
- [x] Require an individual affirmative approval for every movie replacement;
      do not provide a destructive bulk approval. The exact approved local and
      federated source IDs must be revalidated immediately before work starts.
- [x] For each approved replacement, download to a temporary destination,
      validate successful completion and a usable non-empty media file, move it
      into the managed downloads library, then and only then remove the exact
      approved old local copy. A failure or cancellation must leave the old
      local copy untouched and clean up partial output.
- [x] Add controller/service/UI regression tests for catalog isolation, newest
      ordering, Movies/TV filters, already-local warnings, badge default/off/on,
      exact per-movie replacement approval, stale approval rejection, download
      failure, validation failure, cancellation, and delete-after-success order.

## P0 — release blockers

- [x] Reproduce both directions of Jellyfin-to-Jellyfin playback with two
      servers and at least two non-admin users.
- [x] Fix federated items losing the Play button when every configured user rule
      permits the item. Restrictive, genuinely user-dependent items remain
      fail-closed and use per-request sources.
- [x] Replace the anonymous, enumerable `Plugins/Federation/Stream?serverId=...&itemId=...`
      capability with a cryptographically unguessable, item-scoped stream
      authorization mechanism.
- [x] Reject missing, malformed, wrong-item, wrong-server, and revoked stream
      authorization. Bind any caller-supplied user id cryptographically instead
      of accepting it as proof on its own.
- [x] Decide whether per-request capabilities need an expiry in addition to
      stream-time cache, server, and access-rule revocation. Persisted item paths
      cannot simply expire without a refresh strategy. Decision: static paths use
      durable unguessable HMAC capabilities because Jellyfin persists them; server
      credential rotation, disable/removal, cache/source removal, and current
      access rules are checked on every stream and provide immediate revocation.
- [ ] Apply the same authorization policy to normal playback, range requests,
      browser downloads, server downloads, fallback sources, and Plex relays.
- [x] Confirm that unfriending, disabling a server, blocking a user, or removing
      an item invalidates access immediately rather than after cache expiry.
- [ ] Add regression tests for token replay, item/server substitution, omitted
      user identity, expired tokens, revoked friendships, blocked users, range
      requests, and source fallback.
- [ ] Ensure logs explain a denied or failed play without logging API keys,
      stream tokens, Plex tokens, or full credential-bearing URLs.

## P1 — playback quality and diagnostics

- [ ] Add a small playback preflight result to Diagnostics: local item path,
      selected source, peer reachability, plugin-version compatibility, token
      mint result, metadata result, and final media-source viability.
- [ ] Distinguish authorization failures, unreachable peers, timeouts, missing
      remote media, incompatible clients, and transcoder failures in user-facing
      messages.
- [ ] Test direct play, direct stream, transcoding, seeking/range resume, audio,
      subtitles, episodes, multi-source fallback, Proxy mode, Direct mode, Plex,
      and Companion imports.
- [ ] Verify playback on administrator and ordinary-user accounts; never rely on
      the first remote Jellyfin user as an implicit playback identity.
- [ ] Exercise version skew explicitly and present an actionable upgrade message
      when the remote plugin is too old for the negotiated authorization flow.

## P1 — interface polish and accessibility

- [ ] Fix the federated cloud badge against grid cards, list rows, square art,
      portrait art, mobile breakpoints, browser zoom, and custom themes.
- [x] Anchor overlays to the artwork wrapper instead of the outer card wherever
      Jellyfin provides one; avoid shifting card text or inheriting transforms.
- [x] Give injected SVGs an explicit block layout and consistent view-box,
      dimensions, optical alignment, and high-DPI rendering.
- [x] Prevent duplicate badges and stop the source-label MutationObserver loop
      during SPA navigation.
- [ ] Make all status, empty, loading, error, retry, disabled, and success states
      deliberate and visually consistent.
- [ ] Review the Friends, Pools, Companion, Discovery, Libraries, Browse,
      Catalog, and Advanced tabs at narrow, medium, and wide widths.
- [ ] Normalize spacing, field alignment, action placement, button hierarchy,
      wrapping, long server names/URLs, focus rings, keyboard order, labels, and
      minimum touch targets.
- [x] Add reduced-motion handling and proper tab/panel relationships, roving
      focus, and arrow/Home/End keyboard navigation.
- [ ] Add a forced-colors/high-contrast pass and audit names for every injected
      icon control.
- [ ] Avoid raw exception text in the UI. Show a concise message plus a stable
      diagnostic code that can be matched in logs.

## P2 — maintainability and automated UI checks

- [ ] Split the 4,700-line configuration page into maintainable embedded assets
      or clearly bounded modules without breaking Jellyfin's plugin-page loader.
- [ ] Centralize colors, spacing, radii, typography, status styles, and icon
      dimensions as federation CSS custom properties with Jellyfin theme fallbacks.
- [x] Add jsdom regression tests for badge placement, idempotence, admin-only
      polling, tab routing, duplicate IDs, and tab accessibility relationships.
- [ ] Extend DOM coverage to route changes, async failure/retry, and every
      action-sheet state.
- [ ] Add browser screenshot tests when an interactive Chromium/Playwright
      environment is available; keep reference images for desktop and mobile.
- [ ] Add a two-server integration harness with isolated configs, seeded users,
      deterministic media, and a scripted friend handshake/playback smoke test.
- [ ] Run dependency, secret, endpoint-authorization, SSRF, path traversal,
      header injection, cancellation, and resource-exhaustion reviews.

## Release gate

- [x] Clean build with zero warnings.
- [x] Full unit suite green twice, with no flaky test hidden by retries.
- [x] Two-server integration matrix green for both sharing directions and both
      admin/non-admin users.
- [x] UI smoke test completed at desktop and mobile widths.
- [x] No credentials or bearer-style stream capabilities present in logs,
      configuration responses, HTML, or screenshots.
- [x] Version, changelog, manifest checksum, and release archive agree.
- [x] Project owner explicitly reviewed the requested security/reliability scope
      and directed publication after the sandbox gate passed.

## Current validation record (working tree, not released)

- 2026-09-04: Release build of the test project completed in an offline .NET 9
  container with zero warnings; the 48 focused catalog/download/access/sort
  tests passed.
- 2026-09-04: Eight jsdom regression cases passed, including badge off/on and
  live setting reconciliation, artwork anchoring, SPA idempotence, admin-only
  polling, tab wiring, and per-item replacement UI assertions.
- 2026-09-04: Headless Chromium layout smoke passed at 1366×768 laptop,
  1920×1080 TV, and 390×844 mobile widths. Populated Catalog, Downloads, and
  expanded per-title replacement states had no horizontal overflow; separate
  portrait/landscape/list fixtures confirmed the cloud stays inside artwork at
  laptop and TV sizes.
- 2026-09-04: Background replacement regression cases prove valid media is
  committed before deletion, invalid HTML is rejected, cancellation removes
  partial output without deletion, and a stale second approval check retains
  both copies. The browser-supplied title is not trusted for tracking/filenames.
- 2026-09-04: Clean Release build had zero warnings and all 347 .NET tests
  passed twice. Two isolated Jellyfin 10.11.11 servers running this DLL passed
  both federation directions for admin and ordinary viewer accounts: PlaybackInfo
  returned a viable source and every 64-byte Range request returned HTTP 206 with
  exactly 64 bytes.
- 2026-09-04: Released 0.0.123 from commit `6650a4d` to public `master` and
  GitHub. Project/meta/manifest versions agree; the four-file release archive
  was downloaded back from GitHub and independently matched manifest MD5
  `d029debb99b293497cc2042c4d87a6eb`. This release gate is complete.
- 2026-09-04: Settings UI polish and a temporary pause on downloading
  federated content to this server (StartDownload/BrowseDownload/
  ApplyQualityUpgrades) were built and validated (347 .NET tests x2, 8 jsdom
  tests) but only handed to the project owner as a zip, versioned 0.1.0 - no
  tag or GitHub release existed for it yet.
- 2026-09-04: At the project owner's direction this was renumbered to 1.0.0
  (a major, not minor, release) on commit `5611b5d`, rebuilt clean with zero
  warnings, and revalidated: 347 .NET tests passed twice, 8 jsdom tests
  passed. Released to public `master` and GitHub as tag `1.0.0`; the release
  archive was downloaded back from GitHub and independently matched manifest
  MD5 `447d4460900fd8d5e4d225da07abe80d`. This release gate is complete.
  Note: downloading federated content to this server remains temporarily
  disabled in 1.0.0 pending the rework mentioned above - re-enabling that
  flow is separate follow-up work, not yet started.
