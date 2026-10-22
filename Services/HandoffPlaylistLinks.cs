using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

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

        /// <summary>How long a prefetched playlist waits for the player to ask for it.</summary>
        internal static readonly TimeSpan PrefetchKeep = TimeSpan.FromSeconds(30);

        private static readonly Regex UriAttribute = new("URI=\"(?<uri>[^\"]*)\"", RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<string, (DateTime FetchedUtc, string Text, string ContentType)> Prefetched = new(StringComparer.Ordinal);

        /// <summary>HTTP client for friend playlists (no redirects, no cookies).</summary>
        public static HttpClient Http { get; } = new(FederationHttp.Stamp(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            ConnectCallback = FederationHttp.ConnectAsync
        }))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// Rewrites the friend addresses in <paramref name="playlist"/>:
        /// playlists to <paramref name="relayPrefix"/> + the escaped friend URL,
        /// everything else to the friend URL on <paramref name="peerBase"/>.
        /// Lines without a friend gateway address are left as they are.
        /// </summary>
        public static string Rewrite(string playlist, string peerBase, string? relayPrefix)
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
                var friendUrl = ToPeer(uri, peerBase);
                if (friendUrl == null)
                {
                    return uri;
                }

                // Without a relay prefix (direct link: peerBase is this server's own
                // relay), nested playlists go to peerBase like everything else.
                return IsPlaylist(friendUrl) && relayPrefix != null ? relayPrefix + Uri.EscapeDataString(friendUrl) : friendUrl;
            }
        }

        /// <summary>A friend gateway address put on <paramref name="peerBase"/>, or null when it isn't one.</summary>
        public static string? ToPeer(string uri, string peerBase)
        {
            var at = uri.IndexOf(GatewayPath, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : peerBase.TrimEnd('/') + uri.Substring(at);
        }

        /// <summary>
        /// The first friend address in <paramref name="playlist"/>: the init
        /// segment (<c>EXT-X-MAP</c>) when there is one, else the first entry.
        /// </summary>
        public static string? FirstUri(string playlist, string peerBase)
        {
            string? first = null;
            foreach (var raw in playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
                {
                    var m = UriAttribute.Match(line);
                    if (m.Success)
                    {
                        return ToPeer(m.Groups["uri"].Value, peerBase);
                    }
                }
                else if (first == null && line.Length > 0 && !line.StartsWith('#'))
                {
                    first = ToPeer(line, peerBase);
                }
            }

            return first;
        }

        /// <summary>A playlist prefetched for <paramref name="url"/>, taken once, or null.</summary>
        public static (string Text, string ContentType)? TakePrefetched(string url)
        {
            if (Prefetched.TryRemove(url, out var hit) && DateTime.UtcNow - hit.FetchedUtc < PrefetchKeep)
            {
                return (hit.Text, hit.ContentType);
            }

            return null;
        }

        /// <summary>
        /// Right after the master playlist went to the player: fetches the
        /// variant playlist it will ask for next (kept for the relay), and when
        /// the friend shares repeated segment requests, asks for the first
        /// segment so the friend's conversion starts while the player is still
        /// on its way. The player's own request for it then joins that one. Saves
        /// the player's round trips to the friend at start. Never throws.
        /// </summary>
        public static void WarmUp(string masterFromFriend, string peerBase, bool startConversion, ILogger logger)
        {
            var variantUrl = FirstUri(masterFromFriend, peerBase);
            if (variantUrl == null || !IsPlaylist(variantUrl))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    foreach (var kv in Prefetched)
                    {
                        if (now - kv.Value.FetchedUtc > PrefetchKeep)
                        {
                            Prefetched.TryRemove(kv.Key, out _);
                        }
                    }

                    using var response = await Http.GetAsync(variantUrl).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return;
                    }

                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Prefetched[variantUrl] = (DateTime.UtcNow, text, response.Content.Headers.ContentType?.ToString() ?? "application/vnd.apple.mpegurl");

                    var firstSegment = startConversion ? FirstUri(text, peerBase) : null;
                    if (firstSegment == null || IsPlaylist(firstSegment))
                    {
                        return;
                    }

                    // Only the request has to arrive: the friend keeps converting
                    // and holds the segment for the player once it is asked.
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        using var started = await Http.GetAsync(firstSegment, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected while the friend is still converting it.
                    }

                    logger.LogDebug("[Federation] Started the friend's conversion ahead of the player");
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[Federation] Could not warm up a friend's playback");
                }
            });
        }

        private static bool IsPlaylist(string url) => url.Split('?')[0].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

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
