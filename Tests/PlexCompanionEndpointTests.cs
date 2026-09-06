using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlexCompanionEndpointTests
{
    [Fact]
    public void TryGetBaseUrl_PrefersStoredCompanionUrl()
    {
        var friend = new RemoteServer
        {
            Kind = ServerKind.Plex,
            Url = "https://name.ts.net/plex/peer-1",
            CompanionUrl = "https://name.ts.net/"
        };

        Assert.Equal("https://name.ts.net", PlexCompanionEndpoint.TryGetBaseUrl(friend));
        Assert.True(PlexCompanionEndpoint.CanReceivePoolInvite(friend));
        Assert.Equal("https://name.ts.net/api/pools/invite", PlexCompanionEndpoint.PoolInviteUrl("https://name.ts.net"));
    }

    [Fact]
    public void TryGetBaseUrl_DerivesFunnelRoot_FromPlexRelayPath()
    {
        var friend = new RemoteServer
        {
            Kind = ServerKind.Plex,
            Url = "https://freakbob.tail4e0b6f.ts.net/plex/abc"
        };

        Assert.Equal("https://freakbob.tail4e0b6f.ts.net", PlexCompanionEndpoint.TryGetBaseUrl(friend));
    }

    [Fact]
    public void TryGetBaseUrl_IsNull_ForDirectPlexRelayWithoutCompanion()
    {
        var friend = new RemoteServer
        {
            Kind = ServerKind.Plex,
            Url = "https://1-2-3-4.hash.plex.direct:32400"
        };

        Assert.Null(PlexCompanionEndpoint.TryGetBaseUrl(friend));
        Assert.False(PlexCompanionEndpoint.CanReceivePoolInvite(friend));
    }

    [Fact]
    public void TryGetBaseUrl_TreatsFunnelRootUrl_AsCompanion()
    {
        var friend = new RemoteServer
        {
            Kind = ServerKind.Plex,
            Url = "https://freakbob.tail4e0b6f.ts.net"
        };

        Assert.Equal("https://freakbob.tail4e0b6f.ts.net", PlexCompanionEndpoint.TryGetBaseUrl(friend));
    }

    [Fact]
    public void TryGetBaseUrl_IsNull_ForJellyfinFriends()
    {
        var friend = new RemoteServer { Kind = ServerKind.Jellyfin, Url = "https://jf.example:8096" };
        Assert.Null(PlexCompanionEndpoint.TryGetBaseUrl(friend));
    }
}
