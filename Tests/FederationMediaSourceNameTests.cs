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
            MediaStreams = new[] { new MediaStream { Type = MediaStreamType.Video, Height = 2160 } },
        };

        var alternate = FederationMediaSourceNameFilter.BuildAlternate(source, "zander", 5);

        Assert.Equal(FederationMediaSourceProvider.BuildSourceId(source), alternate.Id);
        Assert.Equal("zander", alternate.Name);
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
        MediaStreams = new[] { new MediaStream { Type = MediaStreamType.Video, Height = height } },
    };

    [Fact]
    public void AlternateName_OnlyServer_IsJustTheServerName()
        => Assert.Equal("plex-source", FederationMediaSourceNameFilter.AlternateName(Copy(1080, 1_073_741_824), "plex-source", offline: false, needsDetail: false));

    [Fact]
    public void AlternateName_SeveralCopiesOnOneServer_AddsResolutionAndSize()
        => Assert.Equal("plex-source (2160p · 54.3 GB)", FederationMediaSourceNameFilter.AlternateName(Copy(2160, 58_300_000_000), "plex-source", offline: false, needsDetail: true));

    [Fact]
    public void AlternateName_OfflineServer_IsMarked()
    {
        Assert.Equal("plex-source (offline)", FederationMediaSourceNameFilter.AlternateName(Copy(1080, 1), "plex-source", offline: true, needsDetail: false));
        Assert.Equal("plex-source (1080p · 1 GB, offline)", FederationMediaSourceNameFilter.AlternateName(Copy(1080, 1_073_741_824), "plex-source", offline: true, needsDetail: true));
    }

    [Fact]
    public void AlternateName_MissingDetails_FallsBackToServerName()
        => Assert.Equal("plex-source", FederationMediaSourceNameFilter.AlternateName(new FederatedSource(), "plex-source", offline: false, needsDetail: true));
}
