# Jellyfin Federation project context

Read this file first, then `TODO.md`. The source repository is
the repository checkout (use `git rev-parse --show-toplevel`); deployed plugin
folders, `jellyfin-test`, caches, and backups are not source-of-truth copies.

## Product and repository

- Jellyfin plugin that merges explicitly connected friends' Jellyfin libraries
  into local virtual libraries. This is private friend-to-friend federation,
  not discovery of arbitrary public servers.
- `Plex/` is an optional add-in loaded beside the core DLL. It owns all Plex
  catalog, Companion, metadata/import and `.strm` export integration. Core
  Jellyfin federation must remain functional without it, and an absent provider
  must preserve Plex configuration/cache without Jellyfin-protocol fallthrough.
- `Companion/` is a separate ASP.NET Core app for Plex owners. It handles Plex
  sign-in, library consent, one-time connect codes, peers, and Jellyfin-to-Plex
  imports through a read-only media mount (legacy `.strm` exports are not
  native Plex playback).
- Main plugin target: .NET 10, Jellyfin 12.0.0. Companion remains .NET 9.
- Source branch: `master`; GitHub remote: `Saintdoggie/JellyfinFederationPlugin`.
- Jellyfin 12 release is version 0.0.160; 0.0.135 remains the Jellyfin 10.11
  build. GitHub releases and `manifest.json` are the catalog of record; `TODO.md` tracks remaining work.

## Read these files by task

Core lifecycle and configuration:

- `JellyfinFederationPlugin.csproj` — target framework, Jellyfin dependencies,
  embedded web assets, build exclusions.
- `Plugin.cs` — plugin singleton, encrypted-at-rest secret handling, pages, and
  uninstall cleanup.
- `FederationEntryPoint.cs` — cache initialization, migrations, provisioning,
  web injection, and startup sync.
- `Configuration/PluginConfiguration.cs` — all persisted models, remote server
  kinds, streaming modes, access rules, pools, mappings, and migration flags.
- `Configuration/PluginServiceRegistrator.cs` — service lifetimes and DI map.
- `Tasks/FederationRefreshTask.cs` — scheduled provisioning, sync, and export.

HTTP, UI, and browser injection:

- `Configuration/FederationPluginController.cs` — core plugin HTTP API;
  `Plex/Configuration/PlexController.cs` and `PlexMetadataSetupController.cs`
  own Plex/Companion routes. Check authorization and input validation on every
  changed route; anonymous Plex offers are the intentional exception.
  `Configuration/FederationAddInPageController.cs` serves only named, elevated
  add-in page resources through the core loader in `Web/admin/addinPage.html`.
  `Configuration/IntegrationCatalogController.cs` and
  `Services/IntegrationCatalogService.cs` expose compatible entries from
  `integrations/catalog.json`. That catalog is discovery-only: no package download,
  remote HTML/JavaScript, or third-party executable installation is permitted.
