using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>Small limits added after the code audit: one friend cannot grow a store without bound.</summary>
public class AuditHardeningTests
{
    [Fact]
    public void SessionTokens_OneFriendKeepsOnlyTheNewestTokensUpToTheCap()
    {
        var service = new FederationUserSessionTokenService();
        var tokens = Enumerable.Range(0, FederationUserSessionTokenService.MaxTokensPerFriend + 25)
            .Select(i => service.Issue("friend-a", "user-" + i))
            .ToList();

        // The newest token still works; the very first one was dropped to make room.
        Assert.True(service.TryValidate(tokens.Last(), out var federationId, out _));
        Assert.Equal("friend-a", federationId);
        Assert.False(service.TryValidate(tokens.First(), out _, out _));
    }

    [Fact]
    public void SessionTokens_AnotherFriendIsNotAffectedByTheCap()
    {
        var service = new FederationUserSessionTokenService();
        var other = service.Issue("friend-b", "user-b");
        for (var i = 0; i < FederationUserSessionTokenService.MaxTokensPerFriend + 25; i++)
        {
            service.Issue("friend-a", "user-" + i);
        }

        Assert.True(service.TryValidate(other, out var federationId, out _));
        Assert.Equal("friend-b", federationId);
    }
}
