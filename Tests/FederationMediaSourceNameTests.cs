using System;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationMediaSourceNameTests
{
    [Fact]
    public void PrimarySourceName_SingleServer_IsJustTheServerName()
        => Assert.Equal("freakbob", FederationMediaSourceProvider.PrimarySourceName("freakbob", 1));

    [Fact]
    public void PrimarySourceName_SeveralServers_MarksThePrimary()
        => Assert.Equal("freakbob (primary)", FederationMediaSourceProvider.PrimarySourceName("freakbob", 2));

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
    }

    [Fact]
    public void BuildAlternate_FallsBackToTheItemRuntime_AndToNoStreams()
    {
        var alternate = FederationMediaSourceNameFilter.BuildAlternate(new FederatedSource { ServerId = "s", RemoteItemId = Guid.NewGuid() }, "friend", 5);
        Assert.Equal(5, alternate.RunTimeTicks);
        Assert.Empty(alternate.MediaStreams);
    }
}
