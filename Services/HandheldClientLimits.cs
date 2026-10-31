using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Conversion ceilings for phone and tablet apps (Jellyfin for Android, Litefin,
    /// Findroid, Swiftfin on iPhone/iPad, Streamyfin).
    /// </summary>
    /// <remarks>
    /// These apps send no resolution limit and a very high bitrate limit, so the
    /// only ceiling a handed-off conversion had was the friend link's WAN cap. A 4K
    /// HDR title then became a 4K, 30+ Mbps conversion, which a friend's server
    /// often makes slower than real time; the app's player (ExoPlayer, AVPlayer) gives
    /// up before the first segment and shows "Source error". A handheld screen gains
    /// nothing from more than 1080p, so the conversion is held to 1080p and about
    /// 12 Mbps. TV apps and the web client are left alone.
    /// </remarks>
    internal static class HandheldClientLimits
    {
        /// <summary>Total (video plus audio) cap in Mbps, before the 95% usable share.</summary>
        internal const int CapMbps = 13;

        internal const int MaxWidth = 1920;

        internal const int MaxHeight = 1080;

        private static readonly string[] AppMarkers = { "android", "litefin", "findroid", "swiftfin", "streamyfin", "ios" };

        private static readonly string[] TvDeviceMarkers = { "appletv", "androidtv", "firetv", "smarttv", "shield", "chromecast", "googletv" };

        /// <summary>Whether the app is a phone/tablet app, from the client and device names it signed in with.</summary>
        internal static bool IsHandheld(string? client, string? device)
        {
            if (string.IsNullOrWhiteSpace(client))
            {
                return false;
            }

            var app = client.Trim().ToLowerInvariant();
            if (!Array.Exists(AppMarkers, m => app.Contains(m, StringComparison.Ordinal)) || app.Contains("tv", StringComparison.Ordinal))
            {
                return false;
            }

            var box = (device ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            return !Array.Exists(TvDeviceMarkers, m => box.Contains(m, StringComparison.Ordinal));
        }

        /// <summary>The cap to apply: the lower of the link's cap (if any) and the handheld ceiling.</summary>
        internal static int? Cap(int? linkCapMbps, bool handheld)
            => !handheld ? linkCapMbps : linkCapMbps is > 0 ? Math.Min(linkCapMbps.Value, CapMbps) : CapMbps;

        /// <summary>
        /// Adds a 1080p limit to a forwarded query string (empty or starting with '?')
        /// unless the app asked for its own width or height.
        /// </summary>
        internal static string WithResolution(string query, IEnumerable<KeyValuePair<string, StringValues>> requested, bool handheld)
        {
            if (!handheld
                || RemoteMediaPath.QueryValue(requested, "MaxWidth") != null
                || RemoteMediaPath.QueryValue(requested, "MaxHeight") != null)
            {
                return query;
            }

            var separator = query.Length == 0 ? '?' : '&';
            return string.Create(CultureInfo.InvariantCulture, $"{query}{separator}MaxWidth={MaxWidth}&MaxHeight={MaxHeight}");
        }
    }
}
