using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Adds lower quality levels to the master playlist of a converted title, so a
    /// player (hls.js, ExoPlayer, AVPlayer) steps down instead of buffering when the
    /// connection slows, and back up when it recovers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jellyfin 12.4's own extra variants are too close together to help (20/18/16
    /// Mbps), are disabled for requests from loopback, and all write to the same
    /// files: its output path is a hash of media path, User-Agent, device and play
    /// session, without the bitrate, so a running conversion keeps serving its old
    /// quality under the new variant's address.
    /// </para>
    /// <para>
    /// Each level added here carries <see cref="RungParameter"/>. The server that
    /// converts stamps that level into the User-Agent of the request Jellyfin sees
    /// (<see cref="RungUserAgent"/>), which gives every level its own output files.
    /// The play session stays the same, so Jellyfin stops the previous level's
    /// conversion when another starts: one conversion per play, started only when a
    /// player asks for that level, and stop/pause/limits keep working unchanged.
    /// </para>
    /// <para>
    /// Nothing is added when the video is not being converted (a copied stream has
    /// segment boundaries no other level can match), when no video bitrate was
    /// requested, or when the playlist already has several variants. The first level
    /// is always the playlist's own, untouched, so a play starts exactly as before.
    /// </para>
    /// </remarks>
    internal static class HlsQualityLadder
    {
        /// <summary>Query parameter marking a lower level (1 = first step down).</summary>
        internal const string RungParameter = "FedRung";

        /// <summary>Highest level number accepted from a request.</summary>
        internal const int MaxRung = 6;

        /// <summary>No level goes below this video bitrate (about 1.2 Mbps with audio).</summary>
        internal const long FloorVideoBitrate = 1_000_000;

        /// <summary>Lower levels as a share of the negotiated video bitrate.</summary>
        internal static readonly double[] Steps = { 0.55, 0.30, 0.15 };

        private const string StreamInfTag = "#EXT-X-STREAM-INF:";

        private static readonly Regex Bandwidth = new("(?<=[:,])BANDWIDTH=(?<v>\\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        private static readonly Regex AverageBandwidth = new("(?<=[:,])AVERAGE-BANDWIDTH=(?<v>\\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        private static readonly Regex Resolution = new("(?<=[:,])RESOLUTION=(?<w>\\d+)x(?<h>\\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

        /// <summary>One lower level: its video bitrate and, when smaller than the top, its height.</summary>
        internal sealed record Rung(int Number, long VideoBitrate, int? MaxHeight);

        /// <summary>
        /// The lower levels for a conversion at <paramref name="topVideoBitrate"/>:
        /// about 55%, 30% and 15% of it, never below the floor, never two levels
        /// close together, and with the picture scaled down to suit the bitrate.
        /// </summary>
        /// <param name="topVideoBitrate">The negotiated video bitrate in bits per second.</param>
        /// <param name="topHeight">The top level's picture height, or 0 when unknown.</param>
        internal static IReadOnlyList<Rung> Plan(long topVideoBitrate, int topHeight)
        {
            var rungs = new List<Rung>(Steps.Length);
            var previous = topVideoBitrate;
            foreach (var step in Steps)
            {
                var bitrate = Math.Max(FloorVideoBitrate, (long)(topVideoBitrate * step) / 1000 * 1000);
                if (bitrate > previous * 0.75)
                {
                    // Too close to the level above to be worth a separate conversion.
                    continue;
                }

                var suited = bitrate >= 8_000_000 ? 2160 : bitrate >= 4_000_000 ? 1080 : bitrate >= 1_800_000 ? 720 : 480;
                int? height = topHeight > 0
                    ? (suited < topHeight ? suited : null)
                    : (suited < 2160 ? suited : null);
                rungs.Add(new Rung(rungs.Count + 1, bitrate, height));
                previous = bitrate;
            }

            return rungs;
        }

        /// <summary>
        /// Returns <paramref name="master"/> with lower levels added after its single
        /// variant, or the same string when the playlist does not qualify.
        /// </summary>
        /// <param name="master">A master playlist as Jellyfin (or a friend's gateway) wrote it.</param>
        /// <param name="sourceVideoBitrate">
        /// The original file's bitrate when known. A file bigger than the requested
        /// bitrate cannot be copied, so its video is certainly being converted.
        /// </param>
        internal static string Expand(string master, long? sourceVideoBitrate = null)
        {
            if (string.IsNullOrEmpty(master) || !master.Contains(StreamInfTag, StringComparison.Ordinal))
            {
                return master;
            }

            var lines = master.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
            var infIndex = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith(StreamInfTag, StringComparison.Ordinal))
                {
                    if (infIndex >= 0)
                    {
                        // Already a ladder (Jellyfin's own, or ours): leave it alone.
                        return master;
                    }

                    infIndex = i;
                }
            }

            var uriIndex = infIndex + 1;
            while (uriIndex < lines.Count && (lines[uriIndex].Trim().Length == 0 || lines[uriIndex].StartsWith('#')))
            {
                uriIndex++;
            }

            if (infIndex < 0 || uriIndex >= lines.Count)
            {
                return master;
            }

            var inf = lines[infIndex];
            var uri = lines[uriIndex].Trim();
            var queryAt = uri.IndexOf('?', StringComparison.Ordinal);
            if (queryAt < 0)
            {
                return master;
            }

            var parameters = SplitQuery(uri.Substring(queryAt + 1));
            if (Value(parameters, RungParameter) != null
                || !long.TryParse(Value(parameters, "VideoBitrate"), NumberStyles.None, CultureInfo.InvariantCulture, out var topVideo)
                || topVideo <= FloorVideoBitrate
                || !VideoIsConverted(parameters, topVideo, sourceVideoBitrate))
            {
                return master;
            }

            var resolution = Resolution.Match(inf);
            var topWidth = resolution.Success ? int.Parse(resolution.Groups["w"].Value, CultureInfo.InvariantCulture) : 0;
            var topHeight = resolution.Success ? int.Parse(resolution.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
            var requestedHeight = int.TryParse(Value(parameters, "MaxHeight"), NumberStyles.None, CultureInfo.InvariantCulture, out var maxHeight) ? maxHeight : 0;
            if (topHeight == 0)
            {
                topHeight = requestedHeight;
            }

            var bandwidth = Bandwidth.Match(inf);
            var topBandwidth = bandwidth.Success && long.TryParse(bandwidth.Groups["v"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : 0;
            var audio = topBandwidth > topVideo
                ? topBandwidth - topVideo
                : long.TryParse(Value(parameters, "AudioBitrate"), NumberStyles.None, CultureInfo.InvariantCulture, out var a) && a > 0 ? a : 192_000;

            var rungs = Plan(topVideo, topHeight);
            if (rungs.Count == 0)
            {
                return master;
            }

            var added = new List<string>(rungs.Count * 2);
            foreach (var rung in rungs)
            {
                var total = (rung.VideoBitrate + audio).ToString(CultureInfo.InvariantCulture);
                var rungInf = Bandwidth.Replace(inf, "BANDWIDTH=" + total, 1);
                rungInf = AverageBandwidth.Replace(rungInf, "AVERAGE-BANDWIDTH=" + total, 1);

                var rungParameters = parameters.Select(p => p).ToList();
                Set(rungParameters, "VideoBitrate", rung.VideoBitrate.ToString(CultureInfo.InvariantCulture));
                Set(rungParameters, "AllowVideoStreamCopy", "false");
                if (rung.MaxHeight is { } height)
                {
                    var width = height >= 2160 ? 3840 : height >= 1080 ? 1920 : height >= 720 ? 1280 : 854;
                    if (requestedHeight <= 0 || height < requestedHeight)
                    {
                        Set(rungParameters, "MaxHeight", height.ToString(CultureInfo.InvariantCulture));
                    }

                    if (!int.TryParse(Value(parameters, "MaxWidth"), NumberStyles.None, CultureInfo.InvariantCulture, out var requestedWidth) || requestedWidth <= 0 || width < requestedWidth)
                    {
                        Set(rungParameters, "MaxWidth", width.ToString(CultureInfo.InvariantCulture));
                    }

                    if (topWidth > 0 && topHeight > 0)
                    {
                        var scaledWidth = (int)Math.Round(topWidth * (double)height / topHeight / 2.0, MidpointRounding.AwayFromZero) * 2;
                        rungInf = Resolution.Replace(rungInf, "RESOLUTION=" + scaledWidth.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture), 1);
                    }
                }

                Set(rungParameters, RungParameter, rung.Number.ToString(CultureInfo.InvariantCulture));
                added.Add(rungInf);
                added.Add(uri.Substring(0, queryAt + 1) + string.Join('&', rungParameters.Select(p => p.Value == null ? p.Key : p.Key + "=" + p.Value)));
            }

            lines.InsertRange(uriIndex + 1, added);
            return string.Join('\n', lines);
        }

        /// <summary>The level a request is for (1..<see cref="MaxRung"/>), or null for the top level or a bad value.</summary>
        internal static int? RungOf(IEnumerable<KeyValuePair<string, StringValues>> query)
        {
            var raw = RemoteMediaPath.QueryValue(query, RungParameter);
            return raw != null && raw.Length <= 2
                && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var rung)
                && rung >= 1 && rung <= MaxRung
                    ? rung
                    : null;
        }

        /// <summary>
        /// The User-Agent Jellyfin must see for a level. Jellyfin hashes it into the
        /// conversion's output path, so each level gets its own files.
        /// </summary>
        internal static string RungUserAgent(string? original, int rung)
        {
            var marker = "FederationRung/" + rung.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(original))
            {
                return marker;
            }

            var at = original.IndexOf(" FederationRung/", StringComparison.Ordinal);
            var baseAgent = at >= 0 ? original.Substring(0, at) : original;
            return baseAgent + " " + marker;
        }

        /// <summary>
        /// Stamps the level into the request's User-Agent before Jellyfin reads it.
        /// Does nothing for the top level, so ordinary plays are untouched.
        /// </summary>
        internal static void ApplyRungUserAgent(HttpRequest request)
        {
            if (RungOf(request.Query) is { } rung)
            {
                request.Headers.UserAgent = RungUserAgent(request.Headers.UserAgent.ToString(), rung);
            }
        }

        /// <summary>
        /// The same test on a parsed request query: whether the video is certainly
        /// being converted (false when no video bitrate was requested).
        /// </summary>
        internal static bool VideoIsConverted(IEnumerable<KeyValuePair<string, StringValues>> query, long? sourceVideoBitrate)
        {
            var parameters = query
                .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value.FirstOrDefault()))
                .ToList();
            return long.TryParse(Value(parameters, "VideoBitrate"), NumberStyles.None, CultureInfo.InvariantCulture, out var requested)
                && requested > 0
                && VideoIsConverted(parameters, requested, sourceVideoBitrate);
        }

        private static bool VideoIsConverted(List<KeyValuePair<string, string?>> parameters, long requestedVideoBitrate, long? sourceVideoBitrate)
        {
            var codec = Value(parameters, "VideoCodec");
            if (codec != null && Uri.UnescapeDataString(codec).Split(',')[0].Trim().Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(Value(parameters, "AllowVideoStreamCopy"), "false", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (sourceVideoBitrate is > 0 && sourceVideoBitrate.Value > requestedVideoBitrate)
            {
                return true;
            }

            var reasons = Value(parameters, "TranscodeReasons");
            if (string.IsNullOrEmpty(reasons))
            {
                return false;
            }

            return Uri.UnescapeDataString(reasons).Split(',').Any(reason =>
            {
                var r = reason.Trim();
                return r.StartsWith("Video", StringComparison.OrdinalIgnoreCase)
                    || r.Equals("ContainerBitrateExceedsLimit", StringComparison.OrdinalIgnoreCase)
                    || r.Equals("SubtitleCodecNotSupported", StringComparison.OrdinalIgnoreCase)
                    || r.Equals("RefFramesNotSupported", StringComparison.OrdinalIgnoreCase)
                    || r.Equals("AnamorphicVideoNotSupported", StringComparison.OrdinalIgnoreCase)
                    || r.Equals("InterlacedVideoNotSupported", StringComparison.OrdinalIgnoreCase);
            });
        }

        // Raw (still encoded) pairs: untouched parameters keep their exact text.
        private static List<KeyValuePair<string, string?>> SplitQuery(string query)
        {
            var pairs = new List<KeyValuePair<string, string?>>();
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                pairs.Add(eq < 0 ? new(part, null) : new(part.Substring(0, eq), part.Substring(eq + 1)));
            }

            return pairs;
        }

        private static string? Value(List<KeyValuePair<string, string?>> parameters, string key)
        {
            foreach (var pair in parameters)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private static void Set(List<KeyValuePair<string, string?>> parameters, string key, string value)
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                if (string.Equals(parameters[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    parameters[i] = new(parameters[i].Key, value);
                    return;
                }
            }

            parameters.Add(new(key, value));
        }
    }
}
