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

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationMediaSourceProvider"/> class.
        /// </summary>
        public FederationMediaSourceProvider(
            ILogger<FederationMediaSourceProvider> logger,
            FederationLibraryManager federationManager,
            IHttpContextAccessor httpContextAccessor)
        {
            _logger = logger;
            _federationManager = federationManager;
            _httpContextAccessor = httpContextAccessor;
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

                _logger.LogInformation(
                    "[Federation] Debug GetMediaSources: {Name} ({Type}) key={Key}, sources={SourceCount} [{Sources}]",
                    item.Name,
                    item.GetType().Name,
                    key,
                    entrySources.Length,
                    string.Join(" | ", entrySources.Select(s => $"{s.ServerId}:{s.RemoteItemId}")));

                var sources = new List<MediaSourceInfo>();
                for (int i = 0; i < entrySources.Length; i++)
                {
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

                    var path = BuildPlaybackPath(server, src);
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

                    // The remote's own view of the file. Without Container and
                    // MediaStreams, MediaInfoHelper.SetDeviceSpecificData has nothing
                    // to run StreamBuilder against, so it cannot certify direct play
                    // or direct stream for any device profile and every source comes
                    // back unplayable - which surfaces in clients as
                    // PlaybackError.NO_MEDIA_ERROR ("Unable to find a valid media
                    // source to play") even though a source was returned.
                    var remote = await FetchRemoteSourceAsync(server, src, cancellationToken).ConfigureAwait(false);

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
                        IsRemote = true,
                        Container = remote?.Container,
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
                        RequiresOpening = false,
                        RequiresClosing = false,
                        RunTimeTicks = remote?.RunTimeTicks ?? entry.Metadata.RunTimeTicks ?? item.RunTimeTicks,
                        Type = i == primaryIndex ? MediaSourceType.Default : MediaSourceType.Grouping
                    });
                }

                if (sources.Count == 0)
                {
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
        private async Task<MediaSourceInfo?> FetchRemoteSourceAsync(RemoteServer server, FederatedSource src, CancellationToken cancellationToken)
        {
            var client = _federationManager.GetClient(src.ServerId);
            if (client == null)
            {
                return null;
            }

            // PlaybackInfo is a per-user endpoint, and an API key alone does not
            // identify one. Called out separately because it is a configuration gap
            // with an obvious fix, not a transient remote failure.
            if (string.IsNullOrEmpty(server.UserId))
            {
                _logger.LogWarning(
                    "[Federation] Server {ServerName} has no UserId configured, so its stream details cannot be read and playback will fail. Set a remote user for this server in the plugin settings.",
                    server.Name);
                return null;
            }

            var info = await client.GetPlaybackInfoAsync(
                src.RemoteItemId.ToString("N"),
                cancellationToken: cancellationToken).ConfigureAwait(false);

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

        private string? BuildPlaybackPath(RemoteServer server, FederatedSource src)
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
                return $"{localUrl}/Plugins/Federation/Stream?serverId={Uri.EscapeDataString(src.ServerId)}&itemId={src.RemoteItemId}";
            }

            var client = _federationManager.GetClient(src.ServerId);
            return client?.BuildDirectStreamUrl(src.RemoteItemId.ToString());
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
