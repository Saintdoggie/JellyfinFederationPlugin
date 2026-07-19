# Code Review: JellyfinFederationPlugin

## Project Overview

A Jellyfin plugin that aggregates content from multiple remote Jellyfin servers into virtual libraries using a `federation://` path scheme, with provider-ID-based deduplication, in-memory + JSON-persisted cache, and direct/proxy streaming modes. Targets Jellyfin 10.11 ABI, .NET 9.

The architecture described in `PLAN.md` is largely implemented and the code is reasonably organized. However, there are several security issues, resource leaks, concurrency bugs, and incomplete integrations that would prevent this from working reliably in production.

---

## Critical Severity

### 1. `FederationEntryPoint.RunAsync` is never invoked
`FederationEntryPoint.cs:39` defines `RunAsync()` but nothing calls it. `Plugin.cs:71` only registers services in DI — there's no `IPluginStartup` / `IAsyncPlugin` / `RunAsync` hook wired into the Jellyfin lifecycle. Result: the cache is never loaded from disk, the local server URL is never auto-detected, and libraries are never auto-provisioned on startup. The `FederationItemCache.Initialize` call only happens if some other code path triggers it. The plugin will appear dead until a user manually hits `POST /Plugins/Federation/Refresh`.

### 2. `Plugin.RegisterServices` is never called by Jellyfin
`Plugin.cs:71` — `RegisterServices` is a `static` method, not an override of `BasePlugin.RegisterServices`/`IPlugin.RegisterServices`. The signature should match the Jellyfin DI hook (`public override void RegisterServices(IServiceCollection services)` or the `IPluginServiceRegistrator` interface). As written, none of the services (cache, sync, providers, resolver, controller dependencies) are registered, so DI resolution of `FederationController` will fail with missing-service exceptions. This is the single most blocking issue.

### 3. `meta.json` uses a placeholder GUID
`meta.json:2` — `"guid": "12345678-1234-1234-1234-123456789abc"` is a hardcoded placeholder. `Plugin.cs:38` mirrors it. Jellyfin identifies plugins by GUID; collisions with any other plugin (or future Jellyfin-bundled plugin) using the same placeholder will cause install/load failures. Generate a real `Guid.NewGuid()` value and use it consistently in both `meta.json` and `Plugin.cs`.

### 4. `[AllowAnonymous]` on `Redirect` and `Stream` exposes API keys to anyone on the network
`FederationPluginController.cs:322` and `:353`. In Direct mode the redirect URL contains the remote server's `api_key` in the query string (`FederationStreamHandler.cs:47`). Combined with `AllowAnonymous`, any unauthenticated client on the network can request a redirect URL and recover the remote server's API token. Even in a "friend group" deployment this is too broad. At minimum require authentication (`[Authorize]`) — and consider a short-lived signed-token scheme instead of forwarding the raw remote API key. The PLAN.md §4.2 explicitly acknowledged the warning but the mitigation was never implemented.

### 5. `FederationStreamHandler` proxy doesn't forward auth to the remote stream request
`FederationStreamHandler.cs:111` — `BuildDirectStreamUrl` puts `api_key=` in the query string, but for the proxied request the API key is in the URL (fine). However the remote server may require `X-Emby-Token` header instead; the factory client uses the header, but the proxy uses the query string. If the remote enforces header-only auth, proxy mode silently 401s. Pick one and apply it consistently — and the existing `IRemoteServerClientFactory` HttpClient for that server (which already has the header) should be reused for proxying rather than a separate `_proxyHttpClient`.

### 6. `RedirectStream` action signature is broken
`FederationPluginController.cs:323` — The method returns `Task<IActionResult>` but the body does `return Task.FromResult<IActionResult>(new EmptyResult())` after calling `_streamHandler.HandleRedirectAsync(...)` / `HandleProxyAsync(...)`. Those handler methods write directly to `Response` and then the controller also returns `EmptyResult`. For the proxy path this is fine, but `HandleRedirectAsync` calls `response.Redirect(url, false)` which writes a 302 — and then returning `EmptyResult` triggers the framework to write its own empty 200 body on the same response. Behavior is undefined / framework-dependent. Should return the redirect result directly from the controller rather than writing to `Response` inside the handler.

