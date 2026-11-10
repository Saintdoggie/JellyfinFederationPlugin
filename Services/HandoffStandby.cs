using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// A second friend's server for a converted play. Two uses, both pure rules
    /// here and wired in <see cref="FederationRemoteTranscodeFilter"/>:
    /// <list type="bullet">
    /// <item>At start: when the friend picked for a play does not answer the
    /// master playlist within <see cref="StartCooldown"/> (or answers with an
    /// error), the play starts on the next friend holding the title.</item>
    /// <item>Mid-play: the other friend's copy is listed in the master playlist
    /// as a redundant variant (same attributes, another address). A player that
    /// cannot get segments from the first server moves to the other one at a
    /// segment boundary, playing on from its buffer. The standby converts
    /// nothing, and reserves no bandwidth, until a player asks it.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The mid-play variant is only offered when both servers produce the same
    /// timeline: the video is being converted (a copied stream is cut at the
    /// file's own keyframes), MPEG-TS segments, both copies the same length, the
    /// same sound language available, and no subtitle track selected (track
    /// numbers differ between files). Falling back to converting on the
    /// receiving server is never part of this.
    /// </remarks>
    internal static class HandoffStandby
    {
        /// <summary>How long the first friend gets to answer a play's master playlist when another friend could.</summary>
        internal static readonly TimeSpan StartCooldown = TimeSpan.FromSeconds(8);

        /// <summary>Two copies longer apart than this are different cuts.</summary>
        internal static readonly long MaxRuntimeDifferenceTicks = TimeSpan.TicksPerSecond;

        private const string StreamInfTag = "#EXT-X-STREAM-INF:";

        /// <summary>
        /// Whether this server moves plays between friends on its own: with
        /// automatic copy choice on, and always on a server that may not convert
        /// (there a failed friend otherwise means no video at all).
        /// </summary>
        internal static bool Wanted(PluginConfiguration? config)
            => config != null
                && config.UseFriendsTranscoders
                && (config.SmartSourceSelection || !TranscodePolicy.MayConvertForOwnViewers(config));

        /// <summary>True when both runtimes are known and within a second of each other.</summary>
        internal static bool SameCut(FederatedSource a, FederatedSource b)
            => a.RunTimeTicks is > 0 and var x
                && b.RunTimeTicks is > 0 and var y
                && Math.Abs(x - y) <= MaxRuntimeDifferenceTicks;

        /// <summary>
        /// Whether the request can be answered by another copy at all: no subtitle
        /// track selected by number.
        /// </summary>
        internal static bool AllowsAnotherCopy(IEnumerable<KeyValuePair<string, StringValues>> query)
            => RemoteMediaPath.QueryLong(query, "SubtitleStreamIndex") is not >= 0;

        /// <summary>
        /// Whether another server's segments can continue this request's timeline:
        /// converted video in MPEG-TS, and <see cref="AllowsAnotherCopy"/>.
        /// </summary>
        internal static bool AllowsMidPlaySwitch(IEnumerable<KeyValuePair<string, StringValues>> query, long? sourceBitrate)
        {
            var list = query as IReadOnlyCollection<KeyValuePair<string, StringValues>> ?? query.ToList();
            var container = RemoteMediaPath.QueryValue(list, "SegmentContainer");
            return AllowsAnotherCopy(list)
                && (string.IsNullOrEmpty(container) || string.Equals(container, "ts", StringComparison.OrdinalIgnoreCase))
                && HlsQualityLadder.VideoIsConverted(list, sourceBitrate);
        }

        /// <summary>
        /// The sound track on <paramref name="to"/> that matches what the request
        /// plays from <paramref name="from"/>. <c>Ok</c> is false when no track
        /// in the same language can be named; <c>Index</c> is null when the
        /// request names none and both copies start in the same language.
        /// </summary>
        internal static (bool Ok, int? Index) MapAudio(FederatedSource from, FederatedSource to, long? requestedIndex)
        {
            var fromAudio = Audio(from);
            var toAudio = Audio(to);
            if (fromAudio.Count == 0 || toAudio.Count == 0)
            {
                // Nothing known about the tracks: only a silent-on-both title is safe.
                return (from.MediaStreams != null && to.MediaStreams != null && fromAudio.Count == 0 && toAudio.Count == 0 && requestedIndex is not >= 0, null);
            }

            var playing = requestedIndex is >= 0
                ? fromAudio.FirstOrDefault(a => a.Index == requestedIndex)
                : fromAudio.FirstOrDefault(a => a.IsDefault) ?? fromAudio[0];
            if (playing == null)
            {
                return (false, null);
            }

            MediaStream? match;
            if (string.IsNullOrWhiteSpace(playing.Language))
            {
                // Unlabelled sound: only the one-track case is certain.
                match = fromAudio.Count == 1 && toAudio.Count == 1 ? toAudio[0] : null;
            }
            else
            {
                var same = toAudio.Where(a => !string.IsNullOrWhiteSpace(a.Language) && DevicePlayFit.SameLanguage(a.Language!, playing.Language!)).ToList();
                match = same.FirstOrDefault(a => a.Channels == playing.Channels) ?? same.FirstOrDefault();
            }

            if (match == null)
            {
                return (false, null);
            }

            if (requestedIndex is not >= 0)
            {
                // The friend picks its own default track; that must be the match.
                var friendDefault = toAudio.FirstOrDefault(a => a.IsDefault) ?? toAudio[0];
                return (ReferenceEquals(friendDefault, match)
                    || (!string.IsNullOrWhiteSpace(friendDefault.Language) && !string.IsNullOrWhiteSpace(playing.Language) && DevicePlayFit.SameLanguage(friendDefault.Language!, playing.Language!)), null);
            }

            return (true, match.Index);
        }

        /// <summary>
        /// Whether the standby's request asks for less video than the first
        /// server's (its link is capped below this play): such a server is not a like-for-like
        /// standby and is not offered.
        /// </summary>
        internal static bool IsReduced(string primaryUrl, string standbyUrl)
        {
            var original = QueryOf(primaryUrl);
            var standby = QueryOf(standbyUrl);
            foreach (var key in new[] { "VideoBitrate", "MaxStreamingBitrate", "MaxWidth", "MaxHeight" })
            {
                var was = RemoteMediaPath.QueryLong(original, key);
                var now = RemoteMediaPath.QueryLong(standby, key);
                if ((was is > 0 && now is > 0 && now < was) || (was is not > 0 && now is > 0))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<KeyValuePair<string, StringValues>> QueryOf(string url)
        {
            var at = url.IndexOf('?', StringComparison.Ordinal);
            return RemoteMediaPath.ParseQuery(at < 0 ? string.Empty : url.Substring(at));
        }

        /// <summary>
        /// Returns <paramref name="master"/> with its first variant repeated at
        /// <paramref name="standbyUri"/> (an HLS redundant stream), or the same
        /// string when there is no variant or the address is already listed.
        /// </summary>
        internal static string AddVariant(string master, string standbyUri)
        {
            if (string.IsNullOrEmpty(master) || string.IsNullOrWhiteSpace(standbyUri)
                || standbyUri.IndexOfAny(new[] { '\r', '\n' }) >= 0
                || master.Contains(standbyUri, StringComparison.Ordinal))
            {
                return master;
            }

            var lines = master.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
            var infIndex = lines.FindIndex(l => l.StartsWith(StreamInfTag, StringComparison.Ordinal));
            if (infIndex < 0)
            {
                return master;
            }

            var uriIndex = infIndex + 1;
            while (uriIndex < lines.Count && (lines[uriIndex].Trim().Length == 0 || lines[uriIndex].StartsWith('#')))
            {
                uriIndex++;
            }

            if (uriIndex >= lines.Count)
            {
                return master;
            }

            var end = lines.Count;
            while (end > 0 && lines[end - 1].Trim().Length == 0)
            {
                end--;
            }

            lines.InsertRange(end, new[] { lines[infIndex], standbyUri });
            return string.Join('\n', lines);
        }

        /// <summary>The play session id inside a friend gateway URL's query, or null.</summary>
        internal static string? PlaySessionOf(string? url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            var value = RemoteMediaPath.QueryValue(QueryOf(url), "PlaySessionId");
            return string.IsNullOrWhiteSpace(value) || value.Length > 128 ? null : value;
        }

        private static List<MediaStream> Audio(FederatedSource source)
            => (source.MediaStreams ?? Array.Empty<MediaStream>()).Where(s => s.Type == MediaStreamType.Audio).ToList();
    }
}
