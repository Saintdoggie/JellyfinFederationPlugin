using System;
using System.Globalization;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// What the loading overlay tells a viewer about a slow start.
    /// </summary>
    /// <param name="Seconds">Projected seconds until playback starts, or null when it cannot be estimated.</param>
    /// <param name="Reason">One plain sentence explaining the wait.</param>
    /// <param name="LinkMbps">The measured download speed from the source, if known.</param>
    /// <param name="BitrateMbps">The file's bitrate, if known.</param>
    /// <param name="ServerName">The friend's server the title comes from.</param>
    public sealed record StartEstimate(int? Seconds, string Reason, double? LinkMbps, double? BitrateMbps, string ServerName);

    /// <summary>
    /// Projects how long a federated title takes to start from its bitrate and the
    /// measured speed to the server it comes from.
    /// </summary>
    /// <remarks>
    /// Jellyfin serves the first HLS segment only once the following one exists, so a
    /// start needs about two 3-second segments of the source file to arrive. Measured on
    /// a live server: 93 Mbps over an ~8 Mbps link predicted 74 s and took 75-85 s.
    /// </remarks>
    public static class StartEstimator
    {
        /// <summary>Seconds of source video that must arrive before playback can begin.</summary>
        public const double SourceSecondsNeeded = 6.0;

        /// <summary>Time for everything that is not data transfer (token, transcoder start-up).</summary>
        public const double FixedOverheadSeconds = 4.0;

        /// <summary>Projected waits at or below this are not worth explaining.</summary>
        public const int QuickSeconds = 12;

        /// <summary>
        /// Builds the estimate for one title.
        /// </summary>
        /// <param name="serverName">Display name of the source server.</param>
        /// <param name="bitrateBps">The source file's bitrate in bits per second, if known.</param>
        /// <param name="linkMbps">The measured speed from the source server in Mbps, if known.</param>
        /// <returns>The projection and a one-sentence reason.</returns>
        public static StartEstimate Estimate(string serverName, long? bitrateBps, double? linkMbps)
        {
            var name = string.IsNullOrWhiteSpace(serverName) ? "the source server" : serverName;
            double? bitrateMbps = bitrateBps is > 0 ? bitrateBps.Value / 1_000_000.0 : null;
            double? link = linkMbps is > 0 ? linkMbps : null;

            if (bitrateMbps == null)
            {
                return new StartEstimate(null, $"Getting the first part of the video from {name}.", link, null, name);
            }

            if (link == null)
            {
                return new StartEstimate(
                    null,
                    $"Getting the first part of this {Format(bitrateMbps.Value)} Mbps file from {name}; its connection speed has not been measured yet.",
                    null,
                    bitrateMbps,
                    name);
            }

            var transferSeconds = SourceSecondsNeeded * bitrateMbps.Value / link.Value;
            var total = (int)Math.Clamp(Math.Round(transferSeconds + FixedOverheadSeconds), 3, 900);

            // A projection above QuickSeconds implies the file's bitrate exceeds the link
            // (transfer time is 6 s x bitrate / link, so > 8 s means bitrate > link).
            var reason = total <= QuickSeconds
                ? $"Starting up - {name} is responding quickly."
                : $"This file is {Format(bitrateMbps.Value)} Mbps but the connection to {name} runs at about {Format(link.Value)} Mbps, so the first few seconds of video take about {Math.Round(transferSeconds)} seconds to arrive.";

            return new StartEstimate(total, reason, link, bitrateMbps, name);
        }

        private static string Format(double value)
            => value >= 10 ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
    }
}
