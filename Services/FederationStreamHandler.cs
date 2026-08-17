using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Proxies federated playback through this server (Proxy mode). The remote
    /// api_key is only used between this server and the remote server, so it is
    /// never exposed to clients. Preserves Range requests.
    /// </summary>
    public class FederationStreamHandler
    {
        // Shared for the app lifetime: streaming responses can run for hours.
        private static readonly HttpClient DefaultProxyHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromHours(3)
        };

        /// <summary>
        /// Test-only seam: when set, used instead of <see cref="DefaultProxyHttpClient"/>.
        /// Tests must reset this to null afterwards.
        /// </summary>
        internal static HttpClient? HttpClientOverride { get; set; }

        private static HttpClient ProxyHttpClient => HttpClientOverride ?? DefaultProxyHttpClient;

        // A single ReadAsync stalling this long (remote briefly saturated, tunnel
        // hiccup, etc.) is treated as a failed attempt and retried rather than
        // silently hanging for up to the 3-hour HttpClient timeout above.
        private static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(20);

        // One initial attempt plus up to two resumes. A dropped/stalled connection
        // resumes with a Range request from the last byte actually written to the
        // client instead of failing the whole stream outright.
        private const int MaxAttempts = 3;

        private readonly ILogger<FederationStreamHandler> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly RemoteAccessControlService _accessControl;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationStreamHandler"/> class.
        /// </summary>
        public FederationStreamHandler(
            ILogger<FederationStreamHandler> logger,
            FederationLibraryManager federationManager,
            RemoteAccessControlService accessControl)
        {
            _logger = logger;
            _federationManager = federationManager;
            _accessControl = accessControl;
        }

        /// <summary>
        /// Builds the server-side direct stream URL used when proxying (contains the
        /// remote api_key; never sent to clients or written to logs).
        /// </summary>
        /// <param name="serverId">The remote server to stream from.</param>
        /// <param name="remoteItemId">The item id on that server.</param>
        /// <param name="isAudio">
        /// True to use the remote's audio streaming endpoint. Songs do not stream
        /// reliably from /Videos, so the caller passes through which one it wants.
        /// </param>
        public string BuildDirectStreamUrl(string serverId, string remoteItemId, bool isAudio = false)
        {
            var server = _federationManager.GetServer(serverId);
            if (server == null)
            {
                throw new InvalidOperationException($"Server not found: {serverId}");
            }

            var endpoint = isAudio ? "Audio" : "Videos";
            return $"{server.Url.TrimEnd('/')}/{endpoint}/{remoteItemId}/stream?api_key={Uri.EscapeDataString(server.ApiKey)}&Static=true";
        }

        /// <summary>
        /// Proxies the stream body through this server (Proxy mode). Preserves Range.
        /// </summary>
        public async Task HandleProxyAsync(
            string serverId,
            string remoteItemId,
            HttpRequest request,
            HttpResponse response,
            CancellationToken cancellationToken,
            bool isAudio = false,
            string? requestingUserId = null)
        {
            try
            {
                var server = _federationManager.GetServer(serverId);
                if (server == null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Redundant with FederationMediaSourceProvider already having decided
                // whether to hand this URL out in the first place (see its comment on
                // requestingUserFlag) - re-checked here so a URL that outlived the
                // rule that allowed it (bookmarked, cached, replayed later after the
                // admin tightens an override) is still denied. No requestingUserId at
                // all (an older client, or a URL minted before this feature existed)
                // behaves exactly as before - only a URL that does carry one is
                // subject to this check.
                if (!string.IsNullOrEmpty(requestingUserId)
                    && Guid.TryParse(requestingUserId, out var requestingUserGuid)
                    && Guid.TryParse(remoteItemId, out var remoteItemGuid))
                {
                    var mappingName = _federationManager.Cache.TryGetLocalKeyForRemoteItem(serverId, remoteItemGuid) is string key
                        ? _federationManager.Cache.GetEntryByKey(key)?.MappingName
                        : null;
                    if (!_accessControl.IsAllowed(server, requestingUserGuid, mappingName, remoteItemGuid))
                    {
                        _logger.LogInformation(
                            "[Federation] Denying proxy stream for item {ItemId} on {Server} to user {UserId} (blocked by a per-remote-user access override)",
                            remoteItemId,
                            server.Name,
                            requestingUserId);
                        response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }

                var range = request.Headers["Range"].FirstOrDefault();
                var url = BuildDirectStreamUrl(serverId, remoteItemId, isAudio);
                _logger.LogInformation("[Federation] Proxying item {ItemId} from server {Server}", remoteItemId, server.Name);

                var (rangeStartInit, rangeEnd) = ParseRange(range);
                var rangeStart = rangeStartInit;
                var headersSent = false;
                var buffer = new byte[81920];

                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    try
                    {
                        using var remoteReq = new HttpRequestMessage(HttpMethod.Get, url);
                        var requestRange = rangeStart > 0 || rangeEnd.HasValue
                            ? $"bytes={rangeStart}-{(rangeEnd.HasValue ? rangeEnd.Value.ToString() : string.Empty)}"
                            : null;
                        if (requestRange != null)
                        {
                            remoteReq.Headers.TryAddWithoutValidation("Range", requestRange);
                        }

                        using var remoteResp = await ProxyHttpClient.SendAsync(
                            remoteReq,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken).ConfigureAwait(false);

                        if (!remoteResp.IsSuccessStatusCode && remoteResp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        {
                            if (!headersSent)
                            {
                                response.StatusCode = (int)remoteResp.StatusCode;
                            }

                            return;
                        }

                        // A resume (headers already sent, resuming from a non-zero
                        // offset) asked the remote for "bytes={rangeStart}-" and the
                        // Content-Length already committed to the client covers only
                        // what was promised on the very first attempt. If an
                        // intermediate proxy/tunnel strips the Range header on this
                        // retry, the remote answers 200 with the *whole* file from
                        // byte 0 instead of 206 from rangeStart - accepting that would
                        // splice bytes 0..N onto the wire at the position rangeStart
                        // already reached (corrupted playback) and eventually write
                        // past the committed Content-Length, which is the exact
                        // Kestrel mismatch/reset this handler works elsewhere to
                        // avoid. Require the resume to actually be a resume.
                        if (headersSent && rangeStart > 0
                            && (remoteResp.StatusCode != System.Net.HttpStatusCode.PartialContent
                                || remoteResp.Content.Headers.ContentRange?.From != rangeStart))
                        {
                            response.HttpContext.Abort();
                            return;
                        }

                        if (!headersSent)
                        {
                            response.StatusCode = (int)remoteResp.StatusCode;
                            if (remoteResp.Content.Headers.ContentType != null)
                            {
                                response.ContentType = remoteResp.Content.Headers.ContentType.ToString();
                            }

                            if (remoteResp.Content.Headers.ContentLength.HasValue)
                            {
                                // The number of bytes this response body will actually
                                // contain, full stop - not rangeStart plus that. A 206's
                                // Content-Length is already relative to the requested
                                // range (e.g. a 5,000,000-byte remaining length for a
                                // 6,000,000-byte file when the client asked for
                                // "bytes=1000000-"), so adding rangeStart on top
                                // overstated it by exactly the seek offset on every
                                // non-zero-start request - which is every seek, and every
                                // buffer-ahead read during normal playback. Clients then
                                // waited forever for bytes that were never coming: this is
                                // what "seeking is broken" and "playback stalls" actually
                                // were. A later retry never re-enters this block (guarded
                                // by headersSent below), so this value, captured once, is
                                // also the correct total across every subsequent resume -
                                // each resume's own remaining length keeps summing back to
                                // what was promised here.
                                response.ContentLength = remoteResp.Content.Headers.ContentLength.Value;
                            }

                            if (remoteResp.Headers.Contains("Accept-Ranges"))
                            {
                                response.Headers["Accept-Ranges"] = remoteResp.Headers.GetValues("Accept-Ranges").FirstOrDefault() ?? "bytes";
                            }

                            if (remoteResp.Content.Headers.ContentRange != null)
                            {
                                response.Headers["Content-Range"] = remoteResp.Content.Headers.ContentRange.ToString();
                            }

                            headersSent = true;
                        }

                        await using var remoteStream = await remoteResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        while (true)
                        {
                            int read;
                            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                            {
                                idleCts.CancelAfter(IdleReadTimeout);
                                read = await remoteStream.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
                            }

                            if (read == 0)
                            {
                                return;
                            }

                            // No per-chunk FlushAsync: Kestrel already sends each
                            // WriteAsync over the wire immediately for a streamed
                            // response, so the extra flush was just per-chunk syscall
                            // overhead on the hot path of every byte relayed.
                            await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            rangeStart += read;
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
                    {
                        _logger.LogWarning(
                            ex,
                            "[Federation] Proxy stream for item {ItemId} stalled/dropped at byte {Offset} (attempt {Attempt}/{Max}), retrying",
                            remoteItemId,
                            rangeStart,
                            attempt,
                            MaxAttempts);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Aborting here (rather than just returning) matters whenever
                // headers were already sent: those already committed a
                // Content-Length covering the *entire* remaining file. Returning
                // normally would make ASP.NET think this response completed
                // successfully; Kestrel then discovers far fewer bytes were
                // actually written than promised and throws its own fatal
                // "Content-Length mismatch" InvalidOperationException while
                // flushing the response - which resets the TCP connection instead
                // of closing it cleanly. Video players constantly open and cancel
                // range requests as completely normal behavior (seeking,
                // prefetching, probing), so every single one of those was being
                // turned into what looked like a hard streaming error to the
                // player, forcing it into expensive recovery (re-fetching
                // PlaybackInfo, rebuilding the media source) instead of just
                // opening its next range request - this is what "3 minutes to
                // start, then stutters every ~20s" actually was. When nothing was
                // sent yet, aborting is still safe: the client is already gone
                // either way.
                _logger.LogInformation("[Federation] Proxy stream cancelled by client");
                response.HttpContext.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error proxying stream");
                if (!response.HasStarted)
                {
                    response.StatusCode = StatusCodes.Status500InternalServerError;
                }
                else
                {
                    // Same reasoning as the OperationCanceledException case above:
                    // retries exhausted after headers were already sent, so the
                    // promised Content-Length can never be fulfilled.
                    response.HttpContext.Abort();
                }
            }
        }

        /// <summary>
        /// Parses a "bytes=start-end" Range header value. Missing/unparseable input
        /// is treated as "from the beginning, no upper bound".
        /// </summary>
        private static (long Start, long? End) ParseRange(string? range)
        {
            if (string.IsNullOrEmpty(range) || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return (0, null);
            }

            var spec = range.Substring("bytes=".Length);
            var parts = spec.Split('-');
            var start = parts.Length > 0 && long.TryParse(parts[0], out var s) ? s : 0;
            long? end = parts.Length > 1 && long.TryParse(parts[1], out var e) ? e : (long?)null;
            return (start, end);
        }
    }
}
