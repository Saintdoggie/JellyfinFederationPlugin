using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
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
            FederationItemPersistenceService persistence)
        {
            _logger = logger;
            _federationManager = federationManager;
            _clientFactory = clientFactory;
            _cache = cache;
            _persistence = persistence;
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

                    await _persistence.ReconcileMappingAsync(mapping, cancellationToken, forceRecreateNested: needsNestedMigration).ConfigureAwait(false);
                }

                if (needsNestedMigration)
                {
                    config.MigratedTieredCreationV4 = true;
                    Plugin.Instance?.SaveConfiguration();
                    _logger.LogInformation("[Federation] One-time tiered-creation migration complete");
                }

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

                // Same one-time migration as SyncAllAsync (see there for details). The
                // global flag is only ever set by SyncAllAsync, since this only covers
                // mappings tied to one server - setting it here could skip mappings on
                // other servers that haven't had a full sync yet.
                var needsNestedMigration = !config!.MigratedTieredCreationV4;

                int total = 0;
                int failedSources = 0;
                foreach (var mapping in mappings)
                {
                    var result = await RefreshMappingAsync(mapping, config!, cancellationToken, onlyServerId: serverId).ConfigureAwait(false);
                    total += result.ItemCount;
                    failedSources += result.FailedSources;

                    await _persistence.ReconcileMappingAsync(mapping, cancellationToken, forceRecreateNested: needsNestedMigration).ConfigureAwait(false);
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
            return new SourceSyncResult(total, false, seen);
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

        private sealed record SourceSyncResult(int Count, bool Failed, HashSet<Guid> SeenRemoteItemIds)
        {
            public static SourceSyncResult Failure() => new SourceSyncResult(0, true, new HashSet<Guid>());
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
