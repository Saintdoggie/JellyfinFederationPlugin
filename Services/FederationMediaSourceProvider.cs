using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Provides multiple media sources for federated content: one per remote source
    /// so the user can pick which server to play from in the Jellyfin UI.
    /// Honors each server's <see cref="StreamingMode"/>: Direct sources embed the
    /// remote api_key (documented tradeoff); Proxy sources route through this
    /// server so the remote key never reaches clients.
    /// </summary>
    public class FederationMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<FederationMediaSourceProvider> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly RemoteAccessControlService _accessControl;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationMediaSourceProvider"/> class.
        /// </summary>
        public FederationMediaSourceProvider(
            ILogger<FederationMediaSourceProvider> logger,
            FederationLibraryManager federationManager,
            IHttpContextAccessor httpContextAccessor,
            IAuthorizationContext authorizationContext,
            RemoteAccessControlService accessControl)
        {
            _logger = logger;
            _federationManager = federationManager;
            _httpContextAccessor = httpContextAccessor;
            _authorizationContext = authorizationContext;
            _accessControl = accessControl;
        }

        /// <summary>
        /// Resolves which of this server's own local users is asking, via
        /// Jellyfin's own <see cref="IAuthorizationContext"/> against the current
        /// inbound HTTP request - GetMediaSources always runs inside a real,
        /// authenticated PlaybackInfo request (see <see cref="ResolveLocalServerUrl"/>
        /// for the same assumption elsewhere in this class), so this is reliable
        /// without this plugin having to thread a user id through Jellyfin's own
        /// IMediaSourceProvider interface (which does not carry one). Returns null
        /// when it cannot be resolved (no request context, e.g. an internal call) -
        /// <see cref="RemoteAccessControlService.IsAllowed"/> treats that as "allow",
        /// the same as before this feature existed.
        /// </summary>
        private async Task<Guid?> ResolveLocalUserId()
        {
            try
            {
                var context = _httpContextAccessor.HttpContext;
                if (context == null)
                {
                    return null;
                }

                var info = await _authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
                return info != null && info.UserId != Guid.Empty ? info.UserId : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not resolve the local acting user for an access-control check");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            if (item == null)
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            if (!_federationManager.IsFederatedItem(item))
            {
                _logger.LogDebug(
                    "[Federation] GetMediaSources: {Name} ({Type}) has no FederationKey - not treated as federated",
                    item.Name,
                    item.GetType().Name);
                return Enumerable.Empty<MediaSourceInfo>();
            }

            try
            {
                var key = FederationLibraryManager.GetFederationKey(item);
                var entry = key == null ? null : _federationManager.Cache.GetEntryByKey(key);
                if (entry == null)
                {
                    _logger.LogWarning(
                        "[Federation] Debug GetMediaSources: {Name} ({Type}) has FederationKey={Key} but no matching cache entry",
                        item.Name,
                        item.GetType().Name,
                        key);
                    return Enumerable.Empty<MediaSourceInfo>();
                }

                var entrySources = entry.GetSourcesSnapshot();
                var primaryIndex = Math.Min(entry.PrimarySourceIndex, entrySources.Length - 1);
                var localUserId = await ResolveLocalUserId().ConfigureAwait(false);

                _logger.LogInformation(
                    "[Federation] Debug GetMediaSources: {Name} ({Type}) key={Key}, sources={SourceCount} [{Sources}]",
                    item.Name,
                    item.GetType().Name,
                    key,
                    entrySources.Length,
                    string.Join(" | ", entrySources.Select(s => $"{s.ServerId}:{s.RemoteItemId}")));

                // When the item carries its own stream URL on Path, Jellyfin already
                // builds a full static media source from it (the primary one), so
                // emitting the primary again here would show the same file twice in the
                // client's version picker. Additional servers hosting the same content
                // are still worth offering as alternates.
                //
                // Compared against a freshly built URL rather than just checking that
                // Path is non-empty: item.Path is stamped once at sync time and
                // reconciliation only ever creates or deletes items, never updates them,
                // so a server whose address or api_key has since changed (re-friending
                // mints a new key, for instance) leaves every item pointing at a URL
                // that no longer works. When that happens the freshly built URL differs,
                // and emitting the primary here puts a working source back in front of
                // the client instead of leaving only the stale one.
                var primarySource = primaryIndex >= 0 && primaryIndex < entrySources.Length
                    ? entrySources[primaryIndex]
                    : null;
                var currentPrimaryUrl = primarySource == null
                    ? null
                    : _federationManager.BuildPlaybackUrl(entry.ItemType, primarySource);
                var staticSourceCoversPrimary = !string.IsNullOrEmpty(item.Path)
                    && string.Equals(item.Path, currentPrimaryUrl, StringComparison.Ordinal);

                if (!string.IsNullOrEmpty(item.Path) && !staticSourceCoversPrimary)
                {
                    _logger.LogWarning(
                        "[Federation] {Name} has a stored stream path that no longer matches its server's current address/key; serving a freshly built source instead (a sync will refresh the stored one)",
                        item.Name);
                }

                // Each candidate source needs one remote HTTP round trip
                // (FetchRemoteSourceAsync) to describe the actual file. Resolving
                // every non-network field first and firing all the remote fetches
                // together (instead of one `await` per source in a sequential loop)
                // turns N sources' worth of serial round-trip latency on every single
                // play into roughly one round trip's worth - the slowest source, not
                // the sum of all of them.
                var candidates = new List<(int Index, FederatedSource Src, RemoteServer Server, string Path, string SourceName, bool IsWanCapped)>();
                for (int i = 0; i < entrySources.Length; i++)
                {
                    if (staticSourceCoversPrimary && i == primaryIndex)
                    {
                        // The item's own Path already IS this source (stamped once
                        // at materialization time, outside any request context - see
                        // ResolvePlaybackUrl), and Jellyfin builds a static media
                        // source from it directly without calling this provider, so
                        // there is nothing here to gate. A denied primary source
                        // still logs below so it's visible that this known gap
                        // applied, even though it can't be closed from this hook.
                        if (!_accessControl.IsAllowed(_federationManager.GetServer(entrySources[i].ServerId), localUserId, entry.MappingName, entrySources[i].RemoteItemId))
                        {
                            _logger.LogWarning(
                                "[Federation] {Name}'s primary source is blocked by a per-remote-user override for the current user, but is served via the item's own static Path (set outside any request context) which this provider cannot suppress - alternate sources are still filtered normally",
                                item.Name);
                        }

                        continue;
                    }

                    var src = entrySources[i];
                    var server = _federationManager.GetServer(src.ServerId);
                    if (server == null || !server.Enabled)
                    {
                        // Matches what FederationSyncService already does when
                        // refreshing: a server that is disabled is as unusable as one
                        // that has been removed. Without the Enabled check a disabled
                        // server still produced a source here, and playback failed
                        // later against a host the user had deliberately turned off.
                        _logger.LogWarning(
                            "[Federation] Debug GetMediaSources: {Name} source #{Index} references disabled/missing server {ServerId} - skipping",
                            item.Name,
                            i,
                            src.ServerId);
                        continue;
                    }

                    if (!_accessControl.IsAllowed(server, localUserId, entry.MappingName, src.RemoteItemId))
                    {
                        _logger.LogInformation(
                            "[Federation] {Name} source #{Index} on {ServerName} skipped for the current user by a per-remote-user access override",
                            item.Name,
                            i,
                            server.Name);
                        continue;
                    }

                    var path = BuildPlaybackPath(server, src, entry.ItemType, localUserId);
                    if (path == null)
                    {
                        _logger.LogWarning(
                            "[Federation] Debug GetMediaSources: {Name} source #{Index} on server {ServerName} ({StreamingMode}) produced a null playback path - skipping",
                            item.Name,
                            i,
                            server.Name,
                            server.StreamingMode);
                        continue;
                    }

                    var sourceName = entrySources.Length > 1
                        ? $"{server.Name}{(i == primaryIndex ? " (primary)" : string.Empty)}"
                        : server.Name;

                    // Whether this server's Direct-mode stream to this item is
                    // currently a WanBandwidthMonitor-capped transcode rather than the
                    // raw source file (see BuildPlaybackPath/BuildPlaybackUrl). When it
                    // is, the remote's own PlaybackInfo response fetched below
                    // describes the *original* file - the wrong container, codecs, and
                    // bitrate for what this URL actually serves. Reporting that here
                    // would certify direct play against bytes that do not match, so
                    // it is skipped entirely (also saving a remote round-trip) in
                    // favor of the existing "remote didn't hand back info" fallback
                    // below: SupportsProbing = true lets the transcoder discover the
                    // real, capped stream's actual characteristics itself.
                    var isWanCapped = server.StreamingMode == StreamingMode.Direct
                        && entry.ItemType != "Audio"
                        && _federationManager.BandwidthMonitor.GetEffectiveCapMbps(server) != null;

                    candidates.Add((i, src, server, path, sourceName, isWanCapped));
                }

                // The remote's own view of each file. Without Container and
                // MediaStreams, MediaInfoHelper.SetDeviceSpecificData has nothing
                // to run StreamBuilder against, so it cannot certify direct play
                // or direct stream for any device profile and every source comes
                // back unplayable - which surfaces in clients as
                // PlaybackError.NO_MEDIA_ERROR ("Unable to find a valid media
                // source to play") even though a source was returned.
                var fetchTasks = candidates.Select(c => c.IsWanCapped
                    ? Task.FromResult<MediaSourceInfo?>(null)
                    : FetchRemoteSourceAsync(c.Server, c.Src, localUserId, cancellationToken)).ToArray();
                var remoteResults = await Task.WhenAll(fetchTasks).ConfigureAwait(false);

                var sources = new List<MediaSourceInfo>();
                for (int c = 0; c < candidates.Count; c++)
                {
                    var (i, src, server, path, sourceName, isWanCapped) = candidates[c];
                    var remote = remoteResults[c];

                    _logger.LogInformation(
                        "[Federation] GetMediaSources: {Name} source #{Index} on {ServerName} -> container={Container}, streams={StreamCount}, bitrate={Bitrate}",
                        item.Name,
                        i,
                        server.Name,
                        remote?.Container ?? "(none)",
                        remote?.MediaStreams?.Count ?? 0,
                        remote?.Bitrate);

                    sources.Add(new MediaSourceInfo
                    {
                        // Jellyfin round-trips MediaSourceId through playback URLs and
                        // expects a plain 32-char hex string; the old
                        // "{serverId}:{remoteItemId}" composite did not survive that.
                        Id = BuildSourceId(src),
                        Name = sourceName,
                        Path = path,
                        Protocol = MediaProtocol.Http,

                        // Only true in Direct mode, where Path is a URL on a genuinely
                        // different host. In Proxy mode Path points back at this same
                        // server's own /Plugins/Federation/Stream endpoint, so from the
                        // client's perspective it is local. Getting this wrong matters:
                        // jellyfin-web's supportsDirectPlay() refuses IsRemote sources
                        // outright on clients that lack the RemoteVideo app feature
                        // (several smart-TV webviews), which would needlessly block
                        // Proxy-mode playback that this server is perfectly able to serve.
                        IsRemote = server.StreamingMode == StreamingMode.Direct,
                        // "mp4" for a capped stream matches exactly what
                        // BuildPlaybackUrl requests (VideoCodec=h264&AudioCodec=aac)
                        // for one - not a guess.
                        Container = isWanCapped ? "mp4" : remote?.Container,
                        Size = remote?.Size,
                        Bitrate = remote?.Bitrate,
                        MediaStreams = remote?.MediaStreams ?? new List<MediaStream>(),
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,

                        // Enabled so a client that cannot direct-play the remote's
                        // container still has a path: this server pulls the remote HTTP
                        // stream and transcodes it. That costs local bandwidth and CPU
                        // per federated stream, but with it false any incompatible
                        // container is simply unplayable.
                        SupportsTranscoding = true,

                        // Lets the transcoder/StreamBuilder ffprobe the source itself
                        // when the remote didn't hand back Container/MediaStreams (remote
                        // unreachable, no accessible user, etc.) - the fallback path for
                        // exactly the case the comment above FetchRemoteSourceAsync warns
                        // about, instead of leaving playback with nothing to go on.
                        SupportsProbing = true,
                        RequiresOpening = false,
                        RequiresClosing = false,

                        // Without this, Jellyfin's transcoder defaults for an HTTP input
                        // are -analyzeduration 200M -probesize 1G (see log:
                        // "FFmpeg.DirectStream" command line). At a 15 Mbps 4K source
                        // that had ffmpeg pulling ~370 MB through this proxy before it
                        // would even start writing HLS segments - which is what "click
                        // play, waits 5 minutes" actually is. We already have the
                        // remote's authoritative stream list (see MediaStreams above),
                        // so re-probing hundreds of megabytes of body just to rediscover
                        // what we already told the transcoder is pure loss. 500 ms is
                        // plenty for ffmpeg to sync on a keyframe when it has stream
                        // info already, and drops startup from minutes to seconds.
                        // When the remote didn't hand back streams (SupportsProbing
                        // fallback path above) ffmpeg still succeeds - 500 ms is short
                        // for probing an unknown file, but ffmpeg won't hard-fail on it,
                        // and the alternative is the current five-minute wait.
                        AnalyzeDurationMs = remote != null && remote.MediaStreams != null && remote.MediaStreams.Count > 0 ? 500 : (int?)null,

                        RunTimeTicks = remote?.RunTimeTicks ?? entry.Metadata.RunTimeTicks ?? item.RunTimeTicks,
                        Type = i == primaryIndex ? MediaSourceType.Default : MediaSourceType.Grouping
                    });
                }

                if (sources.Count == 0 && !staticSourceCoversPrimary)
                {
                    // Only a problem when the item's own path isn't already serving the
                    // primary source. When it is, returning nothing here is the normal
                    // single-server case, not a failure.
                    _logger.LogWarning("[Federation] No live sources for {Name}", item.Name);
                }

                return sources;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error getting media sources for {Name}", item.Name);
                return Enumerable.Empty<MediaSourceInfo>();
            }
        }

        /// <summary>
        /// Asks the remote server what the file actually is, via its own PlaybackInfo
        /// endpoint. Returns null when the remote is unreachable or returns nothing,
        /// in which case the caller still emits a source built from cached metadata -
        /// no worse than before, and the warning says why it will probably not play.
        /// </summary>
        private async Task<MediaSourceInfo?> FetchRemoteSourceAsync(RemoteServer server, FederatedSource src, Guid? localUserId, CancellationToken cancellationToken)
        {
            var client = _federationManager.GetClient(src.ServerId);
            if (client == null)
            {
                return null;
            }

            // PlaybackInfo is a per-user endpoint, and an API key alone does not
            // identify one. GetPlaybackInfoAsync falls back to the remote's first
            // user when no UserId is configured, so an empty UserId is not fatal:
            // we still try, and log whether a user was resolved.
            if (string.IsNullOrEmpty(server.UserId))
            {
                _logger.LogInformation(
                    "[Federation] Server {ServerName} has no UserId configured; relying on automatic playback-user resolution in GetPlaybackInfoAsync",
                    server.Name);
            }

            var info = await client.GetPlaybackInfoAsync(
                src.RemoteItemId.ToString("N"),
                cancellationToken: cancellationToken,
                localActingUserId: localUserId?.ToString("N")).ConfigureAwait(false);

            var remote = info?.MediaSources?.FirstOrDefault();
            if (remote == null)
            {
                _logger.LogWarning(
                    "[Federation] No remote media source for item {RemoteItemId} on server {ServerId}; falling back to cached metadata (playback will likely fail)",
                    src.RemoteItemId,
                    src.ServerId);
                return null;
            }

            // The remote's per-stream Path values are paths on *its* filesystem and
            // are meaningless (and needlessly disclosing) here. Only the container and
            // codec details matter for profile matching.
            if (remote.MediaStreams != null)
            {
                foreach (var stream in remote.MediaStreams)
                {
                    stream.Path = null;
                }
            }

            return remote;
        }

        /// <summary>
        /// Deterministic 32-char hex id for a federated source, stable across syncs so
        /// a resumed stream keeps pointing at the same remote.
        /// </summary>
        private static string BuildSourceId(FederatedSource src)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{src.ServerId}:{src.RemoteItemId:N}"));
            return new Guid(bytes).ToString("N");
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            return Task.FromException<ILiveStream>(new NotSupportedException("Live stream opening is not supported for federated content"));
        }

        private string? BuildPlaybackPath(RemoteServer server, FederatedSource src, string itemType, Guid? localUserId)
        {
            if (server.StreamingMode == StreamingMode.Proxy)
            {
                var localUrl = ResolveLocalServerUrl();
                if (string.IsNullOrEmpty(localUrl))
                {
                    _logger.LogWarning(
                        "[Federation] Server {Server} is in Proxy mode but no local server URL could be resolved; skipping source",
                        server.Name);
                    return null;
                }

                // The remote api_key stays server-side; clients only see this server.
                var audioFlag = itemType == "Audio" ? "&audio=true" : string.Empty;
                // Redundant with the access check already applied in GetMediaSources
                // above (which decides whether to emit this source at all) - carried
                // along anyway so FederationController.Stream can re-check it itself
                // at the moment the bytes are actually requested, in case this URL
                // outlives the session it was minted for (bookmarked, cached by a
                // client, replayed later after the admin tightens the rule).
                var requestingUserFlag = localUserId.HasValue ? $"&requestingUserId={localUserId.Value:N}" : string.Empty;
                return $"{localUrl}/Plugins/Federation/Stream?serverId={Uri.EscapeDataString(src.ServerId)}&itemId={src.RemoteItemId:N}{audioFlag}{requestingUserFlag}";
            }

            // Same URL shape the item's own Path uses, so an alternate source behaves
            // identically to the primary one.
            return _federationManager.BuildPlaybackUrl(itemType, src);
        }

        /// <summary>
        /// Resolves this server's public URL: an explicit override from config
        /// when set, otherwise derived from the current incoming HTTP request
        /// (PlaybackInfo is always an authenticated HTTP request), so it works
        /// behind reverse proxies without manual configuration.
        /// </summary>
        private string ResolveLocalServerUrl()
        {
            var configured = _federationManager.GetLocalServerUrl();
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            var context = _httpContextAccessor.HttpContext;
            var request = context?.Request;
            if (request == null)
            {
                return string.Empty;
            }

            var scheme = request.Scheme;
            // Honour X-Forwarded-Proto when present (common behind reverse proxies).
            var forwardedProto = request.Headers["X-Forwarded-Proto"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedProto)
                && (forwardedProto == Uri.UriSchemeHttp || forwardedProto == Uri.UriSchemeHttps))
            {
                scheme = forwardedProto;
            }

            return $"{scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
        }
    }
}
