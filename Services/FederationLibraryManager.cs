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
        /// Materializes a cache entry into a Jellyfin <see cref="BaseItem"/> shell.
        /// </summary>
        public BaseItem MaterializeItem(FederatedCacheEntry entry)
        {
            var item = CreateItemShell(entry.ItemType);
            item.Name = entry.Metadata.Name ?? "Unknown";

            // Deliberately left null: Jellyfin only treats an item as truly
            // "Virtual" (always available, never checked against disk) when Path
            // is empty. A synthetic federation:// URI made Jellyfin compute these
            // as missing/offline instead, which hid them from browsing and from
            // this plugin's own "does this already exist" checks. FederationKey
            // (below) is the identity used everywhere instead of Path.
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

                // ParentId (set by the caller) is the raw hierarchy link; clients
                // separately use SeriesId/SeasonId for navigation (season grouping,
                // "up next"), so both need to point at the same local deterministic ids.
                var seasonEntry = entry.ParentKey != null ? _cache.GetEntryByKey(entry.ParentKey) : null;
                if (seasonEntry != null)
                {
                    ep.SeasonId = ComputeItemId(seasonEntry);
                    var seriesEntry = seasonEntry.ParentKey != null ? _cache.GetEntryByKey(seasonEntry.ParentKey) : null;
                    if (seriesEntry != null)
                    {
                        var seriesId = ComputeItemId(seriesEntry);
                        ep.SeriesId = seriesId;

                        // The actual mechanism Jellyfin's Shows/{id}/Seasons and
                        // Shows/{id}/Episodes endpoints use to find children: Series.
                        // GetSeasons/GetEpisodes filter by SeriesPresentationUniqueKey
                        // matching the series' own GetPresentationUniqueKey() - a
                        // string field, entirely separate from ParentId/AncestorIds/
                        // SeriesId. It's normally computed and stored by Jellyfin's own
                        // library-scan pipeline; CreateItems doesn't touch it, so it
                        // was silently null on every federated episode and season,
                        // which is why they were undiscoverable from the show/season
                        // pages even after 0.0.13's ancestry-ordering fix. The series'
                        // own default (see BaseItem.CreatePresentationUniqueKey) is
                        // just its id in "N" format, matched below for the series item
                        // itself.
                        ep.SeriesPresentationUniqueKey = seriesId.ToString("N");
                    }
                }
            }

            if (item is Season season)
            {
                // Jellyfin's SeriesMetadataService backfills any season a series'
                // episodes reference but that doesn't exist yet, and it matches
                // purely on this field:
                //   seasons.FirstOrDefault(i => i.IndexNumber == seasonNumber)
                // Leaving it null made every federated season invisible to that
                // check, so Jellyfin created a second, empty season next to each
                // real one. Those duplicates render the series' own poster because
                // they have no images of their own.
                season.IndexNumber = entry.Metadata.IndexNumber;
                season.SeriesName = entry.Metadata.SeriesName;

                var seriesEntry = entry.ParentKey != null ? _cache.GetEntryByKey(entry.ParentKey) : null;
                if (seriesEntry != null)
                {
                    var seriesId = ComputeItemId(seriesEntry);
                    season.SeriesId = seriesId;
                    season.SeriesPresentationUniqueKey = seriesId.ToString("N");
                }
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

            // Federation identity: FederationKey is used everywhere instead of Path
            // (see IsFederatedItem, FederationMediaSourceProvider, image/metadata
            // providers, and FederationItemPersistenceService's dedup check).
            item.ProviderIds["FederationKey"] = entry.Key;

            var primary = entry.GetPrimarySource();
            if (primary != null)
            {
                item.ProviderIds["FederationSource"] = primary.ServerId;
                item.ProviderIds["FederationRemoteId"] = primary.RemoteItemId.ToString();
            }

            // Stable local id derived from cache key so the same virtual item survives refreshes.
            item.Id = _libraryManager.GetNewItemId(entry.FederationPath, item.GetType());

            // Explicit rather than relying on BaseItem's lazy default (Id.ToString("N"))
            // - Series.CreatePresentationUniqueKey() can diverge from that default when
            // a library has "EnableAutomaticSeriesGrouping" on. Pinning it here keeps it
            // exactly equal to what Episode/Season.SeriesPresentationUniqueKey above are
            // computed against, regardless of that setting.
            item.PresentationUniqueKey = item.Id.ToString("N");

            item.DateCreated = entry.LastRefreshedUtc == default ? DateTime.UtcNow : entry.LastRefreshedUtc;
            item.DateModified = item.DateCreated;

            // Deliberately false. Jellyfin reads IsVirtualItem as "missing episode",
            // not "has no local file": Series.GetEpisodes and SetSeasonQueryOptions
            // both set query.IsMissing = false unless the user turns on
            // DisplayMissingEpisodes, and BaseItemRepository collapses that to a flat
            //   .Where(e => e.IsVirtualItem == isVirtualItem.Value)
            // so every federated season and episode was filtered out of the
            // Shows/{id}/Seasons and Shows/{id}/Episodes endpoints the show page
            // depends on. Federated items are remote, not missing.
            //
            // This does not expose them to library-scan deletion: Folder.
            // ValidateChildren only removes children where item.IsFileProtocol is
            // true, and that is driven by Path (still null here), not by this flag.
            item.IsVirtualItem = false;

            return item;
        }

        /// <summary>
        /// Computes the deterministic local item id for a cache entry without fully
        /// materializing it. Used to resolve parent ids for nested items (Season
        /// under Series, Episode under Season) - since ids are pure deterministic
        /// hashes of the entry's federation path and CLR type, a child's parent id
        /// is computable even before the parent itself has been persisted.
        /// </summary>
        public Guid ComputeItemId(FederatedCacheEntry entry)
        {
            return _libraryManager.GetNewItemId(entry.FederationPath, GetClrType(entry.ItemType));
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
        public bool IsFederatedItem(BaseItem? item) => GetFederationKey(item) != null;

        /// <summary>
        /// Gets the <c>FederationKey</c> provider id stamped on a materialized federation
        /// item (see <see cref="MaterializeItem"/>), or null if the item isn't federated.
        /// </summary>
        public static string? GetFederationKey(BaseItem? item)
        {
            if (item?.ProviderIds != null && item.ProviderIds.TryGetValue("FederationKey", out var key) && !string.IsNullOrEmpty(key))
            {
                return key;
            }

            return null;
        }

        private static BaseItem CreateItemShell(string itemType)
        {
            return (BaseItem)Activator.CreateInstance(GetClrType(itemType))!;
        }

        private static Type GetClrType(string itemType)
        {
            return itemType switch
            {
                "Movie" => typeof(Movie),
                "Series" => typeof(Series),
                "Season" => typeof(Season),
                "Episode" => typeof(Episode),
                "Audio" => typeof(Audio),
                "MusicAlbum" => typeof(MusicAlbum),
                "MusicVideo" => typeof(MusicVideo),
                "Video" => typeof(Video),
                "Photo" => typeof(Photo),
                "PhotoAlbum" => typeof(PhotoAlbum),
                "Book" => typeof(Book),
                "BoxSet" => typeof(BoxSet),
                _ => typeof(Movie)
            };
        }
    }
}
