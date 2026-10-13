using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationMediaSourceNameTests
{
    [Fact]
    public void PrimarySourceName_SingleServer_IsJustTheServerName()
        => Assert.Equal("plexpal", FederationMediaSourceProvider.PrimarySourceName("plexpal", 1));

    [Fact]
    public void PrimarySourceName_SeveralServers_MarksThePrimary()
        => Assert.Equal("plexpal (primary)", FederationMediaSourceProvider.PrimarySourceName("plexpal", 2));
}
