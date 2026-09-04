# Jellyfin Federation project context

Read this file first, then `TODO.md`. The source repository is
`/var/home/cranky/Documents/JellyfinFederationPlugin-master`; deployed plugin
folders, `jellyfin-test`, caches, and backups are not source-of-truth copies.

## Product and repository

- Jellyfin plugin that merges explicitly connected friends' Jellyfin libraries
  into local virtual libraries. This is private friend-to-friend federation,
  not discovery of arbitrary public servers.
- The plugin also supports Plex as an external catalog source.
- `Companion/` is a separate ASP.NET Core app for Plex owners. It handles Plex
  sign-in, library consent, one-time connect codes, peers, and Jellyfin-to-Plex
  `.strm` imports without requiring Jellyfin on the Plex owner's machine.
- Main plugin target: .NET 9, Jellyfin 10.11.6.
- Source branch: `master`; GitHub remote: `Saintdoggie/JellyfinFederationPlugin`.
- Current published baseline at the start of this pass was version 0.0.122,
  commit `be38416`. Version 0.0.123 has now passed 347 .NET tests twice, jsdom,
  Chromium layout, and a two-server admin/viewer Range-playback matrix; consult
  `git status`, `TODO.md`, and GitHub before assuming publication completed.
  and `TODO.md` before treating it as released.

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

- `Configuration/FederationPluginController.cs` — the entire plugin HTTP API;
  check authorization attributes and input validation for every changed route.
- `Configuration/configPage.html` — embedded admin UI. It is currently a large
  single-file page with Friends, Pools, Companion, Discovery, Libraries,
  Browse, Catalog, and Advanced tabs.
- `Web/federation-badge.js` — jellyfin-web SPA injection: card/detail badges,
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
- `Services/IExternalCatalogProvider.cs` — adapter contract for non-Jellyfin
  sources. `PlexCatalogProvider.cs` is the current implementation.
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
- `Services/FederationPlaybackTokenService.cs` — item-scoped remote playback
  tokens.
- `Services/FederationUserSessionTokenService.cs` — per-user session tokens.
- `Services/FederationTokenAuth.cs` — federation peer authentication.
- `Services/FederationPeerAccessService.cs` — authoritative outgoing sharing
  rules on the content-owning peer.
- `Services/RemoteAccessControlService.cs` — receiving-side enforcement of
  rules pushed by a friend for this server's local users.
- `Services/FederationDownloadService.cs` and `DownloadProgressTracker.cs` —
  device/server downloads, replacement flow, and progress. Quality replacement
  is deliberately two opt-ins plus one exact-title confirmation; it must stage
  and validate the new file before deleting the revalidated old local item.
- `Services/WanBandwidthMonitor.cs` — local/WAN classification and caps.

Friendship and connectivity:

- `Services/FederationFriendService.cs` — handshake, scoped Jellyfin keys,
  friend lifecycle, discovery, pools, per-user rule propagation, and Companion
  linking.
- `Services/TailscaleService.cs` and `ProcessRunner.cs` — environment checks,
  install/login/Funnel workflow, process cancellation, and timeouts.
- `Companion/Program.cs`, `CompanionState.cs`, `PlexAuth.cs`, `PlexClient.cs`,
  `JellyfinImportService.cs`, `StrmExporter.cs`, and
  `ImportSyncBackgroundService.cs` — Companion backend.
- `Companion/wwwroot/index.html` — Companion single-page UI.

Tests and project history:

- `Tests/` — xUnit/Moq regression suite. Locate the closest existing fixture
  before creating a new one; streaming and sharing tests encode hard-won bugs.
- `KNOWN_ISSUES.md` — confirmed unresolved limitations.
- `ideas.md` — historical design notes and root-cause writeups. Read the
  relevant section before modifying persistence or playback.
- `manifest.json`, `meta.json`, and `Jellyfin.Federation.zip` — plugin release
  metadata/artifact.
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
  URLs. These capabilities do not yet carry an expiry; see `TODO.md`.
- Per-request provider URLs cryptographically bind `requestingUserId`; never
  regress to appending an unsigned user id. Userless static paths must continue
  to pass `IsAllowedForEveryConfiguredUser` both when built and when streamed.
- The cloud badge is controlled by `ShowFederatedCloudBadges`, defaults off, and
  reconciles already-rendered cards when the setting changes. It anchors to
  `.cardImageContainer`/`.listItemImage`, and the source label is idempotent.
- The settings page now treats `Catalog` as local-only outgoing sharing (Movies
  or Series, newest first) and `Downloads` as remote-only acquisition (Movies or
  Episodes, newest first). Series-level exclusions are inherited by seasons and
  episodes in `FederationPeerAccessService`; do not remove that ancestor check.
- `PreferHigherQualityRemotes` only enables suggestions.
  `EnableQualityReplacementActions` separately reveals actions, and the API
  accepts exactly one current candidate per call. The download service rechecks
  provider identity and quality both at start and immediately before deletion.
- `Configuration/configPage.html` is approximately 4,700 lines and
  `Web/federation-badge.js` approximately 1,000 lines. Make narrow changes with
  regression tests before attempting structural cleanup.

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
- A local Jellyfin test data tree exists at `/var/home/cranky/jellyfin-test`.
  Determine whether it is active before treating old logs or databases as live.
  Never modify production/test data merely to make a test pass without first
  resolving which instance is in scope.