---

## High Severity

### 7. `HttpClient` socket exhaustion in `FederationImageProvider` and `FederationMetadataProvider`
`FederationImageProvider.cs:35` and `FederationMetadataProvider.cs:36` construct `new HttpClient()` per instance. Since these are DI singletons this isn't catastrophic, but neither type is `IDisposable`, so on plugin unload the sockets aren't cleanly closed. More importantly, these should reuse the shared `IRemoteServerClientFactory` HttpClients (which already have base address + auth) instead of making raw unauthenticated calls. The image URL fetched in `GetImageResponse` (`:141`) already embeds `api_key` in the URL so it works, but the metadata provider's `_httpClient` is never actually used — dead code that should be removed.

### 8. `FederationStreamHandler._proxyHttpClient` not disposed
`FederationStreamHandler.cs:30` — `FederationStreamHandler` is registered as a DI singleton (`Plugin.cs:78`) and creates a `new HttpClient` with a 3-hour timeout, but the class is not `IDisposable`. On plugin unload the underlying `HttpClientHandler`/sockets leak. Implement `IDisposable` and dispose the client, or inject an `IHttpClientFactory` and let Jellyfin manage lifetime.

### 9. `RemoteServerClient` from the factory is never disposed by callers
`RemoteServerClientFactory.cs:62` returns a `new RemoteServerClient(server, logger, httpClient)` (with `_ownsHttpClient = false`) for every `GetClient` call. Callers in `FederationMediaSourceProvider.cs:72`, `FederationImageProvider.cs:76`, `FederationMetadataProvider.cs:85`, `FederationSyncService.cs:178`, and the controller's `GetRemoteLibraries`/`TestAllServers` never `Dispose()` it. Since `_ownsHttpClient == false` the shared HttpClient isn't disposed (good), but the `RemoteServerClient` wrapper itself wraps no unmanaged state beyond the shared client — so this is benign today. Still, the pattern is fragile: if anyone adds an owned field to `RemoteServerClient` later, every call site becomes a leak. Either make the wrapper not implement `IDisposable` when sharing, or have callers wrap in `using`.

### 10. `ConcurrentDictionary` race in `FederationItemCache.UpsertByProviderId` / `UpsertRaw`
`FederationItemCache.cs:107-120` and `:134-147` — The `_entries.AddOrUpdate` update delegate calls `existing.AddSource` + `existing.UpdateFromRemote` and mutates the existing entry in place. `FederatedCacheEntry.AddSource` (`:448`) mutates `Sources` (a `List<FederatedSource>`) without any lock, and `ReSortSources` (`:499`) reassigns `Sources` to a new list. Two concurrent refresh tasks for the same mapping (or the same provider ID) will race on `Sources.Add` / `OrderBy...ToList` and can corrupt the list or throw `InvalidOperationException` (Collection modified). The `RefreshMappingAsync` calls `ClearMapping` then iterates sources sequentially today, so in practice the race is rare, but `SyncServerAsync` can run concurrently with `SyncAllAsync` and both touch the same mapping. Add a lock per entry (or per cache) around mutations of `Sources`/`PrimarySourceIndex`.

