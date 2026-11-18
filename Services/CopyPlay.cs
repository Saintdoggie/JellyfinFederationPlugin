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
        // VideoCodecTagNotSupported is the label inside the file's header (HEVC
        // stored as "hev1" where Apple's players want "hvc1", or Dolby Vision's
        // "dvh1"). Jellyfin's streaming-playlist code rewrites that label while
        // it copies the picture (-tag:v:0 hvc1 / dvh1), so no encoder is needed.
        private static readonly HashSet<string> WrapperOnlyReasons = new(StringComparer.OrdinalIgnoreCase)
        {
            "ContainerNotSupported", "DirectPlayError", "VideoCodecTagNotSupported"
        };

        // Reasons that concern only the sound. With the owner's consent (sound-only
        // conversion) they are answered by re-encoding the sound track alone to a
        // codec the device plays, while the picture is copied. The video reasons
        // above never pass: the picture is never re-encoded.
        private static readonly HashSet<string> SoundOnlyReasons = new(StringComparer.OrdinalIgnoreCase)
        {
            "AudioCodecNotSupported", "AudioBitrateNotSupported", "AudioChannelsNotSupported",
            "AudioProfileNotSupported", "AudioSampleRateNotSupported", "AudioBitDepthNotSupported"
        };

        // Reasons about the picture's range (HDR10, Dolby Vision ...). They pass only
        // when the file's Dolby Vision picture has a base layer the device plays
        // as plain HDR10, HLG or SDR (see HasPlayableBaseLayer).
        private static readonly HashSet<string> RangeReasons = new(StringComparer.OrdinalIgnoreCase)
        {
            "VideoRangeTypeNotSupported", "VideoRangeNotSupported"
        };

        // Dolby Vision kinds whose base layer is an ordinary picture: a device that
        // does not know Dolby Vision plays the base and ignores the extra layer.
        // Profile 5 ("DOVI") is not here: its base layer looks wrong without the
        // Dolby Vision conversion.
        private static readonly HashSet<string> DolbyVisionWithPlainBase = new(StringComparer.OrdinalIgnoreCase)
        {
            "DOVIWithEL", "DOVIWithELHDR10Plus", "DOVIWithHDR10", "DOVIWithHDR10Plus", "DOVIWithHLG", "DOVIWithSDR"
        };

        // Sound limits that matter only when the sound is re-encoded: kept when the
        // sound-only tier answers the request, dropped from a pure copy play.
        private static readonly HashSet<string> SoundEncodingKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "AudioBitRate", "MaxAudioChannels", "TranscodingMaxAudioChannels", "AudioChannels",
            "AudioSampleRate", "MaxAudioBitDepth", "EnableAudioVbrEncoding"
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
            => Fits(query, sourceBitrate, capMbps, streams, soundOnly: false);

        /// <summary>
        /// <see cref="Fits(IEnumerable{KeyValuePair{string, StringValues}}, long?, double, IEnumerable{MediaStream}?, bool)"/>
        /// where this server also leaves out a picture subtitle that would have to be
        /// burned in (<paramref name="dropBurnedSubtitle"/>): the play fits without it.
        /// </summary>
        internal static bool Fits(IEnumerable<KeyValuePair<string, StringValues>> query, long? sourceBitrate, double capMbps, IEnumerable<MediaStream>? streams, bool soundOnly, bool dropBurnedSubtitle)
            => Fits(query, sourceBitrate, capMbps, streams, soundOnly, dropBurnedSubtitle, out _);

        /// <summary>
        /// <see cref="Fits(IEnumerable{KeyValuePair{string, StringValues}}, long?, double, IEnumerable{MediaStream}?)"/>
        /// where the server holding the file allows sound-only conversion
        /// (<paramref name="soundOnly"/>): a request whose only reasons are about
        /// the sound also fits, because the owner copies the picture and re-encodes
        /// just the sound. A reason about the picture still does not fit.
        /// </summary>
        /// <param name="query">The player's request.</param>
        /// <param name="sourceBitrate">The file's bitrate, when known.</param>
        /// <param name="capMbps">The bitrate ceiling towards that server (0 or less: none).</param>
        /// <param name="streams">The file's tracks, when known.</param>
        /// <param name="soundOnly">Whether the owner re-encodes sound for this caller.</param>
        internal static bool Fits(IEnumerable<KeyValuePair<string, StringValues>> query, long? sourceBitrate, double capMbps, IEnumerable<MediaStream>? streams, bool soundOnly)
            => Fits(query, sourceBitrate, capMbps, streams, soundOnly, dropBurnedSubtitle: false, out _);

        /// <summary>The full check; <paramref name="subtitleDropped"/> is true when the play fits only because its burned-in subtitle is left out.</summary>
        /// <param name="query">The player's request.</param>
        /// <param name="sourceBitrate">The file's bitrate, when known.</param>
        /// <param name="capMbps">The bitrate ceiling towards that server (0 or less: none).</param>
        /// <param name="streams">The file's tracks, when known.</param>
        /// <param name="soundOnly">Whether the owner re-encodes sound for this caller.</param>
        /// <param name="dropBurnedSubtitle">Whether a subtitle that needs burning in may be left out.</param>
        /// <param name="subtitleDropped">Whether the subtitle has to be left out.</param>
        internal static bool Fits(IEnumerable<KeyValuePair<string, StringValues>> query, long? sourceBitrate, double capMbps, IEnumerable<MediaStream>? streams, bool soundOnly, bool dropBurnedSubtitle, out bool subtitleDropped)
        {
            subtitleDropped = false;
            var reasons = RemoteMediaPath.QueryValue(query, "TranscodeReasons");
            if (string.IsNullOrWhiteSpace(reasons))
            {
                // A request Jellyfin did not explain (a player that builds its own
                // address): nothing says copying is enough.
                return false;
            }

            foreach (var reason in reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (WrapperOnlyReasons.Contains(reason)
                    || (soundOnly && SoundOnlyReasons.Contains(reason))
                    || (RangeReasons.Contains(reason) && HasPlayableBaseLayer(streams))
                    || (dropBurnedSubtitle && Is(reason, "SubtitleCodecNotSupported") && BurnsInSubtitle(query)))
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
                || (!soundOnly && IsFalse(RemoteMediaPath.QueryValue(query, "AllowAudioStreamCopy"))))
            {
                return false;
            }

            if (BurnsInSubtitle(query))
            {
                if (!dropBurnedSubtitle)
                {
                    return false;
                }

                subtitleDropped = true;
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
            => OwnerAccepts(query, streams, soundOnly: false);

        /// <summary>
        /// The owner's own check, where this owner may re-encode sound
        /// (<paramref name="soundOnly"/>). A link naming the picture as copy is
        /// accepted whatever it says about sound: the picture cannot be encoded,
        /// and sound is the only thing left that can be.
        /// </summary>
        /// <param name="query">The friend's request.</param>
        /// <param name="streams">The file's tracks, when known.</param>
        /// <param name="soundOnly">Whether this owner re-encodes sound for the caller.</param>
        internal static bool OwnerAccepts(IEnumerable<KeyValuePair<string, StringValues>> query, IEnumerable<MediaStream>? streams, bool soundOnly)
            => Is(RemoteMediaPath.QueryValue(query, "VideoCodec") ?? string.Empty, "copy")
                    && (soundOnly || Is(RemoteMediaPath.QueryValue(query, "AudioCodec") ?? string.Empty, "copy"))
                || Fits(query, null, 0, streams, soundOnly);

        /// <summary>
        /// Whether answering this request takes a sound re-encode: the request lists
        /// sound reasons that a pure copy cannot satisfy.
        /// </summary>
        /// <param name="query">The request.</param>
        internal static bool NeedsSoundConversion(IEnumerable<KeyValuePair<string, StringValues>> query)
            => (RemoteMediaPath.QueryValue(query, "TranscodeReasons") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(reason => SoundOnlyReasons.Contains(reason));

        /// <summary>
        /// The owner's rewrite: the same request, unable to start an encoder.
        /// Picture and sound are named as copy, encoding limits are dropped, and
        /// a subtitle that would be burned into the picture is left out.
        /// </summary>
        /// <param name="query">The friend's request.</param>
        internal static string ForceCopy(IEnumerable<KeyValuePair<string, StringValues>> query)
            => ForceCopy(query, soundOnly: false);

        /// <summary>
        /// The owner's rewrite where it may re-encode sound (<paramref name="soundOnly"/>):
        /// when the request needs the sound converted, the picture is named as copy
        /// and the sound is left as the player asked (a codec it plays, its channel
        /// and bitrate limits), so only the sound track is encoded. Otherwise the
        /// same as <see cref="ForceCopy(IEnumerable{KeyValuePair{string, StringValues}})"/>.
        /// </summary>
        /// <param name="query">The friend's request.</param>
        /// <param name="soundOnly">Whether this owner re-encodes sound for the caller.</param>
        internal static string ForceCopy(IEnumerable<KeyValuePair<string, StringValues>> query, bool soundOnly)
        {
            var pairs = query as IReadOnlyCollection<KeyValuePair<string, StringValues>> ?? query.ToList();
            var convertSound = soundOnly && NeedsSoundConversion(pairs) && !Is(RemoteMediaPath.QueryValue(pairs, "AudioCodec") ?? "copy", "copy");
            var burnIn = BurnsInSubtitle(pairs);
            var filtered = RemoteMediaPath.FilterQuery(pairs, (name, value) =>
            {
                if (convertSound && (SoundEncodingKeys.Contains(name) || Is(name, "AudioCodec") || Is(name, "AllowAudioStreamCopy")))
                {
                    return value;
                }

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

            return filtered + (filtered.Length == 0 ? "?" : "&")
                + (convertSound ? "VideoCodec=copy&AllowVideoStreamCopy=true" : "VideoCodec=copy&AudioCodec=copy&AllowVideoStreamCopy=true&AllowAudioStreamCopy=true");
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

        /// <summary>
        /// Whether the file's picture is Dolby Vision with a base layer a device that
        /// does not know Dolby Vision plays as an ordinary picture (profile 7 with its
        /// extra layer, profile 8). Unknown tracks: no.
        /// </summary>
        /// <param name="streams">The file's tracks, when known.</param>
        internal static bool HasPlayableBaseLayer(IEnumerable<MediaStream>? streams)
            => streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video) is { } video
                && DolbyVisionWithPlainBase.Contains(video.VideoRangeType.ToString());

        /// <summary>Whether the request has a picture subtitle that would have to be drawn into the picture.</summary>
        /// <param name="query">The player's request.</param>
        internal static bool NeedsBurnedSubtitle(IEnumerable<KeyValuePair<string, StringValues>> query) => BurnsInSubtitle(query);

        /// <summary>Whether a query name belongs to the burned-in subtitle choice (left out when that subtitle is dropped).</summary>
        /// <param name="name">The query name.</param>
        internal static bool IsSubtitleChoice(string name) => Is(name, "SubtitleStreamIndex") || Is(name, "SubtitleMethod");

        private static bool BurnsInSubtitle(IEnumerable<KeyValuePair<string, StringValues>> query)
            => int.TryParse(RemoteMediaPath.QueryValue(query, "SubtitleStreamIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && string.Equals(RemoteMediaPath.QueryValue(query, "SubtitleMethod"), "Encode", StringComparison.OrdinalIgnoreCase);

        private static bool IsFalse(string? value) => string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        private static bool Is(string name, string key) => string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
    }
}
