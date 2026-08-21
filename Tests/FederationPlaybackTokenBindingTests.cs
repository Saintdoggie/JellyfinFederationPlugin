using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Pins the friendship binding on item-scoped playback tokens (0.0.78): a token
/// minted through a friend's <c>PlaybackToken</c> request carries that friend's
/// federation id, so stream-time validation can reject tokens whose minting
/// friendship no longer exists. Before this, an item token minted just before an
/// unfriend kept authorizing streams for up to 24 hours after the relationship
/// was removed.
/// </summary>
public class FederationPlaybackTokenBindingTests
{
    [Fact]
    public void Issue_BindsTokenToMintingFriend_TryValidateReturnsIt()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a");

        Assert.True(service.TryValidate(token, "ITEM-1", out var federationId));
        Assert.Equal("friend-a", federationId);
    }

    [Fact]
    public void TryValidate_WrongItem_Fails_EvenForMintingFriend()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a");

        Assert.False(service.TryValidate(token, "item-2", out _));
    }

    [Fact]
    public void TryValidate_UnknownToken_Fails()
    {
        var service = new FederationPlaybackTokenService();

        Assert.False(service.TryValidate("no-such-token", "item-1", out var federationId));
        Assert.Null(federationId);
    }
}