### 11. `FederationSyncService.RefreshMappingAsync` clears the mapping before re-fetching
`FederationSyncService.cs:141` — `_cache.ClearMapping(mapping.LocalLibraryName)` wipes all entries for that mapping, then iterates remote sources. If a remote server is unreachable, its entries are gone with no recovery ("Failure = use stale cache" was the stated principle in PLAN.md §2.3 but isn't honored). Either snapshot-and-restore on failure, or only clear entries for the specific `(mapping, server)` being refreshed, or clear after a successful fetch.

### 12. `SyncProgressTracker.TotalItems` is initialized to 100
`FederationSyncService.cs:45` — `SyncProgressTracker.Start(operationId, 100)` hardcodes 100 as the "total," then `Update(operationId, 0, mappings.Count, ...)` overwrites it with the mapping count, not the item count. The percentage reported to the UI is meaningless (denominator = number of mappings, numerator = number of items processed). The progress model conflates two dimensions. Either track mapping-level progress separately from item-level, or compute total items up front.

### 13. `SyncProgressTracker` never has `Cleanup()` invoked
`SyncProgressTracker.cs:81` defines `Cleanup()` but nothing calls it. The `ConcurrentDictionary` grows unbounded; every refresh (manual or scheduled) adds an entry that lives for the process lifetime. Wire it into the refresh task's completion or a periodic scheduled task.

### 14. `FederatedCacheEntry.FederationPath` is dead/duplicated logic
`FederationItemCache.cs:427-438` — Both branches of the `if` return `"federation://" + Key`, so the `if` is pointless. Worse, the property relies on `Key` already containing the full canonical path suffix (mapping/provider:id or mapping/raw/server/id) — which it does, but the property would silently produce wrong paths if anyone changes the key format. Replace with explicit construction via `TryParsePath`/`BuildProviderPath`/`BuildRawPath`, or just always return `"federation://" + Key`.

### 15. `UpdateFromRemote` priority logic is incorrect
`FederationItemCache.cs:472-473` — `isPrimary` is computed as `Sources[PrimarySourceIndex].ServerId == serverId && Sources[PrimarySourceIndex].RemoteItemId == remoteItemId`, but `AddSource` was just called (or is being called concurrently) and may have re-sorted `Sources`, so `PrimarySourceIndex` may no longer point at the source this update is for. Then `if (!isPrimary && !string.IsNullOrEmpty(Metadata.Name)) return;` skips the update. Net effect: the first server to populate the entry wins forever even if a higher-priority server is added later, because the higher-priority server's update is treated as "not primary" and discarded. The `ReSortSources` in `AddSource` does set `PrimarySourceIndex = 0` correctly, but `UpdateFromRemote` is called *after* `AddSource`, so the check should compare against `Sources[PrimarySourceIndex]` *after* the re-sort — which is what the code does, but the issue is that `UpdateFromRemote` skips non-primary sources even when the primary's metadata is empty for a field. Either always update from the highest-priority source that has a non-null value, or rewrite this to track which source last contributed metadata.

### 16. `FederationMediaSourceProvider` is not registered in DI
`Plugin.cs:71-84` registers many services but **`FederationMediaSourceProvider` is missing**. Jellyfin discovers `IMediaSourceProvider` implementations via DI, so as written, federated items will have zero media sources and playback will fail. Add `serviceCollection.AddSingleton<MediaSourceProvider, FederationMediaSourceProvider>();`.

### 17. Resolver, providers, and task are registered but Jellyfin discovers them by interface, not concrete type
`Plugin.cs:79-82` registers `FederationItemResolver`, `FederationImageProvider`, `FederationMetadataProvider`, `FederationRefreshTask` as their concrete types. Jellyfin's `IItemResolver`, `IRemoteImageProvider`, `IRemoteMetadataProvider`, and `IScheduledTask` are discovered via DI scanning for the interface. They need to be registered *as the interface* (e.g. `services.AddSingleton<IItemResolver, FederationItemResolver>()`) or via the assembly-scanning path Jellyfin uses. Combined with issue #2 (RegisterServices never being called), none of these are live.

---

## Medium Severity

### 18. `RemoteServerClient.GetLibrariesAsync` uses `_server.UserId` without falling back to override
`RemoteServerClient.cs:261` ignores any `userId` parameter (the method has none). Other methods (`GetItemsAsync`, `GetItemAsync`, `GetPlaybackInfoAsync`) accept an override. `GetLibrariesAsync` should too for consistency, and the controller's `GetRemoteLibraries` (`FederationPluginController.cs:285`) calls it without a userId — so it always falls back to `users[0].Id`, which on a remote server with multiple users may be an admin user whose library list differs from the configured user.

### 19. `ParseItem` falls back to `Guid.NewGuid()` for missing/invalid IDs
`RemoteServerClient.cs:376` — `item.Id = Guid.TryParse(idStr, out var guid) ? guid : Guid.NewGuid();`. If the remote returns an item without an `Id`, a random GUID is generated on every refresh. That GUID is then used as a cache key (in raw path form) and as the `RemoteItemId` in `FederatedSource`, so every refresh creates a new "source" and the cache grows monotonically with phantom items. Skip items with no valid `Id` instead.

### 20. `FederationItemCache.SaveAsync` is synchronous work on a `Task` return
`FederationItemCache.cs:173-204` — `File.WriteAllText` is blocking I/O wrapped in a method returning `Task.CompletedTask`. Called from `FederationSyncService.SyncAllAsync` (`:72`) in an async context, this blocks a thread-pool thread. Make it truly async (`await File.WriteAllTextAsync`) and consider atomic writes (write to `.tmp`, then `File.Move` with overwrite) to avoid corrupting the cache on crash mid-write.

### 21. `FederationItemCache.SaveAsync` swallows exceptions silently
`FederationItemCache.cs:198-201` logs a warning but the caller (`FederationSyncService.SyncAllAsync:72`) treats the sync as successful. The user sees "Refreshed N items" even though nothing was persisted. Surface the failure in the `SyncResult`.

### 22. `LoadFromDisk` doesn't validate entries
`FederationItemCache.cs:346-368` deserializes a list of `FederatedCacheEntry` and stuffs them into `_entries` keyed by `entry.Key`. If the on-disk file is hand-edited or corrupted such that `entry.Key` doesn't match `BuildProviderKey(...)`/`BuildRawKey(...)`, future lookups via `NormalizeKey` will miss. Validate `Key` against the parsed `FederationPath` and skip mismatches.

### 23. `FederationItemCache.ClearMapping` is O(N) and runs inside a refresh
`FederationItemCache.cs:152-159` does a LINQ Where + ToList over all entries, then TryRemove each. For large caches (tens of thousands of items) this is fine but allocates heavily. Acceptable, but consider a secondary index by `MappingName` if cache grows.

### 24. `FederationLibraryManager.MaterializeItem` always returns a fresh item with no parent
`FederationLibraryManager.cs:92-147` — The materialized `BaseItem` has `Path`, `Id`, `Name`, etc., but no `Parent` / `ParentId` set. Jellyfin's library scan / UI typically requires items to be parented under a `Folder` (the virtual library root). Without a parent, the item may resolve but never appear in any library view. The PLAN.md "Known technical risk" section explicitly flagged this — it remains unresolved. Need to look up the `CollectionFolder` for the mapping's `LocalLibraryName` and set `item.ParentId` to it.

### 25. `MaterializeItem` doesn't set `ProviderIds` on the `ProviderIds` dictionary that's actually a `Dictionary<string, string>` on `BaseItem`
`FederationLibraryManager.cs:124` — `item.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)` re-assigns the property. In current Jellyfin, `BaseItem.ProviderIds` is keyed access; direct reassignment may bypass some internal indexing. Use `item.ProviderIds.Clear()` + `item.ProviderIds[kvp.Key] = kvp.Value` or the `SetProviderId(...)` API.

### 26. `CreateItemShell` default falls back to `Movie`
`FederationLibraryManager.cs:239` — Unknown `itemType` strings produce a `Movie`. If a remote returns `"MusicVideo"` or `"BoxSet"` (both listed as options in `configPage.html:146`), the item will be silently mis-typed. Either expand the switch or skip unknown types.

### 27. `FederationMetadataProvider.GetMetadata` casts via `(MetadataResult<T>)await GetMetadataCommon<T>(...)`
`FederationMetadataProvider.cs:56` etc. — `GetMetadataCommon<T>` returns `object` and is cast. This works but loses type safety and throws `InvalidCastException` if the internal `result` isn't actually `MetadataResult<T>` (it always is today, but it's brittle). Make `GetMetadataCommon` generic-returning (`Task<MetadataResult<T>>`) and drop the casts.

