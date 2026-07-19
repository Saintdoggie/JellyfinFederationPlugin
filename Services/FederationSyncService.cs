using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Refreshes the federation cache by walking each mapping and pulling items
    /// from remote servers. Replaces the old <c>FederationSyncService</c> file writer.
    /// </summary>
    public class FederationSyncService
    {
        private readonly ILogger<FederationSyncService> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly FederationItemCache _cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationSyncService"/> class.
        /// </summary>
        public FederationSyncService(
            ILogger<FederationSyncService> logger,
            FederationLibraryManager federationManager,
            IRemoteServerClientFactory clientFactory,
            FederationItemCache cache)
        {
            _logger = logger;
            _federationManager = federationManager;
            _clientFactory = clientFactory;
            _cache = cache;
        }

        /// <summary>
        /// Refreshes all mappings from all configured remote servers. Never throws:
        /// a failed server leaves the existing cache intact.
        /// </summary>
        public async Task<SyncResult> SyncAllAsync(CancellationToken cancellationToken = default)
        {
            var operationId = Guid.NewGuid().ToString();
            SyncProgressTracker.Start(operationId, 100);
            SyncProgressTracker.Update(operationId, 0, "Starting refresh...");

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

                int totalItems = 0;
                for (int i = 0; i < mappings.Count; i++)
                {
                    var mapping = mappings[i];
                    cancellationToken.ThrowIfCancellationRequested();
                    SyncProgressTracker.Update(operationId, totalItems, mappings.Count, $"Processing mapping {i + 1}/{mappings.Count}: {mapping.LocalLibraryName}");
                    totalItems += await RefreshMappingAsync(mapping, config, cancellationToken).ConfigureAwait(false);
                }

                await _cache.SaveAsync(cancellationToken).ConfigureAwait(false);
                SyncProgressTracker.Complete(operationId, true, $"Refreshed {totalItems} items");
                return new SyncResult
                {
                    Success = true,
                    ItemCount = totalItems,
                    Message = $"Refreshed {totalItems} items across {mappings.Count} mapping(s)",
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
        }

        /// <summary>
        /// Refreshes a specific server by its ID (refreshes all mappings that use it).
        /// </summary>
        public async Task<SyncResult> SyncServerAsync(string serverId, CancellationToken cancellationToken = default)
        {
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

                int total = 0;
                foreach (var mapping in mappings)
                {
                    total += await RefreshMappingAsync(mapping, config!, cancellationToken, onlyServerId: serverId).ConfigureAwait(false);
                }

                await _cache.SaveAsync(cancellationToken).ConfigureAwait(false);
                return new SyncResult { Success = true, ItemCount = total, Message = $"Refreshed {total} items from {server.Name}" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error syncing server {ServerId}", serverId);
                return Failed(ex.Message);
            }
        }

        private async Task<int> RefreshMappingAsync(
            LibraryMapping mapping,
            PluginConfiguration config,
            CancellationToken cancellationToken,
            string? onlyServerId = null)
        {
            _logger.LogInformation("[Federation] Refreshing mapping {Name}", mapping.LocalLibraryName);
            _cache.ClearMapping(mapping.LocalLibraryName);

            int total = 0;
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

                try
                {
                    total += await RefreshSourceAsync(mapping, server, source, config, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Federation] Error refreshing source {Source} on {Server}", source.RemoteLibraryName, server.Name);
                }
            }

            return total;
        }

        private async Task<int> RefreshSourceAsync(
            LibraryMapping mapping,
            RemoteServer server,
            RemoteLibrarySource source,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var client = _clientFactory.GetClient(server);
            if (client == null)
            {
                return 0;
            }

            int total = 0;
            int pageSize = 200;
            int startIndex = 0;
            int pageNumber = 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await client.GetItemsAsync(
                    userId: server.UserId,
                    mediaType: mapping.MediaType,
                    parentId: source.RemoteLibraryId,
                    startIndex: startIndex,
                    limit: pageSize,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (page == null || page.Count == 0)
                {
                    break;
                }

                foreach (var remoteItem in page)
                {
                    try
                    {
                        UpsertRemoteItem(mapping, remoteItem, server, config);
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

            _logger.LogInformation("[Federation] Refreshed {Count} items from {Server}/{Library}", total, server.Name, source.RemoteLibraryName);
            return total;
        }

        private void UpsertRemoteItem(
            LibraryMapping mapping,
            MediaBrowser.Model.Dto.BaseItemDto remoteItem,
            RemoteServer server,
            PluginConfiguration config)
        {
            var itemType = remoteItem.Type.ToString();
            var providerIds = remoteItem.ProviderIds;
            var dedupKeys = config.EnableDedup ? (config.DedupProviderIds ?? new List<string>()) : new List<string>();

            string? matchedProvider = null;
            string? matchedId = null;
            if (providerIds != null && dedupKeys.Count > 0)
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
                    itemType: itemType);
            }
            else
            {
                _cache.UpsertRaw(
                    mappingName: mapping.LocalLibraryName,
                    serverId: server.Id,
                    remoteItemId: remoteItem.Id,
                    remoteItem: remoteItem,
                    serverPriority: server.Priority,
                    itemType: itemType);
            }
        }

        private static SyncResult Failed(string message, string? operationId = null)
        {
            return new SyncResult { Success = false, Message = message, OperationId = operationId };
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
        /// Gets or sets a message describing the result.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the operation ID for progress tracking.
        /// </summary>
        public string? OperationId { get; set; }
    }
}
