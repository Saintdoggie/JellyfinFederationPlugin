using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        private readonly ExternalCatalogRegistry _externalCatalogs;

        // Guards SyncAllAsync/SyncServerAsync against running concurrently with each
        // other (e.g. the 5s-after-startup sync overlapping the hourly scheduled
        // task). Overlapping runs raced to delete-then-recreate the same items during
        // the tiered-creation migration, hit SQLite "database table is locked" errors,
        // and left the library in a half-migrated state - not just wasted work, but
        // destructive when a sync deletes items before recreating them.
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        // The check phase's result for every library of the running sync, so the
        // download phase never asks a friend for its title list twice.
        private Dictionary<string, SourcePlan>? _currentPlans;

        private int _statusLoaded;

        /// <summary>Per friend library sync state shown on the settings page.</summary>
        public FederationSyncStatus Status { get; } = new FederationSyncStatus();

        // Tests lower these to exercise the 1000-page safety cap without 200k upserts.
        internal int CatalogPageSize { get; set; } = 200;

        internal int MaxCatalogPages { get; set; } = 1000;

        // Tests lower these to exercise index paging and id batching.
        internal int IndexPageSize { get; set; } = 2000;

        // A removal larger than both of these waits for the admin (see PruneSafely).
        internal const int LargeRemovalMinimum = 25;

        internal const double LargeRemovalShare = 0.10;

        // Waits before retrying a failed title-list page or download batch. Tests
        // set these to zero.
        internal TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };

        // During a long download the cache file is also written every this many
        // batches, so a restart in the middle loses little.
        internal int SaveEveryBatches { get; set; } = 20;

        // A friend's change notice starts a sync of that friend at most this often.
        internal TimeSpan NotifiedSyncSpacing { get; set; } = TimeSpan.FromMinutes(5);

        // How long to wait before retrying when another sync is running.
        internal TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

        // Test seam for the sync a change notice starts.
        internal Func<string, CancellationToken, Task<SyncResult>>? NotifiedSyncOverride { get; set; }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastNotifiedSync = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _pendingNotifiedSync = new(StringComparer.Ordinal);

        // How many friends the check phase talks to at once.
        internal int MaxParallelFriends { get; set; } = 4;

        // 100 ids keep each request line comfortably under common 8 KB limits
        // (Kestrel, nginx), also behind a reverse proxy.
        internal int IdsBatchSize { get; set; } = 100;

        /// <summary>
        /// Stored per-title hashes are trusted for at most this long; then every
        /// title is fetched again. A backstop for anything the hashes cannot see,
        /// such as an entry another friend's copy of the same title updated.
        /// </summary>
        internal static readonly TimeSpan IndexFullRefreshInterval = TimeSpan.FromHours(24);

        /// <summary>
        /// Gets the outcome of the most recent sync, or null if none has finished
        /// since startup. Exists so the config page can show whether federation is
        /// actually working: until now the only record of a sync failing - a remote
        /// that could not be reached, a source skipped, content preserved rather than
        /// refreshed - was a line in the server log, so a library that had quietly
        /// stopped updating looked identical to one that was fine.
        /// </summary>
        public SyncHealth? LastSync { get; private set; }

        /// <summary>Loads the last saved per-library outcomes once the cache location is known.</summary>
        public void EnsureStatusLoaded()
        {
            var path = _cache.CacheFilePath;
            if (!string.IsNullOrEmpty(path) && Interlocked.Exchange(ref _statusLoaded, 1) == 0)
            {
                Status.Initialize(path);
            }
        }

        /// <summary>
        /// Starts a full sync without waiting for it, so the settings page can poll
        /// its progress instead of holding one request open for the whole sync.
        /// False when a sync is already running.
        /// </summary>
        public bool TryStartSyncAll(CancellationToken stoppingToken)
        {
            if (Status.Running || _syncLock.CurrentCount == 0)
            {
                return false;
            }

            Status.MarkPending();
            _ = Task.Run(async () =>
            {
                try
                {
                    await SyncAllAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Jellyfin is shutting down.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Federation] Background sync failed");
                }
                finally
                {
                    // Skipped (another sync was already running) or ended before any
                    // library was listed, e.g. with no libraries configured.
                    Status.ClearPending();
                }
            });
            return true;
        }

        /// <summary>
        /// A friend reported that its catalog changed (see
        /// <see cref="FederationChangeNotifier"/>): sync just that friend in the
        /// background, so its new titles appear within minutes instead of at the
        /// next scheduled sync. At most one such sync per friend per
        /// <see cref="NotifiedSyncSpacing"/>; notices arriving meanwhile are
        /// covered by the one already scheduled. Returns false when one is
        /// already scheduled.
        /// </summary>
        public bool OnFriendCatalogChanged(string serverId, CancellationToken stoppingToken)
        {
            if (!_pendingNotifiedSync.TryAdd(serverId, 0))
            {
                return false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var since = DateTime.UtcNow - _lastNotifiedSync.GetValueOrDefault(serverId, DateTime.MinValue);
                    if (since < NotifiedSyncSpacing)
                    {
                        await Task.Delay(NotifiedSyncSpacing - since, stoppingToken).ConfigureAwait(false);
                    }

                    // Waits out a running sync instead of dropping the notice.
                    for (var attempt = 0; attempt < 60; attempt++)
                    {
                        var result = NotifiedSyncOverride != null
                            ? await NotifiedSyncOverride(serverId, stoppingToken).ConfigureAwait(false)
                            : await SyncServerAsync(serverId, stoppingToken).ConfigureAwait(false);
                        if (!string.Equals(result.Message, "A sync is already in progress; skipped", StringComparison.Ordinal))
                        {
                            _logger.LogInformation("[Federation] Synced a friend after its change notice: {Message}", result.Message);
                            break;
                        }

                        await Task.Delay(BusyRetryDelay, stoppingToken).ConfigureAwait(false);
                    }

                    _lastNotifiedSync[serverId] = DateTime.UtcNow;
                }
                catch (OperationCanceledException)
                {
                    // Jellyfin is shutting down.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Sync after a friend's change notice failed");
                }
                finally
                {
                    _pendingNotifiedSync.TryRemove(serverId, out _);
                }
            });
            return true;
        }

        private void BeginStatusRun(IEnumerable<LibraryMapping> mappings, PluginConfiguration config, string? onlyServerId)
        {
            EnsureStatusLoaded();
            _persistence.ResetLocalMatchCache();
            var states = new List<LibrarySyncState>();
            foreach (var mapping in mappings)
            {
                foreach (var source in mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
                {
                    if (onlyServerId != null && source.ServerId != onlyServerId)
                    {
                        continue;
                    }

                    var server = config.RemoteServers?.Find(s => s.Id == source.ServerId);
                    states.Add(new LibrarySyncState
                    {
                        Key = StatusKey(mapping, source),
                        ServerId = source.ServerId,
                        ServerName = server?.Name ?? source.ServerName ?? "Removed friend",
                        RemoteLibraryId = source.RemoteLibraryId,
                        RemoteLibraryName = source.RemoteLibraryName ?? source.RemoteLibraryId,
                        LocalLibraryName = mapping.LocalLibraryName
                    });
                }
            }

            Status.BeginRun(states, replaceAll: onlyServerId == null);
        }

        /// <summary>
        /// Check phase: reads every enabled friend library's title list before
        /// anything is downloaded, so each library's status shows what it will
        /// transfer from the start.
        /// </summary>
        private async Task PlanAllAsync(IEnumerable<LibraryMapping> mappings, PluginConfiguration config, string? onlyServerId, CancellationToken cancellationToken)
        {
            var work = new List<(LibraryMapping Mapping, RemoteLibrarySource Source, RemoteServer Server)>();
            foreach (var mapping in mappings)
            {
                foreach (var source in mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
                {
                    if (onlyServerId != null && source.ServerId != onlyServerId)
                    {
                        continue;
                    }

                    var server = config.RemoteServers?.Find(s => s.Id == source.ServerId);
                    if (server != null && server.Enabled)
                    {
                        work.Add((mapping, source, server));
                    }
                }
            }

            // Friends are checked at the same time, each friend's own libraries one
            // after another: a friend never gets more than one request at once from
            // this server, and a slow friend no longer holds up the others.
            var plans = new System.Collections.Concurrent.ConcurrentDictionary<string, SourcePlan>(StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(MaxParallelFriends);
            await Task.WhenAll(work.GroupBy(w => w.Server.Id).Select(async friend =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (var (mapping, source, server) in friend)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        plans[StatusKey(mapping, source)] = await PlanSourceAsync(mapping, server, source, config, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            _currentPlans = new Dictionary<string, SourcePlan>(plans, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A library counts as synced once Jellyfin has stored its titles.</summary>
        private void MarkMappingSaved(LibraryMapping mapping)
        {
            foreach (var source in mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
            {
                var key = StatusKey(mapping, source);
                if (Status.Snapshot().FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))?.State == FederationSyncStatus.Saving)
                {
                    Status.Succeed(key);
                }
            }
        }

        private void RecordSyncOutcome(bool success, string message, int itemCount, int failedSources)
        {
            LastSync = new SyncHealth
            {
                FinishedUtc = DateTime.UtcNow,
                Success = success,
                Message = message,
                ItemCount = itemCount,
                FailedSources = failedSources
            };
        }

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
            IServiceProvider serviceProvider,
            ExternalCatalogRegistry externalCatalogs)
        {
            _logger = logger;
            _federationManager = federationManager;
            _clientFactory = clientFactory;
            _cache = cache;
            _persistence = persistence;
            _bandwidthMonitor = bandwidthMonitor;
            _serviceProvider = serviceProvider;
            _externalCatalogs = externalCatalogs;
        }

        /// <summary>
        /// Refreshes WAN network classification/bandwidth measurement (see
        /// <see cref="WanBandwidthMonitor"/>) for every enabled server, best-effort.
        /// Internally rate-limited and never throws, so it is safe to call at the top
        /// of every sync cycle.
        /// </summary>
        private async Task RefreshWanBandwidthAsync(IEnumerable<RemoteServer> servers, CancellationToken cancellationToken)
        {
            // First, so the measurements below already go to the https address of
            // a peer that redirects there.
            await Task.WhenAll(servers.Where(s => s.Enabled)
                .Select(server => _clientFactory.ProbeHttpsUpgradeAsync(server, cancellationToken) ?? Task.FromResult(false)))
                .ConfigureAwait(false);

            // Parallel rather than sequential: each server's check is now
            // individually bounded (~28s worst case - see MeasureBandwidthMbpsAsync
            // and WanBandwidthMonitor.ClassifyAsync's own timeouts), but a sequential
            // loop still multiplies that by however many servers are actually due for
            // a recheck this cycle. WanBandwidthMonitor's cache is a
            // ConcurrentDictionary keyed per server, so concurrent refreshes for
            // different servers cannot race each other.
            var refreshes = servers.Where(s => s.Enabled)
                .Select(server => _bandwidthMonitor.RefreshIfDueAsync(server, cancellationToken));
            var linkSamples = servers.Where(s => s.Enabled)
                .Select(server => _bandwidthMonitor.MeasureLinkIfDueAsync(server, cancellationToken));
            await Task.WhenAll(refreshes.Concat(linkSamples)).ConfigureAwait(false);
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
                    BeginStatusRun(mappings, config, null);
                    SyncProgressTracker.Complete(operationId, true, "No mappings configured");
                    return new SyncResult { Success = true, Message = "No mappings configured", OperationId = operationId };
                }

                PruneOrphanedServerSources(mappings, config.RemoteServers ?? new List<RemoteServer>());
                PruneUnselectedServerSources();

                BeginStatusRun(mappings, config, null);
                await RefreshWanBandwidthAsync(config.RemoteServers ?? new List<RemoteServer>(), cancellationToken).ConfigureAwait(false);
                SyncProgressTracker.Update(operationId, 0, "Checking what each friend has");
                await PlanAllAsync(mappings, config, null, cancellationToken).ConfigureAwait(false);

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

                // V11 rebuilds every streamable federated item so it picks up the
                // remote's real container instead of a stale "mp4" forced by an
                // older version of MaterializeItem (see MigratedContainerV11) - a
                // wrong container makes Jellyfin's transcoder demux the wrong
                // format entirely, which fails almost instantly and is why
                // federated playback was looping forever on some items.
                var needsContainerMigration = !config.MigratedContainerV11;

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
                        forceRecreateAll: needsRemoteLocationMigration || needsRemotePathMigration || needsStockTypeMigration || needsRemoveShortcutMigration || needsPlaceholderPathMigration || needsContainerMigration).ConfigureAwait(false);
                    MarkMappingSaved(mapping);
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

                if (needsContainerMigration)
                {
                    config.MigratedContainerV11 = true;
                    _logger.LogInformation("[Federation] One-time container-fix (real remote container, not a stale forced \"mp4\") migration complete");
                }

                if (needsNestedMigration || needsSeasonIndexMigration || needsRemoteLocationMigration
                    || needsRemotePathMigration || needsStockTypeMigration || needsRemoveShortcutMigration
                    || needsPlaceholderPathMigration || needsContainerMigration)
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
                RecordSyncOutcome(success, message, totalItems, failedSources);
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
                RecordSyncOutcome(false, ex.Message, 0, 0);
                return Failed(ex.Message, operationId);
            }
            finally
            {
                _currentPlans = null;
                if (Status.Running)
                {
                    Status.EndRun();
                }

                _syncLock.Release();
            }
        }

        /// <summary>
        /// Sweeps cache entries whose server id no longer appears in
        /// <paramref name="remoteServers"/> at all - not merely disabled, but
        /// entirely absent from config. Every other prune in this file only ever
        /// runs as part of actively syncing a specific server (see
        /// <see cref="RefreshMappingAsync"/>) or an explicit admin delete (see
        /// <c>FederationController.DeleteServer</c>'s own call to
        /// <see cref="FederationItemCache.PruneServerSources"/>); once a server is
        /// removed from config by any other means, nothing ever revisits its
        /// now-orphaned cache entries again, since the main sync loop below only
        /// ever iterates <em>currently configured</em> servers - confirmed live as
        /// a real bug: thousands of items from a friend removed hours earlier were
        /// still sitting in the library because their source server was gone from
        /// config but never gone from the cache. Reusing the existing
        /// per-source-empty-set prune (same call <c>DeleteServer</c> already makes)
        /// means the reconciliation pass immediately after this in
        /// <see cref="SyncAllAsync"/> picks up the pruned cache state and deletes
        /// the now-unbacked materialized items the same way any other stale item
        /// already gets cleaned up - no separate deletion path needed.
        /// </summary>
        private void PruneOrphanedServerSources(IReadOnlyList<LibraryMapping> mappings, IReadOnlyList<RemoteServer> remoteServers)
        {
            var configuredServerIds = new HashSet<string>(remoteServers.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
            var emptySeen = new HashSet<Guid>();

            foreach (var mapping in mappings)
            {
                var orphanedServerIds = _cache.GetEntriesForMapping(mapping.LocalLibraryName)
                    .SelectMany(e => e.GetSourcesSnapshot())
                    .Select(s => s.ServerId)
                    .Where(id => !string.IsNullOrEmpty(id) && !configuredServerIds.Contains(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var orphanedServerId in orphanedServerIds)
                {
                    var removed = _cache.PruneServerSources(mapping.LocalLibraryName, orphanedServerId, emptySeen);
                    if (removed > 0)
                    {
                        _logger.LogInformation(
                            "[Federation] Swept {Count} cache entr{Suffix} in {Name} left behind by server {ServerId}, which is no longer in config",
                            removed,
                            removed == 1 ? "y" : "ies",
                            mapping.LocalLibraryName,
                            orphanedServerId);
                    }
                }
            }
        }

        /// <summary>
        /// Removes cached items whose server is still configured but has been
        /// unticked from a mapping (e.g. the admin unchecked the last library
        /// they were receiving from that friend). <see cref="RefreshMappingAsync"/>
        /// only visits servers that still have a source in the mapping, so without
        /// this sweep nothing ever prunes a server that was dropped entirely and
        /// its items stayed in the library forever. A library unticked while
        /// others from the same server remain is already handled by that
        /// server's own prune, so only whole servers are swept here. Mappings
        /// with no sources at all are left alone: they sync nothing either way
        /// and are not evidence of a deliberate deselection.
        /// </summary>
        /// <returns>The names of the mappings that lost entries.</returns>
        public IReadOnlyList<string> PruneUnselectedServerSources()
        {
            var config = Plugin.Instance?.Configuration;
            var affected = new List<string>();
            if (config?.LibraryMappings == null)
            {
                return affected;
            }

            var emptySeen = new HashSet<Guid>();
            foreach (var mapping in config.LibraryMappings.Where(m => m.Enabled))
            {
                var sources = mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>();
                if (sources.Count == 0)
                {
                    continue;
                }

                var selectedServerIds = new HashSet<string>(sources.Select(s => s.ServerId), StringComparer.OrdinalIgnoreCase);
                var dropped = _cache.GetEntriesForMapping(mapping.LocalLibraryName)
                    .SelectMany(e => e.GetSourcesSnapshot())
                    .Select(s => s.ServerId)
                    .Where(id => !string.IsNullOrEmpty(id) && !selectedServerIds.Contains(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var serverId in dropped)
                {
                    var removed = _cache.PruneServerSources(mapping.LocalLibraryName, serverId, emptySeen);
                    if (removed > 0)
                    {
                        _logger.LogInformation(
                            "[Federation] Removed {Count} item(s) from {Name} that came from server {ServerId}, which is no longer selected for it",
                            removed,
                            mapping.LocalLibraryName,
                            serverId);
                        if (!affected.Contains(mapping.LocalLibraryName, StringComparer.OrdinalIgnoreCase))
                        {
                            affected.Add(mapping.LocalLibraryName);
                        }
                    }
                }
            }

            return affected;
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

                BeginStatusRun(mappings, config!, serverId);
                await RefreshWanBandwidthAsync(new[] { server }, cancellationToken).ConfigureAwait(false);
                await PlanAllAsync(mappings, config!, serverId, cancellationToken).ConfigureAwait(false);

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
                var needsContainerMigration = !config!.MigratedContainerV11;

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
                        forceRecreateAll: needsRemoteLocationMigration || needsRemotePathMigration || needsStockTypeMigration || needsRemoveShortcutMigration || needsPlaceholderPathMigration || needsContainerMigration).ConfigureAwait(false);
                    MarkMappingSaved(mapping);
                }

                await _cache.SaveAsync(cancellationToken).ConfigureAwait(false);
                var success = failedSources == 0;
                var serverMessage = success
                    ? $"Refreshed {total} items from {server.Name}"
                    : $"Refreshed {total} items from {server.Name}; {failedSources} source(s) failed (cached data preserved)";
                RecordSyncOutcome(success, serverMessage, total, failedSources);
                return new SyncResult
                {
                    Success = success,
                    ItemCount = total,
                    FailedSources = failedSources,
                    Message = serverMessage
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error syncing server {ServerId}", serverId);

                // Mirrors SyncAllAsync's catch. Without this the health banner keeps
                // reporting the previous run's success after a per-server refresh
                // throws - the precise "stopped working but looks fine" state the
                // banner exists to eliminate.
                RecordSyncOutcome(false, ex.Message, 0, 0);
                return Failed(ex.Message);
            }
            finally
            {
                _currentPlans = null;
                if (Status.Running)
                {
                    Status.EndRun();
                }

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

            // Grouped by server, not iterated source-by-source: PruneServerSources
            // only scopes by (mapping, serverId), not by which specific
            // RemoteLibrarySource an item came from. Pruning after each individual
            // source - the old shape of this loop - meant a mapping with more than
            // one source from the same friend (e.g. two of their folders both
            // feeding this one local library, or one shared and one since declined)
            // would wipe out whatever the OTHER source for that same server had
            // just synced moments earlier in the same cycle, since it isn't in the
            // "just synced" source's own seen set - and the next cycle would
            // flip the deletion the other way. Grouping and pruning once per
            // server, after every one of its sources for this mapping has been
            // attempted, is what actually lets two sources from the same server
            // coexist without erasing each other.
            var sourcesByServer = (mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
                .Where(s => onlyServerId == null || s.ServerId == onlyServerId)
                .GroupBy(s => s.ServerId);

            foreach (var serverSources in sourcesByServer)
            {
                var server = config.RemoteServers?.Find(s => s.Id == serverSources.Key);
                if (server == null || !server.Enabled)
                {
                    _logger.LogWarning("[Federation] Skipping disabled/missing server {ServerId}", serverSources.Key);
                    foreach (var skipped in serverSources)
                    {
                        Status.Fail(StatusKey(mapping, skipped), server == null
                            ? "This friend was removed. Remove the library on the Libraries tab, or add the friend again."
                            : "This friend is turned off on the Friends tab, so it wasn't synced.");
                    }
                    continue;
                }

                var seenAcrossServer = new HashSet<Guid>();
                var anyFailed = false;
                var pluginMissing = false;

                foreach (var source in serverSources)
                {
                    SourceSyncResult result;
                    try
                    {
                        result = await RefreshSourceAsync(mapping, server, source, config, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Federation] Error refreshing source {Source} on {Server}", source.RemoteLibraryName, server.Name);
                        Status.Fail(StatusKey(mapping, source), $"Unexpected error while syncing from {server.Name}: {ex.Message}");
                        anyFailed = true;
                        continue;
                    }

                    if (result.PluginMissing)
                    {
                        // Only ever set for a server that answered as a live
                        // Jellyfin without Federation installed - never for an
                        // unreachable one (see GetFederationPeerStatusAsync,
                        // which reports anything it cannot verify as Unknown,
                        // handled as an ordinary failure below). The same for
                        // every source on this server within one cycle, so
                        // there's no need to keep checking its other sources.
                        pluginMissing = true;
                        break;
                    }

                    if (result.Failed)
                    {
                        anyFailed = true;
                        continue;
                    }

                    total += result.Count;
                    seenAcrossServer.UnionWith(result.SeenRemoteItemIds);
                }

                if (pluginMissing)
                {
                    // A confirmed absence means "this server is no longer a
                    // federation peer", so its items are actively removed rather
                    // than left stale, same as if the server had been deleted
                    // outright (see DeleteServer) - regardless of what its other
                    // sources for this mapping reported.
                    var removedForMissingPlugin = PruneSafely(mapping, server, new HashSet<Guid>());
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

                if (anyFailed)
                {
                    // Keep the existing cache for this whole server untouched:
                    // pruning with a partial seen set (only the sources that
                    // happened to succeed) would delete the failed source's
                    // content as a side effect of an unrelated fetch's success -
                    // exactly the kind of stale/ambiguous situation this
                    // failure-preserves-cache design exists to avoid.
                    failedSources++;
                    continue;
                }

                var pruned = PruneSafely(mapping, server, seenAcrossServer);
                if (pruned > 0)
                {
                    _logger.LogInformation("[Federation] Pruned {Count} stale entries for {Server} in {Mapping}", pruned, server.Name, mapping.LocalLibraryName);
                }
            }

            return new MappingSyncResult(total, failedSources);
        }

        /// <summary>
        /// Removes this friend's titles that its complete list no longer contains,
        /// with two brakes. A title goes only when it was also missing from the
        /// previous complete sync, so a friend that is briefly half-scanned or
        /// misreports once costs nothing. And a large removal (more than
        /// <see cref="LargeRemovalMinimum"/> titles and more than
        /// <see cref="LargeRemovalShare"/> of what this friend has here) waits until
        /// the admin confirms it: every mass deletion so far came from a bug or a
        /// misreading, not from a friend really deleting a tenth of a library.
        /// Returns how many entries were removed.
        /// </summary>
        private int PruneSafely(LibraryMapping mapping, RemoteServer server, HashSet<Guid> seen)
        {
            var key = FederationItemCache.RemovalKey(mapping.LocalLibraryName, server.Id);
            var held = _cache.RemoteIdsHeldFor(mapping.LocalLibraryName, server.Id);
            var missingNow = held.Where(id => !seen.Contains(id)).ToHashSet();
            var pending = _cache.GetPendingRemoval(key) ?? new PendingRemoval();
            var confirmed = missingNow.Where(pending.Missing.Contains).ToHashSet();
            var firstTime = missingNow.Count - confirmed.Count;
            var large = confirmed.Count > LargeRemovalMinimum && confirmed.Count > held.Count * LargeRemovalShare;

            int removed;
            if (large && !pending.Approved)
            {
                removed = _cache.PruneServerSources(mapping.LocalLibraryName, server.Id, held);
                if (pending.HeldCount == 0)
                {
                    _logger.LogWarning(
                        "[Federation] {Server} no longer lists {Count} of {Total} title(s) in {Mapping}; holding the removal until it is confirmed on the Libraries tab",
                        server.Name,
                        confirmed.Count,
                        held.Count,
                        mapping.LocalLibraryName);
                }

                _cache.SetPendingRemoval(key, new PendingRemoval
                {
                    Missing = missingNow,
                    HeldCount = confirmed.Count,
                    HeldSinceUtc = pending.HeldSinceUtc ?? DateTime.UtcNow
                });
            }
            else
            {
                var keep = new HashSet<Guid>(seen);
                keep.UnionWith(missingNow.Where(id => !confirmed.Contains(id)));
                removed = _cache.PruneServerSources(mapping.LocalLibraryName, server.Id, keep);
                _cache.SetPendingRemoval(key, new PendingRemoval { Missing = missingNow.Where(id => !confirmed.Contains(id)).ToHashSet() });
                if (firstTime > 0)
                {
                    _logger.LogInformation(
                        "[Federation] {Server} no longer lists {Count} title(s) in {Mapping}; they are removed if still missing at the next sync",
                        server.Name,
                        firstTime,
                        mapping.LocalLibraryName);
                }
            }

            var heldCount = large && !pending.Approved ? confirmed.Count : 0;
            var waiting = large && !pending.Approved ? 0 : firstTime;
            foreach (var source in (mapping.RemoteLibrarySources ?? new List<RemoteLibrarySource>()).Where(s => s.ServerId == server.Id))
            {
                Status.Update(StatusKey(mapping, source), s =>
                {
                    s.Removed = removed;
                    s.WaitingRemovals = waiting;
                    s.HeldRemovals = heldCount;
                });
            }

            return removed;
        }

        private async Task<SourceSyncResult> RefreshSourceAsync(
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var statusKey = StatusKey(mapping, source);

            // A full sync checks every library before downloading anything (see
            // SyncAllAsync); anything else plans its own library here.
            var plan = _currentPlans != null && _currentPlans.TryGetValue(statusKey, out var planned)
                ? planned
                : await PlanSourceAsync(mapping, server, source, config, cancellationToken).ConfigureAwait(false);

            // A server that isn't another Jellyfin federation peer (Plex today,
            // see IExternalCatalogProvider) speaks its own protocol and has no
            // federation plugin to probe for, so it takes a separate path
            // entirely - it still lands in the same cache through the same
            // UpsertRemoteItem below.
            var externalProvider = _externalCatalogs.For(server);
            if (plan.External && externalProvider != null)
            {
                Status.Update(statusKey, s => s.State = FederationSyncStatus.Downloading);
                var external = await RefreshExternalSourceAsync(mapping, server, source, config, externalProvider, cancellationToken).ConfigureAwait(false);
                if (external.Failed)
                {
                    Status.Fail(statusKey, $"{server.Name} didn't return this library. Titles already here were kept; the Log tab has details.");
                }
                else
                {
                    Status.Update(statusKey, s =>
                    {
                        s.Total = external.Count;
                        s.Downloaded = external.Count;
                        s.State = FederationSyncStatus.Saving;
                    });
                }

                return external;
            }

            if (plan.PluginMissing)
            {
                return SourceSyncResult.PluginMissingResult();
            }

            if (plan.Failed || plan.Client == null)
            {
                return SourceSyncResult.Failure();
            }

            var client = plan.Client;
            var seen = new HashSet<Guid>();
            int total = 0;
            Status.Update(statusKey, s => s.State = FederationSyncStatus.Downloading);
            if (plan.Legacy)
            {
                foreach (var mediaType in MediaTypesFor(mapping))
                {
                    var count = await FetchAndUpsertPagesAsync(mapping, server, source, config, client, mediaType, seen, cancellationToken, statusKey).ConfigureAwait(false);
                    if (count == null)
                    {
                        Status.Fail(statusKey, $"Downloading {source.RemoteLibraryName} from {server.Name} failed part-way. Nothing was removed; the Log tab has details.");
                        return SourceSyncResult.Failure();
                    }

                    total += count.Value;
                }
            }
            else
            {
                foreach (var indexPlan in plan.ByType)
                {
                    var count = await ApplyIndexPlanAsync(indexPlan, mapping, server, source, config, client, seen, cancellationToken).ConfigureAwait(false);
                    if (count == null)
                    {
                        return SourceSyncResult.Failure();
                    }

                    total += count.Value;
                }
            }

            Status.Update(statusKey, s =>
            {
                s.Total ??= total;
                s.State = FederationSyncStatus.Saving;
            });
            _logger.LogInformation("[Federation] Refreshed {Count} items from {Server}/{Library}", total, server.Name, source.RemoteLibraryName);
            return new SourceSyncResult(total, false, false, seen);
        }

        /// <summary>
        /// Sync path for a non-Jellyfin server (see
        /// <see cref="IExternalCatalogProvider"/>). The provider handles paging
        /// and protocol and hands back items already translated into
        /// <see cref="MediaBrowser.Model.Dto.BaseItemDto"/>, so from here on this
        /// is the same upsert - dedup, episode/season nesting, incoming filter -
        /// that a Jellyfin peer's items go through.
        /// </summary>
        private async Task<SourceSyncResult> RefreshExternalSourceAsync(
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            IExternalCatalogProvider provider,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ExternalItem>? items;
            try
            {
                items = await provider.GetItemsAsync(server, source.RemoteLibraryId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to fetch items from {Kind} server {Server}", server.Kind, server.Name);
                return SourceSyncResult.Failure();
            }

            if (items == null)
            {
                // Distinct from an empty library: a failed fetch preserves the
                // cache instead of pruning this server's whole catalog.
                return SourceSyncResult.Failure();
            }

            var seen = new HashSet<Guid>();
            var total = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!IncomingContentFilterService.IsAllowedByIncomingFilter(config.IncomingFilter, item.Dto))
                    {
                        continue;
                    }

                    var entry = UpsertRemoteItem(mapping, item.Dto, server, config, seen);
                    if (entry == null)
                    {
                        // Orphaned episode (its series wasn't synced this cycle) -
                        // deliberately not marked seen, same as the Jellyfin path.
                        continue;
                    }

                    // The provider's own id for this item, which the stream path
                    // needs because the Guid above is derived from it one-way.
                    entry.SetNativeId(server.Id, item.Dto.Id, item.NativeId);
                    seen.Add(item.Dto.Id);
                    total++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Failed to upsert {Kind} item {Name}", server.Kind, item.Dto.Name);
                    return SourceSyncResult.Failure();
                }
            }

            _logger.LogInformation(
                "[Federation] Refreshed {Count} items from {Kind} server {Server}/{Library}",
                total,
                server.Kind,
                server.Name,
                source.RemoteLibraryName);
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
            CancellationToken cancellationToken,
            string? statusKey = null)
        {
            int total = 0;
            int pageSize = CatalogPageSize;
            int startIndex = 0;
            int pageNumber = 1;
            int? cursor = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await client.GetItemsAsync(
                    userId: server.UserId,
                    mediaType: mediaType,
                    parentId: source.RemoteLibraryId,
                    startIndex: startIndex,
                    limit: pageSize,
                    cancellationToken: cancellationToken,
                    cursor: cursor).ConfigureAwait(false);

                if (page == null)
                {
                    return null;
                }

                // Null for an older peer, which then keeps paging by startIndex.
                cursor = (page as PeerCatalogPage.RemoteItemPage)?.NextCursor;
                if (statusKey != null)
                {
                    Status.Update(statusKey, st => st.Downloaded += page.Count);
                }

                var totalRecordCount = (page as PeerCatalogPage.RemoteItemPage)?.TotalRecordCount;

                foreach (var remoteItem in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                        if (!PeerCatalogPage.IsOwned(remoteItem))
                        {
                            continue;
                        }

                        // Receiving-side incoming filter (types, rating ceiling, blocked
                        // tags/genres). Blocks before upsert so the item never lands
                        // in the cache at all — cheaper than materializing then hiding.
                        if (!IncomingContentFilterService.IsAllowedByIncomingFilter(config.IncomingFilter, remoteItem))
                        {
                            continue;
                        }

                        if (UpsertRemoteItem(mapping, remoteItem, server, config, seen) != null)
                        {
                            seen.Add(remoteItem.Id);
                            total++;
                        }

                        // else: skipped as an orphaned episode (its series wasn't
                        // synced this cycle) - deliberately not added to `seen`, so
                        // PruneServerSources does not treat a correctly parented copy
                        // already persisted from an earlier, successful sync as
                        // vanished and delete it.
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Failed to upsert item {Name}", remoteItem.Name);
                        return null;
                    }
                }

                if (page.Count < pageSize)
                {
                    var hasMore = (page as PeerCatalogPage.RemoteItemPage)?.HasMore;
                    if (hasMore == false)
                    {
                        // New peer: it already proved exhaustion server-side, so
                        // no probe fetch is needed - this is the common EOF path
                        // going forward.
                        break;
                    }

                    if (PeerCatalogPage.RemoteItemPage.HasMoreRecords(startIndex, page.Count, totalRecordCount))
                    {
                        _logger.LogWarning(
                            "[Federation] Catalog page for {Source} was truncated ({Count} of {PageSize} at {Start}; total {Total}); keeping cached items",
                            source.RemoteLibraryName,
                            page.Count,
                            pageSize,
                            startIndex,
                            totalRecordCount);
                        return null;
                    }

                    // Empty last page: the remote is exhausted. A short non-empty
                    // page is only EOF when TotalRecordCount is a real catalog total
                    // (larger than this page). Peer/Items reports the page length as
                    // TotalRecordCount, so that case needs a follow-up fetch.
                    if (page.Count > 0
                        && (totalRecordCount is not int knownTotal || knownTotal == page.Count))
                    {
                        var next = await client.GetItemsAsync(
                            userId: server.UserId,
                            mediaType: mediaType,
                            parentId: source.RemoteLibraryId,
                            startIndex: startIndex + page.Count,
                            limit: 1,
                            cancellationToken: cancellationToken,
                            cursor: cursor).ConfigureAwait(false);
                        if (next == null)
                        {
                            return null;
                        }

                        var nextTotal = (next as PeerCatalogPage.RemoteItemPage)?.TotalRecordCount;
                        if (next.Count > 0 || PeerCatalogPage.RemoteItemPage.HasMoreRecords(startIndex + page.Count, next.Count, nextTotal))
                        {
                            _logger.LogWarning(
                                "[Federation] Catalog page for {Source} was truncated ({Count} of {PageSize} at {Start}); keeping cached items",
                                source.RemoteLibraryName,
                                page.Count,
                                pageSize,
                                startIndex);
                            return null;
                        }
                    }

                    break;
                }

                startIndex += pageSize;
                pageNumber++;
                if (pageNumber > MaxCatalogPages)
                {
                    _logger.LogWarning(
                        "[Federation] Safety cap reached at {MaxPages} pages for {Source}; keeping cached items",
                        MaxCatalogPages,
                        source.RemoteLibraryName);
                    return null;
                }
            }

            return total;
        }

        /// <summary>
        /// What the check phase found for one friend library, before anything
        /// is downloaded. Built for every library at the start of a sync so the
        /// settings page can show how much each will transfer.
        /// </summary>
        private sealed class SourcePlan
        {
            public RemoteServerClient? Client { get; set; }

            public bool External { get; set; }

            public bool PluginMissing { get; set; }

            /// <summary>An older friend without the title list: page through everything.</summary>
            public bool Legacy { get; set; }

            public bool Failed { get; set; }

            public List<IndexPlan> ByType { get; } = new();
        }

        /// <summary>One item type of one friend library: the friend's title list against what is stored here.</summary>
        private sealed class IndexPlan
        {
            public string Key { get; init; } = string.Empty;

            public string MediaType { get; init; } = string.Empty;

            public Dictionary<Guid, string> Index { get; init; } = new();

            public SyncIndexSnapshot Next { get; init; } = new();

            public bool Full { get; init; }

            public List<(Guid Id, Guid? Season)> Keep { get; } = new();

            public List<Guid> ToFetch { get; } = new();

            public HashSet<Guid> Changed { get; } = new();

            public int Removed { get; set; }
        }

        /// <summary>
        /// Runs a friend request, retrying a failed one (null) after each of
        /// <see cref="RetryDelays"/>, so one network hiccup does not fail a whole
        /// library.
        /// </summary>
        private async Task<T?> WithRetriesAsync<T>(Func<Task<T?>> attempt, string what, RemoteServer server, CancellationToken cancellationToken)
            where T : class
        {
            for (var i = 0; ; i++)
            {
                var result = await attempt().ConfigureAwait(false);
                if (result != null || i >= RetryDelays.Length)
                {
                    return result;
                }

                _logger.LogInformation("[Federation] {What} from {Server} failed; retrying in {Seconds:0} s", what, server.Name, RetryDelays[i].TotalSeconds);
                await Task.Delay(RetryDelays[i], cancellationToken).ConfigureAwait(false);
            }
        }

        private static string StatusKey(LibraryMapping mapping, RemoteLibrarySource source)
            => FederationSyncStatus.KeyFor(mapping.LocalLibraryName, source.ServerId, source.RemoteLibraryId);

        /// <summary>
        /// Check phase for one friend library: confirms the friend runs Federation
        /// and reads its complete title list for every item type, without
        /// downloading any title. The download phase then works from this plan.
        /// </summary>
        private async Task<SourcePlan> PlanSourceAsync(
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var key = StatusKey(mapping, source);
            var plan = new SourcePlan();
            Status.Update(key, s => s.State = FederationSyncStatus.Checking);

            if (_externalCatalogs.For(server) != null)
            {
                plan.External = true;
                Status.Update(key, s =>
                {
                    s.Mode = "external";
                    s.State = FederationSyncStatus.Ready;
                });
                return plan;
            }

            var client = _clientFactory.GetClient(server);
            if (client == null)
            {
                plan.Failed = true;
                Status.Fail(key, $"Couldn't set up a connection to {server.Name}. Check its address on the Friends tab.");
                return plan;
            }

            // Federation only ever talks to the remote's stock Jellyfin API (Items,
            // Users, PlaybackInfo, ...), so nothing above this point actually proves
            // the remote is a federation peer rather than just any reachable Jellyfin
            // server. Checked per source so it stays current; the client caches the
            // probe briefly, so this is one round trip per server per cycle.
            var peerStatus = await client.GetFederationPeerStatusAsync(cancellationToken).ConfigureAwait(false);
            if (peerStatus == FederationPeerStatus.NotInstalled)
            {
                _logger.LogWarning(
                    "[Federation] {Server} does not have the Federation plugin installed; its items in {Mapping} will be removed",
                    server.Name,
                    mapping.LocalLibraryName);
                plan.PluginMissing = true;
                Status.Fail(key, $"{server.Name} answered, but Federation isn't installed there any more, so its titles were removed from this library.");
                return plan;
            }

            if (peerStatus == FederationPeerStatus.Unknown)
            {
                // Unreachable, or answered from something that isn't the remote's
                // Jellyfin (a tunnel 502, a proxy error page, an access gate). An
                // ordinary failure: the cache is preserved untouched. Reporting it as
                // "plugin missing" would delete this server's library over a blip.
                _logger.LogWarning(
                    "[Federation] Could not confirm whether {Server} is running Federation; skipping it this cycle and keeping its cached content",
                    server.Name);
                plan.Failed = true;
                Status.Fail(key, $"Couldn't reach {server.Name}, or it didn't answer as a Federation server. Titles already here were kept.");
                return plan;
            }

            plan.Client = client;
            var bytesBefore = client.CatalogBytesReceived;
            IReadOnlySet<Guid> changedSeries = new HashSet<Guid>();
            foreach (var mediaType in MediaTypesFor(mapping))
            {
                var indexPlan = await PlanIndexAsync(mapping, server, source, config, client, mediaType, changedSeries, cancellationToken).ConfigureAwait(false);
                if (indexPlan == null)
                {
                    plan.Failed = true;
                    Status.Fail(key, (client.LastCatalogFailure ?? $"{server.Name} didn't send its complete title list for {source.RemoteLibraryName}.") + " Titles already here were kept.");
                    return plan;
                }

                if (!indexPlan.Supported)
                {
                    plan.Legacy = true;
                    plan.ByType.Clear();
                    break;
                }

                plan.ByType.Add(indexPlan.Plan!);
                if (string.Equals(mediaType, "Series", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mediaType, "MusicAlbum", StringComparison.OrdinalIgnoreCase))
                {
                    // Episodes of a changed series and songs of a changed album are
                    // fetched again: their parent links are rebuilt from it.
                    changedSeries = indexPlan.Plan!.Changed;
                }
            }

            var received = client.CatalogBytesReceived - bytesBefore;
            Status.Update(key, s =>
            {
                s.BytesReceived += received;
                s.State = FederationSyncStatus.Ready;
                if (plan.Legacy)
                {
                    s.Mode = "legacy";
                    return;
                }

                s.Mode = plan.ByType.Any(p => p.Full) ? "full" : "changes";
                s.Total = plan.ByType.Sum(p => p.Index.Count);
                s.ToDownload = plan.ByType.Sum(p => p.ToFetch.Count);
                s.ToRemove = plan.ByType.Sum(p => p.Removed);
            });
            return plan;
        }

        private static List<string> MediaTypesFor(LibraryMapping mapping)
        {
            // The /Items API only returns the item type it's asked for. A "Series"
            // mapping otherwise syncs nothing but empty show shells - Episodes are
            // a distinct item type that has to be requested explicitly, after the
            // series so episodes can look their series back up by remote id.
            // Music works the same way: a "MusicAlbum" mapping synced empty albums,
            // because songs (Audio) were never requested.
            var types = new List<string> { mapping.MediaType };
            if (string.Equals(mapping.MediaType, "Series", StringComparison.OrdinalIgnoreCase))
            {
                types.Add("Episode");
            }
            else if (string.Equals(mapping.MediaType, "MusicAlbum", StringComparison.OrdinalIgnoreCase))
            {
                types.Add("Audio");
            }

            return types;
        }

        private sealed record IndexPlanResult(bool Supported, IndexPlan? Plan)
        {
            public static IndexPlanResult Unsupported { get; } = new(false, null);
        }

        /// <summary>
        /// Reads the friend's complete [id, hash] list for one item type and
        /// decides, without downloading, which titles are unchanged, which must be
        /// fetched, and how many disappeared. Null when the list could not be read
        /// completely. A title is kept only when its hash matches what was last
        /// imported and this mapping still holds it; episodes of a series that
        /// changed this cycle are always fetched again, since their season and
        /// parent links are rebuilt from the series entry.
        /// </summary>
        private async Task<IndexPlanResult?> PlanIndexAsync(
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            RemoteServerClient client,
            string mediaType,
            IReadOnlySet<Guid> changedSeries,
            CancellationToken cancellationToken)
        {
            var key = $"{mapping.LocalLibraryName}|{server.Id}|{source.RemoteLibraryId}|{mediaType}";

            var index = new Dictionary<Guid, string>();
            int? cursor = null;
            for (var page = 0; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page >= MaxCatalogPages)
                {
                    _logger.LogWarning("[Federation] Catalog index for {Source} exceeded {MaxPages} pages; keeping cached items", source.RemoteLibraryName, MaxCatalogPages);
                    return null;
                }

                // The first request is a single entry: an older friend answers it
                // with one full item, which is all it costs to find that out.
                var requestCursor = cursor;
                var requestSize = page == 0 ? 1 : IndexPageSize;
                var indexPage = await WithRetriesAsync(
                    () => client.GetCatalogIndexPageAsync(mediaType, source.RemoteLibraryId, requestCursor, requestSize, cancellationToken),
                    "A title-list page",
                    server,
                    cancellationToken).ConfigureAwait(false);
                if (indexPage == null)
                {
                    return null;
                }

                if (!indexPage.Supported)
                {
                    _cache.RemoveSyncIndex(key);
                    return IndexPlanResult.Unsupported;
                }

                foreach (var (id, hash) in indexPage.Entries)
                {
                    index[id] = hash;
                }

                if (!indexPage.HasMore)
                {
                    break;
                }

                if (cursor == indexPage.NextCursor)
                {
                    _logger.LogWarning("[Federation] Catalog index for {Source} stopped advancing; keeping cached items", source.RemoteLibraryName);
                    return null;
                }

                cursor = indexPage.NextCursor;
            }

            var context = SyncContext(mapping, server, config, mediaType);
            var stored = _cache.GetSyncIndex(key);
            var now = DateTime.UtcNow;
            var trusted = stored != null
                && string.Equals(stored.Context, context, StringComparison.Ordinal)
                && now - stored.FullRefreshUtc < IndexFullRefreshInterval
                && now >= stored.FullRefreshUtc;
            var plan = new IndexPlan
            {
                Key = key,
                MediaType = mediaType,
                Index = index,
                Full = !trusted,
                Next = new SyncIndexSnapshot { Context = context, FullRefreshUtc = trusted ? stored!.FullRefreshUtc : now }
            };

            foreach (var (id, hash) in index)
            {
                if (trusted
                    && stored!.Items.TryGetValue(id, out var known)
                    && string.Equals(known.H, hash, StringComparison.Ordinal)
                    && !(known.Series is Guid series && changedSeries.Contains(series))
                    && (known.Skip
                        || (_cache.HoldsRemoteItem(mapping.LocalLibraryName, server.Id, id)
                            && (known.Season is not Guid season || _cache.HoldsRemoteItem(mapping.LocalLibraryName, server.Id, season)))))
                {
                    plan.Next.Items[id] = known;
                    if (!known.Skip)
                    {
                        plan.Keep.Add((id, known.Season));
                    }

                    continue;
                }

                plan.ToFetch.Add(id);
                plan.Changed.Add(id);
            }

            if (stored != null)
            {
                var gone = stored.Items.Where(pair => !index.ContainsKey(pair.Key)).ToList();
                plan.Changed.UnionWith(gone.Select(pair => pair.Key));
                plan.Removed = gone.Count(pair => !pair.Value.Skip);
            }

            return new IndexPlanResult(true, plan);
        }

        /// <summary>
        /// Download phase for one planned item type: marks unchanged titles seen and
        /// fetches the rest by id. Titles missing from the complete list are simply
        /// not marked seen, so the caller's normal prune removes them; a failed batch
        /// returns null and nothing is pruned.
        /// </summary>
        private async Task<int?> ApplyIndexPlanAsync(
            IndexPlan plan,
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            RemoteServerClient client,
            HashSet<Guid> seen,
            CancellationToken cancellationToken)
        {
            var statusKey = StatusKey(mapping, source);
            foreach (var (id, season) in plan.Keep)
            {
                seen.Add(id);
                if (season is Guid knownSeason)
                {
                    seen.Add(knownSeason);
                }
            }

            var total = plan.Keep.Count;
            var batchNumber = 0;
            foreach (var batch in plan.ToFetch.Chunk(Math.Max(1, IdsBatchSize)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesBefore = client.CatalogBytesReceived;
                // Type and library go along: see the Ids query in Peer/Items.
                var items = await WithRetriesAsync(
                    () => client.GetItemsAsync(mediaType: plan.MediaType, parentId: source.RemoteLibraryId, ids: batch, limit: batch.Length, cancellationToken: cancellationToken),
                    "A download batch",
                    server,
                    cancellationToken).ConfigureAwait(false);
                var received = client.CatalogBytesReceived - bytesBefore;
                Status.Update(statusKey, s => s.BytesReceived += received);
                if (items == null)
                {
                    Status.Fail(statusKey, $"Downloading titles from {server.Name} failed part-way" + (client.LastCatalogFailure != null ? $" ({client.LastCatalogFailure})" : ".") + " Nothing was removed; the next sync continues.");
                    return null;
                }

                foreach (var remoteItem in items)
                {
                    if (!plan.Index.TryGetValue(remoteItem.Id, out var hash))
                    {
                        continue;
                    }

                    try
                    {
                        if (!PeerCatalogPage.IsOwned(remoteItem)
                            || !IncomingContentFilterService.IsAllowedByIncomingFilter(config.IncomingFilter, remoteItem))
                        {
                            plan.Next.Items[remoteItem.Id] = new SyncIndexItem { H = hash, Skip = true };
                            continue;
                        }

                        if (UpsertRemoteItem(mapping, remoteItem, server, config, seen) != null)
                        {
                            seen.Add(remoteItem.Id);
                            total++;
                            var isEpisode = remoteItem.Type == Jellyfin.Data.Enums.BaseItemKind.Episode;
                            var isSong = remoteItem.Type == Jellyfin.Data.Enums.BaseItemKind.Audio;
                            plan.Next.Items[remoteItem.Id] = new SyncIndexItem
                            {
                                H = hash,
                                Season = isEpisode ? remoteItem.SeasonId : null,
                                Series = isEpisode ? remoteItem.SeriesId : isSong ? remoteItem.AlbumId : null
                            };
                        }

                        // else: an orphaned episode. Left out of the index so it is
                        // fetched again next cycle, once its series may be present.
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Failed to upsert item {Name}", remoteItem.Name);
                        Status.Fail(statusKey, $"\"{remoteItem.Name}\" from {server.Name} couldn't be stored. Nothing was removed; the Log tab has details.");
                        return null;
                    }
                }

                Status.Update(statusKey, s => s.Downloaded += batch.Length);

                // Record progress after every batch: everything kept or fetched so
                // far is trusted next time, so a sync that fails or is interrupted
                // later resumes with the titles still missing instead of starting
                // over. The prune still waits for a complete, successful pass.
                _cache.SetSyncIndex(plan.Key, CopyOf(plan.Next));
                if (++batchNumber % Math.Max(1, SaveEveryBatches) == 0)
                {
                    await _cache.SaveAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _cache.SetSyncIndex(plan.Key, plan.Next);
            if (plan.ToFetch.Count > 0)
            {
                _logger.LogInformation(
                    "[Federation] {Server}/{Library}: {Changed} of {Total} {Type} item(s) new or changed",
                    server.Name,
                    source.RemoteLibraryName,
                    plan.ToFetch.Count,
                    plan.Index.Count,
                    plan.MediaType);
            }

            return total;
        }

        private static SyncIndexSnapshot CopyOf(SyncIndexSnapshot snapshot) => new()
        {
            Context = snapshot.Context,
            FullRefreshUtc = snapshot.FullRefreshUtc,
            Items = new Dictionary<Guid, SyncIndexItem>(snapshot.Items)
        };

        /// <summary>
        /// Everything on this side that shapes how a fetched title is stored.
        /// Stored hashes are only trusted while this is unchanged.
        /// </summary>
        private static string SyncContext(LibraryMapping mapping, RemoteServer server, PluginConfiguration config, string mediaType)
        {
            var context = System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = typeof(FederationSyncService).Assembly.GetName().Version?.ToString(),
                mapping.MediaType,
                ItemType = mediaType,
                server.Priority,
                config.EnableDedup,
                Dedup = config.EnableDedup ? config.DedupProviderIds : null,
                config.IncomingFilter
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
        }

        /// <summary>
        /// Upserts a single remote item into the cache, returning the resulting
        /// cache entry. Returns null without upserting anything when this is an
        /// Episode whose series has not been synced yet this cycle (see
        /// <see cref="UpsertEpisodeSeason"/>) - skipped rather than materialized
        /// as a loose item at the library root with no SeriesId/SeasonId, which
        /// nothing afterward would ever notice or repair.
        /// </summary>
        private FederatedCacheEntry? UpsertRemoteItem(
            LibraryMapping mapping,
            MediaBrowser.Model.Dto.BaseItemDto remoteItem,
            RemoteServer server,
            PluginConfiguration config,
            HashSet<Guid> seen)
        {
            var itemType = remoteItem.Type.ToString();
            var isEpisode = string.Equals(itemType, "Episode", StringComparison.OrdinalIgnoreCase);
            var parentKey = isEpisode ? UpsertEpisodeSeason(mapping, remoteItem, server, seen) : null;

            // A song sits inside its album when the album was synced (the album pass
            // runs first); a song without one (a single) stays a loose song.
            if (remoteItem.Type == Jellyfin.Data.Enums.BaseItemKind.Audio && remoteItem.AlbumId is Guid albumId)
            {
                var albumKey = _cache.TryGetLocalKeyForRemoteItem(server.Id, albumId);
                if (albumKey != null && _cache.HoldsRemoteItem(mapping.LocalLibraryName, server.Id, albumId))
                {
                    parentKey = albumKey;
                }
            }

            if (isEpisode)
            {
                if (parentKey == null)
                {
                    return null;
                }

                // Merges across servers by (season, episode number) rather than
                // each source getting its own raw-keyed episode - otherwise the
                // same real episode synced from two servers (the same show
                // available from two federated friends) shows up twice. Safe
                // unlike matching episodes by provider id independently (the
                // risk an earlier version of this comment warned about, where
                // two unrelated shows could share a provider id scheme): this is
                // scoped to a season key that is itself only ever produced by
                // UpsertEpisodeSeason once the episode's *series* already
                // matched via its own confirmed provider id, so there is no
                // arbitrary cross-show collision risk.
                //
                // Only when a real episode number is reported. Falling back to a
                // shared placeholder (e.g. 0) for every episode missing one was
                // tried and is exactly what production was showing: every
                // episode in a season lacking numbering (multi-part specials,
                // some sources' metadata gaps - confirmed live on several shows)
                // collided onto the identical key, silently merging dozens of
                // genuinely distinct episodes into one and deleting the rest as
                // "superseded". Without a number to key on, each source's copy
                // stays its own raw-keyed entry instead - occasionally still
                // duplicated across servers, but never destructive.
                if (remoteItem.IndexNumber.HasValue)
                {
                    return _cache.UpsertByProviderId(
                        mappingName: mapping.LocalLibraryName,
                        providerName: "federation-episode",
                        providerId: $"{HashKey(parentKey)}:{remoteItem.IndexNumber.Value}",
                        remoteItem: remoteItem,
                        serverId: server.Id,
                        remoteItemId: remoteItem.Id,
                        serverPriority: server.Priority,
                        itemType: itemType,
                        parentKey: parentKey);
                }

                return _cache.UpsertRaw(
                    mappingName: mapping.LocalLibraryName,
                    serverId: server.Id,
                    remoteItemId: remoteItem.Id,
                    remoteItem: remoteItem,
                    serverPriority: server.Priority,
                    itemType: itemType,
                    parentKey: parentKey);
            }

            var providerIds = remoteItem.ProviderIds;
            var dedupKeys = config.EnableDedup ? (config.DedupProviderIds ?? new List<string>()) : new List<string>();

            string? matchedProvider = null;
            string? matchedId = null;

            if (providerIds != null && dedupKeys.Count > 0)
            {
                foreach (var key in dedupKeys)
                {
                    if (FederationLibraryManager.TryGetProviderId(providerIds, key, out var val))
                    {
                        matchedProvider = key;
                        matchedId = val;
                        break;
                    }
                }
            }

            if (matchedProvider != null && matchedId != null)
            {
                return _cache.UpsertByProviderId(
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

            return _cache.UpsertRaw(
                mappingName: mapping.LocalLibraryName,
                serverId: server.Id,
                remoteItemId: remoteItem.Id,
                remoteItem: remoteItem,
                serverPriority: server.Priority,
                itemType: itemType,
                parentKey: parentKey);
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

            // Merges across servers by (series, season number) rather than each
            // source getting its own raw-keyed season under the one (already
            // provider-id-deduped) series - otherwise the same real show synced
            // from two servers ends up with two parallel season/episode trees,
            // showing every episode twice. Scoped to a series key that has
            // already independently been confirmed to match (via its own real
            // provider id), so this carries none of the "two unrelated shows
            // could share an id scheme" risk that keeps episode/series matching
            // itself restricted to real provider ids.
            //
            // Only when a real season number is reported - see the matching
            // guard in UpsertRemoteItem's episode branch for why a shared
            // placeholder number is not safe to merge on.
            var seasonEntry = remoteItem.ParentIndexNumber.HasValue
                ? _cache.UpsertByProviderId(
                    mappingName: mapping.LocalLibraryName,
                    providerName: "federation-season",
                    providerId: $"{HashKey(seriesKey)}:{remoteItem.ParentIndexNumber.Value}",
                    remoteItem: seasonDto,
                    serverId: server.Id,
                    remoteItemId: remoteItem.SeasonId.Value,
                    serverPriority: server.Priority,
                    itemType: "Season",
                    parentKey: seriesKey)
                : _cache.UpsertRaw(
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

        /// <summary>
        /// Deterministic, fixed-format token for a cache key, used to embed one
        /// key inside another (season-within-series, episode-within-season)
        /// without risking a false match in <c>FederationItemCache</c>'s raw-key
        /// substring check - an embedded key can itself be raw-shaped (e.g. a
        /// series with no real provider id still gets a valid raw key), which
        /// would otherwise make the composite key look like a raw key too.
        /// </summary>
        private static string HashKey(string key)
            => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));

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
    /// The outcome of the most recently completed sync, kept in memory so the
    /// config page can report whether federation is actually working rather than
    /// leaving that information only in the server log.
    /// </summary>
    public class SyncHealth
    {
        /// <summary>Gets or sets when the sync finished (UTC).</summary>
        public DateTime FinishedUtc { get; set; }

        /// <summary>Gets or sets a value indicating whether every source synced cleanly.</summary>
        public bool Success { get; set; }

        /// <summary>Gets or sets the human-readable outcome.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Gets or sets how many items were refreshed.</summary>
        public int ItemCount { get; set; }

        /// <summary>Gets or sets how many sources failed (their cached content was preserved).</summary>
        public int FailedSources { get; set; }
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
