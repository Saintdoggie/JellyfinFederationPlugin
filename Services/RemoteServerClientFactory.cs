using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Shared factory for <see cref="RemoteServerClient"/> instances. Reuses HttpClients per server
    /// so sockets are shared across requests.
    /// </summary>
    public interface IRemoteServerClientFactory
    {
        /// <summary>
        /// Gets or creates a client for the given server configuration.
        /// </summary>
        RemoteServerClient GetClient(RemoteServer server);

        /// <summary>
        /// Gets or creates a client for the given server ID (must be configured).
        /// </summary>
        RemoteServerClient? GetClient(string serverId);

        /// <summary>
        /// Drops the cached HttpClient for the given server (call on config change).
        /// </summary>
        void Invalidate(string serverId);

        /// <summary>
        /// Drops all cached clients.
        /// </summary>
        void InvalidateAll();

        /// <summary>
        /// Learns whether an http peer permanently redirects to https (see
        /// <see cref="RemoteServerClientFactory.EffectiveUrl"/>). Never throws.
        /// </summary>
        Task<bool> ProbeHttpsUpgradeAsync(RemoteServer server, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default implementation of <see cref="IRemoteServerClientFactory"/>.
    /// </summary>
    public class RemoteServerClientFactory : IRemoteServerClientFactory, IDisposable
    {
        private static readonly TimeSpan HttpsRecheckInterval = TimeSpan.FromHours(24);

        // Keyed by server id; From is the configured URL the check was made for,
        // so editing a server's URL discards its old result.
        private static readonly ConcurrentDictionary<string, (string From, string? To, DateTime CheckedUtc)> HttpsUpgrades = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient RedirectProbeClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });

        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteServerClientFactory"/> class.
        /// </summary>
        public RemoteServerClientFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        /// <inheritdoc />
        public RemoteServerClient GetClient(RemoteServer server)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            var httpClient = _httpClients.GetOrAdd(server.Id, _ => CreateHttpClient(server));
            return new RemoteServerClient(server, _loggerFactory.CreateLogger<RemoteServerClient>(), httpClient);
        }

        /// <inheritdoc />
        public RemoteServerClient? GetClient(string serverId)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.Find(s => s.Id == serverId);
            return server == null ? null : GetClient(server);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The old client is retired, not disposed: a sync, stream or download
        /// still using it would otherwise fail on the spot - saving any setting
        /// during a sync aborted it with "Cannot access a disposed object". New
        /// requests get a fresh client; the retired one's pooled connections close
        /// after the handler's idle timeout and it is then garbage collected.
        /// </remarks>
        public void Invalidate(string serverId)
        {
            _httpClients.TryRemove(serverId, out _);
        }

        /// <inheritdoc />
        public void InvalidateAll()
        {
            _httpClients.Clear();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var kvp in _httpClients)
            {
                kvp.Value.Dispose();
            }

            _httpClients.Clear();
        }

        /// <summary>
        /// The base URL to actually talk to a peer on: its configured URL, or the
        /// https form of it once <see cref="ProbeHttpsUpgradeAsync"/> has seen the
        /// peer permanently redirect http to https. The configured URL itself is
        /// never rewritten - friend and pool bookkeeping compare it as a string.
        /// </summary>
        public static string EffectiveUrl(RemoteServer server)
        {
            var configured = server.Url.TrimEnd('/');
            return server.Id != null
                && HttpsUpgrades.TryGetValue(server.Id, out var upgrade)
                && string.Equals(upgrade.From, configured, StringComparison.OrdinalIgnoreCase)
                && upgrade.To != null
                    ? upgrade.To
                    : configured;
        }

        /// <summary>
        /// Checks once a day whether an http peer permanently redirects to https on
        /// the same host. Such a peer answers every http request with a
        /// <c>Connection: close</c> redirect (an extra connection and round trip on
        /// every call), and an http Direct-mode media URL is blocked as mixed content
        /// by a browser viewing this server over https. Never throws.
        /// </summary>
        /// <returns>True when the effective URL changed.</returns>
        public async Task<bool> ProbeHttpsUpgradeAsync(RemoteServer server, CancellationToken cancellationToken)
        {
            var configured = server.Url?.TrimEnd('/');
            if (string.IsNullOrEmpty(configured)
                || server.Id == null
                || server.Kind != ServerKind.Jellyfin
                || !Uri.TryCreate(configured, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttp
                || (HttpsUpgrades.TryGetValue(server.Id, out var known)
                    && string.Equals(known.From, configured, StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - known.CheckedUtc < HttpsRecheckInterval))
            {
                return false;
            }

            string? upgraded = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await RedirectProbeClient.GetAsync($"{configured}/System/Info/Public", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                upgraded = HttpsUpgradeTarget(uri, response.StatusCode, response.Headers.Location);
            }
            catch (Exception ex)
            {
                _loggerFactory.CreateLogger<RemoteServerClientFactory>().LogDebug(ex, "[Federation] https check for {ServerName} failed", server.Name);
                return false;
            }

            var previous = EffectiveUrl(server);
            HttpsUpgrades[server.Id] = (configured, upgraded, DateTime.UtcNow);
            if (string.Equals(previous, EffectiveUrl(server), StringComparison.Ordinal))
            {
                return false;
            }

            if (upgraded != null)
            {
                _loggerFactory.CreateLogger<RemoteServerClientFactory>().LogInformation(
                    "[Federation] {ServerName} permanently redirects to https; using {Url} for requests and Direct streams",
                    server.Name,
                    upgraded);
            }

            Invalidate(server.Id);
            return true;
        }

        /// <summary>
        /// The https base URL a response upgrades to, or null unless it is a
        /// permanent redirect to https on the same host and the same path.
        /// </summary>
        internal static string? HttpsUpgradeTarget(Uri requested, System.Net.HttpStatusCode status, Uri? location)
        {
            if ((status != System.Net.HttpStatusCode.MovedPermanently && status != System.Net.HttpStatusCode.PermanentRedirect)
                || location == null)
            {
                return null;
            }

            var target = location.IsAbsoluteUri ? location : new Uri(requested, location);
            if (target.Scheme != Uri.UriSchemeHttps
                || !string.Equals(target.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
                || !target.AbsolutePath.EndsWith("/System/Info/Public", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var basePath = target.AbsolutePath.Substring(0, target.AbsolutePath.Length - "/System/Info/Public".Length);
            var requestedBase = requested.AbsolutePath.EndsWith("/System/Info/Public", StringComparison.OrdinalIgnoreCase)
                ? requested.AbsolutePath.Substring(0, requested.AbsolutePath.Length - "/System/Info/Public".Length)
                : requested.AbsolutePath;
            if (!string.Equals(basePath.TrimEnd('/'), requestedBase.TrimEnd('/'), StringComparison.Ordinal))
            {
                return null;
            }

            return target.GetLeftPart(UriPartial.Authority) + basePath.TrimEnd('/');
        }

        /// <summary>Test seam: forgets every learned upgrade.</summary>
        internal static void ResetHttpsUpgradesForTests() => HttpsUpgrades.Clear();

        /// <summary>Test seam: records an upgrade without a network call.</summary>
        internal static void SeedHttpsUpgradeForTests(RemoteServer server, string? upgradedUrl)
            => HttpsUpgrades[server.Id] = (server.Url.TrimEnd('/'), upgradedUrl, DateTime.UtcNow);

        private static HttpClient CreateHttpClient(RemoteServer server)
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri(EffectiveUrl(server)),
                Timeout = TimeSpan.FromMinutes(5)
            };

            // server.ApiKey is a scoped federation token, not a real Jellyfin API
            // key - see FederationTokenAuth. Must match RemoteServerClient's own
            // CreateDefaultHttpClient exactly: this factory is the path actually
            // used in production (RemoteServerClient's single-arg constructor is
            // a fallback/test seam), so a mismatch here would silently send every
            // real request with the wrong header while only directly-constructed
            // RemoteServerClients worked.
            client.DefaultRequestHeaders.Add(FederationTokenAuth.Header, server.ApiKey);
            return client;
        }
    }
}
