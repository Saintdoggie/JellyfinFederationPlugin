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
    /// Builds redirect / proxy responses for federated playback. Used by the
    /// <c>Redirect</c> and <c>Stream</c> controller endpoints.
    /// </summary>
    public class FederationStreamHandler
    {
        private readonly ILogger<FederationStreamHandler> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly HttpClient _proxyHttpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationStreamHandler"/> class.
        /// </summary>
        public FederationStreamHandler(
            ILogger<FederationStreamHandler> logger,
            FederationLibraryManager federationManager)
        {
            _logger = logger;
            _federationManager = federationManager;
            _proxyHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromHours(3)
            };
        }

        /// <summary>
        /// Builds a direct stream URL for a remote server + item, with optional range header.
        /// </summary>
        public string BuildDirectStreamUrl(string serverId, string remoteItemId, string? rangeHeader = null)
        {
            var server = _federationManager.GetServer(serverId);
            if (server == null)
            {
                throw new InvalidOperationException($"Server not found: {serverId}");
            }

            var url = $"{server.Url.TrimEnd('/')}/Videos/{remoteItemId}/stream?api_key={Uri.EscapeDataString(server.ApiKey)}&Static=true";
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                url += $"&Range={Uri.EscapeDataString(rangeHeader)}";
            }

            return url;
        }

        /// <summary>
        /// Returns a 302 redirect to the remote server (Direct mode).
        /// </summary>
        public Task HandleRedirectAsync(
            string serverId,
            string remoteItemId,
            HttpResponse response,
            CancellationToken cancellationToken)
        {
            try
            {
                var server = _federationManager.GetServer(serverId);
                if (server == null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }

                var range = response.HttpContext.Request.Headers["Range"].FirstOrDefault();
                var url = BuildDirectStreamUrl(serverId, remoteItemId, range);
                _logger.LogInformation("[Federation] Redirecting to {Url}", url);
                response.Redirect(url, permanent: false);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error during redirect");
                response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Proxies the stream body through this server (Proxy mode). Preserves Range.
        /// </summary>
        public async Task HandleProxyAsync(
            string serverId,
            string remoteItemId,
            HttpRequest request,
            HttpResponse response,
            CancellationToken cancellationToken)
        {
            try
            {
                var server = _federationManager.GetServer(serverId);
                if (server == null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var range = request.Headers["Range"].FirstOrDefault();
                var url = BuildDirectStreamUrl(serverId, remoteItemId, range);
                _logger.LogInformation("[Federation] Proxying {Url}", url);

                using var remoteReq = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(range))
                {
                    remoteReq.Headers.TryAddWithoutValidation("Range", range);
                }

                using var remoteResp = await _proxyHttpClient.SendAsync(
                    remoteReq,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!remoteResp.IsSuccessStatusCode && remoteResp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    response.StatusCode = (int)remoteResp.StatusCode;
                    return;
                }

                response.StatusCode = (int)remoteResp.StatusCode;
                if (remoteResp.Content.Headers.ContentType != null)
                {
                    response.ContentType = remoteResp.Content.Headers.ContentType.ToString();
                }

                if (remoteResp.Content.Headers.ContentLength.HasValue)
                {
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

                await using var remoteStream = await remoteResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[81920];
                int read;
                while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Federation] Proxy stream cancelled by client");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error proxying stream");
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }
}
