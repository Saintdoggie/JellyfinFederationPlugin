using System;
using System.Net;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// Helpers for serving a friend's video to viewers on this home's network
    /// over the direct link: the player fetches from this server's relay route
    /// (<c>Plugins/Federation/DirectLink/{serverId}/Plugins/Federation/Peer/Play/...</c>),
    /// which gets each piece from the friend over the link.
    /// </summary>
    public static class DirectLinkRelay
    {
        /// <summary>The route prefix of the relay (after the server's path base).</summary>
        public const string RoutePrefix = "/Plugins/Federation/DirectLink/";

        /// <summary>The friend gateway path that follows the server id.</summary>
        public const string GatewayPath = "/Plugins/Federation/Peer/Play/";

        private const string FederationPath = "/Plugins/Federation/";

        // X-Forwarded-For/Forwarded are left in place only by a proxy Jellyfin
        // doesn't trust (a trusted one's are consumed); the CDN headers mean the
        // viewer came in from the internet (Cloudflare tunnel and the like).
        private static readonly string[] ProxyHeaders = { "X-Forwarded-For", "Forwarded", "CF-Connecting-IP", "True-Client-IP", "X-Real-IP", "X-Client-IP" };

        /// <summary>The base the player gets for a friend (playlists are rewritten onto it).</summary>
        public static string PlayerBase(string? pathBase, string serverId)
            => (pathBase ?? string.Empty).TrimEnd('/') + RoutePrefix + Uri.EscapeDataString(serverId);

        /// <summary>
        /// The address of a friend URL on the direct link: from
        /// <c>/Plugins/Federation/</c> on, under the link client's base (the
        /// friend puts its own path base in front).
        /// </summary>
        public static Uri ToLinkUri(string friendUrl)
        {
            var uri = new Uri(friendUrl);
            var pathAndQuery = uri.PathAndQuery;
            var at = pathAndQuery.IndexOf(FederationPath, StringComparison.OrdinalIgnoreCase);
            return new Uri("http://federation-direct-link" + (at < 0 ? pathAndQuery : pathAndQuery.Substring(at)));
        }

        /// <summary>
        /// Whether the player is on this home's network, by Jellyfin's own
        /// local-network rules. A request that still carries a forwarding header came
        /// through a proxy Jellyfin doesn't trust, so its real origin is unknown and
        /// it doesn't count: relaying for a remote viewer would use the home upload.
        /// </summary>
        public static bool IsHomeViewer(HttpContext context, INetworkManager? network)
        {
            var ip = context.Connection.RemoteIpAddress;
            if (network == null || ip == null)
            {
                return false;
            }

            foreach (var header in ProxyHeaders)
            {
                if (context.Request.Headers.ContainsKey(header))
                {
                    return false;
                }
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            return network.IsInLocalNetwork(ip);
        }

        /// <summary>True for a playlist answer (rewritten so nested addresses stay on the relay).</summary>
        public static bool IsPlaylist(string path, string? contentType)
            => path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || (contentType != null && contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase));
    }
}
