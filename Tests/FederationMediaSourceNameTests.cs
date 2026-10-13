using Jellyfin.Plugin.Federation.Services;
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
}