### 28. `FederationMetadataProvider.GetMetadataCommon` doesn't set `Path` or `ProviderIds["Federation..."]` on the returned item
`FederationMetadataProvider.cs:99-114` — The constructed `T` has `ProviderIds` from the remote item but no `Path` and no `FederationSource`/`FederationRemoteId` markers that `MaterializeItem` adds. When Jellyfin calls this provider during a metadata refresh, the returned item will lack the federation tracking IDs that `FederationMediaSourceProvider` relies on. Set `item.Path = path` and add the same federation provider-id markers as `MaterializeItem`.

### 29. `FederationRefreshTask.GetDefaultTriggers` reads `Plugin.Instance` at registration time
`FederationRefreshTask.cs:97-111` — The interval is computed when Jellyfin asks for default triggers (typically at plugin load), so changing `RefreshIntervalHours` in the config UI will not update the schedule until the plugin is reloaded. Jellyfin supports `TaskTriggerInfo` with runtime-evaluated intervals via `IConfigurableScheduledTask` — but the trigger itself is a static descriptor. Document this or override the trigger when config changes.

### 30. `FederationRefreshTask.ExecuteAsync` swallows `Exception` but rethrows others
`FederationRefreshTask.cs:89-93` — catches `Exception` and `throw`s, which is correct, but combined with the `OperationCanceledException` branch above, the catch order is fine. However, the `progress?.Report(100)` at `:73` runs even if `SyncAllAsync` returned a `SyncResult` with `Success == false`. Only report 100 on success.

