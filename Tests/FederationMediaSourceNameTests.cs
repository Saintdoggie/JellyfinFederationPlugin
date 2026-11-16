using System;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationMediaSourceNameTests
{
    [Fact]
    public void PrimarySourceName_SingleServer_IsJustTheServerName()
        => Assert.Equal("plex-source", FederationMediaSourceProvider.PrimarySourceName("plex-source", 1));

    [Fact]
    public void PrimarySourceName_SeveralServers_MarksThePrimary()
        => Assert.Equal("plex-source (primary)", FederationMediaSourceProvider.PrimarySourceName("plex-source", 2));

    [Fact]
    public void BuildAlternate_UsesThePlaybackSourceId_ServerName_AndCachedStreams()
    {
        var source = new FederatedSource
        {
            ServerId = "server-b",
            RemoteItemId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Container = "mkv",
            Size = 12345,
            Bitrate = 8_000_000,
            RunTimeTicks = 99,
            MediaStreams = new[] { new MediaStream { Type = MediaStreamType.Video, Height = 2160, BitRate = 8_000_000, Codec = "hevc" } },
        };

        var alternate = FederationMediaSourceNameFilter.BuildAlternate(source, "zander", 5);

        Assert.Equal(FederationMediaSourceProvider.BuildSourceId(source), alternate.Id);
        Assert.Equal("2160p · 8 Mbps · HEVC · ZANDER", alternate.Name);
        Assert.Equal("mkv", alternate.Container);
        Assert.Equal(99, alternate.RunTimeTicks);
        Assert.Equal(2160, Assert.Single(alternate.MediaStreams).Height);
        Assert.False(alternate.SupportsDirectPlay);
        Assert.True(alternate.SupportsDirectStream);
    }

    [Fact]
    public void BuildAlternate_OverCapCannotClaimDirectStream()
    {
        var alternate = FederationMediaSourceNameFilter.BuildAlternate(new FederatedSource(), "friend", 5, fitsBandwidth: false);
        Assert.False(alternate.SupportsDirectPlay);
        Assert.False(alternate.SupportsDirectStream);
        Assert.True(alternate.SupportsTranscoding);
    }

    [Fact]
    public void BuildAlternate_FallsBackToTheItemRuntime_AndToNoStreams()
    {
        var alternate = FederationMediaSourceNameFilter.BuildAlternate(new FederatedSource { ServerId = "s", RemoteItemId = Guid.NewGuid() }, "friend", 5);
        Assert.Equal(5, alternate.RunTimeTicks);
        Assert.Empty(alternate.MediaStreams);
    }

    private static FederatedSource Copy(int height, long bytes) => new()
    {
        ServerId = "s",
        RemoteItemId = Guid.NewGuid(),
        Size = bytes,
        Bitrate = 8_000_000,
        MediaStreams = new[] { new MediaStream { Type = MediaStreamType.Video, Height = height, Codec = "hevc" } },
    };

    [Fact]
    public void AlternateName_AlwaysListsQualityThenCodecThenServerAbbreviation()
        => Assert.Equal("1080p · 8 Mbps · HEVC · PS", FederationMediaSourceNameFilter.AlternateName(Copy(1080, 1_073_741_824), "plex-source", offline: false, needsDetail: false));

    [Fact]
    public void AlternateName_UsesVideoBitrateAndFractionalMbps()
    {
        var source = Copy(2160, 58_300_000_000);
        source.Bitrate = 52_000_000;
        source.MediaStreams![0].BitRate = 45_200_000;
        Assert.Equal("2160p · 45.2 Mbps · HEVC · VPS", FederationMediaSourceNameFilter.AlternateName(source, "vps", offline: false, needsDetail: true));
    }

    [Fact]
    public void AlternateName_OfflineServer_IsMarked()
    {
        Assert.Equal("1080p · 8 Mbps · HEVC · PS · offline", FederationMediaSourceNameFilter.AlternateName(Copy(1080, 1), "plex-source", offline: true, needsDetail: false));
        Assert.Equal("1080p · 8 Mbps · HEVC · PS · offline", FederationMediaSourceNameFilter.AlternateName(Copy(1080, 1_073_741_824), "plex-source", offline: true, needsDetail: true));
    }

    [Fact]
    public void AlternateName_MissingDetails_FallsBackToServerAbbreviation()
        => Assert.Equal("PS", FederationMediaSourceNameFilter.AlternateName(new FederatedSource(), "plex-source", offline: false, needsDetail: true));

    [Fact]
    public void ServerAbbreviation_KeepsShortNamesAndUsesInitialsForLongNames()
    {
        Assert.Equal("VPS", FederationMediaSourceNameFilter.ServerAbbreviation("vps"));
        Assert.Equal("NETFLIX++", FederationMediaSourceNameFilter.ServerAbbreviation("netflix++"));
        Assert.Equal("IJ", FederationMediaSourceNameFilter.ServerAbbreviation("Inspired Jellyfin"));
    }
}
