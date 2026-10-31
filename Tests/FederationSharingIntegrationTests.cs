using System;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FederationSharingIntegrationTests
{
    [Fact]
    public void EndedFriendship_AlsoHidesUsersAndFriends()
    {
        var caller = new RemoteServer { Id = "a", ShareAllUsers = true, SharingExpiresUtc = DateTime.UtcNow.AddMinutes(-1) };
        var other = new RemoteServer { Id = "b" };

        Assert.False(FederationPeerAccessService.IsUserVisible(caller, Guid.NewGuid()));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, other));

        caller.SharingExpiresUtc = DateTime.UtcNow.AddDays(1);
        Assert.True(FederationPeerAccessService.IsUserVisible(caller, Guid.NewGuid()));
        Assert.True(FederationPeerAccessService.IsFriendVisible(caller, other));
    }
}
