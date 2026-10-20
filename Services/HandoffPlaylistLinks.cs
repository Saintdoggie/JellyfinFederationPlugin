using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Receiver side of the handoff: fixes the addresses in a friend's HLS
    /// playlists before the player sees them. The friend writes absolute
    /// addresses from what its own reverse proxy told it, which behind some
    /// proxies or tunnels is <c>http://</c>; a browser on an https page refuses
    /// those (mixed content), so playback failed with no response at all.
    /// <list type="bullet">
    /// <item>Every friend address is rebuilt on the address this server already
    /// uses for that friend (<see cref="RemoteServerClientFactory.EffectiveUrl"/>,
    /// https when the friend redirects to it).</item>
    /// <item>Nested playlists go through this server's
    /// <c>Plugins/Federation/HandoffPlaylist</c> relay, so the friend's addresses
    /// in them are fixed too. Segments go straight to the friend.</item>
    /// </list>
    /// Works with friends on any version that converts for us (0.0.187+).
    /// </summary>
    public static class HandoffPlaylistLinks
    {
        /// <summary>The friend's gateway path; everything before it is replaced.</summary>
        public const string GatewayPath = "/Plugins/Federation/Peer/Play/";

        /// <summary>This server's relay route for nested playlists.</summary>
        public const string RelayRoute = "/Plugins/Federation/HandoffPlaylist";

        private static readonly Regex UriAttribute = new("URI=\"(?<uri>[^\"]*)\"", RegexOptions.Compiled);

        /// <summary>
        /// Rewrites the friend addresses in <paramref name="playlist"/>:
        /// playlists to <paramref name="relayPrefix"/> + the escaped friend URL,
        /// everything else to the friend URL on <paramref name="peerBase"/>.
        /// Lines without a friend gateway address are left as they are.
        /// </summary>
        public static string Rewrite(string playlist, string peerBase, string relayPrefix)
        {
            var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith('#'))
                {
                    lines[i] = UriAttribute.Replace(line, m => "URI=\"" + Map(m.Groups["uri"].Value) + "\"");
                }
                else if (line.Trim().Length > 0)
                {
                    lines[i] = Map(line.Trim());
                }
            }

            return string.Join('\n', lines);

            string Map(string uri)
            {
                var at = uri.IndexOf(GatewayPath, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    return uri;
                }

                var friendUrl = peerBase.TrimEnd('/') + uri.Substring(at);
                var pathOnly = friendUrl.Split('?')[0];
                return pathOnly.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                    ? relayPrefix + Uri.EscapeDataString(friendUrl)
                    : friendUrl;
            }
        }

        /// <summary>
        /// The friend whose gateway <paramref name="url"/> addresses, or null when
        /// the relay must refuse it: only an enabled Jellyfin friend's
        /// <see cref="GatewayPath"/> on the exact address this server uses for it.
        /// Keeps the relay from fetching anything else.
        /// </summary>
        public static RemoteServer? FindRelayTarget(string? url, IEnumerable<RemoteServer> servers)
        {
            if (string.IsNullOrEmpty(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(target.UserInfo)
                || !target.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || target.AbsolutePath.Contains("/../", StringComparison.Ordinal))
            {
                return null;
            }

            return servers.FirstOrDefault(s =>
            {
                if (!s.Enabled || s.Kind != ServerKind.Jellyfin
                    || !Uri.TryCreate(RemoteServerClientFactory.EffectiveUrl(s).TrimEnd('/') + GatewayPath, UriKind.Absolute, out var gateway))
                {
                    return false;
                }

                return string.Equals(gateway.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(gateway.Authority, target.Authority, StringComparison.OrdinalIgnoreCase)
                    && target.AbsolutePath.StartsWith(gateway.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>The relay prefix for a request served under <paramref name="pathBase"/>.</summary>
        public static string RelayPrefix(string? pathBase) => (pathBase ?? string.Empty).TrimEnd('/') + RelayRoute + "?u=";

        /// <summary>Decodes a playlist body (UTF-8).</summary>
        public static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    }
}
