using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
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
        private readonly WanBandwidthMonitor _bandwidthMonitor;
        private readonly IMediaStreamRepository _mediaStreamRepository;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationLibraryManager"/> class.
        /// </summary>
        public FederationLibraryManager(
            ILibraryManager libraryManager,
            ILogger<FederationLibraryManager> logger,
            IRemoteServerClientFactory clientFactory,
            FederationItemCache cache,
            WanBandwidthMonitor bandwidthMonitor,
            IMediaStreamRepository mediaStreamRepository)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _clientFactory = clientFactory;
            _cache = cache;
            _bandwidthMonitor = bandwidthMonitor;
            _mediaStreamRepository = mediaStreamRepository;
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
        /// Gets the WAN bandwidth monitor, so <see cref="FederationMediaSourceProvider"/>
        /// can tell whether a given server's Direct-mode stream is currently a capped
        /// transcode rather than the raw source file.
        /// </summary>
        public WanBandwidthMonitor BandwidthMonitor => _bandwidthMonitor;

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
            // Http source, and LocationType resolves to Remote from the URL alone -
            // BaseItem.GetVersionInfo derives both MediaSourceInfo.Protocol and
            // LocationType from item.Path/PathProtocol regardless of IsShortcut.
            //
            // IsShortcut/ShortcutPath (the .strm mechanism) was tried here to also get
            // MediaSourceInfo.IsRemote = true, but it does not work the way that name
            // suggests: ProbeProvider.FetchShortcutInfo unconditionally does
            // File.ReadAllLines(item.Path), expecting Path to be a real *local* .strm
            // file whose *contents* are the target URL - not the URL itself. Since our
            // Path already *is* the URL, that read throws DirectoryNotFoundException on
            // every metadata refresh, which - because the failure prevents MediaStreams
            // from ever being saved - fires on every single playback attempt, not just
            // the first. Clients see this as a stuck "loading" spinner and often retry,
            // which kills the in-flight ffmpeg transcode and restarts it from scratch.
            // The only thing IsRemote actually gates client-side is a handful of legacy
            // TV platforms (Tizen/webOS/Orsay/OperaTV/EdgeUWP) refusing to direct-play a
            // remote source; leaving it unset costs correctness only for those, in
            // exchange for playback actually working everywhere else.
            var streamUrl = ResolvePlaybackUrl(entry);
            if (streamUrl != null)
            {
                item.Path = streamUrl;
            }

            // Lets Jellyfin certify direct play without waiting on a probe. When the
            // remote did not report one, the container is discovered by the
            // EnableRemoteContentProbe pass described above instead.
            //
            // ResolvePlaybackUrl now always stamps a Path (see its comment - a null
            // Path made Jellyfin's static source a Placeholder, which hid the Play
            // button entirely), including for WAN-capped Direct-mode video, whose URL
            // is forced to mp4/h264/aac (BuildPlaybackUrl) rather than the source
            // file's real container. Stamping the raw container in that case would
            // mismatch what the URL actually serves, so it must still be checked here
            // even though streamUrl itself is no longer null for that case.
            var isWanCappedVideo = false;
            if (streamUrl != null)
            {
                var primaryForContainer = entry.GetPrimarySource();
                isWanCappedVideo = primaryForContainer != null
                    && !IsAudioType(entry.ItemType)
                    && GetServer(primaryForContainer.ServerId) is { StreamingMode: StreamingMode.Direct } capServer
                    && _bandwidthMonitor.GetEffectiveCapMbps(capServer) != null;

                if (isWanCappedVideo)
                {
                    item.Container = "mp4";
                }
                else if (!string.IsNullOrEmpty(entry.Metadata.Container))
                {
                    item.Container = entry.Metadata.Container;
                }
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

            // Not saved for the WAN-capped-Direct case above: that URL serves a
            // forced h264/aac/mp4 transcode of the source, not the raw file, so the
            // remote's real (often much richer, e.g. 4K HEVC) stream data would
            // describe bytes the URL doesn't actually serve. Falling back to
            // EnableRemoteContentProbe there still works, it just costs the probe
            // this is otherwise trying to avoid - an acceptable tradeoff for a
            // narrower, already-slower-by-design path. Must run after item.Id is
            // assigned above, since that's the key this is saved under.
            if (!isWanCappedVideo && entry.Metadata.MediaStreams is { Length: > 0 } streams)
            {
                try
                {
                    _mediaStreamRepository.SaveMediaStreams(item.Id, streams, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not save media streams for {Name}; playback will fall back to a live probe", item.Name);
                }
            }

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
        private string? ResolvePlaybackUrl(FederatedCacheEntry entry)
        {
            if (!IsStreamableType(entry.ItemType))
            {
                return null;
            }

            try
            {
                var primary = entry.GetPrimarySource();
                if (primary == null)
                {
                    return null;
                }

                // Previously left Path null here for Direct mode with WanCapMode Auto
                // or Manual, specifically to avoid freezing a URL that could go stale
                // once WanBandwidthMonitor's classification/measurement changes
                // (reconciliation never updates an existing item's Path in place).
                // That traded a minor problem for a much worse one: with Path null,
                // Jellyfin's own static media source comes back as
                // MediaSourceType.Placeholder (see the comment on item.Path above -
                // this is precisely the "no media here" case it warns about), and
                // GetMediaSources.staticSourceCoversPrimary correctly resolves to
                // false when Path is empty - so the item-detail endpoint
                // (Users/{id}/Items/{itemId}, which embeds Jellyfin core's *static*
                // source rather than calling this plugin's dynamic provider) surfaces
                // that Placeholder to clients. jellyfin-web's Details page uses
                // exactly that embedded source to decide whether to render the Play
                // button at all, regardless of the SupportsDirectPlay/DirectStream/
                // Transcoding flags on it - so every WAN-capped Direct-mode item was
                // permanently unplayable from its own detail page, confirmed live
                // (0.0.37 testing): "Placeholder"/"File" protocol/IsRemote=false on
                // the embedded source despite a fully valid, correctly-capped source
                // being available through PlaybackInfo the whole time.
                //
                // The staleness this was guarding against is already handled: when
                // WanBandwidthMonitor's decision moves on and the stored Path no
                // longer matches a freshly built URL, staticSourceCoversPrimary
                // resolves to false and FederationMediaSourceProvider.GetMediaSources
                // already logs a warning and serves a freshly built alternate source
                // instead (see the comment above staticSourceCoversPrimary). Worst
                // case a client ends up direct-playing the stale-but-still-valid
                // cached URL until the item is recreated - a wrong bitrate, not an
                // unplayable item. That is strictly better than no Play button.
                return BuildPlaybackUrl(entry.ItemType, primary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not build a playback URL for {Key}; item will rely on the media source provider instead", entry.Key);
                return null;
            }
        }

        /// <summary>
        /// Builds the URL a federated item's media actually streams from, or null
        /// when it can't be built: server gone/disabled, Proxy mode with no
        /// configured public URL (sync runs on a background task with no incoming
        /// HTTP request to infer one from) - or, always, Direct mode.
        /// <para>
        /// Direct mode used to return a URL with the remote server's real,
        /// long-lived api_key embedded directly in the query string. That URL was
        /// then stamped onto the item's own static Path (see
        /// <see cref="ResolvePlaybackUrl"/>/<see cref="MaterializeItem"/>) and handed
        /// straight to the browser client for every play - meaning any logged-in
        /// user on this server, not just its admin, could read the friend's real key
        /// out of dev tools/network tab and use it directly against the friend's
        /// server, far beyond what a single stream should have granted them. Direct
        /// mode now always returns null here: there is no working, credential-
        /// bearing URL to persist at sync time any more. The real, short-lived,
        /// single-item-scoped playback URL is instead built live, per request, by
        /// <see cref="FederationMediaSourceProvider.GetMediaSources"/> - minting a
        /// fresh <c>FederationPlaybackTokenService</c> token from the remote server
        /// for exactly the item being played, valid for a bounded window, useless
        /// for anything else. This mirrors the Proxy branch's existing "no
        /// configured public URL degrades to no path rather than a broken one"
        /// pattern - same shape, now also true for Direct, for a different reason.
        /// </para>
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
                // Loopback, not GetLocalServerUrl() (which is the public URL used
                // for peer/federation handshakes). This URL is only ever fetched
                // by the server-side transcoder: federated Proxy streams are
                // fundamentally re-transcoded on this server before HLS is sent
                // to the client, so clients never contact this URL directly.
                // Using the public URL here meant every byte of a Proxy stream
                // went out through DNS/CDN/tunnel and back to the same Jellyfin
                // process - which on a VPS-fronted setup (i.e. essentially every
                // production setup) is what turned 4K playback startup into
                // minutes-long waits. Loopback stays inside the container.
                var localUrl = GetInternalPlaybackBaseUrl();
                var audioFlag = IsAudioType(itemType) ? "&audio=true" : string.Empty;
                return $"{localUrl}/Plugins/Federation/Stream?serverId={Uri.EscapeDataString(src.ServerId)}&itemId={src.RemoteItemId:N}{audioFlag}";
            }

            // Direct mode: see the security rationale on this method's own doc
            // comment above. No URL is built (or persisted) here any more; a
            // per-request, single-item, short-lived token URL is built live instead
            // by FederationMediaSourceProvider.BuildPlaybackPathAsync.
            return null;
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

        /// <summary>
        /// Backfills real per-stream codec/resolution/audio data onto an item that
        /// already exists (created before this was tracked, or synced before the
        /// remote reported any) - the reconciliation loop's equivalent of the save in
        /// <see cref="MaterializeItem"/>, for items that path doesn't run for since
        /// they already exist. Same WAN-capped-Direct exclusion as MaterializeItem:
        /// that URL serves a forced transcode of the source, not the raw file, so the
        /// remote's real stream data would describe bytes the URL doesn't serve.
        /// Returns true when it actually saved anything.
        /// </summary>
        public bool TryPersistMediaStreams(BaseItem item, FederatedCacheEntry entry)
        {
            if (entry.Metadata.MediaStreams is not { Length: > 0 } streams)
            {
                return false;
            }

            var primary = entry.GetPrimarySource();
            var isWanCappedVideo = primary != null
                && !IsAudioType(entry.ItemType)
                && GetServer(primary.ServerId) is { StreamingMode: StreamingMode.Direct } capServer
                && _bandwidthMonitor.GetEffectiveCapMbps(capServer) != null;
            if (isWanCappedVideo)
            {
                return false;
            }

            try
            {
                _mediaStreamRepository.SaveMediaStreams(item.Id, streams, CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not backfill media streams for {Name}", item.Name);
                return false;
            }
        }

        private static bool IsAudioType(string itemType) => itemType is "Audio";

        /// <summary>
        /// Gets the public URL this server advertises to peers (federation
        /// handshakes, peer callbacks). Returns config.ServerUrl or empty when
        /// the admin has not set one - never a made-up loopback address, since
        /// a peer cannot reach that.
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
        /// Base URL the server-side transcoder uses when fetching a Proxy-mode
        /// federated stream from itself. Deliberately loopback by default and NOT
        /// the public URL from <see cref="GetLocalServerUrl"/>: on a tunnel/VPS
        /// setup (which is the common case), routing through the public host
        /// makes every byte round-trip out through DNS/CDN/tunnel and back to
        /// the same process, adding several seconds of latency and the full
        /// tunnel RTT to playback startup. Clients never see this URL - it is
        /// only consumed by the local ffmpeg process running inside the same
        /// Jellyfin instance, which reaches itself fastest over loopback.
        ///
        /// An admin can override this via Configuration.InternalServerUrl (Advanced
        /// settings on the plugin page) for the uncommon case where loopback isn't
        /// actually reachable, or the Kestrel port isn't the default 8096. Port is
        /// otherwise hardcoded to 8096 rather than detected, unlike
        /// FederationMediaSourceProvider's equivalent: this runs during background
        /// library sync, outside any HTTP request, so there is no live connection to
        /// read an actual listening port from.
        /// </summary>
        public string GetInternalPlaybackBaseUrl()
        {
            var config = Plugin.Instance?.Configuration;
            if (!string.IsNullOrEmpty(config?.InternalServerUrl))
            {
                return config.InternalServerUrl.TrimEnd('/');
            }

            return "http://127.0.0.1:8096";
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

        /// <summary>
        /// Case-insensitive provider id lookup. Dedup provider names are configured
        /// lowercase ("imdb", "tmdb", "tvdb"), but Jellyfin stamps them on both
        /// <see cref="BaseItem.ProviderIds"/> and <see cref="MediaBrowser.Model.Dto.BaseItemDto.ProviderIds"/>
        /// Pascal-cased ("Imdb", "Tmdb") and neither dictionary is guaranteed to use
        /// a case-insensitive comparer - a plain <c>TryGetValue(key, ...)</c> with the
        /// configured casing silently misses every real entry, which is why dedup
        /// (both across servers and against content the user already owns locally)
        /// never actually matched anything despite the provider ids being right there.
        /// </summary>
        public static bool TryGetProviderId(IReadOnlyDictionary<string, string>? providerIds, string key, out string value)
        {
            if (providerIds != null)
            {
                foreach (var kv in providerIds)
                {
                    if (!string.IsNullOrEmpty(kv.Value) && string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = kv.Value;
                        return true;
                    }
                }
            }

            value = string.Empty;
            return false;
        }

        private static BaseItem CreateItemShell(string itemType)
        {
            return (BaseItem)Activator.CreateInstance(GetClrType(itemType))!;
        }

        /// <summary>
        /// Maps a cache entry's item type to the Jellyfin CLR type to instantiate.
        ///
        /// These must be Jellyfin's own stock types, never plugin subclasses of them.
        /// BaseItem.GetBaseItemKind() resolves an item's kind with
        /// <c>Enum.Parse&lt;BaseItemKind&gt;(GetType().Name)</c> - it parses the CLR
        /// *class name* - so a subclass named anything not already in that enum throws
        /// ArgumentException. That call sits under DtoService.AttachBasicFields and
        /// under Folder.GetCachedChildren, so a subclass takes down every API response
        /// containing one of these items and every attempt to enumerate a folder that
        /// holds one. 0.0.22 introduced FederatedMovie/FederatedSeries/... to override
        /// LocationType, and that is exactly what happened.
        ///
        /// The override is no longer needed: materialized items carry the remote stream
        /// URL on Path, and BaseItem.LocationType already resolves a non-file path to
        /// Remote on its own.
        /// </summary>
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
