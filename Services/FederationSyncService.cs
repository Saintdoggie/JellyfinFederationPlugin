using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Refreshes the federation cache by walking each mapping and pulling items
    /// from remote servers. A failed server never destroys cached data: stale
    /// entries are only pruned for sources that synced successfully.
    /// </summary>
    public class FederationSyncService
    {
        private readonly ILogger<FederationSyncService> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly FederationItemCache _cache;
        private readonly FederationItemPersistenceService _persistence;
        private readonly WanBandwidthMonitor _bandwidthMonitor;
        private readonly IServiceProvider _serviceProvider;

        // Guards SyncAllAsync/SyncServerAsync against running concurrently with each
        // other (e.g. the 5s-after-startup sync overlapping the hourly scheduled
        // task). Overlapping runs raced to delete-then-recreate the same items during
        // the tiered-creation migration, hit SQLite "database table is locked" errors,
        // and left the library in a half-migrated state - not just wasted work, but
        // destructive when a sync deletes items before recreating them.
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationSyncService"/> class.
        /// </summary>
        public FederationSyncService(
            ILogger<FederationSyncService> logger,
            FederationLibraryManager federationManager,
            IRemoteServerClientFactory clientFactory,
            FederationItemCache cache,
            FederationItemPersistenceService persistence,
            WanBandwidthMonitor bandwidthMonitor,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _federationManager = federationManager;
            _clientFactory = clientFactory;
            _cache = cache;
            _persistence = persistence;
            _bandwidthMonitor = bandwidthMonitor;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Refreshes WAN network classification/bandwidth measurement (see
        /// <see cref="WanBandwidthMonitor"/>) for every enabled server, best-effort.
        /// Internally rate-limited and never throws, so it is safe to call at the top
        /// of every sync cycle.
        /// </summary>
        private Task RefreshWanBandwidthAsync(IEnumerable<RemoteServer> servers, CancellationToken cancellationToken)
        {
            // Parallel rather than sequential: each server's check is now
            // individually bounded (~28s worst case - see MeasureBandwidthMbpsAsync
            // and WanBandwidthMonitor.ClassifyAsync's own timeouts), but a sequential
            // loop still multiplies that by however many servers are actually due for
            // a recheck this cycle. WanBandwidthMonitor's cache is a
            // ConcurrentDictionary keyed per server, so concurrent refreshes for
            // different servers cannot race each other.
            var refreshes = servers.Where(s => s.Enabled)
                .Select(server => _bandwidthMonitor.RefreshIfDueAsync(server, cancellationToken));
            return Task.WhenAll(refreshes);
        }

        /// <summary>
        /// Refreshes all mappings from all configured remote servers.
        /// Failed servers leave their existing cache entries intact.
        /// </summary>
        public async Task<SyncResult> SyncAllAsync(CancellationToken cancellationToken = default)
        {
            if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("[Federation] Sync already in progress; skipping this trigger");
                return new SyncResult { Success = true, Message = "A sync is already in progress; skipped", ItemCount = 0 };
            }

            var operationId = Guid.NewGuid().ToString();
            SyncProgressTracker.Start(operationId);

            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return Failed("Plugin not initialized", operationId);
                }

                var mappings = config.LibraryMappings?.Where(m => m.Enabled).ToList() ?? new List<LibraryMapping>();
                if (mappings.Count == 0)
                {
                    SyncProgressTracker.Complete(operationId, true, "No mappings configured");
                    return new SyncResult { Success = true, Message = "No mappings configured", OperationId = operationId };
                }

                await RefreshWanBandwidthAsync(config.RemoteServers ?? new List<RemoteServer>(), cancellationToken).ConfigureAwait(false);

                // One-time migration: items created before 0.0.16 never had
                // SeriesPresentationUniqueKey set (see FederationLibraryManager.
                // MaterializeItem), which is what the Shows/{id}/Seasons and
                // Shows/{id}/Episodes endpoints actually filter by - so they existed
                // in the database but were undiscoverable from the show page. Runs
                // across every mapping in this sync, then the flag is saved so it
                // never runs again. Note this deletes and recreates affected items
                // with the same deterministic id - any local watch progress on them
                // is not preserved.
                var needsNestedMigration = !config.MigratedTieredCreationV4;

                // V5 rebuilds items so they pick up IsVirtualItem = false and
                // Season.IndexNumber, and sweeps the duplicate seasons Jellyfin
                // created while those seasons had no index (see MigratedSeasonIndexV5).
                var needsSeasonIndexMigration = !config.MigratedSeasonIndexV5;

                // V6 rebuilds every federated item under the remapped CLR types
                // (FederatedEpisode, FederatedMovie, ...) that report LocationType =
                // Remote, so the web client stops painting them "Missing" (it keys that
                // off Type == Episode && LocationType == Virtual). Because ids derive
                // from the CLR type, a one-time recreate is required.
                var needsRemoteLocationMigration = !config.MigratedRemoteLocationV6;

                // V7 rebuilds every federated item so it picks up the remote stream URL
                // on item.Path. Without a path the static media source Jellyfin builds
                // is a placeholder, which is both unplayable itself and suppresses the
                // remote-content probe that would have filled in the codecs (see
                // MigratedRemotePathV7). Reconciliation only creates and deletes items,
                // never updates them in place, so a rebuild is the only way to apply it.
                var needsRemotePathMigration = !config.MigratedRemotePathV7;

                // V8 rebuilds every federated item back under Jellyfin's own CLR types.
                // The plugin subclasses V6 introduced made BaseItem.GetBaseItemKind()
                // throw (it parses the class name into the BaseItemKind enum), which
                // took down every API response and folder enumeration touching a
                // federated item. See MigratedStockTypesV8.
                var needsStockTypeMigration = !config.MigratedStockTypesV8;

                // V9 rebuilds every federated item once more so the IsShortcut/
                // ShortcutPath persisted on it by 0.0.26-0.0.29 gets cleared. Those
                // properties make Jellyfin's ProbeProvider try to read item.Path as a
                // local .strm file - which it isn't - throwing on every metadata
                // refresh (not just once) until the item is recreated. See
                // MigratedRemoveShortcutV9.
                var needsRemoveShortcutMigration = !config.MigratedRemoveShortcutV9;

                // V10 rebuilds every federated item on a Direct-mode WAN-capped server
                // so it picks up the always-stamped item.Path from 0.0.38 (see
                // MigratedPlaceholderPathV10) - without it, those items keep the
                // null-Path/Placeholder condition that hid their Play button forever.
                var needsPlaceholderPathMigration = !config.MigratedPlaceholderPathV10;

                int totalItems = 0;
                int failedSources = 0;
                for (int i = 0; i < mappings.Count; i++)
                {
                    var mapping = mappings[i];
                    cancellationToken.ThrowIfCancellationRequested();
                    SyncProgressTracker.Update(operationId, totalItems, $"Processing mapping {i + 1}/{mappings.Count}: {mapping.LocalLibraryName}");

                    var result = await RefreshMappingAsync(mapping, config, cancellationToken).ConfigureAwait(false);
                    totalItems += result.ItemCount;
                    failedSources += result.FailedSources;

                    await _persistence.ReconcileMappingAsync(
                        mapping,
                        cancellationToken,
                        forceRecreateNested: needsNestedMigration || needsSeasonIndexMigration,
                        sweepSyntheticSeasons: needsSeasonIndexMigration,
                        forceRecreateAll: needsRemoteLocationMigration || needsRemotePathMigration || needsStockTypeMigration || needsRemoveShortcutMigration || needsPlaceholderPathMigration).ConfigureAwait(false);
                }

                if (needsNestedMigration || needsSeasonIndexMigration)
                {
                    config.MigratedTieredCreationV4 = true;
                    config.MigratedSeasonIndexV5 = true;
                    _logger.LogInformation("[Federation] One-time tiered-creation migration complete");
                }

                if (needsRemoteLocationMigration)
                {
                    config.MigratedRemoteLocationV6 = true;
                    _logger.LogInformation("[Federation] One-time remote-location (LocationType=Remote) migration complete");
                }

                if (needsRemotePathMigration)
                {
                    config.MigratedRemotePathV7 = true;
                    _logger.LogInformation("[Federation] One-time remote-path (streamable item.Path) migration complete");
                }

                if (needsStockTypeMigration)
                {
                    config.MigratedStockTypesV8 = true;
                    _logger.LogInformation("[Federation] One-time stock-CLR-type migration complete");
                }

                if (needsRemoveShortcutMigration)
                {
                    config.MigratedRemoveShortcutV9 = true;
                    _logger.LogInformation("[Federation] One-time IsShortcut-removal migration complete");
                }

                if (needsPlaceholderPathMigration)
                {
                    config.MigratedPlaceholderPathV10 = true;
                    _logger.LogInformation("[Federation] One-time WAN-capped Placeholder-path migration complete");
                }

                if (needsNestedMigration || needsSeasonIndexMigration || needsRemoteLocationMigration
                    || needsRemotePathMigration || needsStockTypeMigration || needsRemoveShortcutMigration
                    || needsPlaceholderPathMigration)
                {
                    Plugin.Instance?.SaveConfiguration();
                }

                await DiscoverFriendsOfFriendsAsync(cancellationToken).ConfigureAwait(false);

                await _cache.SaveAsync(cancellationToken).ConfigureAwait(false);

                var success = failedSources == 0;
                var message = success
                    ? $"Refreshed {totalItems} items across {mappings.Count} mapping(s)"
                    : $"Refreshed {totalItems} items across {mappings.Count} mapping(s); {failedSources} source(s) failed (cached data preserved)";
                SyncProgressTracker.Complete(operationId, success, message);
                return new SyncResult
                {
                    Success = success,
                    ItemCount = totalItems,
                    FailedSources = failedSources,
                    Message = message,
                    OperationId = operationId
                };
            }
            catch (OperationCanceledException)
            {
                SyncProgressTracker.Complete(operationId, false, "Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error during refresh");
                SyncProgressTracker.Complete(operationId, false, ex.Message);
                return Failed(ex.Message, operationId);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>
        /// Refreshes a specific server by its ID (refreshes all mappings that use it).
        /// </summary>
        public async Task<SyncResult> SyncServerAsync(string serverId, CancellationToken cancellationToken = default)
        {
            if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("[Federation] Sync already in progress; skipping this trigger");
                return new SyncResult { Success = true, Message = "A sync is already in progress; skipped", ItemCount = 0 };
            }

            try
            {
                var config = Plugin.Instance?.Configuration;
                var server = config?.RemoteServers?.Find(s => s.Id == serverId);
                if (server == null)
                {
                    return Failed("Server not found");
                }

                var mappings = config!.LibraryMappings?
                    .Where(m => m.Enabled && (m.RemoteLibrarySources?.Any(s => s.ServerId == serverId) == true || m.RemoteServerIds.Contains(serverId)))
                    .ToList();

                if (mappings == null || mappings.Count == 0)
                {
                    return Failed("No mappings use this server");
                }

                await RefreshWanBandwidthAsync(new[] { server }, cancellationToken).ConfigureAwait(false);

                // Same one-time migrations as SyncAllAsync (see there for details). The
                // global flags are only ever set by SyncAllAsync, since this only covers
                // mappings tied to one server - setting them here could skip mappings on
                // other servers that haven't had a full sync yet.
                var needsNestedMigration = !config!.MigratedTieredCreationV4;
                var needsSeasonIndexMigration = !config!.MigratedSeasonIndexV5;
                var needsRemoteLocationMigration = !config!.MigratedRemoteLocationV6;
                var needsRemotePathMigration = !config!.MigratedRemotePathV7;
                var needsStockTypeMigration = !config!.MigratedStockTypesV8;
                var needsRemoveShortcutMigration = !config!.MigratedRemoveShortcutV9;
                var needsPlaceholderPathMigration = !config!.MigratedPlaceholderPathV10;

                int total = 0;
                int failedSources = 0;
                foreach (var mapping in mappings)
                {
                    var result = await RefreshMappingAsync(mapping, config!, cancellationToken, onlyServerId: serverId).ConfigureAwait(false);
                    total += result.ItemCount;
                    failedSources += result.FailedSources;

                    await _persistence.ReconcileMappingAsync(
                        mapping,
                        cancellationToken,
                        forceRecreateNested: needsNestedMigration || needsSeasonIndexMigration,
                        sweepSyntheticSeasons: needsSeasonIndexMigration,
                        forceRecreateAll: needsRemoteLocationMigration || needsRemotePathMigration || needsStockTypeMigration || needsRemoveShortcutMigration || needsPlaceholderPathMigration).ConfigureAwait(false);
                }

                await _cache.SaveAsync(cancellationToken).ConfigureAwait(false);
                var success = failedSources == 0;
                return new SyncResult
                {
                    Success = success,
                    ItemCount = total,
                    FailedSources = failedSources,
                    Message = success
                        ? $"Refreshed {total} items from {server.Name}"
                        : $"Refreshed {total} items from {server.Name}; {failedSources} source(s) failed (cached data preserved)"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error syncing server {ServerId}", serverId);
                return Failed(ex.Message);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>
        /// Runs friends-of-friends discovery as part of the normal sync cycle (a
        /// no-op unless AllowFriendsOfFriends is on - see
        /// FederationFriendService.DiscoverFriendsOfFriendsAsync). Best-effort: a
        /// failure here must never fail the sync that items actually depend on.
        /// FederationFriendService is DI-scoped (it needs IAuthenticationManager,
        /// which Jellyfin registers scoped), but this service is a singleton, so a
        /// short-lived scope is created here rather than injecting it directly -
        /// the standard pattern for a singleton that needs a scoped dependency.
        /// </summary>
        private async Task DiscoverFriendsOfFriendsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var friends = scope.ServiceProvider.GetRequiredService<FederationFriendService>();
                var discovered = await friends.DiscoverFriendsOfFriendsAsync(cancellationToken).ConfigureAwait(false);
                if (discovered > 0)
                {
                    _logger.LogInformation("[Federation] Friends-of-friends discovery sent {Count} new friend request(s)", discovered);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Friends-of-friends discovery failed (non-fatal)");
            }
        }

        private async Task<MappingSyncResult> RefreshMappingAsync(
            LibraryMapping mapping,
            PluginConfiguration config,
            CancellationToken cancellationToken,
            string? onlyServerId = null)
        {
            _logger.LogInformation("[Federation] Refreshing mapping {Name}", mapping.LocalLibraryName);

            int total = 0;
            int failedSources = 0;
            foreach (var source in mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
            {
                if (onlyServerId != null && source.ServerId != onlyServerId)
                {
                    continue;
                }

                var server = config.RemoteServers?.Find(s => s.Id == source.ServerId);
                if (server == null || !server.Enabled)
                {
                    _logger.LogWarning("[Federation] Skipping disabled/missing server {ServerId}", source.ServerId);
                    continue;
                }

                SourceSyncResult result;
                try
                {
                    result = await RefreshSourceAsync(mapping, server, source, config, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Federation] Error refreshing source {Source} on {Server}", source.RemoteLibraryName, server.Name);
                    failedSources++;
                    continue;
                }

                if (result.PluginMissing)
                {
                    // Unlike an ordinary failure (network blip, remote overloaded,
                    // temporary 502, ...) where stale cached items are kept so a
                    // transient hiccup doesn't empty the library, a missing Federation
                    // plugin is treated as "this server is no longer a federation
                    // peer" - its items are actively removed rather than left stale,
                    // same as if the server itself had been deleted (see DeleteServer).
                    var removedForMissingPlugin = _cache.PruneServerSources(mapping.LocalLibraryName, source.ServerId, new HashSet<Guid>());
                    if (removedForMissingPlugin > 0)
                    {
                        _logger.LogInformation(
                            "[Federation] Removed {Count} item(s) from {Mapping} sourced from {Server} (Federation plugin no longer detected there)",
                            removedForMissingPlugin,
                            mapping.LocalLibraryName,
                            server.Name);
                    }

                    continue;
                }

                if (result.Failed)
                {
                    // Keep the existing cache for this server untouched.
                    failedSources++;
                    continue;
                }

                total += result.Count;

                // The source synced successfully: drop its stale entries.
                var pruned = _cache.PruneServerSources(mapping.LocalLibraryName, source.ServerId, result.SeenRemoteItemIds);
                if (pruned > 0)
                {
                    _logger.LogInformation("[Federation] Pruned {Count} stale entries for {Server} in {Mapping}", pruned, server.Name, mapping.LocalLibraryName);
                }
            }

            return new MappingSyncResult(total, failedSources);
        }

        private async Task<SourceSyncResult> RefreshSourceAsync(
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var client = _clientFactory.GetClient(server);
            if (client == null)
            {
                return SourceSyncResult.Failure();
            }

            // Federation only ever talks to the remote's stock Jellyfin API (Items,
            // Users, PlaybackInfo, ...), so nothing above this point actually proves
            // the remote is a federation peer rather than just any reachable Jellyfin
            // server - it would happily keep pulling content from one even after the
            // owner uninstalled Federation there. Checked per source (not once up
            // front) so it stays current even if the remote's plugin state changes
            // mid-session.
            if (!await client.HasFederationPluginAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "[Federation] {Server} does not have the Federation plugin installed; its items in {Mapping} will be removed",
                    server.Name,
                    mapping.LocalLibraryName);
                return SourceSyncResult.PluginMissingResult();
            }

            var seen = new HashSet<Guid>();
            var mediaTypesToFetch = new List<string> { mapping.MediaType };

            // The /Items API only returns the item type it's asked for. A "Series"
            // mapping otherwise syncs nothing but empty show shells - Episodes are
            // a distinct item type that has to be requested explicitly. Fetched
            // recursively under the same library in a second pass, same as the
            // series themselves; the series pass runs first so episodes can look
            // their series back up by remote id (see UpsertEpisodeSeason).
            if (string.Equals(mapping.MediaType, "Series", StringComparison.OrdinalIgnoreCase))
            {
                mediaTypesToFetch.Add("Episode");
            }

            int total = 0;
            foreach (var mediaType in mediaTypesToFetch)
            {
                var count = await FetchAndUpsertPagesAsync(mapping, server, source, config, client, mediaType, seen, cancellationToken).ConfigureAwait(false);
                if (count == null)
                {
                    // Request failed: report failure so the caller preserves the cache.
                    return SourceSyncResult.Failure();
                }

                total += count.Value;
            }

            _logger.LogInformation("[Federation] Refreshed {Count} items from {Server}/{Library}", total, server.Name, source.RemoteLibraryName);
            return new SourceSyncResult(total, false, false, seen);
        }

        private async Task<int?> FetchAndUpsertPagesAsync(
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            RemoteServerClient client,
            string mediaType,
            HashSet<Guid> seen,
            CancellationToken cancellationToken)
        {
            int total = 0;
            int pageSize = 200;
            int startIndex = 0;
            int pageNumber = 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await client.GetItemsAsync(
                    userId: server.UserId,
                    mediaType: mediaType,
                    parentId: source.RemoteLibraryId,
                    startIndex: startIndex,
                    limit: pageSize,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (page == null)
                {
                    return null;
                }

                if (page.Count == 0)
                {
                    break;
                }

                foreach (var remoteItem in page)
                {
                    try
                    {
                        // A remote server running this same plugin stamps a
                        // FederationKey provider id on every item it federated in
                        // from somewhere else. Re-importing those would boomerang
                        // content back to servers that already have it (or already
                        // gave it away) as a second, episode-less "federation-like"
                        // copy sitting next to the real one - and in a topology
                        // where two servers federate from each other, would loop
                        // forever. Only pull in content the remote server actually
                        // owns.
                        if (remoteItem.ProviderIds != null && remoteItem.ProviderIds.ContainsKey("FederationKey"))
                        {
                            continue;
                        }

                        UpsertRemoteItem(mapping, remoteItem, server, config, seen);
                        seen.Add(remoteItem.Id);
                        total++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Failed to upsert item {Name}", remoteItem.Name);
                    }
                }

                if (page.Count < pageSize)
                {
                    break;
                }

                startIndex += pageSize;
                pageNumber++;
                if (pageNumber > 1000)
                {
                    _logger.LogWarning("[Federation] Safety cap reached at 1000 pages for {Source}", source.RemoteLibraryName);
                    break;
                }
            }

            return total;
        }

        private void UpsertRemoteItem(
            LibraryMapping mapping,
            MediaBrowser.Model.Dto.BaseItemDto remoteItem,
            RemoteServer server,
            PluginConfiguration config,
            HashSet<Guid> seen)
        {
            var itemType = remoteItem.Type.ToString();
            var isEpisode = string.Equals(itemType, "Episode", StringComparison.OrdinalIgnoreCase);
            var parentKey = isEpisode ? UpsertEpisodeSeason(mapping, remoteItem, server, seen) : null;

            var providerIds = remoteItem.ProviderIds;
            var dedupKeys = config.EnableDedup ? (config.DedupProviderIds ?? new List<string>()) : new List<string>();

            string? matchedProvider = null;
            string? matchedId = null;

            // Episodes never dedup by provider id across servers: series-level
            // dedup already prevents duplicate shows, and matching episodes by
            // provider id independently could nest an episode under the wrong
            // server's season if two shows shared a provider id scheme.
            if (!isEpisode && providerIds != null && dedupKeys.Count > 0)
            {
                foreach (var key in dedupKeys)
                {
                    if (providerIds.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                    {
                        matchedProvider = key;
                        matchedId = val;
                        break;
                    }
                }
            }

            if (matchedProvider != null && matchedId != null)
            {
                _cache.UpsertByProviderId(
                    mappingName: mapping.LocalLibraryName,
                    providerName: matchedProvider,
                    providerId: matchedId,
                    remoteItem: remoteItem,
                    serverId: server.Id,
                    remoteItemId: remoteItem.Id,
                    serverPriority: server.Priority,
                    itemType: itemType,
                    parentKey: parentKey);
            }
            else
            {
                _cache.UpsertRaw(
                    mappingName: mapping.LocalLibraryName,
                    serverId: server.Id,
                    remoteItemId: remoteItem.Id,
                    remoteItem: remoteItem,
                    serverPriority: server.Priority,
                    itemType: itemType,
                    parentKey: parentKey);
            }
        }

        /// <summary>
        /// Ensures a Season cache entry exists for the episode's (Series, Season
        /// number) pair, synthesized from fields on the episode itself since the
        /// remote API is never asked for Seasons directly. Returns the season's
        /// local cache key to use as the episode's ParentKey, or null if the
        /// episode's series hasn't been synced (so the episode should be skipped
        /// rather than orphaned).
        /// </summary>
        private string? UpsertEpisodeSeason(LibraryMapping mapping, MediaBrowser.Model.Dto.BaseItemDto remoteItem, RemoteServer server, HashSet<Guid> seen)
        {
            if (!remoteItem.SeriesId.HasValue || !remoteItem.SeasonId.HasValue)
            {
                return null;
            }

            var seriesKey = _cache.TryGetLocalKeyForRemoteItem(server.Id, remoteItem.SeriesId.Value);
            if (seriesKey == null)
            {
                return null;
            }

            var seasonDto = new MediaBrowser.Model.Dto.BaseItemDto
            {
                Id = remoteItem.SeasonId.Value,
                Name = !string.IsNullOrEmpty(remoteItem.SeasonName) ? remoteItem.SeasonName : $"Season {remoteItem.ParentIndexNumber ?? 0}",
                IndexNumber = remoteItem.ParentIndexNumber
            };

            var seasonEntry = _cache.UpsertRaw(
                mappingName: mapping.LocalLibraryName,
                serverId: server.Id,
                remoteItemId: remoteItem.SeasonId.Value,
                remoteItem: seasonDto,
                serverPriority: server.Priority,
                itemType: "Season",
                parentKey: seriesKey);

            // The season is synthesized from fields on episodes, not returned as
            // its own item from GetItemsAsync, so nothing else ever marks its
            // remote id as seen this sync. Without this, PruneServerSources treats
            // every season as stale and deletes it in the same pass it was just
            // created in - which is exactly what was happening (a whole "Pruned N
            // stale entries" is really N synthesized seasons getting immediately
            // deleted, orphaning every episode's ParentKey).
            seen.Add(remoteItem.SeasonId.Value);

            return seasonEntry.Key;
        }

        private static SyncResult Failed(string message, string? operationId = null)
        {
            return new SyncResult { Success = false, Message = message, OperationId = operationId };
        }

        private sealed record MappingSyncResult(int ItemCount, int FailedSources);

        private sealed record SourceSyncResult(int Count, bool Failed, bool PluginMissing, HashSet<Guid> SeenRemoteItemIds)
        {
            public static SourceSyncResult Failure() => new SourceSyncResult(0, true, false, new HashSet<Guid>());

            public static SourceSyncResult PluginMissingResult() => new SourceSyncResult(0, true, true, new HashSet<Guid>());
        }
    }

    /// <summary>
    /// Result of a sync operation.
    /// </summary>
    public class SyncResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the sync was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the number of items synced.
        /// </summary>
        public int ItemCount { get; set; }

        /// <summary>
        /// Gets or sets the number of sources that failed to sync (cached data preserved).
        /// </summary>
        public int FailedSources { get; set; }

        /// <summary>
        /// Gets or sets a message describing the result.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the operation ID for progress tracking.
        /// </summary>
        public string? OperationId { get; set; }
    }
}
