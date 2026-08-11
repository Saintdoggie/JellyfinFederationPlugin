using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
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

            // The remote stream URL, set as this item's own Path. This is what makes
            // federated media actually playable, and it is why an earlier attempt at a
            // synthetic "federation://" path was wrong rather than the idea of a path
            // being wrong:
            //
            // Jellyfin builds every item's *static* media source in
            // BaseItem.GetVersionInfo from item.Path. With Path null it produces a
            // source with Type = MediaSourceType.Placeholder and no path, container or
            // streams - literally Jellyfin's marker for "there is no media here". Worse,
            // MediaSourceManager.GetPlaybackMediaSources guards its
            // EnableRemoteContentProbe branch on `mediaSources[0].Type != Placeholder`,
            // so a placeholder also *suppresses* the probe that would have discovered
            // the codecs. Clients then get an unplayable source and report
            // "Unable to find a valid media source to play" - which is precisely the
            // reported symptom. A "federation://" path failed differently: Jellyfin
            // parsed it as a local file path that does not exist.
            //
            // An http(s) URL is a protocol Jellyfin natively understands
            // (MediaProtocol.Http), so the static source comes out as a real, probeable
            // Http source. IsShortcut/ShortcutPath below is the same mechanism .strm
            // files use, and LocationType resolves to Remote from the URL alone.
            var (streamUrl, isDirectMode) = ResolvePlaybackUrl(entry);
            if (streamUrl != null)
            {
                item.Path = streamUrl;

                // Only for Direct mode, where the URL really is on another host: this is
                // what makes GetVersionInfo stamp IsRemote on the media source. In Proxy
                // mode the URL points back at this very server, so claiming IsRemote
                // would make clients lacking the "remote video" capability refuse a
                // stream this server is perfectly able to serve.
                if (isDirectMode)
                {
                    item.IsShortcut = true;
                    item.ShortcutPath = streamUrl;
                }
            }

            // Lets Jellyfin certify direct play without waiting on a probe. When the
            // remote did not report one, the container is discovered by the
            // EnableRemoteContentProbe pass described above instead.
            if (!string.IsNullOrEmpty(entry.Metadata.Container))
            {
                item.Container = entry.Metadata.Container;
            }

            item.Overview = entry.Metadata.Overview;
            item.ProductionYear = entry.Metadata.ProductionYear;
            item.PremiereDate = entry.Metadata.PremiereDate;
            item.CommunityRating = entry.Metadata.CommunityRating;
            item.OfficialRating = entry.Metadata.OfficialRating;
            item.RunTimeTicks = entry.Metadata.RunTimeTicks;
            item.Studios = entry.Metadata.Studios ?? Array.Empty<string>();
            item.Genres = entry.Metadata.Genres ?? Array.Empty<string>();

            // Source-server tag: Jellyfin renders Tags as small labeled chips on the
            // item detail page, which is the closest thing to a "which server is this
            // from" badge available without a jellyfin-web client-side plugin. Kept as
            // its own tag (not folded into remote Tags) so it survives even if the
            // remote item has no tags of its own, and reads the same on every client.
            // Cosmetic only, so a config-access failure here must not break
            // materialization of the item itself.
            string? sourceServerName = null;
            try
            {
                sourceServerName = entry.GetPrimarySource() is { } primarySource
                    ? GetServer(primarySource.ServerId)?.Name
                    : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not resolve source server name for tag on {Key}", entry.Key);
            }

            item.Tags = AppendServerTag(entry.Metadata.Tags, sourceServerName);

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
        /// Resolves the stream URL for an entry's primary source, or null when the
        /// entry isn't streamable media (a Series/Season folder), has no source, or
        /// the URL can't be built. Never throws - a failure here must degrade to a
        /// pathless item rather than break materialization entirely, since
        /// <see cref="FederationMediaSourceProvider"/> can still supply a source at
        /// playback time (it has an HTTP request context to resolve a proxy URL from,
        /// which a background sync does not).
        /// </summary>
        private (string? Url, bool IsDirectMode) ResolvePlaybackUrl(FederatedCacheEntry entry)
        {
            if (!IsStreamableType(entry.ItemType))
            {
                return (null, false);
            }

            try
            {
                var primary = entry.GetPrimarySource();
                if (primary == null)
                {
                    return (null, false);
                }

                var url = BuildPlaybackUrl(entry.ItemType, primary);
                var isDirect = GetServer(primary.ServerId)?.StreamingMode == StreamingMode.Direct;
                return (url, url != null && isDirect);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not build a playback URL for {Key}; item will rely on the media source provider instead", entry.Key);
                return (null, false);
            }
        }

        /// <summary>
        /// Builds the URL a federated item's media actually streams from, or null
        /// when it can't be built (server gone/disabled, or Proxy mode with no
        /// configured public URL - sync runs on a background task with no incoming
        /// HTTP request to infer one from).
        /// </summary>
        /// <param name="itemType">Cache entry item type, e.g. "Movie" or "Audio".</param>
        /// <param name="src">The remote source to stream from.</param>
        public string? BuildPlaybackUrl(string itemType, FederatedSource src)
        {
            var server = GetServer(src.ServerId);
            if (server == null || !server.Enabled)
            {
                return null;
            }

            if (server.StreamingMode == StreamingMode.Proxy)
            {
                var localUrl = GetLocalServerUrl();
                if (string.IsNullOrEmpty(localUrl))
                {
                    return null;
                }

                // The remote api_key stays server-side; clients only ever see this server.
                var audioFlag = IsAudioType(itemType) ? "&audio=true" : string.Empty;
                return $"{localUrl}/Plugins/Federation/Stream?serverId={Uri.EscapeDataString(src.ServerId)}&itemId={src.RemoteItemId:N}{audioFlag}";
            }

            // Audio streams from a different endpoint than video; asking /Videos for a
            // song does not reliably work.
            var endpoint = IsAudioType(itemType) ? "Audio" : "Videos";
            return $"{server.Url.TrimEnd('/')}/{endpoint}/{src.RemoteItemId:N}/stream"
                + $"?api_key={Uri.EscapeDataString(server.ApiKey)}&Static=true";
        }

        /// <summary>
        /// Item types whose media is streamed directly. Container types (Series,
        /// Season, BoxSet, PhotoAlbum) are folders and must never get a stream path.
        /// Photo/Book are not streamed through the media pipeline either.
        /// </summary>
        public static bool IsStreamableType(string itemType)
        {
            return itemType is "Movie" or "Episode" or "Video" or "MusicVideo" or "Audio";
        }

        private static bool IsAudioType(string itemType) => itemType is "Audio";

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
        /// Appends a "🌐 ServerName" tag identifying the source server, replacing any
        /// previous server tag of the same shape so re-materializing after a primary
        /// source change doesn't leave stale server tags behind.
        /// </summary>
        public static string[] AppendServerTag(string[]? tags, string? serverName)
        {
            var kept = (tags ?? Array.Empty<string>()).Where(t => !t.StartsWith(ServerTagPrefix, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(serverName))
            {
                return kept.ToArray();
            }

            return kept.Append(ServerTagPrefix + serverName).ToArray();
        }

        private const string ServerTagPrefix = "🌐 ";

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
                "Movie" => typeof(FederatedMovie),
                "Series" => typeof(FederatedSeries),
                "Season" => typeof(FederatedSeason),
                "Episode" => typeof(FederatedEpisode),
                "Audio" => typeof(FederatedAudio),
                "MusicAlbum" => typeof(FederatedMusicAlbum),
                "MusicVideo" => typeof(FederatedMusicVideo),
                "Video" => typeof(FederatedVideo),
                "Photo" => typeof(FederatedPhoto),
                "PhotoAlbum" => typeof(FederatedPhotoAlbum),
                "Book" => typeof(FederatedBook),
                "BoxSet" => typeof(FederatedBoxSet),
                _ => typeof(FederatedMovie)
            };
        }
    }
}
