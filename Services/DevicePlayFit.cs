using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>How much work a copy needs before one device can play it.</summary>
    internal enum PlayFit
    {
        /// <summary>The device did not say what it plays, or the copy is not described.</summary>
        Unknown = 0,

        /// <summary>Plays exactly as stored.</summary>
        Direct = 1,

        /// <summary>The picture plays as stored; only the sound or the wrapper must change (light work).</summary>
        AudioOnly = 2,

        /// <summary>The picture itself must be converted (heavy work).</summary>
        Convert = 3
    }

    /// <summary>The fit of one copy for one device, with the sound track it would play.</summary>
    /// <param name="Fit">The work needed.</param>
    /// <param name="HasPreferredLanguage">Whether the copy has sound in the viewer's language; null when languages are not labelled.</param>
    /// <param name="Why">A few plain words for logs and Playback Info, never ids or addresses.</param>
    internal sealed record DevicePlayFitResult(PlayFit Fit, bool? HasPreferredLanguage, string Why);

    /// <summary>A way to play a title without converting its picture (see <see cref="DevicePlayFit.PlanSoundHelp"/>).</summary>
    /// <param name="ConvertSound">True when the sound must become AAC stereo; false when only the wrapper changes.</param>
    /// <param name="Wrapper">mp4, mkv or ts.</param>
    /// <param name="Video">The picture, copied as stored.</param>
    /// <param name="Audio">The sound track used.</param>
    internal sealed record SoundHelpPlan(bool ConvertSound, string Wrapper, MediaStream Video, MediaStream Audio);

    /// <summary>
    /// Compares a copy's container, video codec and sound track with what the
    /// player said it can play (the device profile every Jellyfin client sends
    /// when it asks to play). Pure: no network, no configuration.
    /// </summary>
    /// <remarks>
    /// This is a ranking hint, not the final word: Jellyfin still runs its own
    /// full check on whichever copy is offered. It only has to be right about the
    /// common, decisive cases - a browser without HEVC, a TV without DTS, a file
    /// with no sound in the viewer's language - and say Unknown otherwise.
    /// </remarks>
    internal static class DevicePlayFit
    {
        /// <summary>The language assumed when the user has not set one in Jellyfin.</summary>
        internal const string DefaultLanguage = "eng";

        internal static DevicePlayFitResult Evaluate(DeviceProfile? profile, string? container, IReadOnlyList<MediaStream>? streams, string? preferredLanguage)
        {
            var audioStreams = streams?.Where(s => s.Type == MediaStreamType.Audio).ToList() ?? new List<MediaStream>();
            var language = string.IsNullOrWhiteSpace(preferredLanguage) ? DefaultLanguage : preferredLanguage!;
            var (audio, hasLanguage) = PickAudio(audioStreams, language);
            var video = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            var playProfiles = profile?.DirectPlayProfiles?.Where(p => p != null && p.Type == DlnaProfileType.Video).ToList();
            if (playProfiles == null || playProfiles.Count == 0 || video == null || string.IsNullOrWhiteSpace(video.Codec))
            {
                return new DevicePlayFitResult(PlayFit.Unknown, hasLanguage, "the device did not say what it plays");
            }

            var videoPlays = playProfiles.Any(p => Lists(p.VideoCodec, video.Codec))
                && VideoConditionsPass(profile!, video);
            if (!videoPlays)
            {
                return new DevicePlayFitResult(PlayFit.Convert, hasLanguage, $"the device does not play {Name(video.Codec)} video like this");
            }

            var whole = playProfiles.Any(p =>
                ContainerMatches(p.Container, container)
                && Lists(p.VideoCodec, video.Codec)
                && (audio == null || string.IsNullOrWhiteSpace(audio.Codec) || Lists(p.AudioCodec, audio.Codec)));
            if (whole && AudioConditionsPass(profile!, audio))
            {
                return new DevicePlayFitResult(PlayFit.Direct, hasLanguage, "plays as stored");
            }

            var audioPlaysSomewhere = audio == null || string.IsNullOrWhiteSpace(audio.Codec)
                || playProfiles.Any(p => Lists(p.VideoCodec, video.Codec) && Lists(p.AudioCodec, audio.Codec));
            return new DevicePlayFitResult(
                PlayFit.AudioOnly,
                hasLanguage,
                audioPlaysSomewhere ? "only the file wrapper must change" : $"only the {Name(audio!.Codec)} sound must change");
        }

        /// <summary>
        /// How a device that plays the picture as stored can get the whole title
        /// without converting video: copy the picture into a wrapper the device
        /// plays and keep the sound, or (when the sound itself does not play)
        /// replace it with AAC stereo. Null when the picture does not play, the
        /// device did not say what it plays, or no wrapper fits.
        /// </summary>
        /// <param name="profile">What the player said it plays.</param>
        /// <param name="streams">The copy's streams.</param>
        /// <param name="audioIndex">The sound track the player asked for, or null for the viewer's language.</param>
        /// <param name="preferredLanguage">The viewer's sound language.</param>
        internal static SoundHelpPlan? PlanSoundHelp(DeviceProfile? profile, IReadOnlyList<MediaStream>? streams, int? audioIndex, string? preferredLanguage)
        {
            var video = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var audioStreams = streams?.Where(s => s.Type == MediaStreamType.Audio).ToList() ?? new List<MediaStream>();
            var audio = audioIndex.HasValue
                ? audioStreams.FirstOrDefault(s => s.Index == audioIndex.Value)
                : PickAudio(audioStreams, string.IsNullOrWhiteSpace(preferredLanguage) ? DefaultLanguage : preferredLanguage!).Stream;
            var playProfiles = profile?.DirectPlayProfiles?.Where(p => p != null && p.Type == DlnaProfileType.Video).ToList();
            if (playProfiles == null || playProfiles.Count == 0 || video == null || string.IsNullOrWhiteSpace(video.Codec)
                || audio == null || string.IsNullOrWhiteSpace(audio.Codec)
                || !VideoConditionsPass(profile!, video))
            {
                return null;
            }

            // Browsers play only MP4 when not given a playlist, so it comes first.
            var wrappers = new[] { "mp4", "mkv", "ts" };
            foreach (var wrapper in wrappers)
            {
                if (playProfiles.Any(p => ContainerMatches(p.Container, wrapper) && Lists(p.VideoCodec, video.Codec) && Lists(p.AudioCodec, audio.Codec))
                    && AudioConditionsPass(profile!, audio)
                    && CopyFits(wrapper, audio.Codec))
                {
                    return new SoundHelpPlan(false, wrapper, video, audio);
                }
            }

            foreach (var wrapper in wrappers)
            {
                if (playProfiles.Any(p => ContainerMatches(p.Container, wrapper) && Lists(p.VideoCodec, video.Codec) && Lists(p.AudioCodec, "aac")))
                {
                    return new SoundHelpPlan(true, wrapper, video, audio);
                }
            }

            return null;
        }

        /// <summary>
        /// The sound track to offer this device first, when the one the viewer
        /// would normally get does not play on it and another track in the same
        /// language does (a film carrying TrueHD beside AC-3 or AAC, for example).
        /// Null leaves the copy's own default alone: the device said nothing about
        /// what it plays, the normal track already plays, or no track in that
        /// language plays. Commentary tracks are never chosen.
        /// </summary>
        /// <param name="profile">What the player said it plays.</param>
        /// <param name="streams">The copy's streams.</param>
        /// <param name="preferredLanguage">The viewer's sound language.</param>
        internal static int? PlayableAudioIndex(DeviceProfile? profile, IReadOnlyList<MediaStream>? streams, string? preferredLanguage)
        {
            var audioStreams = streams?.Where(s => s.Type == MediaStreamType.Audio).ToList() ?? new List<MediaStream>();
            var playProfiles = profile?.DirectPlayProfiles?.Where(p => p != null && p.Type == DlnaProfileType.Video).ToList();
            if (audioStreams.Count < 2 || playProfiles == null || playProfiles.Count == 0)
            {
                return null;
            }

            var video = streams!.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            bool Plays(MediaStream track)
                => !string.IsNullOrWhiteSpace(track.Codec)
                    && playProfiles.Any(p => !string.IsNullOrWhiteSpace(p.AudioCodec)
                        && (video == null || Lists(p.VideoCodec, video.Codec))
                        && Lists(p.AudioCodec, track.Codec))
                    && AudioConditionsPass(profile!, track);

            var language = string.IsNullOrWhiteSpace(preferredLanguage) ? DefaultLanguage : preferredLanguage!;
            var usual = PickAudio(audioStreams, language).Stream;
            if (usual == null || string.IsNullOrWhiteSpace(usual.Codec) || Plays(usual))
            {
                return null;
            }

            bool SameAsUsual(MediaStream track)
                => string.IsNullOrWhiteSpace(usual.Language) || IsUndetermined(usual.Language!)
                    ? string.IsNullOrWhiteSpace(track.Language) || IsUndetermined(track.Language!)
                    : !string.IsNullOrWhiteSpace(track.Language) && SameLanguage(track.Language!, usual.Language!);

            return audioStreams
                .Where(track => !ReferenceEquals(track, usual) && SameAsUsual(track) && !IsCommentary(track) && Plays(track))
                .OrderByDescending(track => track.Channels ?? 0)
                .ThenBy(track => track.Index)
                .Select(track => (int?)track.Index)
                .FirstOrDefault();
        }

        /// <summary>
        /// The copy's streams as this device should see them: unchanged, or a
        /// copy of the list in which the track from <see cref="PlayableAudioIndex"/>
        /// is the only default sound track, so Jellyfin picks it unless the viewer
        /// chose a track themselves. The given list and its streams are never
        /// changed (they are shared with the catalogue and other devices).
        /// </summary>
        /// <param name="profile">What the player said it plays.</param>
        /// <param name="streams">The copy's streams.</param>
        /// <param name="preferredLanguage">The viewer's sound language.</param>
        internal static IReadOnlyList<MediaStream> WithPlayableAudioFirst(DeviceProfile? profile, IReadOnlyList<MediaStream> streams, string? preferredLanguage)
        {
            var index = PlayableAudioIndex(profile, streams, preferredLanguage);
            if (index == null)
            {
                return streams;
            }

            return streams.Select(stream =>
            {
                if (stream.Type != MediaStreamType.Audio || stream.IsDefault == (stream.Index == index.Value))
                {
                    return stream;
                }

                var copy = Copy(stream);
                copy.IsDefault = stream.Index == index.Value;
                return copy;
            }).ToList();
        }

        private static readonly System.Reflection.PropertyInfo[] StreamProperties = typeof(MediaStream)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .ToArray();

        private static MediaStream Copy(MediaStream stream)
        {
            var copy = new MediaStream();
            foreach (var property in StreamProperties)
            {
                property.SetValue(copy, property.GetValue(stream));
            }

            return copy;
        }

        private static bool IsCommentary(MediaStream track)
            => (track.Title ?? string.Empty).Contains("comment", StringComparison.OrdinalIgnoreCase)
                || (track.DisplayTitle ?? string.Empty).Contains("comment", StringComparison.OrdinalIgnoreCase);

        // Sound formats MP4 cannot carry when copied (players would get no sound).
        private static bool CopyFits(string wrapper, string codec)
            => wrapper != "mp4" || Codec(codec) is "aac" or "mp3" or "ac3" or "eac3" or "opus" or "flac" or "alac";

        /// <summary>
        /// The sound track a viewer would get: their language when the copy has it,
        /// preferring the track marked default, otherwise the copy's default track.
        /// </summary>
        internal static (MediaStream? Stream, bool? HasLanguage) PickAudio(IReadOnlyList<MediaStream> audio, string language)
        {
            if (audio.Count == 0)
            {
                return (null, null);
            }

            var labelled = audio.Where(a => !string.IsNullOrWhiteSpace(a.Language) && !IsUndetermined(a.Language!)).ToList();
            var fallback = audio.FirstOrDefault(a => a.IsDefault) ?? audio[0];
            if (labelled.Count == 0)
            {
                return (fallback, null);
            }

            var matching = labelled.Where(a => SameLanguage(a.Language!, language)).ToList();
            return matching.Count == 0
                ? (fallback, false)
                : (matching.FirstOrDefault(a => a.IsDefault) ?? matching[0], true);
        }

        /// <summary>Two- and three-letter codes for the same language compare equal (en/eng, de/ger/deu).</summary>
        internal static bool SameLanguage(string a, string b)
            => string.Equals(Language(a), Language(b), StringComparison.Ordinal);

        // Reduces a language tag to one canonical code: the three-letter
        // terminology code where known, otherwise the tag's own first part.
        private static string Language(string tag)
        {
            var code = tag.Trim().ToLowerInvariant();
            var dash = code.IndexOfAny(new[] { '-', '_' });
            if (dash > 0)
            {
                code = code.Substring(0, dash);
            }

            code = Bibliographic(code);
            if (code.Length == 2)
            {
                if (TwoLetter.TryGetValue(code, out var known))
                {
                    return known;
                }

                try
                {
                    var three = CultureInfo.GetCultureInfo(code).ThreeLetterISOLanguageName;
                    return string.IsNullOrEmpty(three) || three == "ivl" ? code : three.ToLowerInvariant();
                }
                catch (CultureNotFoundException)
                {
                }
            }

            return code;
        }

        // The common ones, so the answer does not depend on the host's culture data.
        private static readonly Dictionary<string, string> TwoLetter = new(StringComparer.Ordinal)
        {
            ["en"] = "eng", ["de"] = "deu", ["fr"] = "fra", ["es"] = "spa", ["it"] = "ita", ["pt"] = "por",
            ["nl"] = "nld", ["sv"] = "swe", ["no"] = "nor", ["da"] = "dan", ["fi"] = "fin", ["pl"] = "pol",
            ["ru"] = "rus", ["uk"] = "ukr", ["cs"] = "ces", ["sk"] = "slk", ["hu"] = "hun", ["ro"] = "ron",
            ["el"] = "ell", ["tr"] = "tur", ["ar"] = "ara", ["he"] = "heb", ["fa"] = "fas", ["hi"] = "hin",
            ["ja"] = "jpn", ["ko"] = "kor", ["zh"] = "zho", ["th"] = "tha", ["vi"] = "vie", ["id"] = "ind"
        };

        private static string Bibliographic(string code) => code switch
        {
            "ger" => "deu",
            "fre" => "fra",
            "dut" => "nld",
            "chi" => "zho",
            "cze" => "ces",
            "gre" => "ell",
            "rum" => "ron",
            "per" => "fas",
            "slo" => "slk",
            _ => code
        };

        private static bool IsUndetermined(string language)
            => language.Equals("und", StringComparison.OrdinalIgnoreCase) || language.Equals("unk", StringComparison.OrdinalIgnoreCase);

        // An empty list in a profile means "any".
        private static bool Lists(string? list, string? value)
        {
            if (string.IsNullOrWhiteSpace(list))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(entry => SameCodec(entry, value!));
        }

        private static bool SameCodec(string a, string b)
            => Codec(a) == Codec(b);

        private static string Codec(string codec) => codec.Trim().ToLowerInvariant() switch
        {
            "h265" => "hevc",
            "avc" => "h264",
            "mpeg4" => "mpeg4",
            "dca" => "dts",
            "ec3" => "eac3",
            var other => other
        };

        // Files report every name their format goes by ("matroska,webm", "mov,mp4,m4a,...").
        private static bool ContainerMatches(string? profileContainers, string? container)
        {
            if (string.IsNullOrWhiteSpace(profileContainers) || string.IsNullOrWhiteSpace(container))
            {
                return true;
            }

            var wanted = profileContainers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Container).ToHashSet(StringComparer.Ordinal);
            return container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Container).Any(wanted.Contains);
        }

        private static string Container(string name) => name.ToLowerInvariant() switch
        {
            "matroska" => "mkv",
            "mpegts" => "ts",
            "m4v" => "mp4",
            var other => other
        };

        private static bool VideoConditionsPass(DeviceProfile profile, MediaStream video)
        {
            foreach (var codecProfile in profile.CodecProfiles ?? Array.Empty<CodecProfile>())
            {
                if (codecProfile == null || codecProfile.Type != CodecType.Video || !Lists(codecProfile.Codec, video.Codec))
                {
                    continue;
                }

                foreach (var condition in codecProfile.Conditions ?? Array.Empty<ProfileCondition>())
                {
                    if (condition != null && !Passes(condition, VideoValue(condition.Property, video)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool AudioConditionsPass(DeviceProfile profile, MediaStream? audio)
        {
            if (audio == null)
            {
                return true;
            }

            foreach (var codecProfile in profile.CodecProfiles ?? Array.Empty<CodecProfile>())
            {
                if (codecProfile == null || codecProfile.Type != CodecType.VideoAudio || !Lists(codecProfile.Codec, audio.Codec))
                {
                    continue;
                }

                foreach (var condition in codecProfile.Conditions ?? Array.Empty<ProfileCondition>())
                {
                    if (condition != null
                        && condition.Property == ProfileConditionValue.AudioChannels
                        && !Passes(condition, audio.Channels?.ToString(CultureInfo.InvariantCulture)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // Only the properties that decide real cases; anything else is left to Jellyfin.
        private static string? VideoValue(ProfileConditionValue property, MediaStream video) => property switch
        {
            ProfileConditionValue.VideoBitDepth => video.BitDepth?.ToString(CultureInfo.InvariantCulture),
            ProfileConditionValue.Width => video.Width?.ToString(CultureInfo.InvariantCulture),
            ProfileConditionValue.Height => video.Height?.ToString(CultureInfo.InvariantCulture),
            ProfileConditionValue.VideoLevel => video.Level?.ToString(CultureInfo.InvariantCulture),
            ProfileConditionValue.VideoProfile => video.Profile,
            ProfileConditionValue.VideoRangeType => video.VideoRangeType.ToString(),
            _ => null
        };

        // An unknown value never fails a condition: unknown is not "unsupported".
        private static bool Passes(ProfileCondition condition, string? actual)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(condition.Value))
            {
                return true;
            }

            var bothNumbers = double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                & double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit);
            switch (condition.Condition)
            {
                case ProfileConditionType.LessThanEqual:
                    return !bothNumbers || number <= limit;
                case ProfileConditionType.GreaterThanEqual:
                    return !bothNumbers || number >= limit;
                case ProfileConditionType.Equals:
                    return bothNumbers ? number.Equals(limit) : string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
                case ProfileConditionType.NotEquals:
                    return bothNumbers ? !number.Equals(limit) : !string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
                case ProfileConditionType.EqualsAny:
                    return condition.Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase));
                default:
                    return true;
            }
        }

        private static string Name(string? codec)
            => string.IsNullOrWhiteSpace(codec) ? "this" : Codec(codec!).ToUpperInvariant();
    }
}
