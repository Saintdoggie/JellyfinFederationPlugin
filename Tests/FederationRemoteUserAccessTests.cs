using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers the per-remote-user access override feature: an admin narrowing what one
/// specific individual login on a friend's server (identified by that friend's own
/// local user id) can see, on top of the friend's existing server-level sharing
/// scope. Since
/// content pulled from a friend is fetched once, server-wide, under one shared
/// hidden account, this friend has no way to tell which of *our* users is actually
/// browsing/streaming at any moment - so enforcement has to happen on the consuming
/// side (<see cref="RemoteAccessControlService"/>), evaluated against the rules the
/// sharing friend pushed down (<see cref="RemoteServer.FriendUserAccessRules"/>).
/// </summary>
[Collection("PluginInstance")]
public class FederationRemoteUserAccessTests : IDisposable
{
    private readonly RealPluginInstance _plugin;

    public FederationRemoteUserAccessTests()
    {
        _plugin = new RealPluginInstance();
    }

    public void Dispose()
    {
        _plugin.Dispose();
    }

    private static RemoteAccessControlService MakeAccessControl()
        => new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance);

    [Fact]
    public void IsAllowed_NoRuleForUser_FallsBackToAllowed()
    {
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        var accessControl = MakeAccessControl();

        var allowed = accessControl.IsAllowed(server, Guid.NewGuid(), "Movies", Guid.NewGuid());

        Assert.True(allowed);
    }

    [Fact]
    public void IsAllowedForEveryConfiguredUser_AllLibrariesWithoutLimits_IsTrue()
    {
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = Guid.NewGuid().ToString("N"),
            Mode = RemoteUserAccessMode.AllLibraries
        });

        Assert.True(RemoteAccessControlService.IsAllowedForEveryConfiguredUser(server, "Movies", Guid.NewGuid(), "R"));
    }

    [Fact]
    public void IsAllowedForEveryConfiguredUser_OneBlockedRule_IsFalse()
    {
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = Guid.NewGuid().ToString("N"),
            Mode = RemoteUserAccessMode.Blocked
        });

        Assert.False(RemoteAccessControlService.IsAllowedForEveryConfiguredUser(server, "Movies", Guid.NewGuid(), "PG"));
    }

    [Fact]
    public void IsAllowed_UnknownLocalUser_FallsBackToAllowed()
    {
        // A background/internal call with no authenticated request context to
        // resolve a local user from - must behave exactly as before this feature
        // existed, even if a rule exists for some other user.
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.Blocked
        });

        var accessControl = MakeAccessControl();

        Assert.True(accessControl.IsAllowed(server, null, "Movies", Guid.NewGuid()));
    }

    [Fact]
    public void IsAllowed_Blocked_DeniesEverything()
    {
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.Blocked
        });

        var accessControl = MakeAccessControl();

        Assert.False(accessControl.IsAllowed(server, localUserId, "Movies", Guid.NewGuid()));
    }

    [Fact]
    public void IsAllowed_CertainItems_OnlyAllowsListedItems()
    {
        var localUserId = Guid.NewGuid();
        var allowedItem = Guid.NewGuid();
        var otherItem = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.CertainItems,
            ItemIds = new List<string> { allowedItem.ToString("N") }
        });

        var accessControl = MakeAccessControl();

        Assert.True(accessControl.IsAllowed(server, localUserId, "Movies", allowedItem));
        Assert.False(accessControl.IsAllowed(server, localUserId, "Movies", otherItem));
    }

    [Fact]
    public void IsAllowed_CertainLibraries_MatchesViaTheMappingsRemoteLibraryId()
    {
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.CertainLibraries,
            LibraryFolderIds = new List<string> { "remote-movies-folder" }
        });
        _plugin.Configuration.RemoteServers.Add(server);
        _plugin.Configuration.LibraryMappings.Add(new LibraryMapping
        {
            LocalLibraryName = "Movies (Alice)",
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = "server-a", RemoteLibraryId = "remote-movies-folder" }
            }
        });
        _plugin.Configuration.LibraryMappings.Add(new LibraryMapping
        {
            LocalLibraryName = "TV (Alice)",
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = "server-a", RemoteLibraryId = "remote-tv-folder" }
            }
        });

        var accessControl = MakeAccessControl();

        Assert.True(accessControl.IsAllowed(server, localUserId, "Movies (Alice)", Guid.NewGuid()));
        Assert.False(accessControl.IsAllowed(server, localUserId, "TV (Alice)", Guid.NewGuid()));
    }

    [Fact]
    public void IsAllowed_CertainLibraries_UnknownMapping_DeniesClosed()
    {
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.CertainLibraries,
            LibraryFolderIds = new List<string> { "remote-movies-folder" }
        });

        var accessControl = MakeAccessControl();

        Assert.False(accessControl.IsAllowed(server, localUserId, "SomeUnmappedLibrary", Guid.NewGuid()));
    }

    /// <summary>
    /// Regression coverage: TryResolveRating used to unconditionally return null
    /// (a documented, deliberate stub - "a future cache-indexed lookup can fill
    /// this in if needed"), which made a per-user MaxAllowedRating ceiling a
    /// permanent no-op for AllLibraries/CertainItems/CertainLibraries even
    /// though it looked fully configured in the UI. It now resolves the item's
    /// actual cached OfficialRating via FederationItemCache instead of the
    /// stub's unconditional null.
    /// </summary>
    [Fact]
    public void IsAllowed_AllLibraries_BlocksItem_AboveThatUsersRatingCeiling()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var localUserId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        cache.UpsertRaw("Movies", "server-a", remoteItemId, new MediaBrowser.Model.Dto.BaseItemDto { Name = "R-Rated Movie", OfficialRating = "R" }, 0, "Movie");

        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.AllLibraries,
            MaxAllowedRating = "PG-13"
        });

        var accessControl = new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance, cache);

        Assert.False(accessControl.IsAllowed(server, localUserId, "Movies", remoteItemId));
    }

    [Fact]
    public void IsAllowed_AllLibraries_AllowsItem_WithinThatUsersRatingCeiling()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var localUserId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        cache.UpsertRaw("Movies", "server-a", remoteItemId, new MediaBrowser.Model.Dto.BaseItemDto { Name = "PG Movie", OfficialRating = "PG" }, 0, "Movie");

        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.AllLibraries,
            MaxAllowedRating = "PG-13"
        });

        var accessControl = new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance, cache);

        Assert.True(accessControl.IsAllowed(server, localUserId, "Movies", remoteItemId));
    }

    [Fact]
    public void IsAllowed_BlockedSeries_HidesSeasonAndEpisodeViaCacheParentKeys()
    {
        var (cache, seriesId, seasonId, episodeId, otherEpisodeId) = MakeShowHierarchy();
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.AllLibraries,
            BlockedItemIds = new List<string> { seriesId.ToString("N") }
        });

        var accessControl = new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance, cache);

        Assert.False(accessControl.IsAllowed(server, localUserId, "TV", seriesId));
        Assert.False(accessControl.IsAllowed(server, localUserId, "TV", seasonId));
        Assert.False(accessControl.IsAllowed(server, localUserId, "TV", episodeId));
        Assert.True(accessControl.IsAllowed(server, localUserId, "TV", otherEpisodeId));
    }

    [Fact]
    public void IsAllowed_BlockedSeason_HidesEpisodeButNotTheSeries()
    {
        var (cache, seriesId, seasonId, episodeId, otherEpisodeId) = MakeShowHierarchy();
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.AllLibraries,
            BlockedItemIds = new List<string> { seasonId.ToString() }
        });

        var accessControl = new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance, cache);

        Assert.True(accessControl.IsAllowed(server, localUserId, "TV", seriesId));
        Assert.False(accessControl.IsAllowed(server, localUserId, "TV", seasonId));
        Assert.False(accessControl.IsAllowed(server, localUserId, "TV", episodeId));
        Assert.True(accessControl.IsAllowed(server, localUserId, "TV", otherEpisodeId));
    }

    [Fact]
    public void IsAllowed_CertainItemsSeries_AllowsDescendantEpisode()
    {
        var (cache, seriesId, seasonId, episodeId, otherEpisodeId) = MakeShowHierarchy();
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = localUserId.ToString("N"),
            Mode = RemoteUserAccessMode.CertainItems,
            ItemIds = new List<string> { seriesId.ToString("N") }
        });

        var accessControl = new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance, cache);

        Assert.True(accessControl.IsAllowed(server, localUserId, "TV", seriesId));
        Assert.True(accessControl.IsAllowed(server, localUserId, "TV", seasonId));
        Assert.True(accessControl.IsAllowed(server, localUserId, "TV", episodeId));
        Assert.False(accessControl.IsAllowed(server, localUserId, "TV", otherEpisodeId));
    }

    [Fact]
    public void IsAllowedForEveryConfiguredUser_BlockedSeries_HidesEpisodeWhenCacheWalksParents()
    {
        var (cache, seriesId, _, episodeId, otherEpisodeId) = MakeShowHierarchy();
        var server = new RemoteServer { Id = "server-a", Name = "Alice" };
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = Guid.NewGuid().ToString("N"),
            Mode = RemoteUserAccessMode.AllLibraries,
            BlockedItemIds = new List<string> { seriesId.ToString("N") }
        });

        Assert.False(RemoteAccessControlService.IsAllowedForEveryConfiguredUser(server, "TV", episodeId, null, cache));
        Assert.True(RemoteAccessControlService.IsAllowedForEveryConfiguredUser(server, "TV", otherEpisodeId, null, cache));
        Assert.True(RemoteAccessControlService.IsAllowedForEveryConfiguredUser(server, "TV", episodeId, null));
    }

    private static (FederationItemCache Cache, Guid SeriesId, Guid SeasonId, Guid EpisodeId, Guid OtherEpisodeId) MakeShowHierarchy()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var otherSeriesId = Guid.NewGuid();
        var otherEpisodeId = Guid.NewGuid();

        var series = cache.UpsertRaw("TV", "server-a", seriesId, new BaseItemDto { Name = "Show" }, 0, "Series");
        var season = cache.UpsertRaw("TV", "server-a", seasonId, new BaseItemDto { Name = "Season 1" }, 0, "Season", parentKey: series.Key);
        cache.UpsertRaw("TV", "server-a", episodeId, new BaseItemDto { Name = "Pilot" }, 0, "Episode", parentKey: season.Key);

        var otherSeries = cache.UpsertRaw("TV", "server-a", otherSeriesId, new BaseItemDto { Name = "Other Show" }, 0, "Series");
        cache.UpsertRaw("TV", "server-a", otherEpisodeId, new BaseItemDto { Name = "Other Pilot" }, 0, "Episode", parentKey: otherSeries.Key);

        return (cache, seriesId, seasonId, episodeId, otherEpisodeId);
    }
}