### 31. `FederationPluginController.AddServer` doesn't validate URL format or duplicate names
`FederationPluginController.cs:200-221` — Accepts any `RemoteServer` object, generates a new `Id`, and appends. No URL validation (could be `javascript:...`), no duplicate name check, no `Enabled` default. A user pasting a malformed URL will silently store it and every later operation against that server will fail. Validate `Uri.TryCreate(server.Url, UriKind.Absolute, out _)` and reject duplicates by name.

### 32. `UpdateServer` overwrites `ApiKey` unconditionally
`FederationPluginController.cs:236` — If the UI sends an empty `ApiKey` (e.g., user edited only the name), the stored key is wiped. The frontend `configPage.js` always sends the current value, but the API contract should preserve the existing key if the incoming one is null/empty.

### 33. `GetSystemInfo` and `GetStatus` are `[AllowAnonymous]`
`FederationPluginController.cs:113` and `:413` expose cache counts, server names, streaming modes, and the detected local URL to unauthenticated clients. Information disclosure. Require at least standard authorization.

### 34. `TestServer` is `[AllowAnonymous]` and makes outbound requests with user-supplied URL
`FederationPluginController.cs:147` — An unauthenticated user can cause the Jellyfin server to make arbitrary outbound HTTP requests to any URL (SSRF). The supplied `RemoteServer.Url` is used verbatim as `HttpClient.BaseAddress`. Require elevation, and validate the URL scheme (http/https only) and reject loopback/link-local unless explicitly allowed.

### 35. `TestServer` returns 200 with `success: false`
`FederationPluginController.cs:159, 165, 191` — Returns `Ok(new { success = false, ... })` for failures. This makes the frontend's `r.ok` check (`configPage.html:215`) treat them as successes in some flows. Use `StatusCode(502, ...)` or similar for actual errors, or be consistent across all error paths.

