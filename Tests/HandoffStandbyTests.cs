using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class HandoffStandbyTests
{
    private const string Master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8192000,AVERAGE-BANDWIDTH=8192000,VIDEO-RANGE=SDR,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1920x1080\n/Plugins/Federation/HandoffPlaylist?u=https%3A%2F%2Fa.example%2Fmain.m3u8\n";

    private static List<KeyValuePair<string, StringValues>> Query(string text) => RemoteMediaPath.ParseQuery(text);

    private static MediaStream Sound(int index, string? language, int channels = 2, bool isDefault = false)
        => new() { Type = MediaStreamType.Audio, Index = index, Language = language, Channels = channels, IsDefault = isDefault };

    private static FederatedSource Copy(string server, long seconds, params MediaStream[] streams)
        => new() { ServerId = server, RemoteItemId = Guid.NewGuid(), RunTimeTicks = seconds * TimeSpan.TicksPerSecond, MediaStreams = streams };

    [Fact]
    public void Wanted_OnlyWithAutomaticChoice_OrWhenThisServerMayNotConvert()
    {
        Assert.False(HandoffStandby.Wanted(null));
        Assert.False(HandoffStandby.Wanted(new PluginConfiguration { LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow }));
        Assert.True(HandoffStandby.Wanted(new PluginConfiguration { SmartSourceSelection = true, LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow }));
        Assert.True(HandoffStandby.Wanted(new PluginConfiguration { LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll }));
        Assert.False(HandoffStandby.Wanted(new PluginConfiguration { SmartSourceSelection = true, UseFriendsTranscoders = false }));
    }

    [Fact]
    public void SameCut_NeedsBothRuntimes_WithinASecond()
    {
        var a = Copy("a", 5400);
        Assert.True(HandoffStandby.SameCut(a, Copy("b", 5400)));
        Assert.True(HandoffStandby.SameCut(a, new FederatedSource { RunTimeTicks = a.RunTimeTicks + TimeSpan.TicksPerSecond }));
        Assert.False(HandoffStandby.SameCut(a, Copy("b", 5402)));
        Assert.False(HandoffStandby.SameCut(a, new FederatedSource()));
        Assert.False(HandoffStandby.SameCut(new FederatedSource(), new FederatedSource()));
    }

    [Theory]
    [InlineData("?VideoCodec=h264&VideoBitrate=8000000&AllowVideoStreamCopy=false", true)]
    [InlineData("?VideoCodec=h264&VideoBitrate=8000000&AllowVideoStreamCopy=false&SegmentContainer=ts", true)]
    [InlineData("?VideoCodec=h264&VideoBitrate=8000000&TranscodeReasons=VideoCodecNotSupported", true)]
    [InlineData("?VideoCodec=h264&VideoBitrate=8000000&AllowVideoStreamCopy=false&SubtitleStreamIndex=-1", true)]
    [InlineData("?VideoCodec=hevc&VideoBitrate=8000000&AllowVideoStreamCopy=false&SegmentContainer=mp4", false)]
    [InlineData("?VideoCodec=h264&VideoBitrate=8000000&AllowVideoStreamCopy=false&SubtitleStreamIndex=3", false)]
    [InlineData("?VideoCodec=copy&VideoBitrate=8000000", false)]
    [InlineData("?VideoCodec=h264&VideoBitrate=8000000&TranscodeReasons=AudioCodecNotSupported", false)]
    [InlineData("?VideoCodec=h264&AllowVideoStreamCopy=false", false)]
    public void MidPlaySwitch_OnlyForConvertedTsVideoWithoutASubtitleTrack(string query, bool expected)
        => Assert.Equal(expected, HandoffStandby.AllowsMidPlaySwitch(Query(query), null));

    [Fact]
    public void MidPlaySwitch_AFileBiggerThanTheRequest_IsCertainlyConverted()
    {
        var query = Query("?VideoCodec=h264&VideoBitrate=8000000");
        Assert.True(HandoffStandby.AllowsMidPlaySwitch(query, 40_000_000));
        Assert.False(HandoffStandby.AllowsMidPlaySwitch(query, 4_000_000));
    }

    [Fact]
    public void Audio_MapsTheRequestedTrackToTheSameLanguageOnTheOtherCopy()
    {
        var from = Copy("a", 100, Sound(1, "eng", 6, true), Sound(2, "ger"));
        var to = Copy("b", 100, Sound(1, "deu"), Sound(2, "en", 2), Sound(3, "en", 6));

        Assert.Equal((true, 3), HandoffStandby.MapAudio(from, to, 1));
        Assert.Equal((true, 1), HandoffStandby.MapAudio(from, to, 2));
        Assert.Equal((false, (int?)null), HandoffStandby.MapAudio(from, to, 9));
        Assert.Equal((false, (int?)null), HandoffStandby.MapAudio(from, Copy("c", 100, Sound(1, "fre")), 1));
    }

    [Fact]
    public void Audio_WithoutARequestedTrack_BothDefaultsMustBeTheSameLanguage()
    {
        var from = Copy("a", 100, Sound(1, "eng", 6, true), Sound(2, "ger"));

        Assert.Equal((true, (int?)null), HandoffStandby.MapAudio(from, Copy("b", 100, Sound(1, "en", 2), Sound(2, "deu")), null));
        Assert.Equal((false, (int?)null), HandoffStandby.MapAudio(from, Copy("b", 100, Sound(1, "deu"), Sound(2, "en")), null));
        Assert.Equal((true, (int?)null), HandoffStandby.MapAudio(from, Copy("b", 100, Sound(1, "deu"), Sound(2, "en", 2, true)), -1));
    }

    [Fact]
    public void Audio_UnlabelledOrUnknownTracks_OnlyTheOneTrackCaseIsAccepted()
    {
        Assert.Equal((true, 4), HandoffStandby.MapAudio(Copy("a", 100, Sound(1, null)), Copy("b", 100, Sound(4, null)), 1));
        Assert.False(HandoffStandby.MapAudio(Copy("a", 100, Sound(1, null), Sound(2, null)), Copy("b", 100, Sound(1, null)), 1).Ok);
        Assert.False(HandoffStandby.MapAudio(Copy("a", 100, Sound(1, "eng")), new FederatedSource(), 1).Ok);
        Assert.False(HandoffStandby.MapAudio(new FederatedSource(), new FederatedSource(), null).Ok);
    }

    [Fact]
    public void Reduced_WhenTheStandbyLinkCarriesLess()
    {
        const string first = "https://a.example/x/main.m3u8?VideoBitrate=8000000&MaxStreamingBitrate=8192000";
        Assert.False(HandoffStandby.IsReduced(first, "https://b.example/x/main.m3u8?VideoBitrate=8000000&MaxStreamingBitrate=8192000"));
        Assert.True(HandoffStandby.IsReduced(first, "https://b.example/x/main.m3u8?VideoBitrate=3000000&MaxStreamingBitrate=8192000"));
        Assert.True(HandoffStandby.IsReduced(first, "https://b.example/x/main.m3u8?VideoBitrate=8000000&MaxStreamingBitrate=8192000&MaxHeight=1080"));
        Assert.False(HandoffStandby.IsReduced(first + "&MaxHeight=1080", "https://b.example/x/main.m3u8?VideoBitrate=8000000&MaxStreamingBitrate=8192000&MaxHeight=1080"));
    }

    [Fact]
    public void AddVariant_RepeatsTheFirstVariantAtTheStandbyAddress_Last()
    {
        const string standby = "/Plugins/Federation/HandoffPlaylist?u=https%3A%2F%2Fb.example%2Fmain.m3u8";
        var result = HandoffStandby.AddVariant(Master, standby);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(5, lines.Length);
        Assert.Equal(lines[1], lines[3]);
        Assert.Equal(standby, lines[4]);
        Assert.Contains("a.example", lines[2]);

        // Adding it again, or a broken address, changes nothing.
        Assert.Same(result, HandoffStandby.AddVariant(result, standby));
        Assert.Same(Master, HandoffStandby.AddVariant(Master, "x\n#EXT-X-ENDLIST"));
        Assert.Same(Master, HandoffStandby.AddVariant(Master, " "));
        Assert.Equal("#EXTM3U\n", HandoffStandby.AddVariant("#EXTM3U\n", standby));
    }

    [Fact]
    public void AddVariant_AfterTheQualityLevels_KeepsThemInOrder()
    {
        var ladder = HlsQualityLadder.Expand("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8192000,RESOLUTION=1920x1080\nmain.m3u8?VideoBitrate=8000000&AudioBitrate=192000&AllowVideoStreamCopy=false\n");
        var result = HandoffStandby.AddVariant(ladder, "standby.m3u8").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(result.Length > 5);
        Assert.Equal("standby.m3u8", result[^1]);
        Assert.Equal(result[1], result[^2]);
        Assert.Contains("FedRung=1", result[4]);
    }

    [Fact]
    public void PlaySession_IsReadFromAGatewayAddress()
    {
        Assert.Equal("abc", HandoffStandby.PlaySessionOf("https://b.example/Plugins/Federation/Peer/Play/t/videos/x/main.m3u8?VideoBitrate=1&PlaySessionId=abc"));
        Assert.Null(HandoffStandby.PlaySessionOf("https://b.example/main.m3u8"));
        Assert.Null(HandoffStandby.PlaySessionOf(null));
        Assert.Null(HandoffStandby.PlaySessionOf("https://b.example/main.m3u8?PlaySessionId=" + new string('x', 200)));
    }

    [Fact]
    public void Handoffs_StandbyGetsTheStop_AndBecomesCurrentOnlyWhenOffered()
    {
        var handoffs = new RemoteTranscodeHandoffs();
        handoffs.AddStandby("p", "b");
        Assert.False(handoffs.Activate("p", "b"));

        handoffs.Record("p", "a");
        Assert.False(handoffs.Activate("p", "b"));
        Assert.False(handoffs.Activate("other", "b"));

        handoffs.AddStandby("p", "b");
        handoffs.AddStandby("p", "a");
        Assert.Equal("a", handoffs.HandedOffTo("p"));
        Assert.True(handoffs.Activate("p", "b"));
        Assert.False(handoffs.Activate("p", "b"));
        Assert.Equal("b", handoffs.HandedOffTo("p"));
        Assert.Equal("b", handoffs.TakePing("p", isPaused: true));

        // The player may go back to the first server.
        Assert.True(handoffs.Activate("p", "a"));
        Assert.Equal(new[] { "a", "b" }, handoffs.TakeStopAll("p"));
        Assert.Empty(handoffs.TakeStopAll("p"));
    }

    [Fact]
    public void Handoffs_APlayThatStartedOnAnotherServer_StopsBoth()
    {
        var handoffs = new RemoteTranscodeHandoffs();
        handoffs.Record("p", "a");
        handoffs.Record("p", "b");

        Assert.Equal(new[] { "b", "a" }, handoffs.TakeStopAll("p"));
    }

    [Fact]
    public void GrantFor_FindsACopyUnderItsOwnIdOrTheItemId_NeverAnotherServers()
    {
        // Fresh ids only: the grant table is shared with tests running alongside.
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var first = Copy("a", 100);
        var second = Copy("b", 100);
        PeerPlaybackGrants.Record(user, item, item.ToString("N"), "a", first.RemoteItemId, "token-a");
        PeerPlaybackGrants.Record(user, item, FederationMediaSourceProvider.BuildSourceId(second), "b", second.RemoteItemId, "token-b");

        Assert.Equal("token-a", FederationRemoteTranscodeFilter.GrantFor(user, item, first).Grant?.Token);
        Assert.Equal("token-b", FederationRemoteTranscodeFilter.GrantFor(user, item, second).Grant?.Token);
        Assert.Null(FederationRemoteTranscodeFilter.GrantFor(user, item, Copy("c", 100)).Grant);
        Assert.Null(FederationRemoteTranscodeFilter.GrantFor(Guid.NewGuid(), item, second).Grant);
    }
}