/// <summary>
/// Covers the admin-facing side of per-remote-user overrides: setting a rule for a
/// friend's own user and pushing the friend's full rule list to them, and receiving
/// the mirror-image push from a friend about our own users - see
/// <see cref="FederationFriendService.SetRemoteUserAccessRuleAsync"/> and
/// <see cref="FederationFriendService.ReceiveRemoteUserAccessRules"/>.
/// </summary>
[Collection("PluginInstance")]
public class FederationRemoteUserAccessPushTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationFriendService _service;

    public FederationRemoteUserAccessPushTests()
    {
        _plugin = new RealPluginInstance();
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

    private void UseFakeHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        FederationFriendService.HttpClientOverride = new HttpClient(new FakeHandler(responder));
    }

    [Fact]
    public async Task SetRemoteUserAccessRuleAsync_UnknownFriend_Fails()
    {
        var (success, message) = await _service.SetRemoteUserAccessRuleAsync(
            "no-such-friend",
            new RemoteUserAccessRule { RemoteUserId = Guid.NewGuid().ToString("N") },
            CancellationToken.None);

        Assert.False(success);
        Assert.Equal("Friend not found.", message);
    }

    [Fact]
    public async Task SetRemoteUserAccessRuleAsync_MissingRemoteUserId_Fails()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "friend-1", Name = "Bob's Server" });

        var (success, _) = await _service.SetRemoteUserAccessRuleAsync("friend-1", new RemoteUserAccessRule { RemoteUserId = string.Empty }, CancellationToken.None);

        Assert.False(success);
    }

    [Fact]
    public async Task SetRemoteUserAccessRuleAsync_SavesLocallyAndPushesTheFullListToTheFriend()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            Url = "http://bob.example",
            ApiKey = "key-1"
        });

        var pushed = new List<string>();
        UseFakeHttp(req =>
        {
            pushed.Add(req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var remoteUserId = Guid.NewGuid().ToString("N");
        var (success, message) = await _service.SetRemoteUserAccessRuleAsync(
            "friend-1",
            new RemoteUserAccessRule { RemoteUserId = remoteUserId, RemoteUserName = "kiddo", Mode = RemoteUserAccessMode.Blocked },
            CancellationToken.None);

        Assert.True(success, message);
        var server = _plugin.Configuration.RemoteServers[0];
        Assert.Single(server.RemoteUserAccessRules);
        Assert.Equal(remoteUserId, server.RemoteUserAccessRules[0].RemoteUserId);
        Assert.Equal(RemoteUserAccessMode.Blocked, server.RemoteUserAccessRules[0].Mode);
        Assert.Contains(pushed, u => u == "http://bob.example/Plugins/Federation/Friends/RemoteUserRules");
    }

    [Fact]
    public async Task SetRemoteUserAccessRuleAsync_AllLibrariesMode_ClearsAnyExistingRule()
    {
        var remoteUserId = Guid.NewGuid().ToString("N");
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob's Server",
            RemoteUserAccessRules = new List<RemoteUserAccessRule>
            {
                new RemoteUserAccessRule { RemoteUserId = remoteUserId, Mode = RemoteUserAccessMode.Blocked }
            }
        });

        var (success, _) = await _service.SetRemoteUserAccessRuleAsync(
            "friend-1",
            new RemoteUserAccessRule { RemoteUserId = remoteUserId, Mode = RemoteUserAccessMode.AllLibraries },
            CancellationToken.None);

        Assert.True(success);
        Assert.Empty(_plugin.Configuration.RemoteServers[0].RemoteUserAccessRules);
    }

    [Fact]
    public void ReceiveRemoteUserAccessRules_MatchesByFederationId_AndReplacesStoredRules()
    {
        var bob = new RemoteServer
        {
            Id = "friend-1",
            FederationId = "bob-fed-id",
            FriendUserAccessRules = new List<RemoteUserAccessRule>
            {
                new RemoteUserAccessRule { RemoteUserId = "stale-user", Mode = RemoteUserAccessMode.Blocked }
            }
        };
        _plugin.Configuration.RemoteServers.Add(bob);

        var newRule = new RemoteUserAccessRule { RemoteUserId = "fresh-user", Mode = RemoteUserAccessMode.CertainItems, ItemIds = new List<string> { "item-1" } };
        _service.ReceiveRemoteUserAccessRules(bob, new RemoteUserAccessRulesPayload
        {
            FromFederationId = "bob-fed-id",
            Rules = new List<RemoteUserAccessRule> { newRule }
        });

        var stored = _plugin.Configuration.RemoteServers[0].FriendUserAccessRules;
        Assert.Single(stored);
        Assert.Equal("fresh-user", stored[0].RemoteUserId);
    }

    [Fact]
    public void ReceiveRemoteUserAccessRules_UnknownFederationId_DoesNothing()
    {
        var bob = new RemoteServer { Id = "friend-1", FederationId = "bob-fed-id" };
        _plugin.Configuration.RemoteServers.Add(bob);

        _service.ReceiveRemoteUserAccessRules(bob, new RemoteUserAccessRulesPayload
        {
            FromFederationId = "someone-else",
            Rules = new List<RemoteUserAccessRule> { new RemoteUserAccessRule { RemoteUserId = "x" } }
        });

        Assert.Empty(_plugin.Configuration.RemoteServers[0].FriendUserAccessRules);
    }

    [Fact]
    public void ReceiveRemoteUserAccessRules_TokenForA_ClaimingB_DoesNotMutateB()
    {
        var alice = new RemoteServer
        {
            Id = "friend-a",
            Name = "Alice",
            FederationId = "alice-fed-id",
            IssuedApiKey = "token-alice"
        };
        var bob = new RemoteServer
        {
            Id = "friend-b",
            Name = "Bob",
            FederationId = "bob-fed-id",
            IssuedApiKey = "token-bob",
            FriendUserAccessRules = new List<RemoteUserAccessRule>
            {
                new RemoteUserAccessRule { RemoteUserId = "bobs-user", Mode = RemoteUserAccessMode.Blocked }
            }
        };
        _plugin.Configuration.RemoteServers.Add(alice);
        _plugin.Configuration.RemoteServers.Add(bob);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[FederationTokenAuth.Header] = "token-alice";
        var caller = FederationTokenAuth.ResolveCaller(httpContext.Request);
        Assert.Same(alice, caller);

        _service.ReceiveRemoteUserAccessRules(caller!, new RemoteUserAccessRulesPayload
        {
            FromFederationId = "bob-fed-id",
            Rules = new List<RemoteUserAccessRule>
            {
                new RemoteUserAccessRule { RemoteUserId = "hijacked", Mode = RemoteUserAccessMode.AllLibraries }
            }
        });

        Assert.Empty(alice.FriendUserAccessRules);
        var bobsRules = Assert.Single(bob.FriendUserAccessRules);
        Assert.Equal("bobs-user", bobsRules.RemoteUserId);
        Assert.Equal(RemoteUserAccessMode.Blocked, bobsRules.Mode);
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

/// <summary>
/// Covers the stream-time re-check in <see cref="FederationStreamHandler.HandleProxyAsync"/>
/// - redundant with <see cref="FederationMediaSourceProvider"/> already deciding
/// whether to hand out a Proxy-mode URL in the first place, but catches a URL that
/// outlives the rule that allowed it.
/// </summary>
[Collection("PluginInstance")]
public class FederationStreamHandlerAccessControlTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationStreamHandler _handler;

    public FederationStreamHandlerAccessControlTests()
    {
        _plugin = new RealPluginInstance();

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Moq.Mock.Of<IRemoteServerClientFactory>());
        var federationManager = new FederationLibraryManager(
            Moq.Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Moq.Mock.Of<IRemoteServerClientFactory>(),
            cache,
            bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var accessControl = new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance);

        // HandleProxyAsync mints a playback token from the remote before relaying
        // for any request that isn't denied outright by the access-control check
        // this class exercises - a fake token-issuing client stands in for that,
        // separate from HttpClientOverride (which fakes the actual byte-relay
        // fetch to the token-gated DirectStream URL).
        var tokenClient = new RemoteServerClient(
            new RemoteServer { Id = "serverA", Url = "http://friend.example:8096", ApiKey = "federation-token" },
            NullLogger.Instance,
            new HttpClient(new DelegatingFakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"fake-playback-token\"}", System.Text.Encoding.UTF8, "application/json")
            })) { BaseAddress = new Uri("http://friend.example:8096") });
        var clientFactory = new Moq.Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(Moq.It.IsAny<RemoteServer>())).Returns(tokenClient);

        _handler = new FederationStreamHandler(NullLogger<FederationStreamHandler>.Instance, federationManager, accessControl, clientFactory.Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()), bandwidthMonitor);
    }

    public void Dispose()
    {
        FederationStreamHandler.HttpClientOverride = null;
        _plugin.Dispose();
    }

    private static (HttpRequest Request, HttpResponse Response) MakeContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new System.IO.MemoryStream();
        return (context.Request, context.Response);
    }

    [Fact]
    public async Task HandleProxyAsync_BlockedRequestingUser_Returns403_AndNeverCallsTheRemote()
    {
        var blockedUserId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "serverA",
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true,
            FriendUserAccessRules = new List<RemoteUserAccessRule>
            {
                new RemoteUserAccessRule { RemoteUserId = blockedUserId.ToString("N"), Mode = RemoteUserAccessMode.Blocked }
            }
        });

        var remoteWasCalled = false;
        FederationStreamHandler.HttpClientOverride = new HttpClient(new DelegatingFakeHandler(_ =>
        {
            remoteWasCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var (request, response) = MakeContext();

        await _handler.HandleProxyAsync(
            "serverA",
            remoteItemId.ToString("N"),
            request,
            response,
            CancellationToken.None,
            isAudio: false,
            requestingUserId: blockedUserId.ToString("N"));

        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.False(remoteWasCalled);
    }

    [Fact]
    public async Task HandleProxyAsync_NoRequestingUserId_BehavesAsBefore()
    {
        // Backward compatibility: a URL minted before this feature existed (or by
        // an older client) carries no requestingUserId at all, and must not be
        // denied just because *some* rule exists for *some* other user.
        var remoteItemId = Guid.NewGuid();
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "serverA",
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true,
            FriendUserAccessRules = new List<RemoteUserAccessRule>
            {
                new RemoteUserAccessRule { RemoteUserId = Guid.NewGuid().ToString("N"), Mode = RemoteUserAccessMode.Blocked }
            }
        });

        FederationStreamHandler.HttpClientOverride = new HttpClient(new DelegatingFakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }));

        var (request, response) = MakeContext();

        await _handler.HandleProxyAsync("serverA", remoteItemId.ToString("N"), request, response, CancellationToken.None);

        Assert.NotEqual(StatusCodes.Status403Forbidden, response.StatusCode);
    }

    private sealed class DelegatingFakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public DelegatingFakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