### 36. Controller route prefix mismatch
`FederationPluginController.cs:20` — `[Route("Plugins/Federation")]` but `Plugin.cs:66` advertises `/Plugins/Federation/ConfigPage`. The `GetConfigPage` action (`:51`) is at `Plugins/Federation/Config` (the route is `Plugins/Federation` + `Config`). `redirectPage.html:25` redirects to `/Plugins/Federation/Config`, which is consistent. But `Plugin.GetPages()` (`:54-61`) only registers `redirectPage.html` under `Name = "Jellyfin Federation"`, so Jellyfin's plugin page link will point at `redirectPage.html` — fine, but the `configPage.html` resource is only reachable via the controller. The `.csproj` embeds `configPage.html` (`:14`) and the controller reads it via reflection (`:59`) — works, but feels like an end-run around `IHasWebPages`. Register both pages via `GetPages()`.

### 37. `configPage.js` is orphaned
`configPage.js` is not referenced by `configPage.html` (which inlines its own script) and not embedded as a resource in `.csproj`. It's dead code referencing `#federationConfigPage` and `ApiClient.updatePluginConfiguration` — patterns that don't match the inline `configPage.html`. Delete it or commit to one approach.

### 38. `configPage.html` has no XSS escaping in `escapeHtml` for attribute context
`configPage.html:92-97` uses `div.textContent = s; return div.innerHTML;` — fine for body text but is then injected into `value="..."` and `data-sname="..."` attributes (`:114, :263`). A server name containing `"` breaks out of the attribute. Use proper attribute escaping (escape `"` and `&` at minimum) for attribute contexts.

### 39. `redirectPage.html` uses `setTimeout(..., 100)` for redirect
`redirectPage.html:23-26` — 100ms delay with no fallback if JS is disabled. Use `<meta http-equiv="refresh" content="0;url=/Plugins/Federation/Config">` as a no-JS fallback.

---

## Low Severity / Nitpicks

### 40. `SyncProgressTracker.cs` has wildly inconsistent indentation
Lines 11-92 mix tabs/spaces and have leading whitespace that doesn't match the surrounding block (e.g. `:16` `   public static void Start` is indented 3 spaces inside the class). Run `dotnet format` against the file.

### 41. `FederationPluginController` namespace is `Jellyfin.Plugin.Federation.Api`
`FederationPluginController.cs:14` — Every other file is in `Jellyfin.Plugin.Federation.<Subfolder>`. The `Api` subnamespace is undocumented and inconsistent with the folder name `Configuration/`. Move to `Jellyfin.Plugin.Federation.Configuration` or rename the folder to `Api/`.

### 42. `RefreshServerRequest` class lives at top level in controller file
`FederationPluginController.cs:494-497` — Move to a separate `Requests/` folder or nest as a private class. Mixing request DTOs with controllers makes the file harder to scan.

### 43. `FederationItemCache.TryParsePath` uses `Substring` instead of spans
`FederationItemCache.cs:258-294` — For a hot path (called per resolution), allocate via `Substring` repeatedly. Use `ReadOnlySpan<char>` for the parse. Low priority — paths are short — but a clean improvement.

### 44. `FederationItemCache._cacheFilePath` defaults to `string.Empty` not null
`FederationItemCache.cs:29` — `Initialize` (`:55`) coerces null to empty. `SaveAsync` (`:175`) and `LoadFromDisk` (`:341`) check `IsNullOrEmpty`. Consistent, but using `string?` throughout would be clearer.

### 45. `Plugin.GetDefaultCachePath` returns a file path, not a directory
`Plugin.cs:51` — Returns `Path.Combine(DataFolderPath, "federation-cache.json")`. The name `CachePath` in `PluginConfiguration` (`:20`) and the SystemInfo endpoint (`FederationPluginController.cs:123-125`) expose this as a "cache path," which a user might reasonably interpret as a directory. Either rename to `CacheFilePath` or treat as a directory and append the filename inside.

### 46. `FederationLibraryManager.TryParseLegacyFederationPath` is undocumented dead code
`FederationLibraryManager.cs:210-229` — No callers, no tests. PLAN.md §2.2 said to keep it "for migration diagnostics only" but nothing uses it. Either wire it into a one-time migration check or remove it.

