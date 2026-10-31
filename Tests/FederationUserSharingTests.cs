using System;
using System.Collections.Generic;
using System.IO;
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
    public void MainSave_PreservesUserSharingAndPeerUsersFilters()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Configuration", "FederationPluginController.cs")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "Configuration", "FederationPluginController.cs"));
        Assert.Contains("server.ShareAllUsers = oldServer.ShareAllUsers;", src);
        Assert.Contains("server.SharedUserIds = oldServer.SharedUserIds;", src);
        Assert.Contains("FederationPeerAccessService.IsUserVisible(caller, guid)", src);
    }

    [Fact]
    public void Sanitize_ShowsTheUserSharingChoice()
    {
        var server = new RemoteServer { ShareAllUsers = true, SharedUserIds = new List<string> { "u1" } };

        var shown = FederationServerPresentation.Sanitize(server, null);

        Assert.Equal(true, shown.GetType().GetProperty("ShareAllUsers")!.GetValue(shown));
        Assert.Equal(new List<string> { "u1" }, shown.GetType().GetProperty("SharedUserIds")!.GetValue(shown));
    }
}
