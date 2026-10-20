using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Owner side of the remote-transcode handoff: serves a friend's player the
    /// native Jellyfin HLS/stream/subtitle response for one of this server's
    /// items, by asking this server's own Jellyfin over loopback.
    /// </summary>
    /// <remarks>
    /// The friend's server still decides how to play (device profile, bitrate,
    /// tracks) and produces the native request; this server runs it, on its own
    /// GPU and its own copy of the file, and the bytes go straight to the player.
    /// Authorization (playback token, friend, item, current sharing) is checked by
    /// the caller on every request; this class only relays.
    /// <list type="bullet">
    /// <item>The internal relay key goes in the Authorization header only. Jellyfin
    /// copies the request <em>query</em> into playlist URIs, so a key there would
    /// end up in the player's hands.</item>
    /// <item>Playlists are rewritten so every URI is an absolute gateway URL on the
    /// same token (see <see cref="HlsPlaylistRewriter"/>), independent of how a
    /// player resolves relative URIs after a redirect.</item>
    /// <item>No retries: a transcode's bytes are not resumable the way a file's are,
    /// and splicing a retried response into a sent one corrupts the stream.</item>
    /// </list>
    /// </remarks>
    public sealed class FederationRemoteTranscodeGateway
    {
        private const int MaxPlaylistBytes = 8 * 1024 * 1024;

        /// <summary>Largest segment held in memory to share between a request and its retries.</summary>
        private const int MaxSharedSegmentBytes = 96 * 1024 * 1024;

        /// <summary>How long a delivered segment is kept for a player's retry of it.</summary>
        private static readonly TimeSpan SharedSegmentKeep = TimeSpan.FromMinutes(1);

        /// <summary>How long a shared segment request may take (a slow 4K start included).</summary>
        private static readonly TimeSpan SharedSegmentTimeout = TimeSpan.FromMinutes(5);

        private static readonly ConcurrentDictionary<string, SharedFetch> SharedSegments = new(StringComparer.Ordinal);

        private static readonly HttpClient Loopback = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false
        })
        {
            // Segment requests legitimately wait for ffmpeg; cancellation (the
            // player going away) is what ends a request, not a clock.
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static readonly string[] PassedRequestHeaders = { "Range", "If-Range", "If-None-Match", "If-Modified-Since" };

        private static readonly string[] PassedResponseHeaders =
        {
            "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Content-Disposition"
        };

        private readonly ILogger<FederationRemoteTranscodeGateway> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationRemoteTranscodeGateway"/> class.
        /// </summary>
        public FederationRemoteTranscodeGateway(ILogger<FederationRemoteTranscodeGateway> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// The absolute base every gateway URL starts with, as the caller reached
        /// this server (through a reverse proxy or tunnel when one reports it).
        /// Ends with a slash; the token and native path follow.
        /// </summary>
        public static string PublicGatewayBase(HttpRequest request)
        {
            static string First(string value) => value.Split(',')[0].Trim();

            var proto = request.Headers["X-Forwarded-Proto"].ToString();
            var host = request.Headers["X-Forwarded-Host"].ToString();
            var scheme = string.IsNullOrEmpty(proto) ? request.Scheme : First(proto);
            var authority = string.IsNullOrEmpty(host) ? request.Host.Value : First(host);
            return $"{scheme}://{authority}{request.PathBase}/Plugins/Federation/Peer/Play/";
        }

        /// <summary>Adds CORS headers: players fetch playlists/segments with XHR from another origin.</summary>
        public static void ApplyCors(HttpContext context)
        {
            var origin = context.Request.Headers["Origin"].ToString();
            var headers = context.Response.Headers;
            if (!string.IsNullOrEmpty(origin))
            {
                // Echoed (not "*") so a client configured to send credentials still
                // works; there are no cookies here, the token is in the path.
                headers["Access-Control-Allow-Origin"] = origin;
                headers["Access-Control-Allow-Credentials"] = "true";
                headers["Vary"] = "Origin";
            }
            else
            {
                headers["Access-Control-Allow-Origin"] = "*";
            }

            headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            headers["Access-Control-Allow-Headers"] = "Range, If-Range, Content-Type";
            headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
            headers["Access-Control-Max-Age"] = "600";
        }

        /// <summary>
        /// Relays one allowed native request to this server's Jellyfin and writes
        /// the (rewritten, for playlists) response.
        /// </summary>
        /// <param name="context">The friend's player request.</param>
        /// <param name="path">The parsed, allowed native path (ids of this server).</param>
        /// <param name="token">The playback token the URL carries, reused in rewritten URIs.</param>
        /// <param name="internalRelayKey">This server's internal relay key.</param>
        /// <param name="loopbackBase">This server's own loopback base URL.</param>
        /// <param name="cancellationToken">The player's request cancellation.</param>
        public async Task RelayAsync(
            HttpContext context,
            RemoteMediaPath path,
            string token,
            string internalRelayKey,
            string loopbackBase,
            CancellationToken cancellationToken)
        {
            var request = context.Request;
            var response = context.Response;
            var nativePath = path.ToNativePath(path.ItemId, path.MediaSourceId);
            var url = $"{loopbackBase.TrimEnd('/')}/{nativePath}{RemoteMediaPath.FilterQuery(request.Query)}";
            var isHead = HttpMethods.IsHead(request.Method);

            if (path.Kind == RemoteMediaKind.HlsSegment && !isHead && !request.Headers.ContainsKey("Range"))
            {
                SharedSegment? shared;
                try
                {
                    shared = await GetSharedSegmentAsync(url, internalRelayKey).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The player gave up waiting; the fetch carries on for its retry.
                    return;
                }

                if (shared != null)
                {
                    ApplyCors(context);
                    response.StatusCode = shared.Status;
                    if (shared.ContentType != null)
                    {
                        response.ContentType = shared.ContentType;
                    }

                    response.Headers.CacheControl = "private, no-store";
                    if (shared.Status >= 200 && shared.Status < 300)
                    {
                        response.ContentLength = shared.Body.Length;
                        await response.Body.WriteAsync(shared.Body, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogInformation("[Federation] Remote transcode: {Kind} for a friend answered HTTP {Status}", path.Kind, shared.Status);
                    }

                    return;
                }

                // Too large to share: relayed as a plain stream below (the
                // segment exists by now, so this does not restart anything).
            }

            using var upstreamRequest = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, url);
            upstreamRequest.Headers.Authorization = JellyfinAuthorization.Create(internalRelayKey);
            upstreamRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            foreach (var header in PassedRequestHeaders)
            {
                if (request.Headers.TryGetValue(header, out var value))
                {
                    upstreamRequest.Headers.TryAddWithoutValidation(header, value.ToArray());
                }
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await Loopback.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Remote transcode: this server's Jellyfin did not answer {Kind}", path.Kind);
                if (!response.HasStarted)
                {
                    response.StatusCode = StatusCodes.Status502BadGateway;
                }

                return;
            }

            using (upstream)
            {
                ApplyCors(context);
                response.StatusCode = (int)upstream.StatusCode;
                var contentType = upstream.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var isPlaylist = upstream.IsSuccessStatusCode
                    && !isHead
                    && (path.Kind is RemoteMediaKind.HlsMaster or RemoteMediaKind.HlsVariant or RemoteMediaKind.SubtitlePlaylist
                        || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase));

                if (!upstream.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "[Federation] Remote transcode: {Kind} for a friend answered HTTP {Status}",
                        path.Kind,
                        (int)upstream.StatusCode);
                }

                if (isPlaylist)
                {
                    var text = await ReadLimitedAsync(upstream, cancellationToken).ConfigureAwait(false);
                    if (text == null)
                    {
                        response.StatusCode = StatusCodes.Status502BadGateway;
                        return;
                    }

                    var gatewayBase = PublicGatewayBase(request) + Uri.EscapeDataString(token) + "/";
                    var rewritten = HlsPlaylistRewriter.Rewrite(text, "/" + nativePath, (target, query) =>
                        target.ItemId == path.ItemId
                            ? gatewayBase + target.ToNativePath(target.ItemId, target.MediaSourceId) + RemoteMediaPath.FilterQuery(RemoteMediaPath.ParseQuery(query))
                            : null);
                    var bytes = Encoding.UTF8.GetBytes(rewritten);
                    response.ContentType = string.IsNullOrEmpty(contentType) ? "application/vnd.apple.mpegurl" : contentType;
                    response.ContentLength = bytes.Length;
                    response.Headers.CacheControl = "private, no-store";
                    await response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    return;
                }

                foreach (var header in PassedResponseHeaders)
                {
                    if (upstream.Headers.TryGetValues(header, out var values) || upstream.Content.Headers.TryGetValues(header, out values))
                    {
                        response.Headers[header] = values.ToArray();
                    }
                }

                response.Headers.CacheControl = "private, no-store";
                if (isHead)
                {
                    return;
                }

                try
                {
                    await using var body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await body.CopyToAsync(response.Body, 256 * 1024, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The player went away (seek, stop): normal.
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested)
                {
                    // Same, surfaced as a write failure.
                }
            }
        }

        /// <summary>
        /// One segment request to this server's Jellyfin, shared by every player
        /// request for the same URL while it runs (and for a minute after).
        /// Jellyfin restarts its converter whenever a segment is asked for before
        /// the converter has written any (a start, or a seek); a player that gives
        /// up after 20 s and asks again would restart it again, forever, whenever
        /// the first segment takes longer than that. Here the retry waits for the
        /// running request instead, and a player giving up does not cancel it.
        /// Null when the segment is too large to hold in memory.
        /// </summary>
        private Task<SharedSegment?> GetSharedSegmentAsync(string url, string internalRelayKey)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in SharedSegments)
            {
                if (kv.Value.Fetch.IsValueCreated && kv.Value.Fetch.Value.IsCompleted && now - kv.Value.StartedUtc > SharedSegmentKeep)
                {
                    SharedSegments.TryRemove(kv);
                }
            }

            // Lazy: GetOrAdd may build two entries in a race, but only the stored
            // one ever starts a request (a second request would restart ffmpeg).
            var entry = SharedSegments.GetOrAdd(url, u => new SharedFetch(new Lazy<Task<SharedSegment?>>(() => FetchSegmentAsync(u, internalRelayKey)), now));
            return entry.Fetch.Value;
        }

        private async Task<SharedSegment?> FetchSegmentAsync(string url, string internalRelayKey)
        {
            await Task.Yield();
            try
            {
                using var timeout = new CancellationTokenSource(SharedSegmentTimeout);
                using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, url);
                upstreamRequest.Headers.Authorization = JellyfinAuthorization.Create(internalRelayKey);
                upstreamRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                using var upstream = await Loopback.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var contentType = upstream.Content.Headers.ContentType?.ToString();
                if (!upstream.IsSuccessStatusCode)
                {
                    Forget(url);
                    return new SharedSegment((int)upstream.StatusCode, contentType, Array.Empty<byte>());
                }

                if (upstream.Content.Headers.ContentLength > MaxSharedSegmentBytes)
                {
                    Forget(url);
                    return null;
                }

                await using var stream = await upstream.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[256 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxSharedSegmentBytes)
                    {
                        Forget(url);
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                return new SharedSegment((int)upstream.StatusCode, contentType, buffer.ToArray());
            }
            catch (Exception ex)
            {
                Forget(url);
                _logger.LogWarning("[Federation] Remote transcode: this server's Jellyfin did not deliver a segment for a friend: {Error}", ex.GetType().Name);
                return new SharedSegment(StatusCodes.Status502BadGateway, null, Array.Empty<byte>());
            }

            // A failed attempt is not reused: the next request tries again.
            void Forget(string key) => _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                SharedSegments.TryRemove(key, out _);
            });
        }

        private static async Task<string?> ReadLimitedAsync(HttpResponseMessage upstream, CancellationToken cancellationToken)
        {
            if (upstream.Content.Headers.ContentLength > MaxPlaylistBytes)
            {
                return null;
            }

            await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxPlaylistBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private sealed record SharedFetch(Lazy<Task<SharedSegment?>> Fetch, DateTime StartedUtc);

        private sealed record SharedSegment(int Status, string? ContentType, byte[] Body);
    }
}
