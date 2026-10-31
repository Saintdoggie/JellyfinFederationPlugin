using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Jellyfin.Plugin.Federation.Services;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Direct link routes: the friend-facing offer (token-authenticated, the only
    /// place connection details are exchanged), and the admin status/settings.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public class DirectLinkController : ControllerBase
    {
        private static readonly string[] PassedRequestHeaders = { "Range", "If-Range", "If-None-Match", "If-Modified-Since", "Accept", "User-Agent" };
        private static readonly string[] PassedResponseHeaders = { "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Content-Disposition" };

        private readonly DirectLinkManager _manager;
        private readonly INetworkManager? _network;

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectLinkController"/> class.
        /// </summary>
        /// <param name="manager">The direct link manager.</param>
        /// <param name="network">Jellyfin's network rules (who is on this home's network).</param>
        public DirectLinkController(DirectLinkManager manager, INetworkManager? network = null)
        {
            _manager = manager;
            _network = network;
        }

        /// <summary>
        /// A viewer on this home's network fetches a friend's handed-off video
        /// here; this server gets it from the friend over the direct link. The
        /// friend checks the token in the path exactly as when the player asks it
        /// directly. Viewers elsewhere are refused (they go to the friend), so a
        /// friend's video never uses this home's upload. When the link is down,
        /// the player is sent to the friend's own address.
        /// </summary>
        /// <param name="serverId">The friend's configured server id.</param>
        /// <param name="cancellationToken">Request aborted.</param>
        /// <returns>The friend's answer.</returns>
        [HttpGet("DirectLink/{serverId}/Plugins/Federation/Peer/Play/{**rest}")]
        [HttpHead("DirectLink/{serverId}/Plugins/Federation/Peer/Play/{**rest}")]
        [AllowAnonymous]
        public async Task Relay(string serverId, CancellationToken cancellationToken)
        {
            Response.Headers.CacheControl = "private, no-store";
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId && s.Enabled);
            var escapedPath = Request.Path.ToUriComponent();
            var at = escapedPath.IndexOf(DirectLinkRelay.GatewayPath, StringComparison.OrdinalIgnoreCase);
            if (server == null || at < 0)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!DirectLinkRelay.IsHomeViewer(HttpContext, _network))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var target = escapedPath.Substring(at) + Request.QueryString.Value;
            if (!_manager.TryGetClient(server.Id, out var link))
            {
                RedirectToFriend(server, target);
                return;
            }

            // Segments the player will ask for next are fetched alongside this one.
            var depth = Math.Clamp(Plugin.Instance?.Configuration?.DirectLinkReadAhead ?? 0, 0, SegmentReadAhead.MaxDepth);
            var readAhead = depth > 0 && HttpMethods.IsGet(Request.Method) && !Request.Headers.ContainsKey("Range");
            if (readAhead && SegmentReadAhead.Shared.Take(server.Id, target) is { } held)
            {
                using (held)
                {
                    var revalidated = await RevalidateReadAheadAsync(link, target, cancellationToken).ConfigureAwait(false);
                    if (revalidated != true)
                    {
                        if (revalidated == null)
                        {
                            RedirectToFriend(server, target);
                        }
                        else
                        {
                            Response.StatusCode = StatusCodes.Status403Forbidden;
                        }

                        return;
                    }

                    bool arrived;
                    try
                    {
                        arrived = await held.WaitHeadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (arrived)
                    {
                        SegmentReadAhead.Shared.After(server.Id, target, depth, ReadAheadFetch(link));
                        WriteHead(StatusCodes.Status200OK, held.ContentType, held.Headers, held.ContentLength);
                        try
                        {
                            await held.ServeAsync(Response.Body, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ObjectDisposedException
                            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                        {
                            // The link went away mid-piece; the player retries.
                            HttpContext.Abort();
                        }

                        return;
                    }
                }

                // The friend didn't send it ahead: ask the usual way.
            }

            using var request = new HttpRequestMessage(HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get, new Uri("http://federation-direct-link" + target));
            foreach (var name in PassedRequestHeaders)
            {
                if (Request.Headers.TryGetValue(name, out var value))
                {
                    request.Headers.TryAddWithoutValidation(name, value.ToArray());
                }
            }

            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            HttpResponseMessage upstream;
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headers.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    upstream = await link.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or ObjectDisposedException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // The link dropped or stalled: send the player to the friend instead.
                    RedirectToFriend(server, target);
                    return;
                }
            }

            using (upstream)
            {
                var contentType = upstream.Content.Headers.ContentType?.ToString();
                var passed = upstream.Headers.Concat(upstream.Content.Headers)
                    .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.SelectMany(h => h.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
                WriteHead((int)upstream.StatusCode, contentType, passed, null);

                if (HttpMethods.IsHead(Request.Method))
                {
                    if (upstream.Content.Headers.ContentLength is { } headLength)
                    {
                        Response.ContentLength = headLength;
                    }

                    return;
                }

                if (upstream.IsSuccessStatusCode && DirectLinkRelay.IsPlaylist(Request.Path.Value ?? string.Empty, contentType))
                {
                    var text = await ReadPlaylistAsync(upstream, cancellationToken).ConfigureAwait(false);
                    if (text == null)
                    {
                        Response.StatusCode = StatusCodes.Status502BadGateway;
                        return;
                    }

                    if (depth > 0)
                    {
                        SegmentReadAhead.Shared.RememberPlaylist(server.Id, target, text);
                    }

                    var body = System.Text.Encoding.UTF8.GetBytes(HandoffPlaylistLinks.Rewrite(text, DirectLinkRelay.PlayerBase(Request.PathBase.Value, server.Id), null));
                    Response.ContentLength = body.Length;
                    await Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (upstream.Content.Headers.ContentLength is { } length)
                {
                    Response.ContentLength = length;
                }

                if (readAhead && upstream.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    SegmentReadAhead.Shared.After(server.Id, target, depth, ReadAheadFetch(link));
                }

                try
                {
                    await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await CopyWithStallTimeoutAsync(stream, Response.Body, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException or ObjectDisposedException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // The link went away mid-piece; the player retries (and is sent
                    // to the friend if the link is still down).
                    HttpContext.Abort();
                }
            }
        }

        private static async Task CopyWithStallTimeoutAsync(System.IO.Stream source, System.IO.Stream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
            }
        }

        private static async Task<bool?> RevalidateReadAheadAsync(HttpClient link, string target, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://federation-direct-link" + target));
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                using var response = await link.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or ObjectDisposedException or OperationCanceledException)
            {
                return null;
            }
        }

        // A segment fetched ahead of the player over the same link.
        private static Func<string, CancellationToken, Task<HttpResponseMessage>> ReadAheadFetch(HttpClient link) => (target, cancellationToken) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://federation-direct-link" + target));
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            return link.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        };

        // The friend's answer is served from this server's own address: only
        // media types, never sniffed, never run as a page.
        private void WriteHead(int status, string? contentType, IReadOnlyDictionary<string, string[]> headers, long? contentLength)
        {
            Response.StatusCode = status;
            Response.ContentType = IsMediaType(contentType) ? contentType! : "application/octet-stream";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            foreach (var name in PassedResponseHeaders)
            {
                if (headers.TryGetValue(name, out var values))
                {
                    Response.Headers[name] = values;
                }
            }

            if (contentLength is { } length)
            {
                Response.ContentLength = length;
            }
        }

        private static bool IsMediaType(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                return false;
            }

            var type = contentType.Split(';')[0].Trim();
            return type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                || type.Equals("text/vtt", StringComparison.OrdinalIgnoreCase)
                || type.Equals("application/x-subrip", StringComparison.OrdinalIgnoreCase)
                || type.Equals("application/mp4", StringComparison.OrdinalIgnoreCase);
        }

        // A playlist answer, read up to 8 MB (null when larger: the friend sent garbage).
        private static async Task<string?> ReadPlaylistAsync(HttpResponseMessage upstream, CancellationToken cancellationToken)
        {
            const int Max = 8 * 1024 * 1024;
            if (upstream.Content.Headers.ContentLength > Max)
            {
                return null;
            }

            await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var copy = new System.IO.MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (copy.Length + read > Max)
                {
                    return null;
                }

                copy.Write(buffer, 0, read);
            }

            return System.Text.Encoding.UTF8.GetString(copy.GetBuffer(), 0, (int)copy.Length);
        }

        // The link isn't up: send the player to the friend's own address (today's
        // handoff), unless that would put http media on an https page.
        private void RedirectToFriend(RemoteServer server, string target)
        {
            var friend = RemoteServerClientFactory.EffectiveUrl(server).TrimEnd('/');
            if (string.Equals(Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                && !friend.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            Response.Headers.Location = friend + target;
        }

        /// <summary>
        /// Server-to-server: a friend offers its half of a direct link and gets
        /// this server's half back. Authenticated with the friend's federation
        /// token like every Peer route.
        /// </summary>
        /// <param name="offer">The friend's connection details.</param>
        /// <param name="cancellationToken">Request aborted.</param>
        /// <returns>This server's connection details, or a refusal.</returns>
        [HttpPost("Peer/DirectLink/Offer")]
        [AllowAnonymous]
        [RequestSizeLimit(16 * 1024)]
        public async Task<IActionResult> Offer([FromBody] DirectLinkOffer? offer, CancellationToken cancellationToken)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            var (status, answer, message) = await _manager.HandleOfferAsync(caller, offer, cancellationToken).ConfigureAwait(false);
            return status == StatusCodes.Status200OK
                ? Ok(answer)
                : StatusCode(status, new { success = false, message });
        }

        /// <summary>Admin: every friend's direct link state.</summary>
        /// <returns>The status.</returns>
        [HttpGet("DirectLink/Status")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult Status() => Ok(_manager.GetStatus());

        /// <summary>Admin: the direct link settings.</summary>
        /// <returns>The settings.</returns>
        [HttpGet("Settings/DirectLink")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetSettings()
        {
            var config = Plugin.Instance!.Configuration;
            return Ok(new { EnableDirectLink = config.DirectLinkOptIn, config.DirectLinkStunServers });
        }

        /// <summary>Admin: saves the direct link settings.</summary>
        /// <param name="body">The settings.</param>
        /// <returns>Result.</returns>
        [HttpPost("Settings/DirectLink")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetSettings([FromBody] DirectLinkSettingsBody? body)
        {
            if (body == null)
            {
                return BadRequest(new { success = false, message = "Missing settings." });
            }

            var config = Plugin.Instance!.Configuration;
            if (body.DirectLinkStunServers != null)
            {
                var servers = body.DirectLinkStunServers.Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0).Distinct().ToArray();
                if (servers.Length > 6 || servers.Any(s => s.Length > 200 || StunServerInvalid(s)))
                {
                    return BadRequest(new { success = false, message = "Use up to 6 STUN servers as host:port." });
                }

                config.DirectLinkStunServers = servers.Length > 0 ? servers : new PluginConfiguration().DirectLinkStunServers;
            }

            if (body.EnableDirectLink.HasValue)
            {
                config.DirectLinkOptIn = body.EnableDirectLink.Value;
            }
            Plugin.Instance.SaveConfiguration();
            foreach (var server in config.RemoteServers)
            {
                _manager.Reconsider(server.Id);
            }

            return Ok(new { success = true, message = "Direct link settings saved." });
        }

        /// <summary>Admin: turns the direct link to one friend on (Auto) or Off.</summary>
        /// <param name="id">The friend's configured server id.</param>
        /// <param name="body">The mode.</param>
        /// <returns>Result.</returns>
        [HttpPost("Friends/{id}/DirectLink")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetFriendMode(string id, [FromBody] DirectLinkModeBody? body)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            var mode = body?.Mode;
            if (!string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { success = false, message = "Mode must be Auto or Off." });
            }

            server.DirectLinkMode = string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase) ? "Off" : "Auto";
            Plugin.Instance!.SaveConfiguration();
            _manager.Reconsider(id);
            return Ok(new { success = true, message = "Saved." });
        }

        /// <summary>Admin: try the direct link to a friend again now.</summary>
        /// <param name="id">The friend's configured server id.</param>
        /// <returns>Result.</returns>
        [HttpPost("DirectLink/Retry/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult Retry(string id)
        {
            if (Plugin.Instance?.Configuration?.RemoteServers?.Any(s => s.Id == id) != true)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            _manager.Reconsider(id);
            return Ok(new { success = true, message = "Retrying." });
        }

        private static bool StunServerInvalid(string value)
        {
            var colon = value.LastIndexOf(':');
            var host = colon > 0 ? value.Substring(0, colon) : value;
            return host.Length == 0 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-'));
        }
    }

    /// <summary>Body of <c>Settings/DirectLink</c>.</summary>
    public class DirectLinkSettingsBody
    {
        /// <summary>Gets or sets a value indicating whether direct links are on.</summary>
        public bool? EnableDirectLink { get; set; }

        /// <summary>Gets or sets the STUN servers (null keeps the current ones).</summary>
        public string[]? DirectLinkStunServers { get; set; }
    }

    /// <summary>Body of <c>Friends/{id}/DirectLink</c>.</summary>
    public class DirectLinkModeBody
    {
        /// <summary>Gets or sets <c>Auto</c> or <c>Off</c>.</summary>
        public string? Mode { get; set; }
    }
}
