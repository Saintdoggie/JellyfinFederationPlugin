using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Rewrites the URIs inside an HLS playlist Jellyfin generated, so a player
    /// that never talked to Jellyfin directly can follow them.
    /// </summary>
    /// <remarks>
    /// Jellyfin writes relative URIs (<c>main.m3u8?...</c>, <c>hls1/main/0.ts?...</c>,
    /// <c>URI="hls1/main/-1.mp4?..."</c>) and copies the request's query into them.
    /// Every URI is resolved against the native request path, checked against
    /// <see cref="RemoteMediaPath"/>, and handed to <c>map</c> to become an absolute
    /// gateway URL with its query filtered. A URI that resolves outside the
    /// allow-list (or isn't parseable) is replaced by nothing rather than passed
    /// through: a player then fails that one rendition instead of ever being sent
    /// to an endpoint the gateway doesn't serve.
    /// </remarks>
    public static class HlsPlaylistRewriter
    {
        private static readonly Regex UriAttribute = new(
            "URI=\"(?<uri>[^\"]*)\"",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromMilliseconds(200));

        /// <summary>
        /// Rewrites <paramref name="playlist"/>, fetched from
        /// <paramref name="nativeRequestPath"/> (e.g. <c>/videos/{id}/master.m3u8</c>).
        /// </summary>
        /// <param name="playlist">The playlist text.</param>
        /// <param name="nativeRequestPath">Path the playlist was served from, used to resolve relative URIs.</param>
        /// <param name="map">Turns an allowed native media path and its raw query into the URI to emit; null drops the URI.</param>
        public static string Rewrite(string playlist, string nativeRequestPath, Func<RemoteMediaPath, string, string?> map)
        {
            var baseUri = new Uri(new Uri("http://native.invalid/"), nativeRequestPath.TrimStart('/'));
            var sb = new StringBuilder(playlist.Length + 256);
            var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                {
                    // keep blank lines as they are
                }
                else if (line.StartsWith('#'))
                {
                    line = UriAttribute.Replace(line, m => "URI=\"" + (Map(m.Groups["uri"].Value) ?? string.Empty) + "\"");
                }
                else
                {
                    line = Map(line.Trim()) ?? "#";
                }

                sb.Append(line);
                if (i < lines.Length - 1)
                {
                    sb.Append('\n');
                }
            }

            return sb.ToString();

            string? Map(string uri)
            {
                if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(baseUri, uri, out var resolved))
                {
                    return null;
                }

                if (!string.Equals(resolved.Host, "native.invalid", StringComparison.OrdinalIgnoreCase))
                {
                    // An absolute URI to somewhere else: never emitted by
                    // Jellyfin, never forwarded by the gateway.
                    return null;
                }

                return RemoteMediaPath.TryParse(Uri.UnescapeDataString(resolved.AbsolutePath), out var path) && path != null
                    ? map(path, resolved.Query)
                    : null;
            }
        }
    }
}
