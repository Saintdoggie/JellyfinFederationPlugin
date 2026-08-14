using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers the friend-request handshake in <see cref="FederationFriendService"/>:
/// sending a request, receiving one, accepting/rejecting, and the callbacks that
/// complete it on the sender's side. Network calls are faked via
/// <see cref="FederationFriendService.HttpClientOverride"/> so these run as fast,
/// deterministic unit tests instead of needing two live Jellyfin servers.
/// </summary>
[Collection("PluginInstance")]
public class FederationFriendServiceTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly List<AuthenticationInfo> _apiKeys = new();
    private readonly Mock<IAuthenticationManager> _authManager;
    private readonly Mock<IRemoteServerClientFactory> _clientFactory;
    private readonly FederationFriendService _service;

    public FederationFriendServiceTests()
    {
        _plugin = new RealPluginInstance();
        _plugin.Configuration.ServerUrl = "http://local.test:8096";

        _authManager = new Mock<IAuthenticationManager>();
        _authManager.Setup(a => a.CreateApiKey(It.IsAny<string>()))
            .Callback<string>(name => _apiKeys.Add(new AuthenticationInfo
            {
                AppName = name,
                AccessToken = "key-" + Guid.NewGuid().ToString("N"),
                DateCreated = DateTime.UtcNow
            }))
            .Returns(Task.CompletedTask);
        _authManager.Setup(a => a.GetApiKeys())
            .ReturnsAsync(() => (IReadOnlyList<AuthenticationInfo>)_apiKeys.ToList());
        _authManager.Setup(a => a.DeleteApiKey(It.IsAny<string>()))
            .Callback<string>(token => _apiKeys.RemoveAll(k => k.AccessToken == token))
            .Returns(Task.CompletedTask);

        var appHost = new Mock<IServerApplicationHost>();
        appHost.SetupGet(h => h.FriendlyName).Returns("This Server");

        var libraryManager = new Mock<ILibraryManager>();
        _clientFactory = new Mock<IRemoteServerClientFactory>();
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, _clientFactory.Object);
        var federationManager = new FederationLibraryManager(libraryManager.Object, NullLogger<FederationLibraryManager>.Instance, _clientFactory.Object, cache, bandwidthMonitor);

        var httpContextAccessor = new Mock<IHttpContextAccessor>();

        _service = new FederationFriendService(
            NullLogger<FederationFriendService>.Instance,
            _authManager.Object,
            appHost.Object,
            federationManager,
            httpContextAccessor.Object,
            _clientFactory.Object,
            Mock.Of<IUserManager>(),
            libraryManager.Object);
    }

    public void Dispose()
    {
        FederationFriendService.HttpClientOverride = null;
        _plugin.Dispose();
    }

    /// <summary>
    /// Routes every HTTP call the same responder: FederationFriendService's own
    /// direct calls (Send/Accept/Reject/Verify - via the static HttpClientOverride
    /// seam) as well as calls made through a RemoteServerClient (e.g.
    /// GetFriendsListAsync, which uses its own constructor-injected HttpClient, not
    /// the override). Both need the same fake so a friends-of-friends discovery test
    /// can fake both the "ask my friend for their friends" call and the "send a
    /// request to the friend-of-friend" call in one place.
    /// </summary>
    private void UseFakeHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        FederationFriendService.HttpClientOverride = new HttpClient(new FakeHandler(responder));
        _clientFactory
            .Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns<RemoteServer>(s => new RemoteServerClient(
                s,
                NullLogger.Instance,
                new HttpClient(new FakeHandler(responder)) { BaseAddress = new Uri(s.Url) }));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
        => new HttpResponseMessage(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    [Fact]
    public void GetOrCreateLocalFederationId_IsStableAcrossCalls()
    {
        var first = _service.GetOrCreateLocalFederationId();
        var second = _service.GetOrCreateLocalFederationId();

        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DiscoverFriendsOfFriendsAsync_Disabled_DoesNothing()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "b", Url = "http://friend-b.example", Name = "B", Enabled = true });
        var calls = 0;
        UseFakeHttp(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });

        var sent = await _service.DiscoverFriendsOfFriendsAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DiscoverFriendsOfFriendsAsync_DiscoversNewFriend_AndSendsRequest()
    {
        _plugin.Configuration.AllowFriendsOfFriends = true;
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "b", Url = "http://friend-b.example", Name = "B", Enabled = true });

        UseFakeHttp(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "http://friend-b.example/Plugins/Federation/Friends/List")
            {
                return Json(HttpStatusCode.OK, new { allowsIntroductions = true, friends = new[] { new { name = "C", url = "http://friend-c.example" } } });
            }

            if (url == "http://friend-c.example/Plugins/Federation/Friends/Request")
            {
                return Json(HttpStatusCode.OK, new { success = true, serverName = "C" });
            }

            throw new InvalidOperationException("Unexpected request to " + url);
        });

        var sent = await _service.DiscoverFriendsOfFriendsAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Contains(_plugin.Configuration.OutgoingFriendRequests, r => r.RemoteServerUrl == "http://friend-c.example");
    }

    [Fact]
    public async Task DiscoverFriendsOfFriendsAsync_AlreadyAFriend_DoesNotResend()
    {
        _plugin.Configuration.AllowFriendsOfFriends = true;
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "b", Url = "http://friend-b.example", Name = "B", Enabled = true });
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "c", Url = "http://friend-c.example", Name = "C", Enabled = true });

        UseFakeHttp(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "http://friend-b.example/Plugins/Federation/Friends/List")
            {
                return Json(HttpStatusCode.OK, new { allowsIntroductions = true, friends = new[] { new { name = "C", url = "http://friend-c.example" } } });
            }

            throw new InvalidOperationException("Should not have requested " + url + " - already a friend");
        });

        var sent = await _service.DiscoverFriendsOfFriendsAsync(CancellationToken.None);

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task DiscoverFriendsOfFriendsAsync_SkipsItself()
    {
        _plugin.Configuration.AllowFriendsOfFriends = true;
        _plugin.Configuration.ServerUrl = "http://local.test:8096";
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "b", Url = "http://friend-b.example", Name = "B", Enabled = true });

        UseFakeHttp(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "http://friend-b.example/Plugins/Federation/Friends/List")
            {
                return Json(HttpStatusCode.OK, new { allowsIntroductions = true, friends = new[] { new { name = "Me", url = "http://local.test:8096" } } });
            }

            throw new InvalidOperationException("Should not have requested " + url + " - that's us");
        });

        var sent = await _service.DiscoverFriendsOfFriendsAsync(CancellationToken.None);

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task SendFriendRequestAsync_Success_MintsKeyAndStoresOutgoingRequest()
    {
        var calls = 0;
        UseFakeHttp(req =>
        {
            calls++;
            Assert.Equal("http://friend.example/Plugins/Federation/Friends/Request", req.RequestUri!.ToString());
            return Json(HttpStatusCode.OK, new { success = true, serverName = "Friend Server" });
        });

        var (success, message) = await _service.SendFriendRequestAsync("http://friend.example", CancellationToken.None);

        Assert.True(success, message);
        Assert.Equal(1, calls);
        var outgoing = Assert.Single(_plugin.Configuration.OutgoingFriendRequests);
        Assert.Equal("http://friend.example", outgoing.RemoteServerUrl);
        Assert.Equal("Friend Server", outgoing.RemoteServerName);
        Assert.False(string.IsNullOrEmpty(outgoing.ApiKey));
        Assert.Contains(_apiKeys, k => k.AccessToken == outgoing.ApiKey);
    }

    [Fact]
    public async Task SendFriendRequestAsync_RemoteRejects_RevokesTheMintedKey()
    {
        UseFakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var (success, _) = await _service.SendFriendRequestAsync("http://friend.example", CancellationToken.None);

        Assert.False(success);
        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
        Assert.Empty(_apiKeys);
    }

    [Fact]
    public async Task SendFriendRequestAsync_AlreadyFriends_FailsWithoutCallingRemote()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "x", Url = "http://friend.example", Name = "Friend" });
        var calls = 0;
        UseFakeHttp(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });

        var (success, message) = await _service.SendFriendRequestAsync("http://friend.example", CancellationToken.None);

        Assert.False(success);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReceiveFriendRequestAsync_Valid_StoresIncomingRequest_AndMarksVerifiedWhenSenderConfirms()
    {
        UseFakeHttp(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var payload = new FriendRequestPayload
        {
            RequestId = "req-1",
            FromServerUrl = "http://sender.example",
            FromServerName = "Sender",
            FromServerId = "sender-id",
            ApiKeyForYou = "key-from-sender"
        };

        var result = await _service.ReceiveFriendRequestAsync(payload, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("This Server", result.ServerName);
        var incoming = Assert.Single(_plugin.Configuration.IncomingFriendRequests);
        Assert.Equal("http://sender.example", incoming.RemoteServerUrl);
        Assert.Equal("key-from-sender", incoming.ApiKey);
        Assert.True(incoming.Verified);
    }

    [Fact]
    public async Task ReceiveFriendRequestAsync_SenderDoesNotConfirm_StoresAsUnverified()
    {
        UseFakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var payload = new FriendRequestPayload
        {
            RequestId = "req-1",
            FromServerUrl = "http://sender.example",
            FromServerName = "Sender",
            ApiKeyForYou = "key-from-sender"
        };

        var result = await _service.ReceiveFriendRequestAsync(payload, CancellationToken.None);

        Assert.True(result.Success);
        var incoming = Assert.Single(_plugin.Configuration.IncomingFriendRequests);
        Assert.False(incoming.Verified);
    }

    [Fact]
    public async Task ReceiveFriendRequestAsync_Malformed_Fails_AndStoresNothing()
    {
        var result = await _service.ReceiveFriendRequestAsync(new FriendRequestPayload(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(_plugin.Configuration.IncomingFriendRequests);
    }

    [Fact]
    public async Task AcceptFriendRequestAsync_ConfirmsWithSender_AddsFriendUsingTheirKey()
    {
        _plugin.Configuration.IncomingFriendRequests.Add(new FriendRequest
        {
            Id = "req-1",
            RemoteServerUrl = "http://sender.example",
            RemoteServerName = "Sender",
            ApiKey = "key-from-sender"
        });

        UseFakeHttp(req =>
        {
            Assert.Equal("http://sender.example/Plugins/Federation/Friends/Accept", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (success, message) = await _service.AcceptFriendRequestAsync("req-1", CancellationToken.None);

        Assert.True(success, message);
        Assert.Empty(_plugin.Configuration.IncomingFriendRequests);
        var server = Assert.Single(_plugin.Configuration.RemoteServers);
        Assert.Equal("http://sender.example", server.Url);
        Assert.Equal("key-from-sender", server.ApiKey);
        Assert.Equal("Sender", server.Name);
    }

    [Fact]
    public async Task AcceptFriendRequestAsync_SenderUnreachable_DoesNotAddFriend_AndRevokesTheNewKey()
    {
        _plugin.Configuration.IncomingFriendRequests.Add(new FriendRequest
        {
            Id = "req-1",
            RemoteServerUrl = "http://sender.example",
            RemoteServerName = "Sender",
            ApiKey = "key-from-sender"
        });

        UseFakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var (success, _) = await _service.AcceptFriendRequestAsync("req-1", CancellationToken.None);

        Assert.False(success);
        Assert.Empty(_plugin.Configuration.RemoteServers);

        // The incoming request is untouched so the admin can retry later.
        Assert.Single(_plugin.Configuration.IncomingFriendRequests);

        // The key minted for the sender before the failed confirmation must not leak.
        Assert.DoesNotContain(_apiKeys, k => k.AppName == "Federation friend: Sender");
    }

    [Fact]
    public async Task RejectFriendRequestAsync_RemovesRequest_RegardlessOfNotifyOutcome()
    {
        _plugin.Configuration.IncomingFriendRequests.Add(new FriendRequest { Id = "req-1", RemoteServerUrl = "http://sender.example" });
        UseFakeHttp(_ => throw new HttpRequestException("sender is offline"));

        var (success, _) = await _service.RejectFriendRequestAsync("req-1", CancellationToken.None);

        Assert.True(success);
        Assert.Empty(_plugin.Configuration.IncomingFriendRequests);
    }

    [Fact]
    public async Task CancelOutgoingFriendRequestAsync_RemovesRequest_AndRevokesKey()
    {
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest { Id = "req-1", RemoteServerUrl = "http://friend.example", ApiKey = "key-1" });
        _apiKeys.Add(new AuthenticationInfo { AppName = "x", AccessToken = "key-1", DateCreated = DateTime.UtcNow });

        var (success, _) = await _service.CancelOutgoingFriendRequestAsync("req-1", CancellationToken.None);

        Assert.True(success);
        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
        Assert.DoesNotContain(_apiKeys, k => k.AccessToken == "key-1");
    }

    [Fact]
    public void HandleAcceptCallback_AddsFriend_UsingGivenKey_AndClearsOutgoingRequest()
    {
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest { Id = "req-1", RemoteServerUrl = "http://friend.example", RemoteServerName = "Friend" });

        _service.HandleAcceptCallback(new FriendRequestPayload
        {
            RequestId = "req-1",
            FromServerUrl = "http://friend.example",
            FromServerName = "Friend",
            ApiKeyForYou = "key-from-friend"
        });

        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
        var server = Assert.Single(_plugin.Configuration.RemoteServers);
        Assert.Equal("key-from-friend", server.ApiKey);
        Assert.Equal("http://friend.example", server.Url);
    }

    [Fact]
    public void HandleAcceptCallback_UnknownRequestId_DoesNothing()
    {
        _service.HandleAcceptCallback(new FriendRequestPayload { RequestId = "does-not-exist", ApiKeyForYou = "key" });

        Assert.Empty(_plugin.Configuration.RemoteServers);
    }

    [Fact]
    public async Task HandleRejectCallbackAsync_RemovesOutgoingRequest_AndRevokesTheKeyWeMinted()
    {
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest { Id = "req-1", ApiKey = "key-1" });
        _apiKeys.Add(new AuthenticationInfo { AppName = "x", AccessToken = "key-1", DateCreated = DateTime.UtcNow });

        await _service.HandleRejectCallbackAsync("req-1");

        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
        Assert.DoesNotContain(_apiKeys, k => k.AccessToken == "key-1");
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
