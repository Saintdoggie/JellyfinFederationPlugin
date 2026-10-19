using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FederationUserSharingTests
{
    [Fact]
    public void NewFriend_SeesNoUsers()
    {
        Assert.False(FederationPeerAccessService.IsUserVisible(new RemoteServer(), Guid.NewGuid()));
    }

    [Fact]
    public void FriendSavedBeforeUserSharingExisted_SeesNoUsers()
    {
        var old = JsonSerializer.Deserialize<RemoteServer>("{\"Name\":\"Old friend\",\"ShareAllLibraries\":true}")!;

        Assert.False(old.ShareAllUsers);
        Assert.False(FederationPeerAccessService.IsUserVisible(old, Guid.NewGuid()));
    }

    [Fact]
    public void ShareAllUsers_ShowsEveryUser()
    {
        var friend = new RemoteServer { ShareAllUsers = true };

        Assert.True(FederationPeerAccessService.IsUserVisible(friend, Guid.NewGuid()));
    }

    [Fact]
    public void SelectedUsers_ShowsOnlyThoseInAnyGuidFormat()
    {
        var shared = Guid.NewGuid();
        var friend = new RemoteServer { SharedUserIds = new List<string> { shared.ToString("D").ToUpperInvariant() } };

        Assert.True(FederationPeerAccessService.IsUserVisible(friend, shared));
        Assert.False(FederationPeerAccessService.IsUserVisible(friend, Guid.NewGuid()));
    }

    [Fact]
    public void MalformedSharedId_IsIgnored()
    {
        var friend = new RemoteServer { SharedUserIds = new List<string> { "not-a-guid", string.Empty } };

        Assert.False(FederationPeerAccessService.IsUserVisible(friend, Guid.Empty));
    }

    [Fact]
    public void FriendList_SharedWithEveryoneByDefault_IncludingFriendsSavedBeforeTheSetting()
    {
        var other = new RemoteServer { Id = "b", Name = "Other" };

        Assert.True(FederationPeerAccessService.IsFriendVisible(new RemoteServer { Id = "a" }, other));
        var old = JsonSerializer.Deserialize<RemoteServer>("{\"Id\":\"a\",\"Name\":\"Old friend\"}")!;
        Assert.True(old.ShareAllFriends);
        Assert.True(FederationPeerAccessService.IsFriendVisible(old, other));
    }

    [Fact]
    public void FriendList_SelectedFriendsOnly()
    {
        var caller = new RemoteServer { Id = "a", ShareAllFriends = false, SharedFriendIds = new List<string> { "b" } };

        Assert.True(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "b" }));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "c" }));
        Assert.False(FederationPeerAccessService.IsFriendVisible(new RemoteServer { Id = "a", ShareAllFriends = false }, new RemoteServer { Id = "b" }));
    }

    [Fact]
    public void FriendList_NeverShowsTheCallerDisabledFriendsPlexOrCompanion()
    {
        var caller = new RemoteServer { Id = "a" };

        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, caller));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "b", Enabled = false }));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "p", Kind = ServerKind.Plex }));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "c", Kind = ServerKind.Companion }));
    }

    [Fact]
    public void ExpiredFriend_SeesNoUsersOrFriends()
    {
        var caller = new RemoteServer { Id = "a", ShareAllUsers = true, SharingExpiresUtc = DateTime.UtcNow.AddMinutes(-1) };

        Assert.True(FederationPeerAccessService.IsSharingExpired(caller));
        Assert.False(FederationPeerAccessService.IsUserVisible(caller, Guid.NewGuid()));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "b" }));
        caller.SharingExpiresUtc = DateTime.UtcNow.AddDays(1);
        Assert.True(FederationPeerAccessService.IsUserVisible(caller, Guid.NewGuid()));
    }
}
