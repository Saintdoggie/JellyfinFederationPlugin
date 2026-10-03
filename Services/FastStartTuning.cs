using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Bounds how long ffmpeg analyses a federated stream before it starts.
    /// </summary>
    /// <remarks>
    /// Jellyfin launches ffmpeg with a server-wide <c>-analyzeduration 200M
    /// -probesize 1G</c> unless the media source carries its own
    /// <see cref="MediaSourceInfo.AnalyzeDurationMs"/>. Those defaults suit local files,
    /// but a federated source is read over the internet: any stream whose parameters
    /// ffmpeg cannot settle early (the PGS subtitle tracks in a Blu-ray remux have no
    /// size until their first picture, often minutes in) keeps it reading until it has
    /// pulled up to a gigabyte before the first output byte. Measured against a ~48 Mbps
    /// MKV with a PGS track first appearing at 80 s over a 100 Mbps link, the default
    /// probe read 486 MB in 39 s; a 3 s window read 20 MB in 1.7 s.
    /// This only changes how much input is read for analysis. Nothing about the
    /// encoded or remuxed output - resolution, bitrate, codecs - depends on it.
    /// </remarks>
    public static class FastStartTuning
    {
        /// <summary>The shortest analysis window handed to ffmpeg.</summary>
        public const int MinAnalyzeDurationMs = 4000;

        /// <summary>The longest analysis window handed to ffmpeg.</summary>
        public const int MaxAnalyzeDurationMs = 8000;

        /// <summary>Used when the source bitrate is not known.</summary>
        public const int DefaultAnalyzeDurationMs = 5000;

        /// <summary>Roughly how many input bytes the analysis window should cost.</summary>
        public const long TargetProbeBytes = 40_000_000;

        /// <summary>
        /// Chooses an analysis window that costs about <see cref="TargetProbeBytes"/>
        /// of input: long enough for low-bitrate sources to show every stream, short
        /// enough that a very high-bitrate remux does not read hundreds of megabytes.
        /// </summary>
        /// <param name="bitrate">Total source bitrate in bits per second, if known.</param>
        /// <returns>The window in milliseconds.</returns>
        public static int ComputeAnalyzeDurationMs(long? bitrate)
        {
            if (bitrate is not > 0)
            {
                return DefaultAnalyzeDurationMs;
            }

            var ms = TargetProbeBytes * 8L * 1000L / bitrate.Value;
            return (int)Math.Clamp(ms, MinAnalyzeDurationMs, MaxAnalyzeDurationMs);
        }

        /// <summary>
        /// Sets the analysis window on every network-delivered source of a federated
        /// item. Sources that already carry a window, local files, and items that are
        /// not federated are left exactly as they were.
        /// </summary>
        /// <param name="item">The item the sources belong to.</param>
        /// <param name="sources">The sources Jellyfin is about to use or return.</param>
        /// <returns>The number of sources changed.</returns>
        public static int Apply(BaseItem? item, IEnumerable<MediaSourceInfo>? sources)
        {
            if (sources == null || Plugin.Instance?.Configuration?.FastStartProbing == false)
            {
                return 0;
            }

            if (FederationLibraryManager.GetFederationKey(item) == null)
            {
                return 0;
            }

            var changed = 0;
            foreach (var source in sources)
            {
                if (source == null
                    || source.Protocol != MediaProtocol.Http
                    || source.AnalyzeDurationMs is > 0)
                {
                    continue;
                }

                source.AnalyzeDurationMs = ComputeAnalyzeDurationMs(SourceBitrate(source));
                changed++;
            }

            return changed;
        }

        private static long? SourceBitrate(MediaSourceInfo source)
        {
            if (source.Bitrate is > 0)
            {
                return source.Bitrate;
            }

            var streamTotal = source.MediaStreams?
                .Where(s => s.BitRate.HasValue)
                .Sum(s => (long)s.BitRate!.Value) ?? 0;
            return streamTotal > 0 ? streamTotal : null;
        }
    }
}
