using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The Beta review gate: pairing codes stay off even when a saved configuration
/// still says they were opted in. Direct links are deliberately unaffected.
/// </summary>
public class PairingReviewGateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void IsEnabled_RequiresBothOptInAndNoReviewLock(bool configuredOn, bool reviewLocked, bool expected)
        => Assert.Equal(expected, PairingService.IsEnabled(configuredOn, reviewLocked));

    [Fact]
    public void ReviewLock_IgnoresASavedOptIn()
    {
        var config = new PluginConfiguration { PairingOptIn = true };
        Assert.False(PairingService.IsEnabled(config));
    }
}
