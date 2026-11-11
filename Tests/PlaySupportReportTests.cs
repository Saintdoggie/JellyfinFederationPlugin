using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlaySupportReportTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static PlaySupportFile File(string name, string container, string video, string? audio, int? bitDepth = 8, string origin = "This server")
    {
        var streams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Codec = video, BitDepth = bitDepth, Index = 0 } };
        if (audio != null)
        {
            streams.Add(new MediaStream { Type = MediaStreamType.Audio, Codec = audio, Language = "eng", IsDefault = true, Index = 1 });
        }

        return new PlaySupportFile(name, origin, container, streams);
    }

    private static string Fit(PlaySupportResult result, string label, string playerId)
    {
        var index = PlaySupportReport.Players.ToList().FindIndex(p => p.Id == playerId);
        return result.Formats.Single(f => f.Label == label).Players[index];
    }

    [Fact]
    public void PlainMp4_PlaysEverywhere()
    {
        var result = PlaySupportReport.Build(new[] { File("A", "mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac") }, Now, false);

        Assert.Equal(1, result.FilesChecked);
        Assert.All(result.Players, p => Assert.Equal(1, p.PlaysAsStored));
        Assert.Equal("H.264, AAC, in MP4", result.Formats.Single().Label);
        Assert.All(result.Formats.Single().Players, word => Assert.Equal("plays", word));
    }

    [Fact]
    public void HevcTenBitWithDts_IsFlaggedForBrowsers_SoundOnlyWhereThePicturePlays()
    {
        var result = PlaySupportReport.Build(new[] { File("B", "matroska,webm", "hevc", "dts", 10) }, Now, false);
        const string label = "HEVC 10-bit, DTS, in MKV";

        Assert.Equal("no", Fit(result, label, "chrome"));
        Assert.Equal("no", Fit(result, label, "firefox"));
        Assert.Equal("sound", Fit(result, label, "safari"));
        Assert.Equal("sound", Fit(result, label, "smarttv"));
        Assert.Equal("plays", Fit(result, label, "androidtv"));
        Assert.Equal("plays", Fit(result, label, "desktop"));

        var chrome = result.Players.Single(p => p.Id == "chrome");
        Assert.Equal(1, chrome.NeedsConverting);
        Assert.Single(chrome.TopReasons);
        Assert.Contains("HEVC", chrome.TopReasons[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TenBitH264_DoesNotPlayWhereOnlyEightBitDoes()
    {
        var result = PlaySupportReport.Build(new[] { File("C", "matroska", "h264", "aac", 10) }, Now, false);

        Assert.Equal("no", Fit(result, "H.264 10-bit, AAC, in MKV", "chrome"));
        Assert.Equal("plays", Fit(result, "H.264 10-bit, AAC, in MKV", "androidtv"));
    }

    [Fact]
    public void MkvInFirefox_IsOnlyAWrapperChange()
    {
        var result = PlaySupportReport.Build(new[] { File("D", "matroska,webm", "h264", "aac") }, Now, false);

        Assert.Equal("sound", Fit(result, "H.264, AAC, in MKV", "firefox"));
        Assert.Equal(1, result.Players.Single(p => p.Id == "firefox").SoundOrWrapperOnly);
    }

    [Fact]
    public void TheMatroskaWebmPair_IsWebmOnlyForWebmCodecs()
    {
        var result = PlaySupportReport.Build(new[] { File("E", "mkv,webm", "vp9", "opus") }, Now, false);

        Assert.Equal("plays", Fit(result, "VP9, OPUS, in WEBM", "firefox"));
    }

    [Fact]
    public void FilesGroupByFormat_LeastPlayableFirst_WithAFewExamples()
    {
        var files = Enumerable.Range(0, 5).Select(i => File("Easy " + i, "mp4", "h264", "aac"))
            .Concat(new[] { File("Hard", "matroska", "hevc", "truehd", 10, "Friend A") });

        var result = PlaySupportReport.Build(files, Now, true);

        Assert.True(result.Truncated);
        Assert.Equal(6, result.FilesChecked);
        Assert.Equal(2, result.Formats.Count);
        Assert.Equal("HEVC 10-bit, TrueHD, in MKV", result.Formats[0].Label);
        Assert.Equal(new[] { "Friend A: Hard" }, result.Formats[0].Examples);
        Assert.Equal(5, result.Formats[1].Count);
        Assert.Equal(PlaySupportReport.ExamplesPerFormat, result.Formats[1].Examples.Count);
    }

    [Fact]
    public void AFileWithNoKnownPicture_IsCountedButNotJudged()
    {
        var files = new[]
        {
            new PlaySupportFile("Unknown", "This server", "mkv", null),
            new PlaySupportFile("Sound only", "This server", "mkv", new[] { new MediaStream { Type = MediaStreamType.Audio, Codec = "aac" } })
        };

        var result = PlaySupportReport.Build(files, Now, false);

        Assert.Equal(2, result.FilesChecked);
        Assert.Equal(2, result.Undescribed);
        Assert.Empty(result.Formats);
        Assert.All(result.Players, p => Assert.Equal(0, p.PlaysAsStored + p.SoundOrWrapperOnly + p.NeedsConverting));
    }

    [Fact]
    public void EveryPlayer_HasAUniqueIdAndAName()
    {
        Assert.Equal(PlaySupportReport.Players.Count, PlaySupportReport.Players.Select(p => p.Id).Distinct().Count());
        Assert.All(PlaySupportReport.Players, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }
}
