using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Linked servers do not convert each other's titles. A linked server's
    /// title plays from that server as stored or as a copy play
    /// (<see cref="CopyPlay"/>), or converted by that server itself when it
    /// offers to. It is never pulled across to be converted on the viewer's
    /// server: that sends the whole file over one server's upload to do work on
    /// a machine that does not hold it. When none of that is possible the play is
    /// refused with the reason, in the file's own terms. See <c>DIRECT-PLAY.md</c>.
    /// </summary>
    internal static class LinkedPlayRule
    {
        /// <summary>Error code of the refusal.</summary>
        internal const string Error = "linked-server-no-conversion";

        /// <summary>Whether the rule covers this request: video work for a linked server's title.</summary>
        internal static bool Applies(RemoteServer? server, RemoteMediaPath path)
            => server is { Linked: true } && path.StartsWork && !path.Audio && path.Kind != RemoteMediaKind.UniversalAudio;

        /// <summary>
        /// Says what the device could not take, from Jellyfin's own reasons and
        /// the file's tracks.
        /// </summary>
        /// <param name="serverName">The linked server holding the file.</param>
        /// <param name="source">The copy that was asked for.</param>
        /// <param name="query">The player's request.</param>
        internal static string Explain(string serverName, FederatedSource? source, IEnumerable<KeyValuePair<string, StringValues>> query)
        {
            var streams = source?.MediaStreams ?? Array.Empty<MediaStream>();
            var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var wantedSound = int.TryParse(RemoteMediaPath.QueryValue(query, "AudioStreamIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == index)
                : null;
            var sound = wantedSound
                ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

            var parts = new List<string>();
            var reasons = (RemoteMediaPath.QueryValue(query, "TranscodeReasons") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool Has(Func<string, bool> match) => reasons.Any(match);

            if (Has(r => r.Equals("VideoCodecNotSupported", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add($"its picture format ({Name(video?.Codec)})");
            }
            else if (Has(r => r.StartsWith("Video", StringComparison.OrdinalIgnoreCase) || r.StartsWith("RefFrames", StringComparison.OrdinalIgnoreCase)
                || r.Equals("AnamorphicVideoNotSupported", StringComparison.OrdinalIgnoreCase) || r.Equals("InterlacedVideoNotSupported", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add($"its picture ({Picture(video)})");
            }

            if (Has(r => r.Equals("AudioCodecNotSupported", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add($"its sound format ({Name(sound?.Profile is { Length: > 0 } p && p.Contains("DTS", StringComparison.OrdinalIgnoreCase) ? p : sound?.Codec)})");
            }
            else if (Has(r => r.StartsWith("Audio", StringComparison.OrdinalIgnoreCase) || r.Equals("SecondaryAudioNotSupported", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(sound?.Channels is > 0 ? $"its {sound.Channels}-channel sound ({Name(sound.Codec)})" : "its sound");
            }

            if (Has(r => r.Equals("ContainerBitrateExceedsLimit", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(source?.Bitrate is > 0
                    ? $"its full quality ({Math.Round(source.Bitrate.Value / 1_000_000d)} Mbps) at this device's quality setting"
                    : "its full quality at this device's quality setting");
            }

            if (Has(r => r.StartsWith("Subtitle", StringComparison.OrdinalIgnoreCase))
                || string.Equals(RemoteMediaPath.QueryValue(query, "SubtitleMethod"), "Encode", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("the chosen subtitle without it being drawn into the picture");
            }

            var stored = string.Join(" / ", new[] { source?.Container, Name(video?.Codec), Name(sound?.Codec) }.Where(s => !string.IsNullOrEmpty(s) && s != "unknown"));
            var cannot = parts.Count == 0 ? "it as stored" : string.Join(" and ", parts);
            var storedText = stored.Length == 0 ? string.Empty : $" It is stored as {stored}.";
            var hint = parts.Count == 1 && parts[0].StartsWith("its full quality", StringComparison.Ordinal)
                ? " Set the player's quality to Auto or Maximum."
                : parts.Any(p => p.StartsWith("the chosen subtitle", StringComparison.Ordinal)) && parts.Count == 1
                    ? " Turn the subtitle off or pick another one."
                    : " Play it on a device or app that supports that, or pick another version or sound track.";
            return $"This device cannot play {cannot}.{storedText} It is on {serverName}, a linked server, and linked servers do not convert each other's titles.{hint}";
        }

        private static string Picture(MediaStream? video)
        {
            if (video == null)
            {
                return "unknown";
            }

            var bits = new List<string> { Name(video.Codec) };
            if (!string.IsNullOrEmpty(video.Profile))
            {
                bits.Add(video.Profile);
            }

            if (video.VideoRangeType != VideoRangeType.Unknown && video.VideoRangeType != VideoRangeType.SDR)
            {
                bits.Add(video.VideoRangeType.ToString());
            }

            if (video.Height is > 0)
            {
                bits.Add($"{video.Height}p");
            }

            return string.Join(", ", bits);
        }

        private static string Name(string? codec)
            => string.IsNullOrWhiteSpace(codec) ? "unknown" : codec.ToLowerInvariant() switch
            {
                "hevc" or "h265" => "HEVC",
                "h264" or "avc" => "H.264",
                "av1" => "AV1",
                "vp9" => "VP9",
                "mpeg2video" => "MPEG-2",
                "vc1" => "VC-1",
                "dts" or "dca" => "DTS",
                "truehd" => "Dolby TrueHD",
                "eac3" => "Dolby Digital Plus",
                "ac3" => "Dolby Digital",
                "aac" => "AAC",
                "flac" => "FLAC",
                "opus" => "Opus",
                "mp3" => "MP3",
                var other => other.Contains(' ') || other.Any(char.IsUpper) ? codec : codec.ToUpperInvariant()
            };
    }
}
