# Jellyfin Federation — reliability, security, and polish

This is the release checklist for the post-0.0.122 quality pass. Items are
ordered by user impact and risk. A polished interface does not compensate for
an unreliable or weak streaming boundary, so playback and authorization ship
first.

## Requested next pass (post-1.0.0)

Reported directly by the project owner after 1.0.0 shipped.

- [x] Advanced tab checkboxes are effectively invisible - `input type="checkbox"
      class="emby-checkbox fed-check"` never gets a Jellyfin `is="emby-checkbox"`
      custom-element upgrade anywhere in `configPage.html`, and there's no fed-*
      CSS fallback for the box/checkmark either, so unlike every other injected
      control on this page nothing draws a visible check state. Fixed with a
      scoped `#federationConfigPage input.fed-check` rule (native `appearance:
      auto` + `accent-color`, high enough specificity to win regardless of
      stylesheet order) - affects every `.fed-check` on every tab, not just
      Advanced, since they all share the one root cause.
- [x] Downloads tab server dropdown sometimes shows "no servers available" even
      with friends configured - `loadBrowseServers()` only ever ran once, the
      first time the tab was opened, guarded by a `browseLoaded` flag; if that
      first open raced ahead of the initial config fetch the dropdown was built
      from an empty placeholder and never touched again for the rest of the
      page's life. Now refreshes on every `loadConfiguration()` resolution and
      every tab visit, preserving the current selection across a refresh.
- [x] Browse/Downloads showed individual federated TV episodes as flat,
      effectively random list entries instead of grouping by series. The
      Downloads tab's TV mode now lists shows (`type=Series`); clicking one
      opens a dedicated episode list for that show, grouped by season, in
      proper watch order (new `Browse/{serverId}/Series/{seriesId}/Episodes`
      endpoint, with a "back to shows" link). Works for both Jellyfin peers
      (ParentId+Recursive against the existing Peer/Items endpoint - no
      receiving-side change needed beyond a new EpisodeOrder sort option) and
      Plex sources (GetSectionItemsAsync already returns show entries
      alongside episodes with a matching SeriesId, just never used before).
- [x] Clicking Download gave no feedback at all - the button was `disabled`
      with only a hover tooltip explaining why, so a click did literally
      nothing. The button is clickable again and always reports back inline
      exactly what the server said (currently "temporarily disabled", per the
      guard added for the 1.0.0 settings-UI-polish work) instead of silently
      doing nothing. The existing global "Downloads in progress" panel above
      the tabs (GetDownloads/DownloadProgressTracker) still needs a proper
      dedicated history/progress view inside this tab, but building that out
      further has little value while download-to-server stays disabled - see
      the P0 entry on re-enabling it below.
- [x] Clicking into a movie/show/episode now opens an info card (poster,
      overview, year, genres, rating) instead of doing nothing - implemented
      as a modal (`showItemInfoModal`), reusing fields the Browse endpoints
      now also return (`overview`, `genres`, `officialRating`,
      `communityRating`). Clicking a TV show still opens its episode list
      instead (that already *is* "clicking in" for a show); the info card is
      for movies and individual episodes.
- [ ] Browse/Catalog/Downloads paging should become infinite scroll with real
      lazy-loading (fetch/render next page as the user nears the bottom, lazy
      image loading for off-screen cover art) instead of the current paging.
      Not started this pass.
- [x] Quality-upgrade review ("better copies to review") gave each movie its
      own one-at-a-time review button, listed every episode candidate flat
      with no cover art, and had no way to act on more than one title at
      once. It now shows cover art on every card (`catalogPosterUrl`, the
      same helper the Catalog tab already used - these are local items, no
      remote-image proxy needed), groups episode candidates under their show
      (selecting the show selects all its episodes), and supports selecting
      several candidates for one "Apply selected" bulk request, gated behind
      two separate confirmations. **This deliberately reverses this file's
      own earlier P0 decision** ("Require an individual affirmative approval
      for every movie replacement; do not provide a destructive bulk
      approval") **at the project owner's explicit request.** `ApplyQualityUpgrades`
      remains temporarily disabled server-side either way, so nothing in this
      change can currently start a real download/removal; if/when it is
      re-enabled, it must still re-validate and download-then-remove each
      selected item on its own (the multi-id request body already supports
      this - see the per-item `operations` handling in
      `applySelectedQualityUpgrades`), not treat a large selection as
      permission to skip that per-item safety work.

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
- 2026-09-04: Post-1.0.0 fixes above (invisible checkboxes, Downloads server
  dropdown, TV show grouping, Download click feedback, item info card) built
  and validated: clean Release build with zero warnings, 347 .NET tests
  passed twice, 10 jsdom tests passed (2 new regression cases added for the
  checkbox and Downloads-dropdown fixes). A live two-server/browser check was
  attempted but could not be completed this session (Chrome extension
  unavailable; no sandbox admin credentials on hand, and none should be
  stored in this repo per this file's own rules) - the TV-show grouping in
  particular is new surface on both the Jellyfin-peer and Plex code paths.
  Flagged to the project owner before release.
- 2026-09-04: Released as 1.1.0 (commit `00a6825`) at the project owner's
  explicit direction, without the live two-server/browser check above -
  build/test validation only. Project/meta/manifest versions agree; the
  release archive was downloaded back from GitHub and independently matched
  manifest MD5 `70b999dbded686c446d792090300d81c`. This release gate is
  complete on the build/test criteria; the two-server integration matrix and
  interactive UI smoke test criteria were explicitly waived for this release,
  not satisfied. First real-world check is the project owner trying it.
- 2026-09-04: At the project owner's direction, reverted the version scheme
  from 1.0.0/1.1.0 back to this project's established 0.0.x numbering - same
  content, renamed only. What was 1.0.0 is now `0.0.124` (rebuilt from
  commit `5611b5d`'s tree in a throwaway worktree, since that commit's own
  files still say 1.0.0 and git history is not rewritten); what was 1.1.0 is
  now `0.0.125` (commit `dc78a38`, this branch's tip). Both rebuilt clean
  with zero warnings and revalidated: 347 .NET tests passed twice, 10 jsdom
  tests passed. The 1.0.0 and 1.1.0 GitHub releases and tags were deleted;
  0.0.124 and 0.0.125 released in their place, each independently verified
  by downloading the published asset back and matching its manifest
  checksum (`bfeaac898a4f3fdff55588a973bb71f4` and
  `6cc8a52c82caedde9d6a609c8616e20c`). Same caveat as the entry above still
  applies: no live two-server/browser check.
- 2026-09-04: Quality-upgrade review grouping/cover-art/bulk-select above
  built and validated: clean Release build with zero warnings, 347 .NET
  tests passed twice, 11 jsdom tests passed (one rewritten to match the new
  bulk-apply design, one new case added asserting exactly two confirmations
  gate `applySelectedQualityUpgrades`). No live two-server/browser check
  again this pass - same reasons as above. Not released; working-tree state
  on `master` only.
