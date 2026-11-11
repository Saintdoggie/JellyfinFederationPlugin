using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>One kind of player the support report checks files against.</summary>
    /// <param name="Id">Stable id used by the page.</param>
    /// <param name="Name">What the admin reads.</param>
    /// <param name="Note">What is uncertain about this player, or null.</param>
    /// <param name="Profile">What it typically plays as stored.</param>
    internal sealed record PlaySupportPlayer(string Id, string Name, string? Note, DeviceProfile Profile);

    /// <summary>One file to check: an own file or a friend's copy.</summary>
    internal sealed record PlaySupportFile(string Name, string Origin, string? Container, IReadOnlyList<MediaStream>? Streams);

    /// <summary>
    /// "Which of my files play without converting, and on what?" - for a server
    /// that does not convert (a small VPS), a file a player cannot take as stored
    /// simply does not play there. Compares every file with what common players
    /// typically play as stored, using the same test as the per-device copy
    /// choice (<see cref="DevicePlayFit"/>).
    /// </summary>
    /// <remarks>
    /// The player tables are typical capabilities, not a measurement of anyone's
    /// actual device: a browser's HEVC support depends on the computer, a TV's
    /// sound formats on the model. Where support is hardware-dependent the table
    /// says "does not play", so the report errs towards flagging a file. The page
    /// states this beside the result.
    /// </remarks>
    internal static class PlaySupportReport
    {
        /// <summary>Most examples listed per format.</summary>
        internal const int ExamplesPerFormat = 3;

        /// <summary>The players checked, in display order.</summary>
        internal static IReadOnlyList<PlaySupportPlayer> Players { get; } = new[]
        {
            Player("chrome", "Chrome or Edge (web)", "HEVC plays only on some computers; counted as not playing.",
                "mp4,mov,webm,mkv", "h264,vp8,vp9,av1", "aac,mp3,opus,flac,vorbis", eightBitH264: true),
            Player("firefox", "Firefox (web)", null,
                "mp4,mov,webm", "h264,vp8,vp9,av1", "aac,mp3,opus,flac,vorbis", eightBitH264: true),
            Player("safari", "Safari (web, Mac and iPhone)", null,
                "mp4,mov", "h264,hevc", "aac,mp3,ac3,eac3,flac,alac", eightBitH264: true),
            Player("androidtv", "Android TV, Google TV or Fire TV app", "Depends on the box; cheap sticks play less.",
                "mkv,mp4,mov,ts,webm", "h264,hevc,vp9,av1,mpeg2video,mpeg4,vc1", "aac,mp3,ac3,eac3,dts,opus,flac,vorbis", eightBitH264: false),
            Player("android", "Android phone or tablet app", null,
                "mkv,mp4,mov,webm", "h264,hevc,vp9,av1", "aac,mp3,ac3,eac3,opus,flac,vorbis", eightBitH264: true),
            Player("apple", "iPhone, iPad or Apple TV app", null,
                "mp4,mov,mkv", "h264,hevc", "aac,mp3,ac3,eac3,flac,alac", eightBitH264: true),
            Player("roku", "Roku", null,
                "mp4,mov,mkv", "h264,hevc,vp9", "aac,mp3,ac3,eac3,flac", eightBitH264: true),
            Player("smarttv", "Samsung or LG TV app", "Newer Samsung TVs do not play DTS; counted as not playing.",
                "mkv,mp4,mov,ts", "h264,hevc,vp9,av1", "aac,mp3,ac3,eac3", eightBitH264: true),
            Player("desktop", "Desktop app (Jellyfin Media Player, Kodi)", null,
                string.Empty, string.Empty, string.Empty, eightBitH264: false)
        };

        /// <summary>Builds the report for <paramref name="files"/>.</summary>
        internal static PlaySupportResult Build(IEnumerable<PlaySupportFile> files, DateTime generatedUtc, bool truncated)
        {
            var players = Players;
            var perPlayer = players.Select(_ => new int[4]).ToArray();
            var reasons = players.Select(_ => new Dictionary<string, int>(StringComparer.Ordinal)).ToArray();
            var formats = new Dictionary<string, FormatTally>(StringComparer.Ordinal);
            var checkedFiles = 0;
            var undescribed = 0;

            foreach (var file in files)
            {
                checkedFiles++;
                var video = file.Streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                if (video == null || string.IsNullOrWhiteSpace(video.Codec))
                {
                    // Nothing known about the picture: cannot be judged either way.
                    undescribed++;
                    continue;
                }

                var container = Wrapper(file.Container, file.Streams!);
                var label = FormatLabel(container, file.Streams!);
                if (!formats.TryGetValue(label, out var tally))
                {
                    formats[label] = tally = new FormatTally(label, players.Count);
                }

                tally.Count++;
                if (tally.Examples.Count < ExamplesPerFormat)
                {
                    tally.Examples.Add(file.Origin + ": " + file.Name);
                }

                for (var p = 0; p < players.Count; p++)
                {
                    var fit = DevicePlayFit.Evaluate(players[p].Profile, container, file.Streams, null);
                    perPlayer[p][(int)fit.Fit]++;
                    tally.Fits[p] = fit.Fit;
                    if (fit.Fit is PlayFit.Convert or PlayFit.AudioOnly)
                    {
                        reasons[p][fit.Why] = reasons[p].TryGetValue(fit.Why, out var n) ? n + 1 : 1;
                    }
                }
            }

            return new PlaySupportResult(
                generatedUtc,
                checkedFiles,
                undescribed,
                truncated,
                players.Select((player, p) => new PlaySupportPlayerResult(
                    player.Id,
                    player.Name,
                    player.Note,
                    perPlayer[p][(int)PlayFit.Direct],
                    perPlayer[p][(int)PlayFit.AudioOnly],
                    perPlayer[p][(int)PlayFit.Convert],
                    reasons[p].OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).Take(4)
                        .Select(r => new PlaySupportReason(r.Key, r.Value)).ToList())).ToList(),
                formats.Values
                    .OrderBy(f => f.Fits.Count(x => x == PlayFit.Direct))
                    .ThenByDescending(f => f.Count)
                    .ThenBy(f => f.Label, StringComparer.Ordinal)
                    .Select(f => new PlaySupportFormat(
                        f.Label,
                        f.Count,
                        f.Fits.Select(Word).ToList(),
                        f.Examples))
                    .ToList());
        }

        /// <summary>
        /// The probe names Matroska and WebM together ("matroska,webm"), and a
        /// player that takes WebM does not thereby take any MKV. WebM only holds
        /// VP8/VP9/AV1 with Opus or Vorbis, so anything else in that pair is MKV.
        /// </summary>
        internal static string? Wrapper(string? container, IReadOnlyList<MediaStream> streams)
        {
            if (string.IsNullOrWhiteSpace(container))
            {
                return container;
            }

            var names = container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var webm = names.Any(n => n.Equals("webm", StringComparison.OrdinalIgnoreCase));
            var mkv = names.Any(n => n.Equals("matroska", StringComparison.OrdinalIgnoreCase) || n.Equals("mkv", StringComparison.OrdinalIgnoreCase));
            if (!webm || !mkv)
            {
                return container;
            }

            var isWebm = streams.Where(s => s.Type == MediaStreamType.Video && !string.IsNullOrWhiteSpace(s.Codec))
                    .All(s => s.Codec!.ToLowerInvariant() is "vp8" or "vp9" or "av1")
                && streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrWhiteSpace(s.Codec))
                    .All(s => s.Codec!.ToLowerInvariant() is "opus" or "vorbis");
            return isWebm ? "webm" : "mkv";
        }

        /// <summary>"HEVC 10-bit, DTS, in MKV" - what a person would call the file type.</summary>
        internal static string FormatLabel(string? container, IReadOnlyList<MediaStream> streams)
        {
            var video = streams.First(s => s.Type == MediaStreamType.Video);
            var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            var picture = CodecName(video.Codec!) + (video.BitDepth is > 8 ? " " + video.BitDepth + "-bit" : string.Empty);
            var sound = audio == null || string.IsNullOrWhiteSpace(audio.Codec) ? "no sound" : CodecName(audio.Codec!);
            var wrapper = string.IsNullOrWhiteSpace(container) ? "unknown wrapper" : ContainerName(container!);
            return picture + ", " + sound + ", in " + wrapper;
        }

        private static string Word(PlayFit fit) => fit switch
        {
            PlayFit.Direct => "plays",
            PlayFit.AudioOnly => "sound",
            PlayFit.Convert => "no",
            _ => "unknown"
        };

        private static string CodecName(string codec) => codec.Trim().ToLowerInvariant() switch
        {
            "h264" or "avc" => "H.264",
            "hevc" or "h265" => "HEVC",
            "mpeg2video" => "MPEG-2",
            "mpeg4" => "MPEG-4",
            "eac3" or "ec3" => "E-AC-3",
            "dts" or "dca" => "DTS",
            "truehd" => "TrueHD",
            var other => other.ToUpperInvariant()
        };

        private static string ContainerName(string container)
        {
            var first = container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? container;
            return first.ToLowerInvariant() switch
            {
                "matroska" or "mkv" => "MKV",
                "mov" or "mp4" or "m4v" => "MP4",
                "mpegts" or "ts" => "TS",
                var other => other.ToUpperInvariant()
            };
        }

        private static PlaySupportPlayer Player(string id, string name, string? note, string containers, string video, string audio, bool eightBitH264)
            => new(id, name, note, new DeviceProfile
            {
                Name = name,
                DirectPlayProfiles = new[]
                {
                    new DirectPlayProfile { Type = DlnaProfileType.Video, Container = containers, VideoCodec = video, AudioCodec = audio }
                },
                CodecProfiles = eightBitH264
                    ? new[]
                    {
                        new CodecProfile
                        {
                            Type = CodecType.Video,
                            Codec = "h264",
                            Conditions = new[] { new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.VideoBitDepth, "8") }
                        }
                    }
                    : Array.Empty<CodecProfile>()
            });

        private sealed class FormatTally
        {
            public FormatTally(string label, int players)
            {
                Label = label;
                Fits = new PlayFit[players];
            }

            public string Label { get; }

            public int Count { get; set; }

            public PlayFit[] Fits { get; }

            public List<string> Examples { get; } = new();
        }
    }

    /// <summary>The finished report.</summary>
    /// <param name="GeneratedUtc">When it was made.</param>
    /// <param name="FilesChecked">Files looked at.</param>
    /// <param name="Undescribed">Files with no known picture format (not judged).</param>
    /// <param name="Truncated">True when the library was larger than the scan limit.</param>
    /// <param name="Players">Totals per player.</param>
    /// <param name="Formats">One row per file type, the least playable first.</param>
    internal sealed record PlaySupportResult(DateTime GeneratedUtc, int FilesChecked, int Undescribed, bool Truncated, IReadOnlyList<PlaySupportPlayerResult> Players, IReadOnlyList<PlaySupportFormat> Formats);

    /// <summary>Totals for one player.</summary>
    internal sealed record PlaySupportPlayerResult(string Id, string Name, string? Note, int PlaysAsStored, int SoundOrWrapperOnly, int NeedsConverting, IReadOnlyList<PlaySupportReason> TopReasons);

    /// <summary>A reason files do not play as stored, with how many files.</summary>
    internal sealed record PlaySupportReason(string Reason, int Count);

    /// <summary>One file type: how many files, and per player "plays", "sound" (only the sound or wrapper is in the way) or "no".</summary>
    internal sealed record PlaySupportFormat(string Label, int Count, IReadOnlyList<string> Players, IReadOnlyList<string> Examples);
}
