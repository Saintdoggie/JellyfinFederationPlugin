using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>What kind of screen is asking. Unknown is a real answer, not a guess.</summary>
    internal enum ViewerClientKind
    {
        Unknown = 0,
        Web = 1,
        Handheld = 2,
        Tv = 3
    }

    /// <summary>
    /// What is known about the device that will play the title. Every field may be
    /// null: capabilities are never invented, only used when reported or observed.
    /// </summary>
    /// <param name="Kind">Phone/tablet, TV, browser, or unknown.</param>
    /// <param name="MaxHeight">Tallest picture the screen can show, in pixels.</param>
    /// <param name="MaxBitrateMbps">The most the device asked for or can take.</param>
    /// <param name="SupportsHdr">Whether the screen shows HDR; null when unknown.</param>
    internal sealed record ViewerDevice(ViewerClientKind Kind, int? MaxHeight = null, double? MaxBitrateMbps = null, bool? SupportsHdr = null)
    {
        internal static readonly ViewerDevice Unknown = new(ViewerClientKind.Unknown);
    }

    /// <summary>One copy of a title on one server, with everything measured about reaching it.</summary>
    internal sealed record SourceCandidate
    {
        /// <summary>Stable identity of this exact copy (server + remote item + remote source).</summary>
        public required string Key { get; init; }

        public required string ServerId { get; init; }

        public string ServerName { get; init; } = string.Empty;

        /// <summary>The file's total bitrate in bits per second; 0 when unknown.</summary>
        public long BitrateBps { get; init; }

        /// <summary>Picture height in pixels; 0 when unknown.</summary>
        public int Height { get; init; }

        public bool Hdr { get; init; }

        public int AudioChannels { get; init; }

        /// <summary>The admin's friend priority; lower is preferred.</summary>
        public int Priority { get; init; }

        /// <summary>Measured speed from the server holding the file, in Mbps.</summary>
        public double? LinkMbps { get; init; }

        /// <summary>Speed this viewer's device measured on its own route to that server, in Mbps.</summary>
        public double? RouteMbps { get; init; }

        /// <summary>The configured or automatic ceiling for this friend's link, in Mbps.</summary>
        public int? CapMbps { get; init; }

        /// <summary>Mbps already promised to other plays from the same server.</summary>
        public double ReservedMbps { get; init; }

        /// <summary>0..1 share of recent probes that succeeded; null when unknown.</summary>
        public double? Reliability { get; init; }

        public bool RecentlyStalled { get; init; }

        /// <summary>Somebody is allowed to convert this copy for the viewer.</summary>
        public bool ConversionAvailable { get; init; }

        /// <summary>
        /// The server holding the file converts it, so only the smaller result crosses
        /// the link. False means the original crosses the link and is converted here.
        /// </summary>
        public bool ConvertsAtSource { get; init; }
    }

    /// <summary>How one candidate scored, with the numbers a person can check.</summary>
    internal sealed record SourceScore(
        SourceCandidate Candidate,
        double Total,
        double Start,
        double Sustain,
        double Quality,
        bool NeedsConversion,
        bool WillStruggle,
        long DeliveredBitrateBps,
        int DeliveredHeight,
        double? UsableMbps,
        double? ProjectedStartSeconds);

    /// <summary>The ranked list plus one plain sentence saying why the first one won.</summary>
    internal sealed record SourceSelection(IReadOnlyList<SourceScore> Ranked, bool KeptCurrent, string Reason)
    {
        public SourceScore? Chosen => Ranked.Count > 0 ? Ranked[0] : null;
    }

    /// <summary>
    /// Picks which copy of a title to play: start quickly and reliably first, keep
    /// playing without buffering second, and only then the best picture - and only
    /// as much picture as the device can show and the connection can carry.
    /// </summary>
    /// <remarks>
    /// Pure: no clock, no network, no configuration. Unknown measurements score as
    /// neutral rather than good or bad, so an unmeasured copy is never promoted on a
    /// guess and never demoted for lack of data.
    /// </remarks>
    internal static class SourceSelectionScorer
    {
        /// <summary>Share of a measured link that a steady stream may use.</summary>
        internal const double UsableShare = 0.85;

        /// <summary>Score given when nothing is known, matching the existing source ranking's default.</summary>
        internal const double NeutralScore = 0.85;

        /// <summary>Scores this close count as equal; the admin's friend priority then decides.</summary>
        internal const double TieMargin = 2.0;

        /// <summary>A challenger must beat the copy already playing by this much before the choice changes.</summary>
        internal const double SwitchMargin = 4.0;

        /// <summary>Extra seconds a conversion needs before its first segment exists.</summary>
        internal const double ConversionStartSeconds = 3.0;

        private const double StartWeight = 40;
        private const double SustainWeight = 35;
        private const double QualityWeight = 25;

        /// <summary>
        /// Ranks the candidates for one viewer.
        /// </summary>
        /// <param name="candidates">Copies that are authorised, online and passed their live check.</param>
        /// <param name="device">What is known about the viewer's device.</param>
        /// <param name="currentKey">The copy chosen last time for this viewer and title, if any.</param>
        /// <param name="mayLeaveCurrent">False while the current copy is inside its minimum dwell.</param>
        /// <returns>Best first.</returns>
        internal static SourceSelection Rank(IReadOnlyList<SourceCandidate> candidates, ViewerDevice? device, string? currentKey = null, bool mayLeaveCurrent = true)
        {
            device ??= ViewerDevice.Unknown;
            if (candidates.Count == 0)
            {
                return new SourceSelection(Array.Empty<SourceScore>(), false, string.Empty);
            }

            var scored = candidates.Select(c => Score(c, device)).ToList();
            var best = scored.Max(s => s.Total);

            // Within the tie margin of the best, the admin's priority decides, then
            // the score; everything else follows in score order.
            var ranked = scored
                .OrderBy(s => best - s.Total <= TieMargin ? 0 : 1)
                .ThenBy(s => best - s.Total <= TieMargin ? s.Candidate.Priority : 0)
                .ThenByDescending(s => s.Total)
                .ThenBy(s => s.Candidate.Priority)
                .ThenBy(s => s.Candidate.Key, StringComparer.Ordinal)
                .ToList();

            var kept = false;
            var current = currentKey == null ? null : ranked.FirstOrDefault(s => string.Equals(s.Candidate.Key, currentKey, StringComparison.Ordinal));
            if (current != null && !ReferenceEquals(current, ranked[0])
                && (!mayLeaveCurrent || ranked[0].Total - current.Total < SwitchMargin))
            {
                ranked.Remove(current);
                ranked.Insert(0, current);
                kept = true;
            }

            return new SourceSelection(ranked, kept, Explain(ranked, device, kept));
        }

        /// <summary>Scores one copy for one device.</summary>
        internal static SourceScore Score(SourceCandidate c, ViewerDevice device)
        {
            var usable = UsableMbps(c, device);
            var bitrateMbps = c.BitrateBps > 0 ? c.BitrateBps / 1_000_000.0 : (double?)null;
            var linkOnly = LinkMbps(c);

            // The copy has to shrink when it is known to be bigger than what the
            // connection or the device can take.
            var needsConversion = bitrateMbps.HasValue && usable.HasValue && bitrateMbps.Value > usable.Value;
            var converted = needsConversion && c.ConversionAvailable;

            var deliveredMbps = converted ? usable!.Value : bitrateMbps;
            var deliveredHeight = DeliveredHeight(c.Height, converted ? deliveredMbps : null);

            // What actually crosses the link from the server holding the file.
            var crossingMbps = converted && c.ConvertsAtSource ? deliveredMbps : bitrateMbps;

            double sustain;
            var struggle = false;
            if (!crossingMbps.HasValue || !linkOnly.HasValue)
            {
                sustain = needsConversion && !c.ConversionAvailable ? Math.Clamp(usable!.Value / bitrateMbps!.Value, 0, 1) : NeutralScore;
                struggle = needsConversion && !c.ConversionAvailable;
            }
            else
            {
                var room = linkOnly.Value * UsableShare;
                sustain = Math.Clamp(room / crossingMbps.Value, 0, 1);
                struggle = sustain < 1;
                if (needsConversion && !c.ConversionAvailable)
                {
                    // Over the device's or the cap's limit with nobody to shrink it.
                    sustain = Math.Min(sustain, Math.Clamp(usable!.Value / bitrateMbps!.Value, 0, 1));
                    struggle = true;
                }
            }

            if (c.RecentlyStalled)
            {
                sustain *= 0.6;
            }

            var reliability = c.Reliability is >= 0 and <= 1 ? c.Reliability.Value : NeutralScore;

            double? startSeconds = null;
            if (crossingMbps.HasValue && linkOnly.HasValue)
            {
                startSeconds = StartEstimator.ProjectedSeconds((long)(crossingMbps.Value * 1_000_000), linkOnly.Value);
                if (converted)
                {
                    startSeconds += ConversionStartSeconds;
                }
            }

            var start = StartScore(startSeconds) * (0.5 + (0.5 * reliability));
            var quality = QualityScore(c, device, deliveredMbps, deliveredHeight, converted);
            var total = Math.Round((StartWeight * start) + (SustainWeight * sustain) + (QualityWeight * quality), 3);

            return new SourceScore(
                c,
                total,
                Math.Round(start, 4),
                Math.Round(sustain, 4),
                Math.Round(quality, 4),
                needsConversion,
                struggle,
                deliveredMbps.HasValue ? (long)(deliveredMbps.Value * 1_000_000) : 0,
                deliveredHeight,
                usable,
                startSeconds);
        }

        /// <summary>
        /// The steady rate this viewer can receive from this server: the slowest of the
        /// measured link, the device's own measured route and the configured ceilings,
        /// less what is already promised to other plays. Null when nothing is known.
        /// </summary>
        internal static double? UsableMbps(SourceCandidate c, ViewerDevice device)
        {
            double? limit = null;
            var link = LinkMbps(c);
            if (link.HasValue)
            {
                limit = Math.Max(0.5, (link.Value - Math.Max(0, c.ReservedMbps)) * UsableShare);
            }

            if (c.CapMbps is > 0)
            {
                limit = limit.HasValue ? Math.Min(limit.Value, c.CapMbps.Value) : c.CapMbps.Value;
            }

            if (device.MaxBitrateMbps is > 0)
            {
                limit = limit.HasValue ? Math.Min(limit.Value, device.MaxBitrateMbps.Value) : device.MaxBitrateMbps.Value;
            }

            return limit;
        }

        private static double? LinkMbps(SourceCandidate c)
        {
            var link = c.LinkMbps is > 0 ? c.LinkMbps : null;
            var route = c.RouteMbps is > 0 ? c.RouteMbps : null;
            return link.HasValue && route.HasValue ? Math.Min(link.Value, route.Value) : link ?? route;
        }

        /// <summary>1 for a quick start, sliding to 0.5 at the start budget and to 0 at two minutes.</summary>
        internal static double StartScore(double? seconds)
        {
            if (!seconds.HasValue)
            {
                return NeutralScore;
            }

            var s = seconds.Value;
            if (s <= StartEstimator.QuickSeconds)
            {
                return 1;
            }

            if (s <= StartEstimator.StartBudgetSeconds)
            {
                return 1 - (0.5 * (s - StartEstimator.QuickSeconds) / (StartEstimator.StartBudgetSeconds - StartEstimator.QuickSeconds));
            }

            return Math.Max(0, 0.5 * (1 - ((s - StartEstimator.StartBudgetSeconds) / (120.0 - StartEstimator.StartBudgetSeconds))));
        }

        /// <summary>The picture height a conversion to this bitrate reasonably produces.</summary>
        internal static int HeightForBitrate(double mbps)
            => mbps < 1.5 ? 480 : mbps < 3.5 ? 720 : mbps < 10 ? 1080 : mbps < 18 ? 1440 : 2160;

        private static int DeliveredHeight(int sourceHeight, double? convertedMbps)
        {
            var height = sourceHeight;
            if (convertedMbps.HasValue)
            {
                var target = HeightForBitrate(convertedMbps.Value);
                height = height > 0 ? Math.Min(height, target) : target;
            }

            return height;
        }

        private static double QualityScore(SourceCandidate c, ViewerDevice device, double? deliveredMbps, int deliveredHeight, bool converted)
        {
            if (deliveredHeight <= 0 && !deliveredMbps.HasValue)
            {
                // Nothing known about the picture: below any described copy that fits.
                return 0.45;
            }

            // Picture the viewer can actually see: no reward above the screen's height.
            var screen = device.MaxHeight is > 0 ? device.MaxHeight.Value : 2160;
            var seen = deliveredHeight > 0 ? Math.Min(deliveredHeight, screen) : Math.Min(HeightForBitrate(deliveredMbps ?? 4), screen);
            var resolution = Math.Clamp(Math.Log2(seen / 360.0) / Math.Log2(Math.Min(screen, 2160) / 360.0), 0, 1);
            if (screen <= 360)
            {
                resolution = 1;
            }

            // Enough bits for that picture; more than plenty earns nothing extra.
            var plenty = seen >= 2160 ? 25.0 : seen >= 1440 ? 14.0 : seen >= 1080 ? 8.0 : seen >= 720 ? 4.0 : 2.0;
            var bits = deliveredMbps.HasValue ? Math.Clamp(Math.Sqrt(deliveredMbps.Value / plenty), 0.3, 1) : 0.7;

            var score = (0.6 * resolution) + (0.4 * bits);

            // HDR helps only on a screen known to show it and only when it survives.
            if (c.Hdr && !converted && device.SupportsHdr == true)
            {
                score += 0.06;
            }
            else if (c.Hdr && device.SupportsHdr == false && !converted)
            {
                // An HDR file on an SDR screen needs tone mapping somewhere.
                score -= 0.06;
            }

            if (c.AudioChannels > 2 && device.Kind != ViewerClientKind.Handheld)
            {
                score += 0.02;
            }

            if (converted)
            {
                // A conversion costs a generation of quality and somebody's hardware;
                // bigger originals cost more to convert for the same result.
                score -= 0.15 + (c.Height >= 2160 ? 0.03 : 0);
            }

            return Math.Clamp(score, 0, 1);
        }

        private static string Explain(IReadOnlyList<SourceScore> ranked, ViewerDevice device, bool kept)
        {
            var chosen = ranked[0];
            var name = string.IsNullOrWhiteSpace(chosen.Candidate.ServerName) ? "this server" : chosen.Candidate.ServerName;
            var what = Describe(chosen.Candidate);
            if (ranked.Count == 1)
            {
                return $"{Sentence(what)} from {name} is the only copy available.";
            }

            if (kept)
            {
                return $"Staying with {what} from {name}; no other copy is clearly better right now.";
            }

            // The most informative comparison: the best-looking copy that lost.
            var rival = ranked.Skip(1)
                .OrderByDescending(s => s.Candidate.Height)
                .ThenByDescending(s => s.Candidate.BitrateBps)
                .First();
            var rivalName = string.IsNullOrWhiteSpace(rival.Candidate.ServerName) ? "another server" : rival.Candidate.ServerName;
            var rivalWhat = Describe(rival.Candidate);
            var rivalBetterPicture = rival.Candidate.Height > chosen.Candidate.Height
                || (rival.Candidate.Height == chosen.Candidate.Height && rival.Candidate.BitrateBps > chosen.Candidate.BitrateBps);

            if (rivalBetterPicture && rival.Candidate.BitrateBps > 0 && rival.UsableMbps.HasValue
                && rival.Candidate.BitrateBps / 1_000_000.0 > rival.UsableMbps.Value)
            {
                return $"{Sentence(rivalWhat)} on {rivalName} needs {Mbps(rival.Candidate.BitrateBps / 1_000_000.0)} Mbps but only about {Mbps(rival.UsableMbps.Value)} Mbps is usable, so {what} from {name} plays instead.";
            }

            if (rivalBetterPicture && device.MaxHeight is > 0 && rival.Candidate.Height > device.MaxHeight.Value)
            {
                return $"This screen shows up to {device.MaxHeight.Value}p, so {what} from {name} plays instead of {rivalWhat} on {rivalName}.";
            }

            if (rival.ProjectedStartSeconds is > StartEstimator.StartBudgetSeconds
                && chosen.ProjectedStartSeconds is { } quick && quick < rival.ProjectedStartSeconds.Value)
            {
                return $"{Sentence(what)} from {name} starts in about {Math.Round(quick)} s; {rivalWhat} on {rivalName} would take about {Math.Round(rival.ProjectedStartSeconds.Value)} s.";
            }

            if (chosen.Total - rival.Total <= TieMargin)
            {
                return $"{Sentence(what)} from {name} and {rivalWhat} on {rivalName} are equally good; {name} is preferred by friend priority.";
            }

            return $"{Sentence(what)} from {name} is the best copy this device and connection can play smoothly.";
        }

        internal static string Describe(SourceCandidate c)
        {
            var parts = new List<string>(3);
            if (c.Height > 0)
            {
                parts.Add(c.Height >= 2000 ? "4K" : c.Height.ToString(CultureInfo.InvariantCulture) + "p");
            }

            if (c.Hdr)
            {
                parts.Add("HDR");
            }

            if (c.BitrateBps > 0)
            {
                parts.Add(Mbps(c.BitrateBps / 1_000_000.0) + " Mbps");
            }

            return parts.Count == 0 ? "the copy" : "the " + string.Join(" ", parts) + " copy";
        }

        private static string Sentence(string text)
            => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        private static string Mbps(double value)
            => value >= 10 ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
    }
}
