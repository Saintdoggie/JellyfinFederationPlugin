# Federation Plugin Rewrite Plan

## Target Architecture

```
Remote Jellyfin A ─┐
Remote Jellyfin B ─┼─→ FederationPlugin (live query + metadata cache)
Remote Jellyfin C ─┘         │
                             ├─→ IItemResolver: federation:// → virtual items (live)
                             ├─→ IRemoteMetadataProvider: pulls+merges by provider ID
                             ├─→ IMediaSourceProvider: multiple sources per item (dedup)
                             ├─→ Direct stream redirect (no proxy)
                             └─→ Auto-provisioned libraries (no user folder step)
```

Key principle: the `.strm`/`.nfo` file approach is **deleted entirely**. Items live only as in-memory/resolved virtual items keyed by provider IDs, with a persisted cache for performance.

### Confirmed decisions

- Architecture: hybrid live + cache (live resolution, background refresh as cache fill).
- Dedup: by provider ID (IMDb/TMDb/TVDB), with raw fallback for items lacking provider IDs.
- Streaming: direct to remote server with embedded API token (friend group acceptable).
- Scope: full rewrite to target architecture.
- Migration: not needed — start fresh, no legacy `.strm` files to carry over.

---

## Phase 1 — Foundation & Setup Pain Fixes