### 47. `FederationStreamHandler.BuildDirectStreamUrl` URL-encodes `rangeHeader` as a query param named `Range`
`FederationStreamHandler.cs:50` — Appends `&Range=<encoded>` to the stream URL. Jellyfin's stream endpoint does not accept `Range` as a query parameter; it expects an HTTP `Range` header. The proxy path correctly forwards the header (`:114`) but the redirect path bakes it into the URL where it's silently ignored by the remote server. Drop the `Range` query param from the redirect URL; the client will re-issue its own `Range` header against the redirected URL.

### 48. `RemoteServerClient.BuildDirectStreamUrl` and `FederationStreamHandler.BuildDirectStreamUrl` duplicate logic
`RemoteServerClient.cs:337-340` vs `FederationStreamHandler.cs:39-54`. Two implementations of the same URL build, diverging only in the `Range` query param. Centralize into one helper.

### 49. `FederationStreamHandler.HandleProxyAsync` flushes per 80KB chunk
`FederationStreamHandler.cs:155` — `await response.Body.FlushAsync(...)` after every 81920-byte read forces a TCP send per chunk. For high-bandwidth streams this is fine but adds syscall overhead. Drop the explicit `FlushAsync` and let the response buffer naturally, or flush less often.

### 50. `FederationSyncService.RefreshSourceAsync` uses `pageNumber` for a safety cap but never uses the variable otherwise
`FederationSyncService.cs:187, 224` — `pageNumber` is incremented and compared to 1000 but isn't part of the request. Use `startIndex / pageSize + 1` or remove the variable and just cap `startIndex`.

### 51. `RemoteServerClient.GetItemsAsync` catches all exceptions and returns empty list
`RemoteServerClient.cs:140-144` — Network errors, auth failures, and parse errors all collapse into "empty list." The caller (`FederationSyncService.RefreshSourceAsync:200`) treats `page.Count == 0` as "done" and stops paging — so a transient 500 mid-pagination silently truncates the sync. Distinguish "no more items" from "request failed" by throwing or returning a richer result.

### 52. `RemoteServerClient.CreateDefaultHttpClient` adds `X-Emby-Token` without checking if `ApiKey` is empty
`RemoteServerClient.cs:574` — If `ApiKey` is empty (user forgot to fill it in), the header is added with an empty value. Some servers may treat this as an auth attempt and reject; others ignore. Log a warning when `ApiKey` is empty.

### 53. `LibraryProvisioningService.GetCollectionType` maps `Photo`/`PhotoAlbum` to `homevideos`
`LibraryProvisioningService.cs:162` — That mapping is surprising; Jellyfin has a `photos` collection type. Verify intent against Jellyfin 10.11's `CollectionTypeOptions` enum.

### 54. `LibraryProvisioningService.EnsureLibraryAsync` doesn't pass `cancellationToken` to `AddVirtualFolder`
`LibraryProvisioningService.cs:113-117` — The token is accepted by the method but not forwarded. Check whether `ILibraryManager.AddVirtualFolder` has a cancellation overload and use it.

### 55. `FederationItemCache.LoadFromDisk` uses `File.ReadAllText` (sync) inside an instance method called from `Initialize` (sync)
`FederationItemCache.cs:348` — `Initialize` is called from `FederationEntryPoint.RunAsync` (`:72`), an async context. Use `File.ReadAllTextAsync` and make `Initialize` async.

### 56. `FederationEntryPoint.DetectLocalServerUrl` has a try/catch around a single return statement
`FederationEntryPoint.cs:87-100` — The body is `return "http://localhost:8096";` — nothing throws. The catch is unreachable. Either implement real detection (bind addresses from `IServerConfigurationManager`) or remove the try/catch.

### 57. Tests use `.Wait()` on async methods
`FederationCacheSerializationTests.cs:30, 49` — `cache.SaveAsync().Wait()` blocks. Use `await` with `async Task` test methods (xunit supports this).

