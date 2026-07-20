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
        private static readonly HttpClient ProxyHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromHours(3)
        };

        private readonly ILogger<FederationStreamHandler> _logger;
        private readonly FederationLibraryManager _federationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationStreamHandler"/> class.
        /// </summary>
        public FederationStreamHandler(
            ILogger<FederationStreamHandler> logger,
            FederationLibraryManager federationManager)
        {
            _logger = logger;
            _federationManager = federationManager;
        }

        /// <summary>
        /// Builds the server-side direct stream URL used when proxying (contains the
        /// remote api_key; never sent to clients or written to logs).
        /// </summary>
        public string BuildDirectStreamUrl(string serverId, string remoteItemId)
        {
            var server = _federationManager.GetServer(serverId);
            if (server == null)
            {
                throw new InvalidOperationException($"Server not found: {serverId}");
            }

            return $"{server.Url.TrimEnd('/')}/Videos/{remoteItemId}/stream?api_key={Uri.EscapeDataString(server.ApiKey)}&Static=true";
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
                var url = BuildDirectStreamUrl(serverId, remoteItemId);
                _logger.LogInformation("[Federation] Proxying item {ItemId} from server {Server}", remoteItemId, server.Name);

                using var remoteReq = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(range))
                {
                    remoteReq.Headers.TryAddWithoutValidation("Range", range);
                }

                using var remoteResp = await ProxyHttpClient.SendAsync(
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
