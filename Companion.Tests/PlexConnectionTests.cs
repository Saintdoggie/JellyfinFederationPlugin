using FederationCompanion;

namespace FederationCompanion.Tests;

public sealed class PlexConnectionTests
{
    [Fact]
    public void SelectFederationConnection_PrefersPublicDirectHttpsOverLanAndRelay()
    {
        var server = new PlexResource
        {
            Connections = new List<PlexConnection>
            {
                new() { Uri = "http://192.168.1.20:32400", Local = true },
                new() { Uri = "https://relay.plex.example", Relay = true },
                new() { Uri = "https://public.plex.direct:32400" }
            }
        };

        var selected = PlexAuth.SelectFederationConnection(server);

        Assert.NotNull(selected);
        Assert.Equal("https://public.plex.direct:32400", selected.Uri);
    }

    [Fact]
    public void SelectFederationConnection_UsesSecureRelayBeforeLanOnlyAddress()
    {
        var server = new PlexResource
        {
            Connections = new List<PlexConnection>
            {
                new() { Uri = "http://10.0.0.5:32400", Local = true },
                new() { Uri = "https://relay.plex.example", Relay = true }
            }
        };

        Assert.True(PlexAuth.SelectFederationConnection(server)!.Relay);
    }
}
