using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Copy play: the server that holds a file repackages it into a streaming
    /// playlist without re-encoding picture or sound, for a player that cannot
    /// take the plain file (another wrapper, or an app whose player only reads
    /// playlists). It is direct play in another wrapper, not a conversion, so a
    /// server that converts nothing still does it. See <c>DIRECT-PLAY.md</c>.
    /// </summary>
    internal static class CopyPlay
    {
        // Jellyfin's reasons that mean "the file is fine, only the way it is
        // handed over is not". Every other reason needs an encoder.
        private static readonly HashSet<string> WrapperOnlyReasons = new(StringComparer.OrdinalIgnoreCase)
        {
            "ContainerNotSupported", "DirectPlayError"
        };

        // Limits that only make sense when encoding; left in, Jellyfin would
        // read them as a wish to shrink the picture.
        private static readonly HashSet<string> EncodingOnlyKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "VideoBitRate", "MaxWidth", "MaxHeight", "Width", "Height", "MaxFramerate", "Framerate",
            "MaxVideoBitDepth", "Level", "Profile", "AudioBitRate", "MaxAudioChannels", "TranscodingMaxAudioChannels",
            "AudioChannels", "AudioSampleRate", "MaxAudioBitDepth", "RequireAvc", "DeInterlace", "RequireNonAnamorphic",
            "EnableAudioVbrEncoding", "AlwaysBurnInSubtitleWhenTranscoding"
        };

        /// <summary>
        /// Whether a playlist request, as the viewer's own Jellyfin built it for
        /// the device, needs no re-encoding: it can be answered by copying the
        /// file's picture and sound. Decided from Jellyfin's own
        /// <c>TranscodeReasons</c>, which it worked out against the exact version
        /// and track being played.
        /// </summary>
        /// <param name="query">The player's request.</param>
        /// <param name="sourceBitrate">The file's bitrate, when known.</param>
        /// <param name="capMbps">The bitrate ceiling towards that server (0 or less: none).</param>
        /// <param name="streams">The file's tracks, when known.</param>
        internal static bool Fits(IEnumerable<KeyValuePair<string, StringValues>> query, long? sourceBitrate, double capMbps, IEnumerable<MediaStream>? streams = null)
        {
            var reasons = RemoteMediaPath.QueryValue(query, "TranscodeReasons");
            if (string.IsNullOrWhiteSpace(reasons))
            {
                // A request Jellyfin did not explain (a player that builds its own
                // address): nothing says copying is enough.
                return false;
            }

            foreach (var reason in reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (WrapperOnlyReasons.Contains(reason))
                {
                    continue;
                }

                // Jellyfin also says "sound not supported" when its own track
                // preference left it nothing to compare (a file whose only track
                // is not flagged as the default), though the player takes that
                // sound as it is. Believed only when the tracks themselves agree.
                if (!Is(reason, "AudioCodecNotSupported") || !EverySoundTrackCopies(query, streams))
                {
                    return false;
                }
            }

            if (IsFalse(RemoteMediaPath.QueryValue(query, "AllowVideoStreamCopy"))
                || IsFalse(RemoteMediaPath.QueryValue(query, "AllowAudioStreamCopy")))
            {
                return false;
            }

            if (BurnsInSubtitle(query))
            {
                return false;
            }

            // Too big for the connection: somebody has to shrink it.
            return capMbps <= 0 || sourceBitrate is not > 0 || sourceBitrate.Value <= capMbps * 1_000_000;
        }

        /// <summary>
        /// The owner's own check before it serves a copy play. The links inside
        /// a playlist it already rewrote name picture and sound as copy; those
        /// pass as they are. Anything else must fit like on the receiver.
        /// </summary>
        /// <param name="query">The friend's request.</param>
        /// <param name="streams">The file's tracks, when known.</param>
        internal static bool OwnerAccepts(IEnumerable<KeyValuePair<string, StringValues>> query, IEnumerable<MediaStream>? streams)
            => (Is(RemoteMediaPath.QueryValue(query, "VideoCodec") ?? string.Empty, "copy")
                    && Is(RemoteMediaPath.QueryValue(query, "AudioCodec") ?? string.Empty, "copy"))
                || Fits(query, null, 0, streams);

        /// <summary>
        /// The owner's rewrite: the same request, unable to start an encoder.
        /// Picture and sound are named as copy, encoding limits are dropped, and
        /// a subtitle that would be burned into the picture is left out.
        /// </summary>
        /// <param name="query">The friend's request.</param>
        internal static string ForceCopy(IEnumerable<KeyValuePair<string, StringValues>> query)
        {
            var pairs = query as IReadOnlyCollection<KeyValuePair<string, StringValues>> ?? query.ToList();
            var burnIn = BurnsInSubtitle(pairs);
            var filtered = RemoteMediaPath.FilterQuery(pairs, (name, value) =>
            {
                if (EncodingOnlyKeys.Contains(name)
                    || name.Contains("-level", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("-profile", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("-rangetype", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("-videobitdepth", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("-audiochannels", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("-deinterlace", StringComparison.OrdinalIgnoreCase)
                    || Is(name, "VideoCodec") || Is(name, "AudioCodec")
                    || Is(name, "AllowVideoStreamCopy") || Is(name, "AllowAudioStreamCopy"))
                {
                    return null;
                }

                if (burnIn && (Is(name, "SubtitleStreamIndex") || Is(name, "SubtitleMethod")))
                {
                    return null;
                }

                return value;
            });

            return filtered + (filtered.Length == 0 ? "?" : "&") + "VideoCodec=copy&AudioCodec=copy&AllowVideoStreamCopy=true&AllowAudioStreamCopy=true";
        }

        // Sound a streaming playlist can carry unchanged, per segment kind.
        private static readonly string[] TsSound = { "aac", "ac3", "eac3", "mp3" };
        private static readonly string[] Mp4Sound = { "aac", "ac3", "eac3", "mp3", "flac", "alac", "opus" };

        // Whichever track gets played, the player named its format and it has no
        // more channels than the player takes. No track number is compared, so it
        // holds on either server (they number tracks differently).
        private static bool EverySoundTrackCopies(IEnumerable<KeyValuePair<string, StringValues>> query, IEnumerable<MediaStream>? streams)
        {
            var sound = streams?.Where(s => s.Type == MediaStreamType.Audio).ToList();
            if (sound == null || sound.Count == 0)
            {
                return false;
            }

            var wanted = (RemoteMediaPath.QueryValue(query, "AudioCodec") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var carried = Is(RemoteMediaPath.QueryValue(query, "SegmentContainer") ?? "ts", "mp4") ? Mp4Sound : TsSound;
            var maxChannels = int.TryParse(RemoteMediaPath.QueryValue(query, "TranscodingMaxAudioChannels"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && max > 0
                ? max
                : int.MaxValue;
            return sound.All(s => !s.IsExternal
                && !string.IsNullOrEmpty(s.Codec)
                && wanted.Contains(s.Codec, StringComparer.OrdinalIgnoreCase)
                && carried.Contains(s.Codec, StringComparer.OrdinalIgnoreCase)
                && (s.Channels ?? 2) <= maxChannels);
        }

        private static bool BurnsInSubtitle(IEnumerable<KeyValuePair<string, StringValues>> query)
            => int.TryParse(RemoteMediaPath.QueryValue(query, "SubtitleStreamIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && string.Equals(RemoteMediaPath.QueryValue(query, "SubtitleMethod"), "Encode", StringComparison.OrdinalIgnoreCase);

        private static bool IsFalse(string? value) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        private static bool Is(string name, string key) => string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
    }
}
