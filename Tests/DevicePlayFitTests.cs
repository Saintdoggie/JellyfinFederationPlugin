using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The device-fit hint that lets a copy the player can show as stored outrank one
/// that somebody would have to convert - decisive on a server that may not convert.
/// </summary>
public class DevicePlayFitTests
{
    // A browser without HEVC: H.264 in MP4 or MKV, AAC/MP3/Opus sound.
    private static DeviceProfile Browser() => new()
    {
        DirectPlayProfiles = new[]
        {
            new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mp4,m4v", VideoCodec = "h264,vp9", AudioCodec = "aac,mp3,opus" },
            new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mkv", VideoCodec = "h264,vp9", AudioCodec = "aac,mp3,opus" },
            new DirectPlayProfile { Type = DlnaProfileType.Audio, Container = "mp3" }
        },
        CodecProfiles = new[]
        {
            new CodecProfile
            {
                Type = CodecType.Video,
                Codec = "h264",
                Conditions = new[]
                {
                    new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.VideoBitDepth, "8"),
                    new ProfileCondition(ProfileConditionType.LessThanEqual, ProfileConditionValue.VideoLevel, "52")
                }
            }
        }
    };

    private static List<MediaStream> Streams(string video, string audio, string? language = "eng", int? bitDepth = 8, int channels = 2)
        => new()
        {
            new MediaStream { Type = MediaStreamType.Video, Codec = video, Height = 1080, Width = 1920, BitDepth = bitDepth, Level = 41 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = audio, Language = language, Channels = channels, IsDefault = true }
        };

    [Fact]
    public void H264AacInMp4_PlaysAsStored()
    {
        var fit = DevicePlayFit.Evaluate(Browser(), "mov,mp4,m4a,3gp,3g2,mj2", Streams("h264", "aac"), null);

        Assert.Equal(PlayFit.Direct, fit.Fit);
        Assert.True(fit.HasPreferredLanguage);
    }

    [Fact]
    public void MatroskaReportedByItsLongName_MatchesMkv()
        => Assert.Equal(PlayFit.Direct, DevicePlayFit.Evaluate(Browser(), "matroska,webm", Streams("h264", "aac"), null).Fit);

    [Fact]
    public void HevcOnADeviceWithoutHevc_NeedsThePictureConverted()
    {
        var fit = DevicePlayFit.Evaluate(Browser(), "mkv", Streams("hevc", "aac"), null);

        Assert.Equal(PlayFit.Convert, fit.Fit);
        Assert.Contains("HEVC", fit.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void H265IsTheSameCodecAsHevc()
    {
        var profile = Browser();
        profile.DirectPlayProfiles[1].VideoCodec = "h264,h265";

        Assert.Equal(PlayFit.Direct, DevicePlayFit.Evaluate(profile, "mkv", Streams("hevc", "aac"), null).Fit);
    }

    [Fact]
    public void TenBitH264_FailsTheDevicesBitDepthLimit()
        => Assert.Equal(PlayFit.Convert, DevicePlayFit.Evaluate(Browser(), "mkv", Streams("h264", "aac", bitDepth: 10), null).Fit);

    [Fact]
    public void UnknownBitDepth_IsNotTreatedAsUnsupported()
        => Assert.Equal(PlayFit.Direct, DevicePlayFit.Evaluate(Browser(), "mkv", Streams("h264", "aac", bitDepth: null), null).Fit);

    [Fact]
    public void DtsSoundOnly_IsLightWork()
    {
        var fit = DevicePlayFit.Evaluate(Browser(), "mkv", Streams("h264", "dts"), null);

        Assert.Equal(PlayFit.AudioOnly, fit.Fit);
        Assert.Contains("DTS", fit.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongWrapperOnly_IsLightWork()
        => Assert.Equal(PlayFit.AudioOnly, DevicePlayFit.Evaluate(Browser(), "avi", Streams("h264", "aac"), null).Fit);

    [Fact]
    public void NoProfile_IsUnknown_NotAGuess()
    {
        Assert.Equal(PlayFit.Unknown, DevicePlayFit.Evaluate(null, "mkv", Streams("hevc", "dts"), null).Fit);
        Assert.Equal(PlayFit.Unknown, DevicePlayFit.Evaluate(new DeviceProfile(), "mkv", Streams("hevc", "dts"), null).Fit);
        Assert.Equal(PlayFit.Unknown, DevicePlayFit.Evaluate(Browser(), "mkv", null, null).Fit);
    }

    [Fact]
    public void Language_DefaultsToEnglish_AndFollowsTheUsersPreference()
    {
        var german = Streams("h264", "aac", "ger");

        Assert.False(DevicePlayFit.Evaluate(Browser(), "mkv", german, null).HasPreferredLanguage);
        Assert.False(DevicePlayFit.Evaluate(Browser(), "mkv", german, "  ").HasPreferredLanguage);
        Assert.True(DevicePlayFit.Evaluate(Browser(), "mkv", german, "deu").HasPreferredLanguage);
        Assert.True(DevicePlayFit.Evaluate(Browser(), "mkv", german, "de").HasPreferredLanguage);
        Assert.True(DevicePlayFit.Evaluate(Browser(), "mkv", Streams("h264", "aac", "en"), null).HasPreferredLanguage);
    }

    [Fact]
    public void UnlabelledSound_LanguageIsUnknown()
    {
        Assert.Null(DevicePlayFit.Evaluate(Browser(), "mkv", Streams("h264", "aac", null), null).HasPreferredLanguage);
        Assert.Null(DevicePlayFit.Evaluate(Browser(), "mkv", Streams("h264", "aac", "und"), null).HasPreferredLanguage);
    }

    [Fact]
    public void TheTrackInTheViewersLanguageDecidesTheFit()
    {
        // Default track is DTS in German; the English track is AAC and plays as stored.
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", BitDepth = 8 },
            new() { Type = MediaStreamType.Audio, Codec = "dts", Language = "ger", IsDefault = true },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng" }
        };

        Assert.Equal(PlayFit.Direct, DevicePlayFit.Evaluate(Browser(), "mkv", streams, "eng").Fit);
        Assert.Equal(PlayFit.AudioOnly, DevicePlayFit.Evaluate(Browser(), "mkv", streams, "ger").Fit);
    }
}

/// <summary>How the fit and the language move the ranking.</summary>
public class SourceSelectionFitTests
{
    private static SourceCandidate Copy(string key, int height, double mbps, PlayFit fit, bool mayConvert, bool? language = true, int priority = 0)
        => new()
        {
            Key = key,
            ServerId = key,
            ServerName = key,
            Height = height,
            BitrateBps = (long)(mbps * 1_000_000),
            LinkMbps = 200,
            Fit = fit,
            ConversionAvailable = mayConvert,
            HasPreferredLanguage = language,
            Priority = priority
        };

    [Fact]
    public void OnAServerThatMayNotConvert_ThePlayableCopyBeatsTheBetterLookingOne()
    {
        var selection = SourceSelectionScorer.Rank(
            new[]
            {
                Copy("big", 2160, 40, PlayFit.Convert, mayConvert: false),
                Copy("small", 720, 3, PlayFit.Direct, mayConvert: false, priority: 1)
            },
            new ViewerDevice(ViewerClientKind.Web));

        Assert.Equal("small", selection.Chosen!.Candidate.Key);
        Assert.Contains("cannot play", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SoundOnlyWork_IsAlsoUnplayable_WhenNobodyMayConvert()
    {
        var selection = SourceSelectionScorer.Rank(
            new[]
            {
                Copy("dts", 1080, 10, PlayFit.AudioOnly, mayConvert: false),
                Copy("aac", 1080, 8, PlayFit.Direct, mayConvert: false, priority: 1)
            },
            ViewerDevice.Unknown);

        Assert.Equal("aac", selection.Chosen!.Candidate.Key);
    }

    [Fact]
    public void WhenConversionIsAllowed_AnAsStoredCopyOfEqualPictureStillWins()
    {
        var selection = SourceSelectionScorer.Rank(
            new[]
            {
                Copy("hevc", 1080, 8, PlayFit.Convert, mayConvert: true),
                Copy("h264", 1080, 8, PlayFit.Direct, mayConvert: true, priority: 1)
            },
            ViewerDevice.Unknown);

        Assert.Equal("h264", selection.Chosen!.Candidate.Key);
    }

    [Fact]
    public void ACopyInAnotherLanguage_LosesToOneInTheViewersLanguage()
    {
        var selection = SourceSelectionScorer.Rank(
            new[]
            {
                Copy("foreign", 2160, 25, PlayFit.Direct, mayConvert: true, language: false),
                Copy("mine", 1080, 8, PlayFit.Direct, mayConvert: true, language: true, priority: 1)
            },
            ViewerDevice.Unknown);

        Assert.Equal("mine", selection.Chosen!.Candidate.Key);
        Assert.Contains("your language", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFit_ChangesNothing()
    {
        var plain = Copy("a", 1080, 8, PlayFit.Unknown, mayConvert: false, language: null);
        var before = SourceSelectionScorer.Score(plain with { Fit = PlayFit.Unknown }, ViewerDevice.Unknown).Total;
        var direct = SourceSelectionScorer.Score(plain with { Fit = PlayFit.Direct }, ViewerDevice.Unknown).Total;

        Assert.Equal(before, direct);
    }
}

/// <summary>The per-device meaning of a title's own media source id.</summary>
public class ViewerCopyBindingTests
{
    [Fact]
    public void Binding_IsPerUserDeviceAndTitle_AndExpires()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var binding = new ViewerCopyBinding { UtcNow = () => now };
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        binding.Bind(user, "Phone", item, "friend:abc", "src1");

        Assert.Equal(("friend:abc", "src1"), binding.Find(user, " phone ", item));
        Assert.Null(binding.Find(user, "tv", item));
        Assert.Null(binding.Find(Guid.NewGuid(), "phone", item));
        Assert.Null(binding.Find(user, "phone", Guid.NewGuid()));

        // Being used keeps it alive past the limit; left alone it goes.
        now += ViewerCopyBinding.ForgetAfter - TimeSpan.FromMinutes(1);
        Assert.NotNull(binding.Find(user, "phone", item));
        now += ViewerCopyBinding.ForgetAfter - TimeSpan.FromMinutes(1);
        Assert.NotNull(binding.Find(user, "phone", item));
        now += ViewerCopyBinding.ForgetAfter + TimeSpan.FromMinutes(1);
        Assert.Null(binding.Find(user, "phone", item));
    }

    [Fact]
    public void Forget_RemovesOnlyThatBinding()
    {
        var binding = new ViewerCopyBinding();
        var user = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        binding.Bind(user, "d", a, "x:1", null);
        binding.Bind(user, "d", b, "y:2", null);

        binding.Forget(user, "d", a);

        Assert.Null(binding.Find(user, "d", a));
        Assert.Equal(("y:2", (string?)null), binding.Find(user, "d", b));
    }
}

/// <summary>Hourly readings for a day, then daily, averaged per friend.</summary>
public class LinkSpeedHistoryTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Average_UsesEveryRecentReading_AndSamplingSlowsAfterADay()
    {
        var history = new LinkSpeedHistory();
        Assert.Null(history.AverageMbps("f", Start));
        Assert.Equal(LinkSpeedHistory.LearningInterval, history.NextDelay("f", Start));

        for (var hour = 0; hour < 24; hour++)
        {
            history.Record("f", hour < 12 ? 10 : 30, Start.AddHours(hour));
        }

        var now = Start.AddHours(24);
        Assert.Equal(20, history.AverageMbps("f", now));
        Assert.Equal(24, history.SampleCount("f", now));
        Assert.Equal(LinkSpeedHistory.SettledInterval, history.NextDelay("f", now));
    }

    [Fact]
    public void OnlyTheLatestDayOfReadingsIsKept_AndOldOnesStopCounting()
    {
        var history = new LinkSpeedHistory();
        for (var n = 0; n < 30; n++)
        {
            history.Record("f", n < 6 ? 1000 : 10, Start.AddHours(n));
        }

        Assert.Equal(10, history.AverageMbps("f", Start.AddHours(30)));
        Assert.Null(history.AverageMbps("f", Start.AddDays(40)));
        Assert.Equal(LinkSpeedHistory.LearningInterval, history.NextDelay("f", Start.AddDays(40)));
    }

    [Fact]
    public void NonsenseReadingsAreIgnored_AndRemoveForgets()
    {
        var history = new LinkSpeedHistory();
        history.Record("f", 0, Start);
        history.Record("f", -5, Start);
        history.Record("f", double.NaN, Start);
        history.Record("", 10, Start);
        Assert.Null(history.AverageMbps("f", Start));

        history.Record("f", 12.5, Start);
        history.Remove("f");
        Assert.Null(history.AverageMbps("f", Start));
    }

    [Fact]
    public void ReadingsSurviveARestart_AndADamagedFileStartsEmpty()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fed-linkspeed-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            var path = System.IO.Path.Combine(folder, "speeds.json");
            var first = new LinkSpeedHistory();
            first.Initialize(path);
            first.Record("f", 40, Start);
            first.Record("f", 60, Start.AddHours(1));

            var second = new LinkSpeedHistory();
            second.Initialize(path);
            Assert.Equal(50, second.AverageMbps("f", Start.AddHours(2)));

            System.IO.File.WriteAllText(path, "{not json");
            var third = new LinkSpeedHistory();
            third.Initialize(path);
            Assert.Null(third.AverageMbps("f", Start.AddHours(2)));
        }
        finally
        {
            System.IO.Directory.Delete(folder, true);
        }
    }
}
