using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Turns what the plugin already knows about a copy and its server into the
    /// plain numbers <see cref="SourceSelectionScorer"/> compares. No network calls.
    /// </summary>
    internal static class SourceCandidateFactory
    {
        /// <summary>Identity of one copy, shared by every place that ranks sources.</summary>
        internal static string Key(FederatedSource source)
            => source.ServerId + ":" + source.RemoteItemId.ToString("N");

        /// <summary>Describes a copy from the catalog's cached data.</summary>
        internal static SourceCandidate FromCached(FederatedSource source, RemoteServer server, WanBandwidthMonitor? monitor, PluginConfiguration? config)
            => Build(
                Key(source),
                server,
                source.Priority,
                source.Bitrate is > 0 ? source.Bitrate.Value : SumBitrate(source.MediaStreams),
                source.MediaStreams,
                monitor,
                config);

        /// <summary>Describes a copy from the friend's live answer about the file.</summary>
        internal static SourceCandidate FromLive(string key, RemoteServer server, int priority, long bitrateBps, IReadOnlyList<MediaStream>? streams, WanBandwidthMonitor? monitor, PluginConfiguration? config)
            => Build(key, server, priority, bitrateBps > 0 ? bitrateBps : SumBitrate(streams), streams, monitor, config);

        /// <summary>
        /// What the client and device names say about the screen. Only phone and
        /// tablet apps get a height (they gain nothing above 1080p); everything else
        /// stays unknown rather than guessed.
        /// </summary>
        internal static ViewerDevice Device(string? client, string? device, bool browser)
        {
            if (HandheldClientLimits.IsHandheld(client, device))
            {
                return new ViewerDevice(ViewerClientKind.Handheld, HandheldClientLimits.MaxHeight);
            }

            var names = ((client ?? string.Empty) + " " + (device ?? string.Empty)).ToLowerInvariant();
            if (names.Contains("tv", StringComparison.Ordinal) || names.Contains("shield", StringComparison.Ordinal)
                || names.Contains("chromecast", StringComparison.Ordinal) || names.Contains("roku", StringComparison.Ordinal)
                || names.Contains("xbox", StringComparison.Ordinal) || names.Contains("kodi", StringComparison.Ordinal))
            {
                return new ViewerDevice(ViewerClientKind.Tv);
            }

            return browser || names.Contains("web", StringComparison.Ordinal)
                ? new ViewerDevice(ViewerClientKind.Web)
                : ViewerDevice.Unknown;
        }

        /// <summary>
        /// One sentence for Playback Info saying why the copy being played was chosen,
        /// or null when the smart choice is off or the title has only one usable copy.
        /// Names servers and formats only - never addresses, ids or tokens.
        /// </summary>
        internal static string? ExplainPlayed(
            FederatedCacheEntry entry,
            FederatedSource played,
            Func<string, RemoteServer?> serverById,
            Func<string, bool> isOffline,
            WanBandwidthMonitor? monitor,
            PluginConfiguration? config,
            ViewerDevice device)
        {
            if (config?.SmartSourceSelection != true)
            {
                return null;
            }

            var candidates = new List<SourceCandidate>();
            foreach (var source in entry.GetSourcesSnapshot())
            {
                var server = serverById(source.ServerId);
                if (server is not { Enabled: true } || isOffline(source.ServerId))
                {
                    continue;
                }

                var candidate = FromCached(source, server, monitor, config);
                if (candidates.TrueForAll(c => !string.Equals(c.Key, candidate.Key, StringComparison.Ordinal)))
                {
                    candidates.Add(candidate);
                }
            }

            var playedKey = Key(played);
            var mine = candidates.Find(c => string.Equals(c.Key, playedKey, StringComparison.Ordinal));
            if (candidates.Count < 2 || mine == null)
            {
                return null;
            }

            var best = SourceSelectionScorer.Rank(candidates, device);
            if (string.Equals(best.Chosen!.Candidate.Key, playedKey, StringComparison.Ordinal))
            {
                return best.Reason;
            }

            // The default copy is chosen once for everyone; this device may suit another.
            var kept = SourceSelectionScorer.Rank(candidates, device, playedKey);
            if (kept.KeptCurrent)
            {
                return kept.Reason;
            }

            var top = best.Chosen.Candidate;
            var topName = string.IsNullOrWhiteSpace(top.ServerName) ? "another server" : top.ServerName;
            return $"Playing {SourceSelectionScorer.Describe(mine)} from {mine.ServerName}. For this device and connection {SourceSelectionScorer.Describe(top)} on {topName} ranks higher; it can be picked under versions.";
        }

        private static SourceCandidate Build(string key, RemoteServer server, int priority, long bitrateBps, IReadOnlyList<MediaStream>? streams, WanBandwidthMonitor? monitor, PluginConfiguration? config)
        {
            var video = streams?.Where(s => s.Type == MediaStreamType.Video).OrderByDescending(s => s.Height ?? 0).FirstOrDefault();
            var peerDirect = server is { StreamingMode: StreamingMode.Direct, Kind: ServerKind.Jellyfin };
            return new SourceCandidate
            {
                Key = key,
                ServerId = server.Id,
                ServerName = server.Name ?? string.Empty,
                BitrateBps = Math.Max(0, bitrateBps),
                Height = video?.Height ?? 0,
                Hdr = video != null && video.VideoRangeType is not (VideoRangeType.SDR or VideoRangeType.Unknown),
                AudioChannels = streams?.Where(s => s.Type == MediaStreamType.Audio).Select(s => s.Channels ?? 0).DefaultIfEmpty(0).Max() ?? 0,
                Priority = priority,
                LinkMbps = monitor?.GetMeasuredLinkMbps(server.Id),
                CapMbps = monitor?.GetEffectiveCapMbps(server),

                // A Direct-mode Jellyfin friend shrinks its own file (handoff, or the
                // capped DirectStream gateway), so only the result crosses the link.
                // Anything else is converted here from the original - when allowed.
                ConvertsAtSource = peerDirect,
                ConversionAvailable = peerDirect || TranscodePolicy.MayConvertForOwnViewers(config)
            };
        }

        private static long SumBitrate(IEnumerable<MediaStream>? streams)
            => streams?.Where(s => s.BitRate is > 0).Sum(s => (long)s.BitRate!.Value) ?? 0;
    }
}
