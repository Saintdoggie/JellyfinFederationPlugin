using FederationCompanion;
using Xunit;

namespace FederationCompanion.Tests;

public class PlexRemoteEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:32400")]
    [InlineData("http://192.168.1.20:32400")]
    [InlineData("http://10.0.0.8:32400")]
    [InlineData("http://100.64.12.3:32400")]
    [InlineData("http://100.100.1.1:32400")]
    [InlineData("http://localhost:32400")]
    [InlineData("http://plex.local:32400")]
    public void LanAndTailscaleAddresses_ArePrivate(string url)
        => Assert.True(PlexRemoteEndpoint.IsLanOrPrivateHost(url));

    [Theory]
    [InlineData("https://name.tail12345.ts.net")]
    [InlineData("https://1-2-3-4.abcdef.plex.direct:32400")]
    [InlineData("https://relay.plex.direct:443")]
    public void FunnelAndPlexDirect_AreNotPrivate(string url)
        => Assert.False(PlexRemoteEndpoint.IsLanOrPrivateHost(url));

    [Fact]
    public void SelectFriendFacing_PrefersPublicHttps_OverRelay_AndSkipsLan()
    {
        var connections = new[]
        {
            new PlexConnection { Uri = "http://192.168.1.20:32400", Local = true, Protocol = "http" },
            new PlexConnection { Uri = "http://100.64.1.20:32400", Local = false, Protocol = "http" },
            new PlexConnection { Uri = "https://relay.plex.direct:443", Local = false, Relay = true, Protocol = "https" },
            new PlexConnection { Uri = "https://1-2-3-4.hash.plex.direct:32400", Local = false, Relay = false, Protocol = "https" }
        };

        var selected = PlexRemoteEndpoint.SelectFriendFacing(connections);

        Assert.NotNull(selected);
        Assert.Equal("https://1-2-3-4.hash.plex.direct:32400", selected!.Uri);
    }

    [Fact]
    public void SelectFriendFacing_FallsBackToRelay_WhenNoPublicDirect()
    {
        var connections = new[]
        {
            new PlexConnection { Uri = "http://10.0.0.5:32400", Local = true, Protocol = "http" },
            new PlexConnection { Uri = "https://relay.plex.direct:443", Local = false, Relay = true, Protocol = "https" }
        };

        var selected = PlexRemoteEndpoint.SelectFriendFacing(connections);

        Assert.NotNull(selected);
        Assert.True(selected!.Relay);
    }

    [Fact]
    public void SelectFriendFacing_ReturnsNull_WhenOnlyLanExists()
    {
        var connections = new[]
        {
            new PlexConnection { Uri = "http://192.168.1.20:32400", Local = true, Protocol = "http" },
            new PlexConnection { Uri = "http://100.64.1.20:32400", Local = false, Protocol = "http" }
        };

        Assert.Null(PlexRemoteEndpoint.SelectFriendFacing(connections));
    }

    [Fact]
    public void PublicHttpsUrl_RejectsTailscaleCgnat()
    {
        Assert.False(PlexRemoteEndpoint.IsPublicHttpsUrl("https://100.64.1.20"));
        Assert.True(PlexRemoteEndpoint.IsPublicHttpsUrl("https://name.tail12345.ts.net"));
        Assert.False(PlexRemoteEndpoint.IsPublicHttpsUrl("http://name.tail12345.ts.net"));
    }
}
