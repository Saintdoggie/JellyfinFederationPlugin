using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// Covers multi-server pools: an admin-curated group where joining introduces you
/// to every other member automatically, but each pairwise connection still goes
/// through the ordinary friend-request handshake - no server ever connects to
/// another without a human on that side clicking Accept.
/// </summary>
[Collection("PluginInstance")]
public class FederationPoolTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly List<AuthenticationInfo> _apiKeys = new();
    private readonly Mock<IAuthenticationManager> _authManager;
    private readonly Mock<IRemoteServerClientFactory> _clientFactory;
    private readonly FederationFriendService _service;

    public FederationPoolTests()
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
        => new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    [Fact]
    public void CreatePool_AddsPoolOwnedByThisServer_WithSelfAsSoleMember()
    {
        var pool = _service.CreatePool("Movie Night");

        var stored = Assert.Single(_plugin.Configuration.Pools);
        Assert.Equal(pool.Id, stored.Id);
        Assert.Equal("Movie Night", stored.Name);
        Assert.True(stored.IsOwner);
        var member = Assert.Single(stored.Members);
        Assert.Equal("http://local.test:8096", member.Url);
    }

    [Fact]
    public async Task SendPoolInviteAsync_UnknownPool_Fails()
    {
        var (success, message) = await _service.SendPoolInviteAsync("no-such-pool", "http://friend.example", CancellationToken.None);

        Assert.False(success);
        Assert.Equal("Pool not found.", message);
    }

    [Fact]
    public async Task SendPoolInviteAsync_Success_PayloadCarriesPoolIdentityAndRoster()
    {
        var pool = _service.CreatePool("Movie Night");
        string? capturedBody = null;

        UseFakeHttp(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, new { success = true, serverName = "Invitee" });
        });

        var (success, message) = await _service.SendPoolInviteAsync(pool.Id, "http://invitee.example", CancellationToken.None);

        Assert.True(success, message);
        Assert.NotNull(capturedBody);
        var payload = JsonSerializer.Deserialize<FriendRequestPayload>(capturedBody!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(pool.Id, payload!.PoolId);
        Assert.Equal("Movie Night", payload.PoolName);
        Assert.NotNull(payload.PoolRoster);
        Assert.Contains(payload.PoolRoster!, m => m.Url == "http://local.test:8096");

        var outgoing = Assert.Single(_plugin.Configuration.OutgoingFriendRequests);
        Assert.Equal(pool.Id, outgoing.PoolId);
    }

    [Fact]
    public async Task SendPoolInviteAsync_TargetIsAlreadyAFriend_AddsDirectlyInsteadOfSendingAFriendRequest()
    {
        // The whole point of a pool is not re-doing the handshake for someone you
        // already trust - inviting an existing friend must never hit "already
        // friends" the way a plain SendFriendRequestAsync would.
        var pool = _service.CreatePool("Movie Night");
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob",
            Url = "http://bob.example",
            ApiKey = "key-1",
            FederationId = "bob-fed-id"
        });

        string? capturedUrl = null;
        UseFakeHttp(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (success, message) = await _service.SendPoolInviteAsync(pool.Id, "http://bob.example", CancellationToken.None);

        Assert.True(success, message);
        Assert.Contains(pool.Members, m => m.Url == "http://bob.example");

        // A pool notice, not a fresh friend-request handshake.
        Assert.Equal("http://bob.example/Plugins/Federation/Pools/Notice", capturedUrl);

        // No new friend request was ever created for someone already known.
        Assert.Empty(_plugin.Configuration.OutgoingFriendRequests);
    }

    [Fact]
    public async Task AddFriendToPoolAsync_ExistingFriend_AddsWithoutRetypingUrl()
    {
        var pool = _service.CreatePool("Movie Night");
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob",
            Url = "http://bob.example",
            ApiKey = "key-1",
            FederationId = "bob-fed-id"
        });

        UseFakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var (success, message) = await _service.AddFriendToPoolAsync(pool.Id, "friend-1", CancellationToken.None);

        Assert.True(success, message);
        Assert.Contains(pool.Members, m => m.Url == "http://bob.example");
    }

    [Fact]
    public async Task AddFriendToPoolAsync_UnknownPool_Fails()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "friend-1", Name = "Bob", Url = "http://bob.example" });

        var (success, message) = await _service.AddFriendToPoolAsync("no-such-pool", "friend-1", CancellationToken.None);

        Assert.False(success);
        Assert.Contains("Pool not found", message);
    }

    [Fact]
    public async Task AddFriendToPoolAsync_UnknownFriend_Fails()
    {
        var pool = _service.CreatePool("Movie Night");

        var (success, message) = await _service.AddFriendToPoolAsync(pool.Id, "no-such-friend", CancellationToken.None);

        Assert.False(success);
        Assert.Contains("Friend not found", message);
    }

    [Fact]
    public async Task ReceivePoolNotice_FromExistingFriend_AdoptsPoolAndFansOutToUnknownMembers()
    {
        // Reached via someone we're already friends with - no accept step, but the
        // resulting fan-out to a member we don't know yet still goes through the
        // ordinary friend-request handshake, same as the accept-flow path.
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob",
            Url = "http://bob.example",
            ApiKey = "key-1",
            FederationId = "bob-fed-id"
        });

        var introducedTo = new List<string>();
        UseFakeHttp(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/Plugins/Federation/Friends/Request", StringComparison.Ordinal))
            {
                introducedTo.Add(url);
                return Json(HttpStatusCode.OK, new { success = true, serverName = "Introduced" });
            }

            throw new InvalidOperationException("Unexpected request to " + url);
        });

        await _service.ReceivePoolNotice(
            new PoolNoticePayload
            {
                FromFederationId = "bob-fed-id",
                PoolId = "pool-1",
                PoolName = "Movie Night",
                OwnerFederationId = "fed-owner",
                OwnerName = "Owner",
                Roster = new List<PoolMember>
                {
                    new PoolMember { FederationId = "fed-owner", Name = "Owner", Url = "http://owner.example" },
                    new PoolMember { FederationId = "bob-fed-id", Name = "Bob", Url = "http://bob.example" }
                }
            },
            CancellationToken.None);

        var pool = Assert.Single(_plugin.Configuration.Pools);
        Assert.Equal("pool-1", pool.Id);
        Assert.Equal(3, pool.Members.Count); // us, Bob, Owner
        Assert.Contains(pool.Members, m => m.Url == "http://local.test:8096");

        // Fanned out to Owner (not already known) via a real friend request; never
        // re-sent anything to Bob, who is how we learned about this in the first place.
        Assert.Contains(introducedTo, u => u == "http://owner.example/Plugins/Federation/Friends/Request");
        Assert.DoesNotContain(introducedTo, u => u.StartsWith("http://bob.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReceivePoolNotice_FromUnknownFederationId_DoesNothing()
    {
        var called = false;
        UseFakeHttp(req => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        await _service.ReceivePoolNotice(
            new PoolNoticePayload { FromFederationId = "someone-we-dont-know", PoolId = "pool-1" },
            CancellationToken.None);

        Assert.Empty(_plugin.Configuration.Pools);
        Assert.False(called);
    }

    [Fact]
    public async Task AcceptFriendRequestAsync_WithPoolInfo_AdoptsPoolAndIntroducesOtherMembers()
    {
        // We were invited into a pool that already has a third member ("C") besides
        // the inviter ("B") - accepting B's invite should both connect us to B (the
        // ordinary friend flow) and automatically send a *separate* friend request to
        // C so the mesh keeps forming, without C being connected to us silently.
        _plugin.Configuration.IncomingFriendRequests.Add(new FriendRequest
        {
            Id = "req-1",
            RemoteServerUrl = "http://b.example",
            RemoteServerName = "B",
            RemoteServerId = "fed-b",
            ApiKey = "key-from-b",
            PoolId = "pool-1",
            PoolName = "Movie Night",
            PoolOwnerFederationId = "fed-owner",
            PoolOwnerName = "Owner",
            PoolRoster = new List<PoolMember>
            {
                new PoolMember { FederationId = "fed-owner", Name = "Owner", Url = "http://owner.example" },
                new PoolMember { FederationId = "fed-b", Name = "B", Url = "http://b.example" },
                new PoolMember { FederationId = "fed-c", Name = "C", Url = "http://c.example" }
            }
        });

        var introducedTo = new List<string>();
        UseFakeHttp(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "http://b.example/Plugins/Federation/Friends/Accept")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url.EndsWith("/Plugins/Federation/Friends/Request", StringComparison.Ordinal))
            {
                introducedTo.Add(url);
                return Json(HttpStatusCode.OK, new { success = true, serverName = "Introduced" });
            }

            throw new InvalidOperationException("Unexpected request to " + url);
        });

        var (success, message) = await _service.AcceptFriendRequestAsync("req-1", CancellationToken.None);

        Assert.True(success, message);

        // Connected to the inviter as an ordinary friend.
        Assert.Contains(_plugin.Configuration.RemoteServers, s => s.Url == "http://b.example");

        // Adopted the pool locally with the full roster (owner, B, C, and us).
        var pool = Assert.Single(_plugin.Configuration.Pools);
        Assert.Equal("pool-1", pool.Id);
        Assert.False(pool.IsOwner);
        Assert.Equal(4, pool.Members.Count);
        Assert.Contains(pool.Members, m => m.Url == "http://local.test:8096");

        // Fanned out to Owner and C (not B, already connected; not ourselves).
        Assert.Contains(introducedTo, u => u == "http://owner.example/Plugins/Federation/Friends/Request");
        Assert.Contains(introducedTo, u => u == "http://c.example/Plugins/Federation/Friends/Request");
        Assert.DoesNotContain(introducedTo, u => u.StartsWith("http://b.example", StringComparison.Ordinal));
    }

    [Fact]
    public void LeavePool_RemovesLocalRecord()
    {
        var pool = _service.CreatePool("Temp Pool");

        var removed = _service.LeavePool(pool.Id);

        Assert.True(removed);
        Assert.Empty(_plugin.Configuration.Pools);
    }

    [Fact]
    public void LeavePool_UnknownPool_ReturnsFalse()
    {
        Assert.False(_service.LeavePool("does-not-exist"));
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
