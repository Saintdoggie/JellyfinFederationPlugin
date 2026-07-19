using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// In-memory + persisted cache of resolved federated items.
    /// </summary>
    public class FederationItemCache
    {
        private readonly ILogger<FederationItemCache> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ConcurrentDictionary<string, FederatedCacheEntry> _entries = new();
        private string _cacheFilePath = string.Empty;
        private DateTime _lastRefreshUtc = DateTime.MinValue;

        public FederationItemCache(
            ILogger<FederationItemCache> logger,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Gets the last refresh time (UTC).
        /// </summary>
        public DateTime LastRefresh => _lastRefreshUtc;

        /// <summary>
        /// Gets the number of entries in the cache.
        /// </summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Sets the on-disk cache file location and loads any existing cache.
        /// </summary>
        public void Initialize(string cacheFilePath)
        {
            _cacheFilePath = cacheFilePath ?? string.Empty;
            LoadFromDisk();
        }

        /// <summary>
        /// Gets the cache entry for a federation path, or null if not present.
        /// </summary>
        public FederatedCacheEntry? GetEntry(string federationPath)
        {
            var key = NormalizeKey(federationPath);
            if (key == null)
            {
                return null;
            }

            _entries.TryGetValue(key, out var entry);
            return entry;
        }

        /// <summary>
        /// Gets all entries for a mapping (by mapping name).
        /// </summary>
        public IEnumerable<FederatedCacheEntry> GetEntriesForMapping(string mappingName)
        {
            foreach (var kvp in _entries)
            {
                if (kvp.Value.MappingName.Equals(mappingName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return kvp.Value;
                }
            }
        }

        /// <summary>
        /// All entries currently in the cache.
        /// </summary>
        public IEnumerable<FederatedCacheEntry> GetAllEntries() => _entries.Values;

        /// <summary>
        /// Upserts a cache entry for the given provider id key within a mapping.
        /// </summary>
        public FederatedCacheEntry UpsertByProviderId(
            string mappingName,
            string providerName,
            string providerId,
            BaseItemDto remoteItem,
            string serverId,
            Guid remoteItemId,
            int serverPriority,
            string itemType)
        {
            var key = BuildProviderKey(mappingName, providerName, providerId);
            var entry = _entries.AddOrUpdate(
                key,
                _ => CreateEntry(key, mappingName, itemType, remoteItem, serverId, remoteItemId, serverPriority),
                (_, existing) =>
                {
                    existing.AddSource(serverId, remoteItemId, serverPriority);
                    existing.UpdateFromRemote(remoteItem, serverId, remoteItemId, serverPriority);
                    existing.LastRefreshedUtc = DateTime.UtcNow;
                    return existing;
                });

            _lastRefreshUtc = DateTime.UtcNow;
            return entry;
        }

        /// <summary>
        /// Upserts a raw cache entry (no provider id) keyed by server + remote item.
        /// </summary>
        public FederatedCacheEntry UpsertRaw(
            string mappingName,
            string serverId,
            Guid remoteItemId,
            BaseItemDto remoteItem,
            int serverPriority,
            string itemType)
        {
            var key = BuildRawKey(mappingName, serverId, remoteItemId);
            var entry = _entries.AddOrUpdate(
                key,
                _ => CreateEntry(key, mappingName, itemType, remoteItem, serverId, remoteItemId, serverPriority),
                (_, existing) =>
                {
                    existing.AddSource(serverId, remoteItemId, serverPriority);
                    existing.UpdateFromRemote(remoteItem, serverId, remoteItemId, serverPriority);
                    existing.LastRefreshedUtc = DateTime.UtcNow;
                    return existing;
                });

            _lastRefreshUtc = DateTime.UtcNow;
            return entry;
        }

        /// <summary>
        /// Removes all entries belonging to a mapping (used on resync).
        /// </summary>
        public void ClearMapping(string mappingName)
        {
            var toRemove = _entries.Where(kvp => kvp.Value.MappingName.Equals(mappingName, StringComparison.OrdinalIgnoreCase)).Select(kvp => kvp.Key).ToList();
            foreach (var k in toRemove)
            {
                _entries.TryRemove(k, out _);
            }
        }

        /// <summary>
        /// Clears the entire cache.
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
            _lastRefreshUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Persists the cache to disk.
        /// </summary>
        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_cacheFilePath))
            {
                return Task.CompletedTask;
            }

            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var payload = new CachePayload
                {
                    LastRefreshUtc = _lastRefreshUtc,
                    Entries = _entries.Values.ToList()
                };

                var json = JsonSerializer.Serialize(payload, CacheJsonOptions);
                File.WriteAllText(_cacheFilePath, json);
                _logger.LogDebug("[Federation] Cache saved to {Path} ({Count} entries)", _cacheFilePath, _entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to save cache to {Path}", _cacheFilePath);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Builds a federation path for a deduped entry.
        /// </summary>
        public static string BuildProviderPath(string mappingName, string providerName, string providerId)
            => $"federation://{mappingName}/{providerName}:{providerId}";

        /// <summary>
        /// Builds a federation path for a raw entry.
        /// </summary>
        public static string BuildRawPath(string mappingName, string serverId, Guid remoteItemId)
            => $"federation://{mappingName}/raw/{serverId}/{remoteItemId}";

        /// <summary>
        /// Builds the cache key for a deduped entry.
        /// </summary>
        public static string BuildProviderKey(string mappingName, string providerName, string providerId)
            => $"{mappingName}/{providerName}:{providerId}";

        /// <summary>
        /// Builds the cache key for a raw entry.
        /// </summary>
        public static string BuildRawKey(string mappingName, string serverId, Guid remoteItemId)
            => $"{mappingName}/raw/{serverId}/{remoteItemId}";

        /// <summary>
        /// Tries to parse a federation path into its components.
        /// </summary>
        public static bool TryParsePath(
            string path,
            out string mappingName,
            out string? providerName,
            out string? providerId,
            out string? rawServerId,
            out Guid? rawRemoteItemId)
        {
            mappingName = string.Empty;
            providerName = null;
            providerId = null;
            rawServerId = null;
            rawRemoteItemId = null;

            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            const string prefix = "federation://";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = path.Substring(prefix.Length);
            var firstSlash = rest.IndexOf('/');
            if (firstSlash <= 0)
            {
                return false;
            }

            mappingName = rest.Substring(0, firstSlash);
            var remainder = rest.Substring(firstSlash + 1);

            if (remainder.StartsWith("raw/", StringComparison.OrdinalIgnoreCase))
            {
                var rawParts = remainder.Substring(4).Split('/', 2);
                if (rawParts.Length != 2)
                {
                    return false;
                }

                rawServerId = rawParts[0];
                if (!Guid.TryParse(rawParts[1], out var rawId))
                {
                    return false;
                }

                rawRemoteItemId = rawId;
                return true;
            }

            var colon = remainder.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            providerName = remainder.Substring(0, colon);
            providerId = remainder.Substring(colon + 1);
            return true;
        }

        private static string? NormalizeKey(string federationPath)
        {
            if (!TryParsePath(federationPath, out var mapping, out var providerName, out var providerId, out var rawServerId, out var rawRemoteItemId))
            {
                return null;
            }

            if (providerName != null && providerId != null)
            {
                return BuildProviderKey(mapping, providerName, providerId);
            }

            if (rawServerId != null && rawRemoteItemId.HasValue)
            {
                return BuildRawKey(mapping, rawServerId, rawRemoteItemId.Value);
            }

            return null;
        }

        private FederatedCacheEntry CreateEntry(
            string key,
            string mappingName,
            string itemType,
            BaseItemDto remoteItem,
            string serverId,
            Guid remoteItemId,
            int serverPriority)
        {
            var entry = new FederatedCacheEntry
            {
                Key = key,
                MappingName = mappingName,
                ItemType = itemType,
                PrimarySourceIndex = 0,
                LastRefreshedUtc = DateTime.UtcNow
            };
            entry.AddSource(serverId, remoteItemId, serverPriority);
            entry.UpdateFromRemote(remoteItem, serverId, remoteItemId, serverPriority);
            return entry;
        }

        private void LoadFromDisk()
        {
            if (string.IsNullOrEmpty(_cacheFilePath) || !File.Exists(_cacheFilePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_cacheFilePath);
                var payload = JsonSerializer.Deserialize<CachePayload>(json, CacheJsonOptions);
                if (payload?.Entries != null)
                {
                    foreach (var entry in payload.Entries)
                    {
                        if (!string.IsNullOrEmpty(entry.Key))
                        {
                            _entries[entry.Key] = entry;
                        }
                    }
                }

                _lastRefreshUtc = payload?.LastRefreshUtc ?? DateTime.MinValue;
                _logger.LogInformation("[Federation] Loaded {Count} cache entries from {Path}", _entries.Count, _cacheFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to load cache from {Path}", _cacheFilePath);
            }
        }

        private static readonly JsonSerializerOptions CacheJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private class CachePayload
        {
            public DateTime LastRefreshUtc { get; set; }
            public List<FederatedCacheEntry> Entries { get; set; } = new();
        }
    }

    /// <summary>
    /// One entry in the federation cache. May represent multiple remote sources (deduped).
    /// </summary>
    public class FederatedCacheEntry
    {
        /// <summary>
        /// Cache key (mapping/provider:id or mapping/raw/server/remoteId).
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Mapping name this entry belongs to.
        /// </summary>
        public string MappingName { get; set; } = string.Empty;

        /// <summary>
        /// Item type (Movie / Series / Episode / Audio / etc.).
        /// </summary>
        public string ItemType { get; set; } = "Movie";

        /// <summary>
        /// The list of remote sources that provide this item.
        /// </summary>
        public List<FederatedSource> Sources { get; set; } = new();

        /// <summary>
        /// Index into <see cref="Sources"/> of the primary source.
        /// </summary>
        public int PrimarySourceIndex { get; set; }

        /// <summary>
        /// Cached metadata snapshot.
        /// </summary>
        public FederatedItemMetadata Metadata { get; set; } = new();

        /// <summary>
        /// Last refreshed UTC.
        /// </summary>
        public DateTime LastRefreshedUtc { get; set; }

        /// <summary>
        /// The federation path for this entry (built from Key).
        /// </summary>
        public string FederationPath
        {
            get
            {
                if (Key.Contains("/raw/", StringComparison.OrdinalIgnoreCase))
                {
                    return "federation://" + Key;
                }

                return "federation://" + Key;
            }
        }

        /// <summary>
        /// Gets the primary source.
        /// </summary>
        public FederatedSource? PrimarySource => Sources.Count > 0 ? Sources[Math.Min(PrimarySourceIndex, Sources.Count - 1)] : null;

        /// <summary>
        /// Adds or updates a remote source.
        /// </summary>
        public void AddSource(string serverId, Guid remoteItemId, int serverPriority)
        {
            var existing = Sources.FirstOrDefault(s => s.ServerId == serverId && s.RemoteItemId == remoteItemId);
            if (existing != null)
            {
                existing.Priority = serverPriority;
                return;
            }

            Sources.Add(new FederatedSource
            {
                ServerId = serverId,
                RemoteItemId = remoteItemId,
                Priority = serverPriority
            });

            ReSortSources();
        }

        /// <summary>
        /// Updates the metadata snapshot from a remote item, prioritizing the primary source.
        /// </summary>
        public void UpdateFromRemote(BaseItemDto remoteItem, string serverId, Guid remoteItemId, int serverPriority)
        {
            var isPrimary = PrimarySource == null || Sources.Count == 1 ||
                            Sources[PrimarySourceIndex].ServerId == serverId && Sources[PrimarySourceIndex].RemoteItemId == remoteItemId;

            if (!isPrimary && !string.IsNullOrEmpty(Metadata.Name))
            {
                return;
            }

            Metadata.Name = remoteItem.Name ?? Metadata.Name;
            Metadata.Overview = remoteItem.Overview ?? Metadata.Overview;
            Metadata.ProductionYear = remoteItem.ProductionYear ?? Metadata.ProductionYear;
            Metadata.PremiereDate = remoteItem.PremiereDate ?? Metadata.PremiereDate;
            Metadata.CommunityRating = remoteItem.CommunityRating ?? Metadata.CommunityRating;
            Metadata.OfficialRating = remoteItem.OfficialRating ?? Metadata.OfficialRating;
            Metadata.RunTimeTicks = remoteItem.RunTimeTicks ?? Metadata.RunTimeTicks;
            Metadata.SeriesName = remoteItem.SeriesName ?? Metadata.SeriesName;
            Metadata.IndexNumber = remoteItem.IndexNumber ?? Metadata.IndexNumber;
            Metadata.ParentIndexNumber = remoteItem.ParentIndexNumber ?? Metadata.ParentIndexNumber;
            Metadata.Album = remoteItem.Album ?? Metadata.Album;
            Metadata.AlbumArtist = remoteItem.AlbumArtist ?? Metadata.AlbumArtist;
            Metadata.Genres = remoteItem.Genres ?? Metadata.Genres;
            Metadata.Tags = remoteItem.Tags ?? Metadata.Tags;
            Metadata.Studios = remoteItem.Studios?.Select(s => s.Name ?? string.Empty).ToArray() ?? Metadata.Studios;
            Metadata.Artists = remoteItem.Artists != null ? remoteItem.Artists.ToArray() : Metadata.Artists;
            Metadata.ProviderIds = remoteItem.ProviderIds ?? Metadata.ProviderIds;
        }

        private void ReSortSources()
        {
            Sources = Sources.OrderBy(s => s.Priority).ThenBy(s => s.ServerId).ToList();
            PrimarySourceIndex = 0;
        }
    }

    /// <summary>
    /// One remote source for a federated item.
    /// </summary>
    public class FederatedSource
    {
        public string ServerId { get; set; } = string.Empty;
        public Guid RemoteItemId { get; set; }
        public int Priority { get; set; }
    }

    /// <summary>
    /// Serializable metadata snapshot for a federated item.
    /// </summary>
    public class FederatedItemMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string? Overview { get; set; }
        public int? ProductionYear { get; set; }
        public DateTime? PremiereDate { get; set; }
        public float? CommunityRating { get; set; }
        public string? OfficialRating { get; set; }
        public long? RunTimeTicks { get; set; }
        public string? SeriesName { get; set; }
        public int? IndexNumber { get; set; }
        public int? ParentIndexNumber { get; set; }
        public string? Album { get; set; }
        public string? AlbumArtist { get; set; }
        public string[]? Genres { get; set; }
        public string[]? Tags { get; set; }
        public string[]? Studios { get; set; }
        public string[]? Artists { get; set; }
        public Dictionary<string, string>? ProviderIds { get; set; }
        public List<FederatedPerson>? People { get; set; }
    }

    /// <summary>
    /// Serializable person record.
    /// </summary>
    public class FederatedPerson
    {
        public string Name { get; set; } = string.Empty;
        public string? Role { get; set; }
        public string? Type { get; set; }
    }
}
