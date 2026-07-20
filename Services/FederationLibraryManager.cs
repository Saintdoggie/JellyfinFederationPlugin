using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Coordinates federation resolution: looks up items in the cache, builds virtual
    /// <see cref="BaseItem"/> shells, and exposes remote server clients via the shared
    /// <see cref="IRemoteServerClientFactory"/>.
    /// </summary>
    public class FederationLibraryManager
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<FederationLibraryManager> _logger;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly FederationItemCache _cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationLibraryManager"/> class.
        /// </summary>
        public FederationLibraryManager(
            ILibraryManager libraryManager,
            ILogger<FederationLibraryManager> logger,
            IRemoteServerClientFactory clientFactory,
            FederationItemCache cache)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _clientFactory = clientFactory;
            _cache = cache;
        }

        /// <summary>
        /// Gets the item cache.
        /// </summary>
        public FederationItemCache Cache => _cache;

        /// <summary>
        /// Gets the client factory.
        /// </summary>
        public IRemoteServerClientFactory ClientFactory => _clientFactory;

        /// <summary>
        /// Initializes the manager (loads cache if not already loaded).
        /// </summary>
        public void Initialize(string cacheFilePath)
        {
            _logger.LogInformation("[Federation] Initializing Federation Library Manager");
            _cache.Initialize(cacheFilePath);
        }

        /// <summary>
        /// Resolves a federation:// path to a virtual <see cref="BaseItem"/>, looking up the
        /// cache live. Returns null when the path is not a federation path or no cache entry
        /// exists yet.
        /// </summary>
        public BaseItem? ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var entry = _cache.GetEntry(path);
            if (entry == null)
            {
                return null;
            }

            return MaterializeItem(entry);
        }

        /// <summary>
        /// Materializes a cache entry into a Jellyfin <see cref="BaseItem"/> shell.
        /// </summary>
        public BaseItem MaterializeItem(FederatedCacheEntry entry)
        {
            var item = CreateItemShell(entry.ItemType);
            item.Name = entry.Metadata.Name ?? "Unknown";
            item.Path = entry.FederationPath;
            item.Overview = entry.Metadata.Overview;
            item.ProductionYear = entry.Metadata.ProductionYear;
            item.PremiereDate = entry.Metadata.PremiereDate;
            item.CommunityRating = entry.Metadata.CommunityRating;
            item.OfficialRating = entry.Metadata.OfficialRating;
            item.RunTimeTicks = entry.Metadata.RunTimeTicks;
            item.Studios = entry.Metadata.Studios ?? Array.Empty<string>();
            item.Genres = entry.Metadata.Genres ?? Array.Empty<string>();
            item.Tags = entry.Metadata.Tags ?? Array.Empty<string>();

            if (item is Episode ep)
            {
                ep.SeriesName = entry.Metadata.SeriesName;
                ep.IndexNumber = entry.Metadata.IndexNumber;
                ep.ParentIndexNumber = entry.Metadata.ParentIndexNumber;
            }

            if (item is Audio audio)
            {
                audio.Album = entry.Metadata.Album;
                audio.AlbumArtists = entry.Metadata.AlbumArtist != null ? new[] { entry.Metadata.AlbumArtist } : Array.Empty<string>();
                audio.Artists = entry.Metadata.Artists ?? Array.Empty<string>();
                audio.IndexNumber = entry.Metadata.IndexNumber;
            }

            // Provider IDs - record all dedup provider ids on the local shell so Jellyfin
            // can match against them.
            item.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry.Metadata.ProviderIds != null)
            {
                foreach (var kvp in entry.Metadata.ProviderIds)
                {
                    item.ProviderIds[kvp.Key] = kvp.Value;
                }
            }

            // Federation tracking ids
            var primary = entry.GetPrimarySource();
            if (primary != null)
            {
                item.ProviderIds["FederationSource"] = primary.ServerId;
                item.ProviderIds["FederationRemoteId"] = primary.RemoteItemId.ToString();
            }

            // Stable local id derived from cache key so the same virtual item survives refreshes.
            item.Id = _libraryManager.GetNewItemId(entry.FederationPath, item.GetType());
            item.DateCreated = entry.LastRefreshedUtc == default ? DateTime.UtcNow : entry.LastRefreshedUtc;
            item.DateModified = item.DateCreated;
            item.IsVirtualItem = true;

            return item;
        }

        /// <summary>
        /// Gets a remote server client for the given server ID.
        /// </summary>
        public RemoteServerClient? GetClient(string serverId) => _clientFactory.GetClient(serverId);

        /// <summary>
        /// Gets all cache entries for a mapping.
        /// </summary>
        public IEnumerable<FederatedCacheEntry> GetEntriesForMapping(string mappingName)
            => _cache.GetEntriesForMapping(mappingName);

        /// <summary>
        /// Gets all cache entries.
        /// </summary>
        public IEnumerable<FederatedCacheEntry> GetAllEntries() => _cache.GetAllEntries();

        /// <summary>
        /// Returns the remote server configuration for an ID, or null.
        /// </summary>
        public RemoteServer? GetServer(string serverId)
        {
            return Plugin.Instance?.Configuration?.RemoteServers?.Find(s => s.Id == serverId);
        }

        /// <summary>
        /// Gets the configured local server URL (auto-detected or overridden).
        /// </summary>
        public string GetLocalServerUrl()
        {
            var config = Plugin.Instance?.Configuration;
            if (!string.IsNullOrEmpty(config?.ServerUrl))
            {
                return config.ServerUrl.TrimEnd('/');
            }

            return string.Empty;
        }

        /// <summary>
        /// Checks if an item is federated.
        /// </summary>
        public bool IsFederatedItem(BaseItem? item)
        {
            return item?.Path != null && item.Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses a federation path into components (delegates to <see cref="FederationItemCache.TryParsePath"/>).
        /// </summary>
        public static bool TryParseFederationPath(
            string federationPath,
            out string mappingName,
            out string? providerName,
            out string? providerId,
            out string? rawServerId,
            out Guid? rawRemoteItemId)
            => FederationItemCache.TryParsePath(federationPath, out mappingName, out providerName, out providerId, out rawServerId, out rawRemoteItemId);

        private static BaseItem CreateItemShell(string itemType)
        {
            return itemType switch
            {
                "Movie" => new Movie(),
                "Series" => new Series(),
                "Season" => new Season(),
                "Episode" => new Episode(),
                "Audio" => new Audio(),
                "MusicAlbum" => new MusicAlbum(),
                "MusicVideo" => new MusicVideo(),
                "Video" => new Video(),
                "Photo" => new Photo(),
                "PhotoAlbum" => new PhotoAlbum(),
                "Book" => new Book(),
                "BoxSet" => new BoxSet(),
                _ => new Movie()
            };
        }
    }
}
