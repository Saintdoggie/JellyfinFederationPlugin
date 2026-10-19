using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Keeps one "Popular on {friend}" collection per friend that shares its
    /// popularity list (<see cref="RemoteServer.SharePopularity"/> on their
    /// side) and that this server shows (<see cref="RemoteServer.ShowPopularCollection"/>).
    /// The collection holds the local federated copies of the friend's most
    /// played titles that this server already has; it never adds titles.
    /// A Jellyfin collection works in every client, unlike a web-only home row.
    /// </summary>
    public class FederationPopularCollectionService
    {
        /// <summary>The most titles one collection shows.</summary>
        internal const int MaxTitles = 20;

        /// <summary>
        /// Provider id marking a collection as this plugin's, holding the
        /// friend's server id. Lets a refresh find its collections even when
        /// Jellyfin re-saved one after it was deleted (a metadata save queued
        /// at creation can write it back) or the state file was lost.
        /// </summary>
        internal const string ProviderIdName = "FederationPopular";

        private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly FederationItemCache _cache;
        private readonly FederationLibraryManager _federationManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;
        private readonly ILogger<FederationPopularCollectionService> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _rerunRequested;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationPopularCollectionService"/> class.
        /// </summary>
        public FederationPopularCollectionService(
            IRemoteServerClientFactory clientFactory,
            FederationItemCache cache,
            FederationLibraryManager federationManager,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            ILogger<FederationPopularCollectionService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _federationManager = federationManager;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
            _logger = logger;
        }

        /// <summary>
        /// Builds, updates or removes every friend's collection. A friend that
        /// cannot be reached keeps its current collection, and one friend's
        /// failure never stops the others. Asked while a refresh runs, it runs
        /// again right after that one instead of being dropped. Returns whether
        /// this call did the work itself.
        /// </summary>
        public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _rerunRequested, 1);
            var ran = false;
            while (Volatile.Read(ref _rerunRequested) == 1 && await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    while (Interlocked.Exchange(ref _rerunRequested, 0) == 1)
                    {
                        await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
                        ran = true;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            return ran;
        }

        private async Task RefreshOnceAsync(CancellationToken cancellationToken)
        {
            var servers = Plugin.Instance?.Configuration?.RemoteServers ?? new List<RemoteServer>();
            var collections = LoadState();
            var changed = false;
            try
            {
                foreach (var server in servers.Where(s => s.Kind == ServerKind.Jellyfin))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        List<Guid>? wanted = null;
                        if (server.Enabled && server.ShowPopularCollection)
                        {
                            var popular = await _clientFactory.GetClient(server).GetPopularTitlesAsync(cancellationToken).ConfigureAwait(false);
                            if (popular == null)
                            {
                                continue;
                            }

                            wanted = popular.Shared ? MapToLocalItems(server.Id, popular.Titles) : null;
                        }

                        changed |= await ApplyAsync(server, wanted, collections).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "[Federation] Could not update the popular collection for {ServerName}", server.Name);
                    }
                }

                // Friends removed since the last run, also ones only a leftover
                // tagged collection remembers.
                var gone = collections.Keys
                    .Concat(FindTagged(null).Select(b => b.ProviderIds[ProviderIdName]))
                    .Where(id => servers.All(s => s.Id != id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                foreach (var serverId in gone)
                {
                    try
                    {
                        changed |= await ApplyAsync(new RemoteServer { Id = serverId }, null, collections).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Could not remove the popular collection of a former friend");
                    }
                }
            }
            finally
            {
                // Also after a cancellation: what was created must be remembered.
                if (changed)
                {
                    SaveState(collections);
                }
            }
        }

        /// <summary>
        /// The local items for a friend's popular titles, most played first,
        /// only those this server has synced. Internal for tests.
        /// </summary>
        internal List<Guid> MapToLocalItems(string serverId, IEnumerable<PopularTitle> titles)
        {
            var result = new List<Guid>();
            foreach (var title in titles.OrderByDescending(t => t.Plays))
            {
                if (!Guid.TryParse(title.Id, out var remoteId))
                {
                    continue;
                }

                var key = _cache.TryGetLocalKeyForRemoteItem(serverId, remoteId);
                var entry = key == null ? null : _cache.GetEntryByKey(key);
                if (entry == null)
                {
                    continue;
                }

                var localId = _federationManager.ComputeItemId(entry);
                if (!result.Contains(localId) && _libraryManager.GetItemById(localId) != null)
                {
                    result.Add(localId);
                }

                if (result.Count >= MaxTitles)
                {
                    break;
                }
            }

            return result;
        }

        /// <summary>Removes every popular collection this plugin made (plugin uninstall).</summary>
        public void RemoveAll()
        {
            foreach (var id in LoadState().Values)
            {
                DeleteCollection(id);
            }

            foreach (var leftover in FindTagged(null))
            {
                DeleteCollection(leftover.Id);
            }

            SaveState(new Dictionary<string, Guid>());
        }

        private async Task<bool> ApplyAsync(RemoteServer server, List<Guid>? wanted, Dictionary<string, Guid> collections)
        {
            collections.TryGetValue(server.Id, out var collectionId);
            var tagged = FindTagged(server.Id);
            var existing = (collectionId == Guid.Empty ? null : _libraryManager.GetItemById(collectionId) as BoxSet)
                ?? tagged.FirstOrDefault();

            if (wanted == null || wanted.Count == 0)
            {
                var removed = false;
                foreach (var boxSet in tagged.Append(existing).Where(b => b != null).DistinctBy(b => b!.Id))
                {
                    DeleteCollection(boxSet!.Id);
                    removed = true;
                }

                return collections.Remove(server.Id) || removed;
            }

            // Duplicates (an earlier copy that came back after a delete).
            foreach (var extra in tagged.Where(b => existing == null || b.Id != existing.Id))
            {
                DeleteCollection(extra.Id);
            }

            if (existing != null && collectionId != existing.Id)
            {
                collections[server.Id] = existing.Id;
            }

            var name = "Popular on " + (string.IsNullOrWhiteSpace(server.Name) ? "a friend" : server.Name);
            if (existing == null)
            {
                var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = name,
                    IsLocked = true,
                    ItemIdList = wanted.Select(id => id.ToString("N")).ToArray(),
                    ProviderIds = new Dictionary<string, string> { [ProviderIdName] = server.Id }
                }).ConfigureAwait(false);
                collections[server.Id] = created.Id;
                _logger.LogInformation("[Federation] Created the collection \"{Name}\" with {Count} titles", name, wanted.Count);
                return true;
            }

            var current = existing.GetLinkedChildren().Select(i => i.Id).ToHashSet();
            var remove = current.Where(id => !wanted.Contains(id)).ToList();
            var add = wanted.Where(id => !current.Contains(id)).ToList();
            if (remove.Count > 0)
            {
                await _collectionManager.RemoveFromCollectionAsync(existing.Id, remove).ConfigureAwait(false);
            }

            if (add.Count > 0)
            {
                await _collectionManager.AddToCollectionAsync(existing.Id, add).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>This plugin's collections for one friend, or for all friends when <paramref name="serverId"/> is null.</summary>
        private List<BoxSet> FindTagged(string? serverId)
        {
            try
            {
                return _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.BoxSet },
                        Recursive = true
                    })
                    .OfType<BoxSet>()
                    .Where(b => b.ProviderIds != null
                        && b.ProviderIds.TryGetValue(ProviderIdName, out var owner)
                        && (serverId == null || string.Equals(owner, serverId, StringComparison.Ordinal)))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not list popular collections");
                return new List<BoxSet>();
            }
        }

        private void DeleteCollection(Guid id)
        {
            try
            {
                if (_libraryManager.GetItemById(id) is BoxSet boxSet)
                {
                    _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true }, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not remove a friend's popular collection {Id}", id);
            }
        }

        private static string? StatePath()
        {
            var dir = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "federation-popular-collections.json");
        }

        private Dictionary<string, Guid> LoadState()
        {
            var path = StatePath();
            try
            {
                if (path != null && File.Exists(path))
                {
                    return JsonSerializer.Deserialize<Dictionary<string, Guid>>(File.ReadAllText(path), FileJson)
                        ?? new Dictionary<string, Guid>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not read the popular collections list");
            }

            return new Dictionary<string, Guid>();
        }

        private void SaveState(Dictionary<string, Guid> collections)
        {
            var path = StatePath();
            if (path == null)
            {
                return;
            }

            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(collections, FileJson));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not save the popular collections list");
            }
        }
    }
}