### 1.1 Configuration overhaul (`Configuration/PluginConfiguration.cs`)
- Add `ServerUrl` (local server's own reachable URL, auto-detected, overridable) — replaces hardcoded `localhost:8096`.
- Add `CachePath` (default: plugin data dir, exposed in UI) — no more hidden AppData guesswork.
- Add `StreamingMode` enum per `RemoteServer`: `Direct` / `Proxy` (default `Direct`).
- Add `EnableDedup` bool (default true), `DedupProviderIds` list (default `["imdb","tmdb","tvdb"]`).
- Add `AutoProvisionLibraries` bool (default true).
- Remove the implicit assumption that the user will add libraries manually.

### 1.2 Local server URL auto-detection
- On `Plugin` init, resolve the local Jellyfin bound address via `IServerConfigurationManager` + `IHttpServer` / `ServerConfiguration.LocalNetworkAddresses`. Cache as `ServerUrl`.
- Expose `GET /Plugins/Federation/SystemInfo` showing detected URL so user can verify/correct.
- Used by stream redirect endpoints and image provider.

### 1.3 Auto-provision libraries
- In `FederationEntryPoint.RunAsync`, after config load, if `AutoProvisionLibraries` is true, call a new `LibraryProvisioningService.EnsureLibrariesAsync()` which uses `ILibraryManager.AddVirtualFolder` to create one library per `LibraryMapping` (Movies / TV / Music types). Idempotent — skip if folder with the same name already exists.
- Wire `LibraryOptions.PathInfos` to `federation://<mappingName>` virtual path (already half-done in `FederationVirtualFolderManager.cs:96` — finish it).
- Re-run provisioning on mapping add/remove via UI.
- This replaces the "go to Dashboard → Libraries → add folder manually" message at `FederationSyncService.cs:74-81`.

### 1.4 Delete file-based approach
- Remove `Services/FederationFileService.cs` (and `.strm`/`.nfo` writing entirely).
- Remove references from `Services/FederationSyncService.cs` (`SyncAllAsync`, `SyncServerAsync`).
- Remove `ClearAll`, `RescanLibraries`, `GetFederationBasePath`, file-based status/health/statistics endpoints from `Configuration/FederationPluginController.cs`.
- The hardcoded `localhost:8096` reference at `FederationFileService.cs:515` goes away with the file.

### 1.5 DI registration
- `Plugin.cs` doesn't register anything in DI today — every consumer news up `FederationLibraryManager` manually. Move to a proper `IServiceCollection` extension or register in `FederationEntryPoint`. Required so singletons (cache, clients) are shared.

---

## Phase 2 — Live Item Resolution (no more sync)

### 2.1 `FederationItemResolver` rewrite (`Resolvers/FederationItemResolver.cs`)
- Currently returns null if item not in cache (`FederationItemResolver.cs:84-89`). New behavior: parse `federation://<mappingName>/<providerKey>/<providerId>` path, look up the item live in `FederationItemCache`, materialize `Movie`/`Series`/`Episode`/`Audio` with `Path = federation://...`.
- Cache is filled lazily — see 2.3.

### 2.2 `federation://` path scheme change
- **Current:** `federation://<serverId>/<itemId>` (random GUIDs remote-side, breaks on dedup).
- **New:** `federation://<mappingName>/<providerName>:<providerId>` for deduped items, or `federation://<mappingName>/raw/<serverId>/<remoteItemId>` for items without provider IDs.
- Update `FederationLibraryManager.TryParseFederationPath` accordingly. Keep a legacy parser for migration diagnostics only (not for production resolution).

### 2.3 `FederationItemCache` (in-memory + persisted JSON/SQLite)
- New service. Keyed by `(mappingName, providerName, providerId)` for deduped entries; `(mappingName, "raw", serverId, remoteItemId)` for raw.
- Holds: resolved `BaseItem` shell, list of remote sources `[{serverId, remoteItemId}]`, `PrimarySourceIndex`, last-refreshed timestamp.
- Persisted to `<CachePath>/federation-cache.json` (or SQLite if it grows).
- Background refresh task (`FederationRefreshTask`, replaces `FederationSyncTask`) walks each mapping, calls `RemoteServerClient.GetItemsAsync`, merges by provider ID, updates cache. Run hourly by default (configurable). Failure = use stale cache; never blocks resolution.

### 2.4 `FederationLibraryManager.AddFederatedItemAsync` — finish it
- Replace the TODO at `Services/FederationLibraryManager.cs:184` with `ILibraryManager.CreateItem` (or register via the resolver path). Today the in-memory `_federatedItems` dict is the only store; that's why nothing shows up.

---

## Phase 3 — Deduplication

### 3.1 Remote item → provider ID extraction
- `RemoteServerClient.GetItemsAsync` currently doesn't request `ProviderIds`. Add `Fields=...,ProviderIds` to the query string at `Services/RemoteServerClient.cs:80-82` and parse the `ProviderIds` dictionary from each item element.

### 3.2 Merge logic in `FederationRefreshTask`
- For each remote item:
  - If it has any provider ID in `DedupProviderIds`: merge into existing cache entry for that `(providerName, providerId)`, appending `(serverId, remoteItemId)` to the sources list.
  - If no provider ID: create a "raw" entry keyed by `(serverId, remoteItemId)`.
- Track which source is "primary" (first seen, or by configured per-server priority).
- Confirmed: items without provider IDs (home movies, music, photos) fall back to raw entries. Music dedupe by metadata is out of scope for v1.

### 3.3 Multi-source `IMediaSourceProvider`
- Rewrite `Services/FederationMediaSourceProvider.GetMediaSources` to return one `MediaSourceInfo` per remote source for the item, each with:
  - `Id` = `<serverId>:<remoteItemId>`
  - `Name` = `FriendA's Server (direct)` / `FriendB's Server (direct)`
  - `Path` = direct remote stream URL (see Phase 4)
  - `SupportsDirectPlay` / `SupportsDirectStream` = true
  - `IsRemote` = true
- Jellyfin's UI then lets the user pick the source on playback — native multi-source behavior.

---

## Phase 4 — Direct Streaming

### 4.1 New stream redirect endpoint
- Replace `FederationStreamHandler` proxy logic with a 302-redirect endpoint at `GET /Plugins/Federation/Redirect?serverId=X&itemId=Y`.
- Build URL: `<remoteServerUrl>/Videos/<itemId>/stream?api_key=<apiKey>&Static=true` (pattern already present at `FederationPluginController.cs:500`).
- Return `Redirect(url)` instead of proxying the body. This is the "direct" mode.
- Preserve Range header support by passing through as query params (Jellyfin supports direct range on its stream endpoint).
- Keep `StreamingMode=Proxy` as a fallback option per server — existing `FederationStreamHandler` becomes the proxy implementation, used only when configured.

### 4.2 Token safety
- API keys live in the redirect URL. For a private friend group this is acceptable. Document clearly in config UI: "Direct mode exposes the remote API key to clients on your network." (warning added regardless — confirmed decision.)

### 4.3 Image provider direct URLs
- `Providers/FederationImageProvider.cs` already builds direct URLs to the remote server — no change needed except ensuring `api_key` is appended where the remote server requires it for unauthenticated image fetches (make it a per-server config flag).

---

## Phase 5 — Metadata & Images

### 5.1 `IRemoteMetadataProvider` implementation
- New `Providers/FederationMetadataProvider.cs` (movies / series / episodes / audio).
- For a given item, look up the cache, fetch fresh metadata from the primary source's remote server, populate `Overview`, `Genres`, `Studios`, `People`, `ProductionYear`, etc.
- Disable Jellyfin's own TMDb/TVDB lookups for federated libraries (via `LibraryOptions.TypeOptions[].MetadataFetchers`) so remote metadata isn't overwritten — this was a bug source in the old NFO approach.

### 5.2 People / cast
- `RemoteServerClient.GetItemsAsync` currently requests `People` field but the parser at `Services/RemoteServerClient.cs:130-300` doesn't extract them. Add parsing.

---

## Phase 6 — UI / Config Page

### 6.1 Rewrite `Configuration/configPage.html` / `configPage.js`
- Sections: Servers (add/edit/test/delete), Library Mappings (with auto-provision toggle per mapping), Streaming mode per server, Dedup settings, Cache path, Diagnostics.
- Remove the manual "add folder to library" instructions.
- Add a "Refresh cache now" button (triggers `FederationRefreshTask` on demand).
- Add a "Diagnostics" panel: show detected local URL, cache path, last refresh, server reachability.
- Add the direct-mode token warning per the streaming decision.

### 6.2 Migration helper (optional, lightweight)
- On first run after upgrade, if old `~/.local/share/jellyfin/federation/*.strm` files exist, show a one-time banner offering to wipe them and provision virtual libraries. Assumed not needed for this user's setup, but keep the detection in place for other users upgrading.

---

## Phase 7 — Cleanup & Hardening

- Delete `Services/FederationFileService.cs`, `Services/FederationVirtualFolderManager.cs` (folded into `LibraryProvisioningService`), old `.strm`/`.nfo` code paths.
- Fix the per-request `new RemoteServerClient` pattern (multiple places, e.g. `FederationPluginController.cs:178, 405, 513`) — use a shared `IRemoteServerClientFactory` keyed by server ID so `HttpClient`s are reused.
- Security: replace `[AllowAnonymous]` on mutating endpoints (`UpdateConfiguration`, `AddServer`, `DeleteServer`, `ClearAll`) with `[RequiresElevation]`. Today any user (or anyone on the network) can reconfigure the plugin.
- Add cancellation token plumbing through refresh task.
- Add tests (none exist today): unit tests for path parsing, dedup merge, and cache serialization.

---

## Known technical risk

**`ILibraryManager.CreateItem` for virtual items** — Jellyfin's API for fully virtual (non-filesystem) items is finicky. The `IItemResolver` path is the supported route but requires the parent folder to exist. **Must be verified against Jellyfin 10.11 ABI** during implementation: whether `CreateItem` works for resolver-only items or whether a real backing folder (empty, marked as federation root) is needed. This is the single highest technical risk and will be the first spike in the build phase.

## Build order

1. Phase 1 (setup pain fixes + DI) — deliverable: plugin no longer requires manual folder setup.
2. Phase 2 (resolver + cache) — deliverable: items appear live without sync.
3. Phase 3 (dedup + multi-source) — deliverable: same title on two servers shows once.
4. Phase 4 (direct streaming) — deliverable: playback works direct-to-remote.
5. Phase 5 (metadata + images) — deliverable: metadata populated, no TMDb override.
6. Phase 6 (UI) — deliverable: one-click setup.
7. Phase 7 (cleanup + tests) — deliverable: production-ready.
