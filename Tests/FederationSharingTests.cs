using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers per-friend sharing control: which of this server's own libraries a
/// specific friend can see, enforced via an existing local user the admin picks
/// (Jellyfin's own EnabledFolders policy, not anything this plugin polices itself),
/// plus the wire push/receive that tells the friend which user id to query as. The
/// admin picks an existing user rather than the plugin creating one, because
/// IUserManager.CreateUserAsync does not reliably work on every Jellyfin build (see
/// the comment on FederationFriendService.UpdateFriendSharingAsync) - reusing an
/// account Jellyfin's own admin UI already created successfully sidesteps that.
/// </summary>
[Collection("PluginInstance")]
public class FederationSharingTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly Mock<IUserManager> _userManager;
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
        var federationManager = new FederationLibraryManager(libraryManager.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor);

        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        _userManager = new Mock<IUserManager>();

        _service = new FederationFriendService(
            NullLogger<FederationFriendService>.Instance,
            authManager.Object,
            appHost.Object,
            federationManager,
            httpContextAccessor.Object,
            clientFactory.Object,
            _userManager.Object,
            libraryManager.Object);
    }

    public void Dispose()
    {
        FederationFriendService.HttpClientOverride = null;
        _plugin.Dispose();
    }

    private void UseFakeHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        FederationFriendService.HttpClientOverride = new HttpClient(new FakeHandler(responder));
    }

    private static Jellyfin.Database.Implementations.Entities.User MakeStubUser(string username)
        => (Jellyfin.Database.Implementations.Entities.User)Activator.CreateInstance(
            typeof(Jellyfin.Database.Implementations.Entities.User),
            username,
            "Default",
            "Default")!;

    [Fact]
    public async Task UpdateFriendSharingAsync_UnknownFriend_Fails()
    {
        var (success, message) = await _service.UpdateFriendSharingAsync("no-such-friend", true, new List<string>(), string.Empty, CancellationToken.None);

        Assert.False(success);
        Assert.Equal("Friend not found.", message);
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_Narrowing_WithNoLocalUserPicked_Fails()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "friend-1", Name = "Bob's Server" });

        var (success, message) = await _service.UpdateFriendSharingAsync("friend-1", false, new List<string>(), string.Empty, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("Pick a local account", message);
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_Narrowing_WithUnknownLocalUser_Fails()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "friend-1", Name = "Bob's Server" });
        var missingUserId = Guid.NewGuid();
        _userManager.Setup(m => m.GetUserById(missingUserId)).Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var (success, message) = await _service.UpdateFriendSharingAsync("friend-1", false, new List<string>(), missingUserId.ToString(), CancellationToken.None);

        Assert.False(success);
        Assert.Equal("That local account no longer exists.", message);
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_Narrowing_AppliesEnabledFoldersToThePickedUser_AndPushesTheUpdate()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            Url = "http://bob.example",
            ApiKey = "key-1",
            FederationId = "bob-fed-id"
        });

        var pickedUserId = Guid.NewGuid();
        _userManager.Setup(m => m.GetUserById(pickedUserId)).Returns(MakeStubUser("kids-account"));

        UserPolicy? appliedPolicy = null;
        _userManager
            .Setup(m => m.UpdatePolicyAsync(pickedUserId, It.IsAny<UserPolicy>()))
            .Callback<Guid, UserPolicy>((_, p) => appliedPolicy = p)
            .Returns(Task.CompletedTask);

        var folderId = Guid.NewGuid();
        var pushed = new List<string>();
        UseFakeHttp(req =>
        {
            pushed.Add(req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (success, message) = await _service.UpdateFriendSharingAsync(
            "friend-1",
            shareAll: false,
            folderIds: new List<string> { folderId.ToString() },
            localUserId: pickedUserId.ToString(),
            CancellationToken.None);

        Assert.True(success, message);

        var server = _plugin.Configuration.RemoteServers[0];
        Assert.False(server.ShareAllLibraries);
        Assert.Contains(folderId.ToString(), server.SharedLibraryFolderIds);
        Assert.Equal(pickedUserId.ToString(), server.LocalShareUserId);

        Assert.NotNull(appliedPolicy);
        Assert.False(appliedPolicy!.EnableAllFolders);
        Assert.Contains(folderId, appliedPolicy.EnabledFolders);
        Assert.False(appliedPolicy.IsAdministrator);

        Assert.Contains(pushed, u => u == "http://bob.example/Plugins/Federation/Friends/SharedUserUpdate");
    }

    [Fact]
    public async Task UpdateFriendSharingAsync_ShareEverything_NeedsNoLocalUser_AndDoesNotPushAnUpdate()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            Url = "http://bob.example",
            ApiKey = "key-1"
        });

        var (success, message) = await _service.UpdateFriendSharingAsync("friend-1", true, new List<string>(), string.Empty, CancellationToken.None);

        Assert.True(success, message);
        Assert.True(_plugin.Configuration.RemoteServers[0].ShareAllLibraries);
        _userManager.Verify(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()), Times.Never);
    }

    [Fact]
    public void ReceiveSharedUserUpdate_MatchesByFederationId_AndUpdatesTheUserIdWeQueryAs()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            Url = "http://bob.example",
            ApiKey = "key-1",
            FederationId = "bob-fed-id",
            UserId = "old-user-id"
        });

        _service.ReceiveSharedUserUpdate(new SharedUserUpdatePayload
        {
            FromFederationId = "bob-fed-id",
            UserId = "new-restricted-user-id"
        });

        Assert.Equal("new-restricted-user-id", _plugin.Configuration.RemoteServers[0].UserId);
    }

    [Fact]
    public void ReceiveSharedUserUpdate_UnknownFederationId_DoesNothing()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            FederationId = "bob-fed-id",
            UserId = "old-user-id"
        });

        _service.ReceiveSharedUserUpdate(new SharedUserUpdatePayload
        {
            FromFederationId = "someone-else",
            UserId = "new-restricted-user-id"
        });

        Assert.Equal("old-user-id", _plugin.Configuration.RemoteServers[0].UserId);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
