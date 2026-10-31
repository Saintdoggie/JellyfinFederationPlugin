using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Companion's public Funnel root, distinct from the <c>/plex/{peerId}</c>
    /// media URL stored on a Plex friend. Pool invites and version checks have
    /// to hit Companion itself, not Plex.
    /// </summary>
    public static class PlexCompanionEndpoint
    {
        /// <summary>
        /// Companion's Funnel/public base URL, or null when this friend is a
        /// direct Plex Relay/Remote Access entry with no Companion listener.
        /// </summary>
        public static string? TryGetBaseUrl(RemoteServer? friend)
        {
            if (friend == null || friend.Kind != ServerKind.Plex)
            {
                return null;
            }

            if (TryNormalize(friend.CompanionUrl, out var stored))
            {
                return stored;
            }

            if (!Uri.TryCreate(friend.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return null;
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.StartsWith("/plex/", StringComparison.OrdinalIgnoreCase)
                || ((string.IsNullOrEmpty(path) || path == "/")
                    && uri.Host.IndexOf("plex.direct", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }

            return null;
        }

        /// <summary>
        /// True when a pool invite can be delivered to Companion rather than
        /// a Jellyfin <c>/Plugins/Federation</c> route.
        /// </summary>
        public static bool CanReceivePoolInvite(RemoteServer? friend)
            => TryGetBaseUrl(friend) != null;

        public static string PoolInviteUrl(string companionBase)
            => companionBase.TrimEnd('/') + "/api/pools/invite";

        public static string InfoUrl(string companionBase)
            => companionBase.TrimEnd('/') + "/api/federation/info";

        /// <summary>
        /// Reads Companion's advertised Federation plugin version, or null when
        /// Companion is unreachable or older than this endpoint.
        /// </summary>
        public static async Task<string?> TryGetVersionAsync(RemoteServer friend, CancellationToken cancellationToken)
        {
            var baseUrl = TryGetBaseUrl(friend);
            if (baseUrl == null)
            {
                return string.IsNullOrWhiteSpace(friend.FederationPluginVersion) ? null : friend.FederationPluginVersion;
            }

            try
            {
                using var response = await SharedHttp.GetAsync(InfoUrl(baseUrl), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("federationPluginVersion", out var version)
                    || doc.RootElement.TryGetProperty("FederationPluginVersion", out version))
                {
                    var text = version.GetString();
                    return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                }
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

        private static bool TryNormalize(string? url, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return false;
            }

            normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return true;
        }
    }
}