- `Web/admin/configPage.html` — embedded core admin UI. It is currently a large
  single-file page with seven tabs since 0.0.215: Friends (pools and friends of
  friends are sections there), Libraries, Hide titles (`catalog`), Downloads
  (`browse`), Free up space (`storage`), Settings (`advanced`; Beta, Integrations
  and connectivity are sections there) and Log. Settings save themselves
  (`scheduleAutosave`; a card's `data-fed-save` names its endpoint). Library
  choices are the one explicit Save (`saveLibraries`): a settings autosave must
  never commit unsaved library picks.
  `Plex/Web/admin/plexPage.html` owns Plex/Companion setup, import and export UI.
- `Web/client/federation-badge.js` — jellyfin-web SPA injection: card/detail badges,
  action-sheet entries, downloads, hiding/sharing controls, and origin filter.
- `Services/WebClientInjector.cs` and `Middleware/BadgeScriptInjectionMiddleware.cs`
  — disk and serve-time injection paths. Both must remain idempotent.
- `Middleware/ConfigurationPageCompressionFixMiddleware.cs` — workaround for
  Jellyfin configuration-page response compression.

Federation data flow:

- `Services/FederationSyncService.cs` — serialized sync orchestration, paging,
  mappings, dedup, external sources, pruning, and reconciliation calls.
- `Services/RemoteServerClient.cs` — Jellyfin peer HTTP client, token/session
  calls, item/media parsing, version probing, and retry behavior.
- `Services/IExternalCatalogProvider.cs` and `ExternalCatalogRegistry.cs` —
  optional non-Jellyfin provider contracts. `Plex/Services/PlexCatalogProvider.cs`
  and the other concrete Plex services are compiled only into the add-in.
- `Plex/Configuration/PlexServiceRegistrator.cs` — add-in DI registrations;
  `Plex/INSTALL.md` — install/removal behavior. Build a non-release package with
  `scripts/package-plex-addin.sh`.
- `Services/FederationItemCache.cs` — persisted, deduplicated catalog and
  remote-to-local source index.
- `Services/FederationItemPersistenceService.cs` — reconciles cache entries
  into Jellyfin database items and performs migration cleanup.
- `Services/FederationLibraryManager.cs` — materializes stock Jellyfin entity
  types, computes stable IDs, stamps metadata, and builds playback paths.
- `Services/LibraryProvisioningService.cs` — creates/removes shadow paths and
  virtual libraries.
- `Providers/FederationMetadataProvider.cs` and `FederationImageProvider.cs` —
  metadata/image integration.

Playback, authorization, and downloads:

- `Services/FederationMediaSourceProvider.cs` — per-request media sources,
  acting-user resolution, source fallback, remote PlaybackInfo, and paths.
- `Services/FederationStreamHandler.cs` — proxy/direct relays, byte ranges,
  retries, cancellation, and external-source streaming.
- `Services/FederationPlaybackTokenService.cs` — item-scoped, purpose-scoped
  remote playback/download tokens and the ordinary-download throttle.
- `Services/FederationUserSessionTokenService.cs` — per-user session tokens. Both
  this service and playback tokens persist only SHA-256 hashes, flush on shutdown,
  and serialize file writes so a restart does not invalidate cached direct plays.
- `Services/FederationTokenAuth.cs` — federation peer authentication.
- `Services/FederationPeerAccessService.cs` — authoritative outgoing sharing
  rules on the content-owning peer.
- `Services/RemoteAccessControlService.cs` — receiving-side enforcement of
  rules pushed by a friend for this server's local users.
- `Services/FederationDownloadService.cs` and `DownloadProgressTracker.cs` —
  server downloads (copy to this server), bounded batch transfers, partial-file
  validation, and progress.
- Device downloads (0.0.188): `FederationCanDownloadFilter.cs` (CanDownload on
  friends' titles), `FederationDeviceDownloadFilter.cs` (answers native
  `/Items/{id}/Download`), `FederationDeviceDownloadService.cs` (who may, which
  friend, viewer-bound Download permit), `DeviceDownload.cs` (file names, route).
  Owner side: `DirectStream?download=true` names the file; viewer allowance in
  `FederationPlaybackTokenService.TryReserveViewerDownload`.
- Watch parties (0.0.189): `FederatedPartyService.cs` (parties, links, heartbeat,
  peer messages), `PartyLink.cs` (pure relay rules: echo suppression, Wait/Go
  loading handshake, ground-truth state; unit tested), `PartyItemMapper.cs`
  (title matching across servers), `PartyWire.cs`, `FederatedPartyFilters.cs`
  (adds friends' parties to `/SyncPlay/List`, answers `/SyncPlay/Join`). E2E:
  `scripts/federation_party_smoke.py` with `scripts/syncplay_player.py`.
  Party wire v2 adds `PartyPeerGate.cs`: negotiated connection IDs, ordered
  sequences, heartbeat send timing, bounded queues and stale-heartbeat rejection.
  Keep version-1 fallback for older friends and never apply events across links.
- `Services/FederationFriendFavoritesService.cs` — friends' favorites: owner
  `Peer/Favorites`, viewer `FriendFavorites`; rows in `Web/client/federation-badge.js`. The old quality replacement endpoint is retired; Storage
  cleanup is delete-only and revalidates every exact local item immediately.
- 0.0.190: `Services/DownloadDestinations.cs` (pure: where a server download goes,
  on-disk naming, `DownloadedFileRegistry` keeping copies outside the downloads folder
  unshareable); `Services/FederationViewerActivity.cs` (viewer side
  `FederationViewerReporter` → owner `Peer/Viewers` → `FederationRemoteViewerService`
  user-less dashboard sessions; `FederatedRoute`); `Services/TranscodeHardware.cs`
  (`Peer/Hardware`, auto/manual hardware, `Decide` placement, default off, hooked in
  `FederationMediaSourceProvider` before a grant is recorded); `Playback/Connection`
  feeds the "Federation" block in the player's Playback Info (`federation-badge.js`).
- `Services/WanBandwidthMonitor.cs` — local/WAN classification and caps.
- `Services/PlaybackRouteMetrics.cs` — bounded per-user/device/route browser
  telemetry (numeric only, never media URLs); native clients without telemetry keep
  declared/server-route limits. `Services/FederationBandwidthAllocator.cs` shares
  measured capacity between PlaySessionIds, replaces same-device retries, and
  rebalances estimated capacity instead of refusing new plays when the estimate fills.
- Provider playback enumerates every Jellyfin remote media source. Exact remote
  `mediaSourceId` values must stay bound through local source IDs, DirectStream,
  `PeerPlaybackGrants`, HLS/progressive/subtitle paths, and native loopback URLs.
- `Services/NativeClientCapabilities.cs` resolves native app identity from the
  authenticated session and standard Jellyfin headers, including token-only requests.
  Raw HTTP Direct Play is enabled only for suitable native transports; known Android
  raw-HTTP and browser paths retain Direct Stream/transcode behavior. This is transport
  detection, not a codec blacklist.
- Playback Info reports play method, observed transport, conversion location, and
  original/delivered formats independently. Unknown is not Direct Play.
- Proxy/Plex over-cap sources require conversion. Never throttle the original input
  to ffmpeg; cap the converted output instead.
- Remote-transcode handoff (0.0.187): `Services/RemoteMediaPath.cs` (allow-list
  shared by both sides), `HlsPlaylistRewriter.cs`, `FederationRemoteTranscodeGateway.cs`
  + controller `Peer/Play/{token}/{**path}` and `Peer/PlayControl` (owner);
  `FederationRemoteTranscodeFilter.cs`, `PeerTranscodeCapability.cs`,
  `PeerPlaybackGrants.cs`, `RemoteTranscodeHandoffs.cs`,
  `RemoteTranscodeSessionBridge.cs` (receiver); `RemoteTranscodeSessionRegistry.cs`.
- `Services/FederationVisibilityService.cs` — friend per-user rules hide titles via
  a per-user BlockedTags tag; same decision function as playback
  (`RemoteAccessControlService.Evaluate`).

Friendship and connectivity:

- `Services/FederationFriendService.cs` — handshake, scoped Jellyfin keys,
  friend lifecycle, discovery, pools, per-user rule propagation, and Companion
  linking.
- `Services/TailscaleService.cs` and `ProcessRunner.cs` — environment checks,
  install/login/Funnel workflow, process cancellation, and timeouts.
- `Companion/Program.cs`, `CompanionState.cs`, `PlexAuth.cs`, `PlexClient.cs`,
  `PlexFederationRelay.cs`, `JellyfinImportService.cs`, `StrmExporter.cs`, and
  `ImportSyncBackgroundService.cs` — Companion backend.
- `Companion/wwwroot/index.html` — Companion single-page UI.

Tests and project history:

- `Tests/` — xUnit/Moq regression suite. Locate the closest existing fixture
  before creating a new one; streaming and sharing tests encode hard-won bugs.
- `manifest.json` and `meta.json` — plugin release metadata. The plugin zip is
  a GitHub release asset, not committed.
- `.github/workflows/companion-release.yml` — rolling Companion builds; it is
  separate from plugin version releases.
- `TODO.md` — active reliability/security/polish release checklist.

## Non-negotiable engineering invariants

- Never subclass Jellyfin entity types. `BaseItem.GetBaseItemKind()` parses the
  CLR class name as a Jellyfin enum, and unknown subclasses poison database
  enumeration. Materialize Jellyfin's stock `Movie`, `Series`, `Season`,
  `Episode`, `Audio`, and related types only.
- Reconciliation creates or deletes items; it generally does not update a
  persisted item in place. Any change to a persisted `BaseItem` property needs
  an explicit invalidation/recreation migration, or existing installs will keep
  the old value indefinitely.
- Parent/child order matters. External providers must return series before
  seasons/episodes, and nested items need matching parent IDs plus
  `SeriesPresentationUniqueKey` values.
- Never re-federate already-federated content. An item carrying a
  `FederationKey` is not eligible for outgoing sharing.
- Treat remote failure as transient: distinguish `null`/failure from a genuine
  empty library so a network blip never erases a cached catalog.
- Never send Jellyfin API keys, Plex tokens, internal relay keys, or upstream
  credential-bearing URLs to a browser/client or logs.
- Never trust a query-string user ID as authentication. Identity must come from
  a validated Jellyfin session or a cryptographically bound, scoped token.
- Sharing/access changes, server disablement, and unfriending must take effect
  at actual stream time, not only at the previous sync.
- Media relays must preserve valid Range semantics and cancellation. Do not
  splice a full 200 response into a resumed 206 stream.
- Proxy/transcoder URLs should use loopback or `InternalServerUrl`; public
  `ServerUrl` is for peers and must not hairpin local ffmpeg traffic through a
  public tunnel/CDN.
- Configuration GET responses must be sanitized. Configuration POST handlers
  must preserve fields managed by dedicated endpoints and migration flags.
- Client injection must tolerate jellyfin-web SPA navigation, repeated
  initialization, read-only web roots, custom themes, and changing DOM shapes.

## Current playback/security architecture and remaining risks

- Static playback paths are permitted only when every configured local-user
  override allows that exact item. A `CertainLibraries` rule matching the item's
  mapping therefore keeps the Play button; a blocked, rating-limited, or
  user-dependent item remains without a shared path and relies on the provider.
- `Plugins/Federation/Stream` remains anonymous because Jellyfin's ffmpeg fetch
  does not forward the viewer session. Every URL now has an HMAC-SHA256
  capability bound to server, item, media kind, and optional normalized user.
  Stream time also verifies server enabled state, cache/source membership, and
  the current access rule. Rotating/removing the server credential revokes old
  URLs. Current v2 capabilities expire and bind download/auto-selection purpose; non-expiring legacy signatures are rejected and stored paths are restamped.
- Per-request provider URLs cryptographically bind `requestingUserId`; never
  regress to appending an unsigned user id. Userless static paths must continue
  to pass `IsAllowedForEveryConfiguredUser` both when built and when streamed.
- The cloud badge is controlled by `ShowFederatedCloudBadges`, defaults off, and
  reconciles already-rendered cards when the setting changes. It anchors to
  `.cardImageContainer`/`.listItemImage`, and the source label is idempotent.
- The settings page treats `Catalog` (labelled Hide titles) as local-only outgoing sharing (Movies
  or Series, newest first) and `Downloads` as remote-only acquisition (Movies or
  Episodes, newest first). Series-level exclusions are inherited by seasons and
  episodes in `FederationPeerAccessService`; do not remove that ancestor check.
- `PreferHigherQualityRemotes` only enables suggestions. The legacy
  `EnableQualityReplacementActions` field is forced off; the compatibility
  Apply endpoint always refuses replacement downloads. Storage removal requires
  explicit selected IDs plus confirmation and rebuilds the candidate set before
  deleting any exact local file.
- The core and Plex admin pages and `Web/client/federation-badge.js` are large
  single-file assets. Make narrow changes with regression tests before attempting
  structural cleanup.
- Native Jellyfin relay keys are process-lifetime and memory-only. The legacy
  `InternalRelayApiKey` configuration field stays empty; never persist that
  admin-equivalent credential through `ApiKeyProtector` or plugin XML.
- Pool notices and existing-pool invitations require sender membership; accept/reject
  callbacks must match the invited friend. Pool membership and pending requests are capped.
- Discovery is review-only and never contacts candidates automatically. Friend accept/reject
  callbacks require the pending handshake credential; only explicit admin requests choose sharing.
- Friend-facing HTTP disables redirects and outbound .NET proxy use. Reverse-proxy
  deployments remain supported, including browser playback-header checks. Control responses use
  `ResponseHeadersRead` and `HttpContentReader` limits rather than unbounded reads.
- Direct-link requests carry the authenticated link-friend context, punching and
  QUIC acceptance use exact endpoints, and read-ahead revalidates before serving.
  Pairing remains review-locked; non-home relay tickets are not implemented.

## Validation and release policy

- Do not commit generated `bin/`, `obj/`, `.dotnet/`, `.nuget/`, cache, local
  state, logs, credentials, or installed/deployed plugin copies.
- Do not store test/admin passwords in this repository, documentation, shell
  history, logs, screenshots, or test fixtures.
- Before a push: inspect the diff, run a clean build, run the full test suite
  twice, and run focused tests for changed behavior. For playback/security work,
  also complete the two-server ordinary-user/admin matrix in `TODO.md`.
- Push only production-ready work to `master`; never push a half-working state
  merely to preserve progress. Do not rewrite shared history.
- Plugin releases require one agreed version across `.csproj`, `meta.json`, the
  manifest entry, tag, release asset URL, zip contents, and checksum.
- Inspect the exact release archive before publishing. A plugin release must
  contain the expected DLL and metadata/assets, not source or secrets.
- Publishing a GitHub release and pushing `master` affect all users. Do them
  only after the release gate in `TODO.md` is satisfied. The project owner has
  requested releases for completed safe fixes, but that is not permission to
  bypass validation or release unfinished work.
- Companion uses the separate `companion-latest` rolling workflow and should
  only be triggered by intentional, validated changes under `Companion/`.

## Work in progress

- Automatic copy and server switching is being built on branch `work/smart-playback`; the agreed rules, step order and the exact stopping point are the first section of `TODO.md`. Read it before touching playback, source selection or `WanBandwidthMonitor`.
- Since 0.0.211 a title this server owns can carry a friend's copy as an extra media source, only for a device that cannot play the own file as stored (`Services/OwnedTitleFriendCopies.cs`, `FederationMediaSourceProvider.FriendCopiesForOwnedTitleAsync`, E2E `scripts/federation_owned_title_smoke.py`). That path must stay free for every other play of a local title: no remote call unless the device's profile says the own file needs converting.
- Since 0.0.213 two servers of one owner can be linked (`Services/LinkedServerService.cs`, `Services/LinkCode.cs`, `Configuration/LinkedServerController.cs`, E2E `scripts/federation_linked_smoke.py`). A link is made only after an admin on each server typed the other's six-digit RFC 6238 code (5 wrong codes stop it, 10-minute window); keys only travel over https, private/Tailscale addresses or local names. Linked servers lend each other their own direct friends with their own key (`Peer/Linked/Friends`, needs the friend token and the link key), so to those friends both servers are one. Invariants: a friend used through a link (`RemoteServer.IsThroughLink`) has no `IssuedApiKey`, is never sent anything but `PlayControl` among peer posts, is never unfriended, reconnected, given rules or pool invites from here, is never lent on (no chains), loses to a direct friendship, and is excluded from `FindByFederationId` and `Friends/List`. Never let a config save create or change link fields.
- Since 0.0.214 sound help (`Services/SoundHelpService.cs`, `Services/SoundHelpEncoder.cs`, `Configuration/SoundHelpController.cs`, `DevicePlayFit.PlanSoundHelp`, E2E `scripts/federation_sound_help_smoke.py`): on a server that may not convert, a federated source the device plays the picture of but not the file (no friend converting it) gets a `SoundHelp/{id}/stream.{mp4|mkv|ts}` progressive TranscodingUrl. Wrapper-only: one ffmpeg copies both streams. Sound: one ffmpeg reads the title once (picture to stdout, sound copy to a loopback TCP port), the sound goes in ordered pieces to a linked server's `Peer/Linked/Sound/{playId}` (friend token + link key) which returns AAC stereo, and a second ffmpeg joins them (`-copyts` + equal `-itsoffset`). Invariants: video is never sent to the linked server; the play URL holds only a random id (no key, no address); ffmpeg errors are logged with addresses replaced; the linked server refuses when it may not convert, has no ffmpeg, or runs 4 plays.
- Since 0.0.210 a converted play can move between friends' servers (`Services/HandoffStandby.cs`, wired in `FederationRemoteTranscodeFilter`, tracked in `RemoteTranscodeHandoffs`, E2E `scripts/federation_standby_smoke.py`). Falling back to converting on the receiving server is still not wanted. The standby must never reserve bandwidth or start a conversion before a player asks it.

## Local environment notes

- This workspace may not expose `dotnet` directly. Previous work used a .NET 9
  SDK container when needed. Request the necessary sandbox approval rather than
  skipping validation.
- Interactive browser/computer-use tooling is not guaranteed per session.
  Node 22 and jsdom are currently present, so DOM regression tests are possible.
  Use a real browser/screenshot harness when Chromium/Playwright or an attached
  browser tool is available.
- On 2026-09-04 local Playwright smoke fixtures passed at laptop, TV, and mobile
  widths; temporary harnesses/screenshots live under `/tmp` and are not release
  assets. See the dated validation record in `TODO.md` for the exact scope.
- Locate test data through the configured test instance or disposable QA fixture.
  Determine whether an instance is active before treating old logs or databases as live.
  Never modify production/test data merely to make a test pass without first
  resolving which instance is in scope.
