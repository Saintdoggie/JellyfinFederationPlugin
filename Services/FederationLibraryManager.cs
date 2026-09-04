using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        /// <summary>
        /// Metadata fields this plugin itself populates on every federated item
        /// (see <see cref="MaterializeItem"/>) - locked so Jellyfin's own local
        /// metadata providers never overwrite them with an unrelated match.
        /// Exposed for <see cref="FederationItemPersistenceService"/> to backfill
        /// onto items created before this locking existed.
        /// </summary>
        public static readonly MetadataField[] LockedMetadataFields =
        {
            MetadataField.Name,
            MetadataField.Overview,
            MetadataField.OfficialRating,
            MetadataField.Genres,
            MetadataField.Studios,
            MetadataField.Tags,
            MetadataField.Runtime
        };

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
            // Deliberately independent of whether streamUrl above is non-null, and
            // no longer WAN-cap aware: the capped Direct-mode transcode URL that
            // used to force Container="mp4" here is internal-only (never served to
            // any client - see BuildStaticPath/BuildPlaybackPathAsync), while every
            // URL a client actually receives - the stamped proxy-gateway Path and
            // the provider's token-gated DirectStream URL alike - serves the raw
            // source file. The remote's real container describes those bytes.
            //
            // Gated on the server being resolvable and enabled, mirroring
            // BuildPlaybackUrl's own "no server, no URL" guard.
            if (IsStreamableType(entry.ItemType) && entry.GetPrimarySource() is { } primaryForContainer
                && GetServer(primaryForContainer.ServerId) is { Enabled: true })
            {
                if (!string.IsNullOrEmpty(entry.Metadata.Container))
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

            // Every field set above already came from the remote source's own
            // metadata - locking them keeps Jellyfin's own local metadata
            // providers (TMDb, OMDb, ...) from ever running an "identify" search
            // against a federated item and overwriting it with an unrelated
            // match. Confirmed live: dozens of Plex-sourced movies with weak
            // source metadata (no clean title match) ended up all displaying the
            // exact same wrong name from one bad shared search result - looking
            // like mass duplication, but actually mass mislabeling of otherwise-
            // distinct items whose own cached data (see FederatedCacheEntry) was
            // correct the whole time. Cast/ProductionLocations are deliberately
            // left unlocked - this plugin never sets either, so local enrichment
            // there is harmless.
            item.LockedFields = LockedMetadataFields;

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

            // Media streams are intentionally NOT saved here. MediaStreamInfos rows have
            // a foreign key on the item's own BaseItems row, which for a brand-new item
            // doesn't exist yet at this point - MaterializeItem only builds the in-memory
            // BaseItem; ILibraryManager.CreateItems is what actually persists it, and that
            // always happens later (see FederationItemPersistenceService, which batches
            // items tier-by-tier before calling CreateItems). Saving here reliably threw
            // a FOREIGN KEY constraint failure for every newly-created item's first sync,
            // silently caught and downgraded to "fall back to a live probe" - which
            // produces confusing metadata for anything the source's own container
            // mislabels (e.g. a plain stereo track whose internal title says "Surround
            // 5.1"), rather than the clean data ToDto/ApplyMediaDetails actually built.
            // The caller persists these via TryPersistMediaStreams once CreateItems has
            // run for this item's tier.

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
                return primary == null ? null : BuildStaticPath(entry, primary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not build a playback URL for {Key}; item will rely on the media source provider instead", entry.Key);
                return null;
            }
        }

        /// <summary>
        /// The client-facing static Path this plugin stamps on a federated item:
        /// this server's own <c>/Plugins/Federation/Stream</c> proxy gateway, for
        /// both streaming modes. That endpoint uses a signed, item-scoped URL and
        /// mints a short-lived remote token per request (see
        /// <see cref="FederationStreamHandler.BuildDirectStreamUrlAsync"/>), so
        /// unlike <see cref="BuildPlaybackUrl"/>'s Direct-mode output it is safe to
        /// persist on <c>item.Path</c>, which Jellyfin serializes straight into
        /// client-facing static media sources.
        /// <para>
        /// Why a Path matters at all: Jellyfin resolves <c>LocationType</c> from
        /// item.Path, and jellyfin-web's <c>canPlay()</c> hides the Play button for
        /// any non-Program item with <c>LocationType === 'Virtual'</c> - i.e. an
        /// empty Path. Direct mode used to return null here (token-security), which
        /// left every Direct-mode item without a Play button anywhere in the web
        /// client, regardless of the perfectly good source
        /// <see cref="FederationMediaSourceProvider"/> serves through PlaybackInfo.
        /// Routing the stamped URL through this server's proxy costs a relay hop
        /// for the static source, but playback is only possible with a button.
        /// </para>
        /// <para>
        /// Null when the caller cannot prove an item-specific shared path is safe.
        /// The entry-aware overload should be preferred for materialized items; this
        /// conservative overload remains for static exports/downloads that do not
        /// carry enough context to evaluate per-user library/rating rules.
        /// </para>
        /// </summary>
        public string? BuildStaticPath(string itemType, FederatedSource src)
        {
            var server = GetServer(src.ServerId);
            if (server == null || !server.Enabled || string.IsNullOrEmpty(server.ApiKey))
            {
                return null;
            }

            if (server.FriendUserAccessRules != null && server.FriendUserAccessRules.Count > 0)
            {
                return null;
            }

            return BuildProxyStreamUrl(itemType, src);
        }

        /// <summary>
        /// Builds the shared item Path when every configured local-user override
        /// allows this exact item. This avoids one narrow rule removing the Play
        /// button for every other user while preserving the rule boundary for items
        /// that genuinely differ by user.
        /// </summary>
        public string? BuildStaticPath(FederatedCacheEntry entry, FederatedSource src)
        {
            var server = GetServer(src.ServerId);
            if (server == null || !server.Enabled
                || !RemoteAccessControlService.IsAllowedForEveryConfiguredUser(
                    server,
                    entry.MappingName,
                    src.RemoteItemId,
                    entry.Metadata.OfficialRating))
            {
                return null;
            }

            return BuildProxyStreamUrl(entry.ItemType, src);
        }

        /// <summary>
        /// Builds this server's own loopback proxy-gateway URL for a source (see
        /// <see cref="BuildStaticPath"/>). Contains an item-scoped HMAC capability,
        /// never the remote server credential used to create it.
        /// </summary>
        private string? BuildProxyStreamUrl(string itemType, FederatedSource src, string? requestingUserId = null)
        {
            var server = GetServer(src.ServerId);
            if (server == null || !server.Enabled || string.IsNullOrEmpty(server.ApiKey))
            {
                return null;
            }

            // Loopback, not GetLocalServerUrl() (which is the public URL used
            // for peer/federation handshakes). This URL is fetched by this
            // server's own transcoder and by clients through this server's
            // normal reverse-proxy/host setup - never a reason to hairpin out
            // through DNS/CDN/tunnel and back to the same Jellyfin process,
            // which on a VPS-fronted setup (i.e. essentially every production
            // setup) is what turned 4K playback startup into minutes-long waits.
            var localUrl = GetInternalPlaybackBaseUrl();
            var audioFlag = IsAudioType(itemType) ? "&audio=true" : string.Empty;
            var userFlag = string.IsNullOrEmpty(requestingUserId) ? string.Empty : $"&requestingUserId={Uri.EscapeDataString(requestingUserId)}";
            var signature = CreateProxySignature(src.ServerId, src.RemoteItemId, IsAudioType(itemType), requestingUserId);
            return $"{localUrl}/Plugins/Federation/Stream?serverId={Uri.EscapeDataString(src.ServerId)}&itemId={src.RemoteItemId:N}{audioFlag}{userFlag}&sig={signature}";
        }

        /// <summary>
        /// Creates an item/user-scoped signature for the capability media URL. The
        /// configured remote credential is only HMAC key material and never appears
        /// in the URL; changing or removing the server immediately invalidates it.
        /// </summary>
        public string CreateProxySignature(string serverId, Guid remoteItemId, bool isAudio, string? requestingUserId)
        {
            var server = GetServer(serverId);
            if (server == null || !server.Enabled || string.IsNullOrEmpty(server.ApiKey))
            {
                return string.Empty;
            }

            var normalizedUser = Guid.TryParse(requestingUserId, out var userGuid) ? userGuid.ToString("N") : string.Empty;
            var payload = $"v1\n{serverId}\n{remoteItemId:N}\n{(isAudio ? "1" : "0")}\n{normalizedUser}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(server.ApiKey));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        /// <summary>Validates a proxy URL signature in constant time.</summary>
        public bool ValidateProxySignature(string serverId, Guid remoteItemId, bool isAudio, string? requestingUserId, string? signature)
        {
            if (string.IsNullOrEmpty(signature)
                || signature.Length != 64
                || signature.Any(c => !Uri.IsHexDigit(c))
                || (!string.IsNullOrEmpty(requestingUserId) && !Guid.TryParse(requestingUserId, out _)))
            {
                return false;
            }

            var expected = CreateProxySignature(serverId, remoteItemId, isAudio, requestingUserId);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var suppliedBytes = Encoding.UTF8.GetBytes(signature);
            return expectedBytes.Length == suppliedBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        /// <summary>
        /// Builds the URL a federated item's media would stream from for a given
        /// source, or null when it can't be built at all: server gone/disabled.
        /// <para>
        /// For Proxy mode this is the same loopback proxy-gateway URL that
        /// <see cref="BuildStaticPath"/> stamps on <c>item.Path</c>. For Direct
        /// mode this still builds the real, federation-token-bearing remote URL
        /// (WAN-cap decision, container/codec/bitrate query params and all) - it is
        /// the source of truth for "what would this source actually serve" that
        /// <see cref="MaterializeItem"/> uses to derive <c>Container</c>, and it is
        /// deliberately never handed to a client: <see cref="BuildStaticPath"/>
        /// (used for <c>item.Path</c>, which Jellyfin serializes straight into
        /// client-facing static media sources) routes Direct mode through this
        /// server's secret-free proxy gateway instead, and
        /// <see cref="FederationMediaSourceProvider.GetMediaSources"/> mints a
        /// short-lived, single-item-scoped token URL per request for its own
        /// sources. Callers that could put this return value in front of a client
        /// MUST treat a Direct-mode result as internal-only.
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

            // A non-Jellyfin server is always proxied regardless of the configured
            // mode: Direct mode below builds a Jellyfin-specific streaming URL
            // that means nothing to another product, and these servers'
            // credentials authenticate against the whole remote server (no scoped
            // per-item token equivalent), so they must never reach a client.
            if (server.StreamingMode == StreamingMode.Proxy || server.Kind != ServerKind.Jellyfin)
            {
                return BuildProxyStreamUrl(itemType, src);
            }

            // Direct mode: real URL, api_key and all - see this method's doc
            // comment. Internal-only; never persisted to item.Path or otherwise
            // handed to a client (see ResolvePlaybackUrl and
            // FederationMediaSourceProvider.BuildPlaybackPathAsync).
            var endpoint = IsAudioType(itemType) ? "Audio" : "Videos";
            var baseUrl = $"{server.Url.TrimEnd('/')}/{endpoint}/{src.RemoteItemId:N}/stream";
            var apiKeyParam = $"api_key={Uri.EscapeDataString(server.ApiKey)}";

            // A cap never applies to audio (already a fraction of any sensible video
            // cap). For video, WanBandwidthMonitor decides: direct play (the original,
            // and still default, behavior) whenever it can - same network, unknown, or
            // a WAN link that measured generously fast - and only a real number once
            // it has positively confirmed both that the link is WAN-only *and* what it
            // can actually sustain.
            var capMbps = IsAudioType(itemType) ? null : _bandwidthMonitor.GetEffectiveCapMbps(server);
            if (capMbps == null)
            {
                return $"{baseUrl}?{apiKeyParam}&Static=true";
            }

            // Have the remote transcode down to the largest bitrate this link can
            // sustain before this server ever pulls a byte, instead of pulling the raw
            // (potentially 25+ Mbps for a 4K HDR release) source file across the
            // internet only to immediately re-encode it.
            var videoBitrateBps = capMbps.Value * 1_000_000L;
            var heightParam = server.WanMaxHeight > 0 ? $"&MaxHeight={server.WanMaxHeight}" : string.Empty;
            return $"{baseUrl}.mp4?{apiKeyParam}&VideoCodec=h264&AudioCodec=aac&VideoBitrate={videoBitrateBps}&AudioBitrate=256000{heightParam}";
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
        /// they already exist. No WAN-capped exclusion any more: the capped
        /// transcode URL is internal-only, while every client-facing URL serves the
        /// raw source file, so the remote's real stream data describes the bytes
        /// clients actually get. Returns true when it actually saved anything.
        /// </summary>
        public bool TryPersistMediaStreams(BaseItem item, FederatedCacheEntry entry)
        {
            if (entry.Metadata.MediaStreams is not { Length: > 0 } streams)
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
        /// The constant tag stamped on every federated item, always as the
        /// first tag so it renders at the front of the item's tag list. Lets
        /// every federated title - from every server - be filtered as one
        /// group in any Jellyfin client's tag filter, independent of the
        /// per-server "🌐 ServerName" tag below (which varies per source).
        /// </summary>
        public const string FederationTagName = "Federated";

        /// <summary>
        /// Builds a federated item's tag list: the constant
        /// <see cref="FederationTagName"/> first (see its doc comment), then the
        /// remote item's own tags, then a "🌐 ServerName" tag identifying the
        /// source server - replacing any previous tag of either known shape so
        /// re-materializing after a primary source change doesn't leave stale
        /// server tags (or duplicate "Federated" markers) behind.
        /// </summary>
        public static string[] AppendServerTag(string[]? tags, string? serverName)
        {
            var kept = (tags ?? Array.Empty<string>())
                .Where(t => !t.StartsWith(ServerTagPrefix, StringComparison.Ordinal)
                    && !string.Equals(t, FederationTagName, StringComparison.OrdinalIgnoreCase));
            var withMarker = kept.Prepend(FederationTagName);
            return string.IsNullOrEmpty(serverName)
                ? withMarker.ToArray()
                : withMarker.Append(ServerTagPrefix + serverName).ToArray();
        }

        private const string ServerTagPrefix = "🌐 ";

        /// <summary>
        /// Extracts the source server name from a "🌐 ServerName" tag if one is present -
        /// the same tag <see cref="AppendServerTag"/> stamps on every materialized
        /// federation item. Used to label a federated item without a second lookup
        /// through <see cref="FederationItemCache"/> or <see cref="PluginConfiguration"/>.
        /// </summary>
        public static string? GetServerNameFromTags(IReadOnlyList<string>? tags)
        {
            var tag = tags?.FirstOrDefault(t => t.StartsWith(ServerTagPrefix, StringComparison.Ordinal));
            return tag != null ? tag.Substring(ServerTagPrefix.Length) : null;
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
