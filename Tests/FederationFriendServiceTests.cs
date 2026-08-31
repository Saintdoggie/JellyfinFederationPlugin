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
        var federationManager = new FederationLibraryManager(libraryManager.Object, NullLogger<FederationLibraryManager>.Instance, _clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var httpContextAccessor = new Mock<IHttpContextAccessor>();

        _service = new FederationFriendService(
            NullLogger<FederationFriendService>.Instance,
            _authManager.Object,
            appHost.Object,
            federationManager,
            httpContextAccessor.Object,
            _clientFactory.Object);
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
    public async Task SendFriendRequestAsync_Success_MintsTokenAndStoresOutgoingRequest()
    {
        var calls = 0;
        FriendRequestPayload? sentPayload = null;
        UseFakeHttp(req =>
        {
            calls++;
            Assert.Equal("http://friend.example/Plugins/Federation/Friends/Request", req.RequestUri!.ToString());
            sentPayload = System.Text.Json.JsonSerializer.Deserialize<FriendRequestPayload>(
                req.Content!.ReadAsStringAsync().Result,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return Json(HttpStatusCode.OK, new { success = true, serverName = "Friend Server" });
        });

        var (success, message) = await _service.SendFriendRequestAsync("http://friend.example", CancellationToken.None);

        Assert.True(success, message);
        Assert.Equal(1, calls);
        var outgoing = Assert.Single(_plugin.Configuration.OutgoingFriendRequests);
        Assert.Equal("http://friend.example", outgoing.RemoteServerUrl);
        Assert.Equal("Friend Server", outgoing.RemoteServerName);
        Assert.False(string.IsNullOrEmpty(outgoing.ApiKey));
        Assert.True(sentPayload?.SupportsFederationToken);
        Assert.Equal(outgoing.ApiKey, sentPayload?.ApiKeyForYou);
    }

    [Fact]
    public async Task SendFriendRequestAsync_RemoteRejects_DoesNotStoreOutgoingRequest()
    {
        UseFakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var (success, _) = await _service.SendFriendRequestAsync("http://friend.example", CancellationToken.None);

        Assert.False(success);
        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
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
            ApiKeyForYou = "key-from-sender",
            SupportsFederationToken = true
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
            ApiKeyForYou = "key-from-sender",
            SupportsFederationToken = true
        };

        var result = await _service.ReceiveFriendRequestAsync(payload, CancellationToken.None);

        Assert.True(result.Success);
        var incoming = Assert.Single(_plugin.Configuration.IncomingFriendRequests);
        Assert.False(incoming.Verified);
    }

    [Fact]
    public async Task ReceiveFriendRequestAsync_MissingFederationTokenSupport_Rejected()
    {
        var payload = new FriendRequestPayload
        {
            RequestId = "req-1",
            FromServerUrl = "http://sender.example",
            FromServerName = "Sender",
            ApiKeyForYou = "key-from-sender"
        };

        var result = await _service.ReceiveFriendRequestAsync(payload, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(_plugin.Configuration.IncomingFriendRequests);
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
    public async Task AcceptFriendRequestAsync_SenderUnreachable_DoesNotAddFriend()
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
    public async Task CancelOutgoingFriendRequestAsync_RemovesRequest()
    {
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest { Id = "req-1", RemoteServerUrl = "http://friend.example", ApiKey = "key-1" });

        var (success, _) = await _service.CancelOutgoingFriendRequestAsync("req-1", CancellationToken.None);

        Assert.True(success);
        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
    }

    [Fact]
    public void HandleAcceptCallback_AddsFriend_UsingGivenKey_AndClearsOutgoingRequest()
    {
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest { Id = "req-1", RemoteServerUrl = "http://friend.example", RemoteServerName = "Friend" });

        var accepted = _service.HandleAcceptCallback(new FriendRequestPayload
        {
            RequestId = "req-1",
            FromServerUrl = "http://friend.example",
            FromServerName = "Friend",
            ApiKeyForYou = "key-from-friend",
            SupportsFederationToken = true
        });

        Assert.True(accepted);
        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
        var server = Assert.Single(_plugin.Configuration.RemoteServers);
        Assert.Equal("key-from-friend", server.ApiKey);
        Assert.Equal("http://friend.example", server.Url);
    }

    [Fact]
    public void HandleAcceptCallback_UnknownRequestId_DoesNothing()
    {
        var accepted = _service.HandleAcceptCallback(new FriendRequestPayload { RequestId = "does-not-exist", ApiKeyForYou = "key", SupportsFederationToken = true });

        Assert.False(accepted);
        Assert.Empty(_plugin.Configuration.RemoteServers);
    }

    [Fact]
    public void HandleAcceptCallback_MissingFederationTokenSupport_Rejected()
    {
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest { Id = "req-1", RemoteServerUrl = "http://friend.example", RemoteServerName = "Friend" });

        var accepted = _service.HandleAcceptCallback(new FriendRequestPayload
        {
            RequestId = "req-1",
            FromServerUrl = "http://friend.example",
            ApiKeyForYou = "key-from-friend"
        });

        Assert.False(accepted);
        Assert.Empty(_plugin.Configuration.RemoteServers);
        Assert.Single(_plugin.Configuration.OutgoingFriendRequests);
    }

    [Fact]
    public void HandleRejectCallbackAsync_RemovesOutgoingRequest()
    {
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest { Id = "req-1", ApiKey = "key-1" });

        _service.HandleRejectCallbackAsync("req-1");

        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
    }

    [Fact]
    public void HandleAcceptCallback_StoresTheKeyWeMinted_AsIssuedApiKey()
    {
        // The key minted for the friend when the outgoing request was first sent
        // (FriendRequest.ApiKey) must survive onto the resulting RemoteServer as
        // IssuedApiKey, so it can be revoked later if this friendship is removed -
        // previously it was minted, sent, and then simply forgotten.
        _plugin.Configuration.OutgoingFriendRequests.Add(new FriendRequest
        {
            Id = "req-1",
            RemoteServerUrl = "http://friend.example",
            RemoteServerName = "Friend",
            ApiKey = "key-we-minted-for-them"
        });

        _service.HandleAcceptCallback(new FriendRequestPayload
        {
            RequestId = "req-1",
            FromServerUrl = "http://friend.example",
            FromServerName = "Friend",
            ApiKeyForYou = "key-from-friend",
            SupportsFederationToken = true
        });

        var server = Assert.Single(_plugin.Configuration.RemoteServers);
        Assert.Equal("key-from-friend", server.ApiKey);
        Assert.Equal("key-we-minted-for-them", server.IssuedApiKey);
    }

    [Fact]
    public async Task NotifyAndRevokeOnUnfriendAsync_NotifiesFriend()
    {
        var posted = false;
        FederationFriendService.HttpClientOverride = new HttpClient(new FakeHandler(req =>
        {
            posted = req.RequestUri!.ToString().EndsWith("/Plugins/Federation/Friends/Unfriend", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var server = new RemoteServer { Url = "http://friend.example", ApiKey = "their-token", IssuedApiKey = "issued-token" };

        await _service.NotifyAndRevokeOnUnfriendAsync(server, CancellationToken.None);

        Assert.True(posted);
    }

    [Fact]
    public async Task NotifyAndRevokeOnUnfriendAsync_NotifyFailure_DoesNotThrow()
    {
        FederationFriendService.HttpClientOverride = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var server = new RemoteServer { Url = "http://friend.example", ApiKey = "their-token", IssuedApiKey = "issued-token" };

        // Their access is cut the moment the caller deletes this RemoteServer
        // entry (FederationTokenAuth only ever matches a token against a
        // currently-configured friend) - this method's only remaining job is a
        // best-effort notification, which must never throw even when it fails
        // (offline, unreachable, or an old plugin version without this endpoint).
        await _service.NotifyAndRevokeOnUnfriendAsync(server, CancellationToken.None);
    }

    [Fact]
    public void FindByFederationId_MatchesOnFederationId_CaseInsensitive()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "s1", FederationId = "ABC-123" });

        var found = _service.FindByFederationId("abc-123");

        Assert.NotNull(found);
        Assert.Equal("s1", found!.Id);
    }

    [Fact]
    public void FindByFederationId_UnknownId_ReturnsNull()
    {
        Assert.Null(_service.FindByFederationId("does-not-exist"));
        Assert.Null(_service.FindByFederationId(null));
    }

    [Fact]
    public void CreateCompanionFriend_Success_AddsCompanionServer_AndReturnsConnectCode()
    {
        var (success, message, connectCode) = _service.CreateCompanionFriend("Alex's Plex", false, new List<string> { "folder-1" });

        Assert.True(success, message);
        Assert.False(string.IsNullOrEmpty(connectCode));

        var server = Assert.Single(_plugin.Configuration.RemoteServers);
        Assert.Equal(ServerKind.Companion, server.Kind);
        Assert.Equal("Alex's Plex", server.Name);
        Assert.False(server.ShareAllLibraries);
        Assert.Equal(new List<string> { "folder-1" }, server.SharedLibraryFolderIds);
        Assert.False(string.IsNullOrEmpty(server.IssuedApiKey));
        Assert.True(server.Enabled);

        var decoded = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(connectCode!)).RootElement;
        Assert.Equal("http://local.test:8096", decoded.GetProperty("url").GetString());
        Assert.Equal(server.IssuedApiKey, decoded.GetProperty("token").GetString());
        Assert.Equal("Alex's Plex", decoded.GetProperty("name").GetString());
    }

    [Fact]
    public void CreateCompanionFriend_NoLocalUrlConfigured_Fails()
    {
        _plugin.Configuration.ServerUrl = string.Empty;

        var (success, _, connectCode) = _service.CreateCompanionFriend("Alex's Plex", true, null);

        Assert.False(success);
        Assert.Null(connectCode);
        Assert.Empty(_plugin.Configuration.RemoteServers);
    }

    [Fact]
    public void CreateCompanionFriend_BlankName_DefaultsToPlexFriend()
    {
        var (success, _, _) = _service.CreateCompanionFriend(string.Empty, true, null);

        Assert.True(success);
        Assert.Equal("Plex friend", Assert.Single(_plugin.Configuration.RemoteServers).Name);
    }

    [Fact]
    public void CreateCompanionFriend_AssignsFederationId_SoMintedPlaybackTokensResolve()
    {
        var (success, _, _) = _service.CreateCompanionFriend("Alex's Plex", true, null);

        Assert.True(success);
        var server = Assert.Single(_plugin.Configuration.RemoteServers);
        Assert.False(string.IsNullOrEmpty(server.FederationId));
    }

    [Fact]
    public void EnsureFederationId_EmptyId_BackfillsAndPersists()
    {
        var server = new RemoteServer { Kind = ServerKind.Companion, Name = "Legacy companion" };
        _plugin.Configuration.RemoteServers.Add(server);

        _service.EnsureFederationId(server);

        Assert.False(string.IsNullOrEmpty(server.FederationId));
        Assert.Equal(
            server.FederationId,
            _plugin.Configuration.RemoteServers.Single(s => s.Id == server.Id).FederationId);
    }

    [Fact]
    public void EnsureFederationId_ExistingId_IsNoOp()
    {
        var server = new RemoteServer { FederationId = "keep-me" };

        _service.EnsureFederationId(server);

        Assert.Equal("keep-me", server.FederationId);
    }

    [Fact]
    public void EnsureFederationId_AfterBackfill_PlaybackTokenBindsToResolvableFriend()
    {
        // Simulates the 0.0.116 Companion break end to end: an entry with an
        // empty FederationId minted playback tokens bound to "", which
        // FindByFederationId could never re-resolve at stream time - every
        // DirectStream request from that friend 403'd. After the heal, the same
        // mint/resolve round trip finds the friend again.
        var server = new RemoteServer { Kind = ServerKind.Companion, Name = "Legacy companion" };
        _plugin.Configuration.RemoteServers.Add(server);
        _service.EnsureFederationId(server);

        var tokenService = new FederationPlaybackTokenService();
        var token = tokenService.Issue("item-1", server.FederationId);

        Assert.True(tokenService.TryValidate(token, "item-1", out var ownerFederationId));
        Assert.NotNull(_service.FindByFederationId(ownerFederationId));
    }

    [Fact]
    public void GetCompanionConnectCode_ReconstructsFromStoredToken_WithoutRotatingIt()
    {
        var (_, _, firstCode) = _service.CreateCompanionFriend("Alex's Plex", true, null);
        var server = Assert.Single(_plugin.Configuration.RemoteServers);

        var (success, message, secondCode) = _service.GetCompanionConnectCode(server);

        Assert.True(success, message);
        Assert.Equal(firstCode, secondCode);
    }

    [Fact]
    public void GetCompanionConnectCode_NoIssuedToken_Fails()
    {
        var server = new RemoteServer { Kind = ServerKind.Companion, Name = "Alex's Plex" };

        var (success, _, connectCode) = _service.GetCompanionConnectCode(server);

        Assert.False(success);
        Assert.Null(connectCode);
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