### 58. Tests pass `null!` for `ILibraryManager` to `FederationItemCache`
`FederationCacheDedupTests.cs:19`, `FederationCacheSerializationTests.cs:16` — Works today because the tests only exercise upsert/clear/parse paths that never touch `_libraryManager`, but if any code path under test touches it, NRE. Inject a stub.

### 59. Test coverage is narrow
Only cache dedup, serialization round-trip, and path parsing are tested. No tests for: `FederationSyncService.UpsertRemoteItem` dedup-key selection, `FederationStreamHandler` URL building, `FederationLibraryManager.MaterializeItem` for each item type, `RemoteServerClient.ParseItem` for malformed JSON. PLAN.md §7 called out path parsing / dedup merge / cache serialization — those are done — but the rest of the codebase is unverified.

### 60. `meta.json` `targetAbi` is `10.11.0.0` but `csproj` references `Jellyfin.Controller` `10.11.6`
`meta.json:10` vs `JellyfinFederationPlugin.csproj:36-37`. Align the versions — Jellyfin checks `targetAbi` against its running version, and a mismatch causes the plugin to be rejected on load.

### 61. `.gitignore` line 364-365 lists `/.gitattributes` and `/.gitignore`
`.gitignore:364-365` — A repo's `.gitignore` shouldn't ignore itself. Remove these lines.

### 62. No `AGENTS.md` / `README.md` / build instructions
The repo has `PLAN.md` but no `README.md` with build/test commands. Anyone cloning will have to guess `dotnet build` / `dotnet test`.

### 63. No `dotnet format` / analyzer config
No `.editorconfig`, no `Directory.Build.props`, no analyzer packages. The codebase has inconsistent brace style, indentation (`SyncProgressTracker.cs`), and `var` usage. Adopt `dotnet format` and a style analyzer.

### 64. No CI workflow
`.git/` has no `.github/workflows/`. No automated build or test on PR.

---

## Architectural Observations (not actionable as bugs)

- **`Plugin.Instance` static singleton** is used widely as a service locator (`FederationLibraryManager.GetServer:170`, `FederationSyncService.SyncAllAsync:50`, `FederationPluginController` throughout). This works but makes testing harder and hides DI dependencies. Consider injecting `IOptionsMonitor<PluginConfiguration>` or a `FederationConfigProvider` singleton instead.
- **`FederationItemCache` is a single JSON file**. The PLAN.md §2.3 hedged ("or SQLite if it grows"). For libraries with tens of thousands of items across multiple servers, the JSON load/save at every refresh will become a bottleneck. Worth a spike for SQLite before v1 release.
- **`FederationRefreshTask.GetDefaultTriggers` MaxRuntimeTicks = 30 min** (`FederationRefreshTask.cs:108`). For large federations, a full refresh may take longer than 30 min and be killed mid-save. Make configurable or remove the cap.
- **No retry/backoff on remote server failures.** `RemoteServerClient` catches and returns empty/null immediately. For transient network blips during a refresh, this drops items from the cache (combined with issue #11, permanently until next refresh).

---

## Summary of Blockers

If I had to pick the must-fix issues before any testing:

1. **#2** — `RegisterServices` isn't overriding the Jellyfin hook → DI is empty → nothing works.
2. **#16** — `FederationMediaSourceProvider` isn't registered → no playback.
3. **#17** — Resolver/providers/task registered as concrete types, not interfaces → Jellyfin won't discover them.
4. **#1** — `FederationEntryPoint.RunAsync` never called → cache never loaded → resolver returns null for everything.
5. **#3** — Placeholder GUID → install conflicts.
6. **#24** — Materialized items have no parent → won't appear in libraries.
7. **#4/#34** — `AllowAnonymous` + SSRF + API key exposure → security blocker for any shared deployment.

Everything else is hardening. The bones of the architecture are sound and the PLAN.md is unusually thorough; the gap is in the Jellyfin-integration glue.
