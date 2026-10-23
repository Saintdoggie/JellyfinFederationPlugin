using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FederationFriendSharingTests
{
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
    public void FriendList_NeverShowsTheCallerDisabledPairedPlexOrCompanion()
    {
        var caller = new RemoteServer { Id = "a" };

        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, caller));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "b", Enabled = false }));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "d", Paired = true }));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "p", Kind = ServerKind.Plex }));
        Assert.False(FederationPeerAccessService.IsFriendVisible(caller, new RemoteServer { Id = "c", Kind = ServerKind.Companion }));

        var picked = new RemoteServer { Id = "a", ShareAllFriends = false, SharedFriendIds = new List<string> { "b" } };
        Assert.False(FederationPeerAccessService.IsFriendVisible(picked, new RemoteServer { Id = "b", Enabled = false }));
    }

    [Fact]
    public void MainSave_PreservesFriendSharingAndFriendsListFilters()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Configuration", "FederationPluginController.cs")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "Configuration", "FederationPluginController.cs"));
        Assert.Contains("server.ShareAllFriends = oldServer.ShareAllFriends;", src);
        Assert.Contains("server.SharedFriendIds = oldServer.SharedFriendIds;", src);
        Assert.Contains("s.ShareAllFriends,", src);
        Assert.Contains("s.SharedFriendIds,", src);
        Assert.Contains("FederationPeerAccessService.IsFriendVisible(caller, s)", src);
    }
}
