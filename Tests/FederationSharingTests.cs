using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers per-friend sharing control: which of this server's own libraries a
/// specific friend can see. Purely local state under the federation-token model
/// - <see cref="FederationPeerAccessService"/> enforces
/// <see cref="RemoteServer.ShareAllLibraries"/>/<see cref="RemoteServer.SharedLibraryFolderIds"/>
/// itself, server-side, on every <c>Peer/*</c> request, so unlike the old model
/// (a dedicated local Jellyfin user + native EnabledFolders policy, pushed to the
/// friend so their plugin queried as that user) there is nothing to notify a
/// friend of and no Jellyfin user account involved at all.
/// </summary>
[Collection("PluginInstance")]
public class FederationSharingTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationFriendService _service;

    public FederationSharingTests()
    {
        _plugin = new RealPluginInstance();
        _plugin.Configuration.ServerUrl = "http://local.test:8096";
        _plugin.Configuration.LocalFederationId = "self-fed-id";

        var authManager = new Mock<IAuthenticationManager>();
        var appHost = new Mock<IServerApplicationHost>();
        appHost.SetupGet(h => h.FriendlyName).Returns("This Server");

        var libraryManager = new Mock<ILibraryManager>();
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var federationManager = new FederationLibraryManager(libraryManager.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var httpContextAccessor = new Mock<IHttpContextAccessor>();

        _service = new FederationFriendService(
            NullLogger<FederationFriendService>.Instance,
            authManager.Object,
            appHost.Object,
            federationManager,
            httpContextAccessor.Object,
            clientFactory.Object);
    }

    public void Dispose()
    {
        FederationFriendService.HttpClientOverride = null;
        _plugin.Dispose();
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_UnknownFriend_Fails()
    {
        var (success, message) = await _service.UpdateFriendSharingAsync("no-such-friend", true, new List<string>());

        Assert.False(success);
        Assert.Equal("Friend not found.", message);
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_Narrowing_SavesLocally_NoLocalAccountNeeded()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            Url = "http://bob.example",
            ApiKey = "key-1"
        });

        var folderId = Guid.NewGuid().ToString();

        var (success, message) = await _service.UpdateFriendSharingAsync(
            "friend-1",
            shareAll: false,
            folderIds: new List<string> { folderId });

        Assert.True(success, message);

        var server = _plugin.Configuration.RemoteServers[0];
        Assert.False(server.ShareAllLibraries);
        Assert.Contains(folderId, server.SharedLibraryFolderIds);
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_ExcludedItemIds_SavesLocally()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            Url = "http://bob.example",
            ApiKey = "key-1"
        });

        var excludedId = Guid.NewGuid().ToString("N");

        var (success, message) = await _service.UpdateFriendSharingAsync(
            "friend-1",
            shareAll: true,
            folderIds: new List<string>(),
            excludedItemIds: new List<string> { excludedId });

        Assert.True(success, message);
        Assert.Contains(excludedId, _plugin.Configuration.RemoteServers[0].ExcludedItemIds);
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_ShareEverything_SavesLocally()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            Url = "http://bob.example",
            ApiKey = "key-1"
        });

        var (success, message) = await _service.UpdateFriendSharingAsync("friend-1", true, new List<string>());

        Assert.True(success, message);
        Assert.True(_plugin.Configuration.RemoteServers[0].ShareAllLibraries);
    }
}
