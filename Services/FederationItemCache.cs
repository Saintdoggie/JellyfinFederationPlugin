using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// In-memory + persisted cache of resolved federated items.
    /// </summary>
    public class FederationItemCache
    {
        private readonly ILogger<FederationItemCache> _logger;
        private readonly ConcurrentDictionary<string, FederatedCacheEntry> _entries = new();
        private readonly ConcurrentDictionary<(string ServerId, Guid RemoteItemId), string> _remoteIndex = new();
        private string _cacheFilePath = string.Empty;
        private DateTime _lastRefreshUtc = DateTime.MinValue;

        public FederationItemCache(ILogger<FederationItemCache> logger)
        {
            _logger = logger;
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
        /// Gets the cache entry directly by its key (mapping/provider:id or
        /// mapping/raw/server/remoteId, without the <c>federation://</c> prefix).
        /// Used to look entries back up from a <c>FederationKey</c> provider id
        /// stamped on a materialized <see cref="MediaBrowser.Controller.Entities.BaseItem"/>.
        /// </summary>
        public FederatedCacheEntry? GetEntryByKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            _entries.TryGetValue(key, out var entry);
            return entry;
        }

        /// <summary>
        /// Looks up the local cache key of the entry a remote item was synced into,
        /// by the same (server, remote item id) pair it was upserted under. Used to
        /// link an Episode/Season back to its Series entry: an episode only carries
        /// its remote SeriesId/SeasonId (ids on the *remote* server), which can't be
        /// turned into a local cache key without this index, since the parent entry
        /// may be keyed by provider id (dedup) or by raw server+id.
        /// </summary>
        public string? TryGetLocalKeyForRemoteItem(string serverId, Guid remoteItemId)
        {
            _remoteIndex.TryGetValue((serverId, remoteItemId), out var key);
            return key;
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
            string itemType,
            string? parentKey = null)
        {
            var key = BuildProviderKey(mappingName, providerName, providerId);
            var entry = _entries.AddOrUpdate(
                key,
                _ => CreateEntry(key, mappingName, itemType, remoteItem, serverId, remoteItemId, serverPriority),
                (_, existing) =>
                {
                    existing.AddSource(serverId, remoteItemId, serverPriority);
                    existing.UpdateFromRemote(remoteItem, serverId, remoteItemId, serverPriority);
                    return existing;
                });

            entry.ParentKey = parentKey;
            _remoteIndex[(serverId, remoteItemId)] = key;
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
            string itemType,
            string? parentKey = null)
        {
            var key = BuildRawKey(mappingName, serverId, remoteItemId);
            var entry = _entries.AddOrUpdate(
                key,
                _ => CreateEntry(key, mappingName, itemType, remoteItem, serverId, remoteItemId, serverPriority),
                (_, existing) =>
                {
                    existing.AddSource(serverId, remoteItemId, serverPriority);
                    existing.UpdateFromRemote(remoteItem, serverId, remoteItemId, serverPriority);
                    return existing;
                });

            entry.ParentKey = parentKey;
            _remoteIndex[(serverId, remoteItemId)] = key;
            _lastRefreshUtc = DateTime.UtcNow;
            return entry;
        }

        /// <summary>
        /// Removes sources belonging to the given server within a mapping when the
        /// remote item id was not seen during the latest successful sync of that
        /// server. Entries left without any source are removed. Returns the number
        /// of entries removed.
        /// </summary>
        public int PruneServerSources(string mappingName, string serverId, IReadOnlyCollection<Guid> seenRemoteItemIds)
        {
            var removed = 0;
            foreach (var kvp in _entries)
            {
                var entry = kvp.Value;
                if (!entry.MappingName.Equals(mappingName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsRawKey(entry.Key))
                {
                    // Raw entries are keyed by server + remote item id.
                    if (TryParsePath("federation://" + entry.Key, out _, out _, out _, out var rawServerId, out var rawRemoteItemId)
                        && rawServerId == serverId
                        && rawRemoteItemId.HasValue)
                    {
                        // Gone from the remote entirely.
                        var vanished = !seenRemoteItemIds.Contains(rawRemoteItemId.Value);

                        // Or still present, but now held under a provider-id key
                        // instead. An item first seen without provider ids lands under
                        // a raw key; once the remote reports its ids (a metadata
                        // refresh there, or dedup being switched on here) the very same
                        // item is upserted under "Movies/imdb:tt…" and the raw entry is
                        // orphaned. It could never be pruned before, because its remote
                        // id *is* still seen every sync - so the library ended up
                        // showing every affected title twice, once per key. The remote
                        // index records which key currently owns a given remote item,
                        // so anything else claiming it is by definition the stale copy.
                        var superseded = !vanished
                            && _remoteIndex.TryGetValue((serverId, rawRemoteItemId.Value), out var owningKey)
                            && !string.Equals(owningKey, entry.Key, StringComparison.OrdinalIgnoreCase);

                        if ((vanished || superseded) && _entries.TryRemove(kvp.Key, out _))
                        {
                            removed++;
                        }
                    }

                    continue;
                }

                entry.RemoveSourcesNotIn(serverId, seenRemoteItemIds);

                // A source can also be stale the other way round: the remote item
                // is still seen, but a *different* key now owns it in the remote
                // index (e.g. dedup was turned off, or the provider id this entry
                // was keyed on disappeared from the remote's metadata, so the item
                // re-lands under a raw key or a different provider key on the next
                // sync). RemoveSourcesNotIn can't catch that - the remote id is
                // still "seen" - so without this check the old provider-keyed entry
                // never lets go of its source and the title stays duplicated
                // forever, which is exactly what production was showing.
                var supersededIds = new HashSet<Guid>();
                foreach (var source in entry.GetSourcesSnapshot())
                {
                    if (source.ServerId == serverId
                        && _remoteIndex.TryGetValue((serverId, source.RemoteItemId), out var owningKey)
                        && !string.Equals(owningKey, entry.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        supersededIds.Add(source.RemoteItemId);
                    }
                }

                if (supersededIds.Count > 0)
                {
                    entry.RemoveSources(serverId, supersededIds);
                }

                if (entry.GetSourcesSnapshot().Length == 0 && _entries.TryRemove(kvp.Key, out _))
                {
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes all entries belonging to a mapping.
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
        /// Persists the cache to disk atomically (temp file + move).
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
                    Entries = _entries.Values.Select(e => e.Snapshot()).ToList()
                };

                var json = JsonSerializer.Serialize(payload, CacheJsonOptions);
                var tempPath = _cacheFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _cacheFilePath, true);
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

        private static bool IsRawKey(string key)
            => key.Contains("/raw/", StringComparison.OrdinalIgnoreCase);

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
    /// Mutations are guarded by an internal lock; readers should use the snapshot accessors.
    /// </summary>
    public class FederatedCacheEntry
    {
        private readonly object _sync = new();

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
        /// The list of remote sources that provide this item. Do not enumerate directly
        /// from consumers; use <see cref="GetSourcesSnapshot"/> for thread-safe reads.
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
        public string FederationPath => "federation://" + Key;

        /// <summary>
        /// Cache key of the entry this one nests under (Season under Series, Episode
        /// under Season). Null for top-level entries (Movie, Series).
        /// </summary>
        public string? ParentKey { get; set; }

        /// <summary>
        /// Gets a thread-safe snapshot of the sources list.
        /// </summary>
        public FederatedSource[] GetSourcesSnapshot()
        {
            lock (_sync)
            {
                return Sources.ToArray();
            }
        }

        /// <summary>
        /// Gets the primary source, or null when the entry has no sources.
        /// </summary>
        public FederatedSource? GetPrimarySource()
        {
            lock (_sync)
            {
                return Sources.Count > 0 ? Sources[Math.Min(PrimarySourceIndex, Sources.Count - 1)] : null;
            }
        }

        /// <summary>
        /// Adds or updates a remote source.
        /// </summary>
        public void AddSource(string serverId, Guid remoteItemId, int serverPriority)
        {
            lock (_sync)
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
        }

        /// <summary>
        /// Removes sources for the given server whose remote item id is not in
        /// <paramref name="keepRemoteItemIds"/>. Returns the number removed.
        /// </summary>
        public int RemoveSourcesNotIn(string serverId, IReadOnlyCollection<Guid> keepRemoteItemIds)
        {
            lock (_sync)
            {
                var before = Sources.Count;
                Sources = Sources
                    .Where(s => !(s.ServerId == serverId && !keepRemoteItemIds.Contains(s.RemoteItemId)))
                    .ToList();
                if (PrimarySourceIndex >= Sources.Count)
                {
                    PrimarySourceIndex = 0;
                }

                return before - Sources.Count;
            }
        }

        /// <summary>
        /// Removes sources for the given server whose remote item id is in
        /// <paramref name="remoteItemIds"/>. Returns the number removed.
        /// </summary>
        public int RemoveSources(string serverId, ICollection<Guid> remoteItemIds)
        {
            lock (_sync)
            {
                var before = Sources.Count;
                Sources = Sources
                    .Where(s => !(s.ServerId == serverId && remoteItemIds.Contains(s.RemoteItemId)))
                    .ToList();
                if (PrimarySourceIndex >= Sources.Count)
                {
                    PrimarySourceIndex = 0;
                }

                return before - Sources.Count;
            }
        }

        /// <summary>
        /// Updates the metadata snapshot from a remote item, prioritizing the primary source.
        /// </summary>
        public void UpdateFromRemote(BaseItemDto remoteItem, string serverId, Guid remoteItemId, int serverPriority)
        {
            lock (_sync)
            {
                var isPrimary = Sources.Count <= 1
                    || (Sources.Count > PrimarySourceIndex
                        && Sources[PrimarySourceIndex].ServerId == serverId
                        && Sources[PrimarySourceIndex].RemoteItemId == remoteItemId);

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
                Metadata.Container = remoteItem.Container ?? Metadata.Container;
                Metadata.MediaStreams = remoteItem.MediaStreams ?? Metadata.MediaStreams;
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
                Metadata.People = remoteItem.People != null
                    ? remoteItem.People.Select(p => new FederatedPerson
                    {
                        Name = p.Name ?? string.Empty,
                        Role = p.Role,
                        Type = p.Type.ToString()
                    }).ToList()
                    : Metadata.People;
                LastRefreshedUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Creates a detached copy safe for serialization while the entry is mutated.
        /// </summary>
        public FederatedCacheEntry Snapshot()
        {
            lock (_sync)
            {
                return new FederatedCacheEntry
                {
                    Key = Key,
                    MappingName = MappingName,
                    ItemType = ItemType,
                    Sources = Sources.ToList(),
                    PrimarySourceIndex = PrimarySourceIndex,
                    Metadata = Metadata,
                    LastRefreshedUtc = LastRefreshedUtc,
                    ParentKey = ParentKey
                };
            }
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

        /// <summary>
        /// Gets or sets the media container reported by the remote (e.g. "mkv").
        /// Stamped onto the materialized item so Jellyfin can certify direct play
        /// without first probing the remote URL.
        /// </summary>
        public string? Container { get; set; }

        /// <summary>
        /// Gets or sets the remote's real per-stream codec/resolution/audio/subtitle
        /// data (video/audio/subtitle tracks), stamped onto the materialized item so
        /// Jellyfin's own client-compatibility check can certify direct play without
        /// falling back to a live probe over the WAN link on every play - see
        /// FederationLibraryManager.MaterializeItem.
        /// </summary>
        public MediaStream[]? MediaStreams { get; set; }

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

        /// <summary>
        /// Gets or sets the source server's own native id for this item, when it
        /// isn't a Guid the way a Jellyfin item id is - specifically a Plex
        /// <c>ratingKey</c>. Needed because a federated item's
        /// <see cref="FederatedSource.RemoteItemId"/> is a Guid, and the Guid a
        /// Plex item gets is *derived* from its ratingKey (see
        /// <c>PlexApiClient.RatingKeyToGuid</c>) and therefore can't be reversed
        /// back into one. Kept here so the stream path can ask Plex for the
        /// item's current file location at play time rather than caching a part
        /// id that a library re-scan on their end would silently invalidate.
        /// Null for Jellyfin-sourced items, which need no such mapping.
        /// </summary>
        public string? RemoteNativeId { get; set; }
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
