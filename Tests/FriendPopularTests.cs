using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// What's popular on friends' servers: counts of distinct users over the last 30
/// days from Jellyfin's own watch history, own media only, per-viewer visibility.
/// </summary>
[Collection("PluginInstance")]
public class FriendPopularTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();
    private readonly FederationItemCache _cache = new(NullLogger<FederationItemCache>.Instance);
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();

    public FriendPopularTests()
    {
        // Off on new installs; these owner tests share by default unless they say otherwise.
        _plugin.Configuration.SharePopularWithFriends = true;
    }

    public void Dispose() => _plugin.Dispose();

    private static User NewUser(string name, bool disabled = false)
    {
        var user = new User(name, "auth", "reset") { Id = Guid.NewGuid() };
        user.AddDefaultPermissions();
        user.SetPermission(PermissionKind.IsDisabled, disabled);
        return user;
    }

    private (FederationFriendPopularService Service, FederationLibraryManager Manager) Create(Mock<IRemoteServerClientFactory>? factory = null)
    {
        _library.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string key, Type type) => new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(type.FullName + key))));
        factory ??= new Mock<IRemoteServerClientFactory>();
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory.Object);
        var manager = new FederationLibraryManager(_library.Object, NullLogger<FederationLibraryManager>.Instance, factory.Object,
            _cache, monitor, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var service = new FederationFriendPopularService(
            _library.Object,
            _users.Object,
            _userData.Object,
            manager,
            new FederationPeerAccessService(_library.Object),
            factory.Object,
            NullLogger<FederationFriendPopularService>.Instance);
        return (service, manager);
    }

    private void Played(User user, BaseItem item, int daysAgo)
        => _userData.Setup(d => d.GetUserData(user, item)).Returns(new UserItemData
        {
            Key = item.Id.ToString("N"),
            Played = true,
            LastPlayedDate = DateTime.UtcNow.AddDays(-daysAgo)
        });

    private static RemoteServer Caller(string? blockedItem = null, string? viewer = null)
    {
        var caller = new RemoteServer { Id = "caller", Name = "Caller", Enabled = true, ShareAllLibraries = true };
        if (blockedItem != null)
        {
            caller.RemoteUserAccessRules.Add(new RemoteUserAccessRule { RemoteUserId = viewer!, BlockedItemIds = { blockedItem } });
        }

        return caller;
    }

    [Fact]
    public void Rank_CountsDistinctUsers_InsideTheWindow_ThenMostRecent()
    {
        var now = DateTime.UtcNow;
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var stale = Guid.NewGuid();
        var ranked = FederationFriendPopularService.Rank(new[]
        {
            (a, u1, now.AddDays(-1)), (a, u1, now.AddDays(-2)),           // one user twice counts once
            (b, u1, now.AddDays(-3)), (b, u2, now.AddDays(-4)),           // two users
            (c, u1, now.AddDays(-1)),                                     // one user
            (stale, u1, now.AddDays(-31)), (stale, u2, now.AddDays(-45))  // outside the window
        }, now);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(b, ranked[0].Id);
        Assert.Equal(2, ranked[0].Count);
        Assert.DoesNotContain(ranked, r => r.Id == stale);
        Assert.All(ranked.Skip(1), r => Assert.Equal(1, r.Count));
    }

    [Fact]
    public void RollUp_EpisodesToSeries_TracksToAlbum_OthersToThemselves()
    {
        var series = Guid.NewGuid();
        var album = Guid.NewGuid();
        Assert.Equal(series, FederationFriendPopularService.RollUpTarget(new Episode { Id = Guid.NewGuid(), SeriesId = series }));
        Assert.Equal(album, FederationFriendPopularService.RollUpTarget(new Audio { Id = Guid.NewGuid(), ParentId = album }));
        var movie = new Movie { Id = Guid.NewGuid() };
        Assert.Equal(movie.Id, FederationFriendPopularService.RollUpTarget(movie));
        Assert.Null(FederationFriendPopularService.RollUpTarget(new Episode { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void Owner_SharesRankedCountsOnly_OwnMediaOnly_OnlyWhatTheViewerMaySee()
    {
        var alice = NewUser("alice");
        var bob = NewUser("bob");
        var gone = NewUser("gone", disabled: true);
        _users.Setup(u => u.GetUsers()).Returns(new[] { alice, bob, gone });

        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var ep1 = new Episode { Id = Guid.NewGuid(), Name = "E1", SeriesId = series.Id };
        var ep2 = new Episode { Id = Guid.NewGuid(), Name = "E2", SeriesId = series.Id };
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie" };
        var old = new Movie { Id = Guid.NewGuid(), Name = "Old news" };
        var blocked = new Movie { Id = Guid.NewGuid(), Name = "Blocked for viewer" };
        var federated = new Movie { Id = Guid.NewGuid(), Name = "Federated", ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "x" } };
        var downloaded = new Movie { Id = Guid.NewGuid(), Name = "Downloaded", ProviderIds = new Dictionary<string, string> { [FederationLibraryManager.FederationDownloadedFromProviderId] = "friend" } };
        var secret = new Movie { Id = Guid.NewGuid(), Name = "Only disabled user" };
        foreach (var item in new BaseItem[] { series, ep1, ep2, movie, old, blocked, federated, downloaded, secret })
        {
            _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }

        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User == alice))).Returns(new List<BaseItem> { ep1, movie, old, blocked, federated, downloaded });
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User == bob))).Returns(new List<BaseItem> { ep2, movie, blocked });
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User == gone))).Returns(new List<BaseItem> { secret });
        Played(alice, ep1, 1);
        Played(bob, ep2, 2);
        Played(alice, movie, 6);
        Played(bob, movie, 5);
        Played(alice, old, 40);
        Played(alice, blocked, 3);
        Played(bob, blocked, 3);
        Played(alice, federated, 1);
        Played(alice, downloaded, 1);
        Played(gone, secret, 1);

        var viewer = Guid.NewGuid().ToString("N");
        var (service, _) = Create();
        var body = service.GetSharedPopular(Caller(blocked.Id.ToString("N"), viewer), viewer);

        Assert.True(body.Shared);
        // The episodes roll up to one series (two users); the movie also has two
        // users but was played less recently than the series' latest play.
        Assert.Equal(new[] { series.Id.ToString("N"), movie.Id.ToString("N") }, body.Items!.Select(i => i.Id).ToArray());
        Assert.All(body.Items!, i => Assert.Equal(2, i.Viewers));

        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bob", json, StringComparison.OrdinalIgnoreCase);

        // Another viewer on the same friend has no rule, so they also get the blocked title.
        Assert.Contains(blocked.Id.ToString("N"), service.GetSharedPopular(Caller(), Guid.NewGuid().ToString("N")).Items!.Select(i => i.Id));
    }

    [Fact]
    public void Owner_IsComputedOnceThenCached_AndNothingIsRecordedWhenOff()
    {
        var alice = NewUser("alice");
        _users.Setup(u => u.GetUsers()).Returns(new[] { alice });
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie" };
        _library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });
        Played(alice, movie, 6);
        var (service, _) = Create();

        service.GetSharedPopular(Caller(), null);
        service.GetSharedPopular(Caller(), Guid.NewGuid().ToString("N"));
        _library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once);

        _plugin.Configuration.SharePopularWithFriends = false;
        var off = service.GetSharedPopular(Caller(), null);
        Assert.False(off.Shared);
        Assert.Empty(off.Items!);
    }

    [Fact]
    public void Settings_ShowOnSharingOff_ByDefault()
    {
        var config = new PluginConfiguration();
        Assert.False(config.SharePopularWithFriends);
        Assert.True(config.ShowFriendsPopular);
    }

    [Fact]
    public async Task Viewer_SeesOneRowPerFriend_OnlyTitlesTheyCanOpenHere_AndItIsCached()
    {
        var friend = new RemoteServer { Id = "friend", Name = "Friend", Url = "http://friend.local", ApiKey = "k", Enabled = true, Kind = ServerKind.Jellyfin };
        _plugin.Configuration.RemoteServers.Add(friend);
        var r1 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        var r3 = Guid.NewGuid();
        var e1 = _cache.UpsertRaw("Movies", "friend", r1, new BaseItemDto { Name = "One" }, 0, "Movie");
        var e2 = _cache.UpsertRaw("Movies", "friend", r2, new BaseItemDto { Name = "Two" }, 0, "Movie");
        var e3 = _cache.UpsertRaw("Movies", "friend", r3, new BaseItemDto { Name = "Hidden here" }, 0, "Movie");

        var handler = new PopularHandler(new[] { r2, Guid.NewGuid(), r3, r1 });
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.Is<RemoteServer>(s => s.Id == "friend")))
            .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(friend.Url) }));
        var (service, manager) = Create(factory);
        var id1 = manager.ComputeItemId(e1);
        var id2 = manager.ComputeItemId(e2);
        var id3 = manager.ComputeItemId(e3);

        var viewer = NewUser("viewer");
        var other = NewUser("other");
        _library.Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(q => q.User == viewer)))
            .Returns((InternalItemsQuery q) => q.ItemIds.Where(id => id != id3).ToList());
        _library.Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(q => q.User == other)))
            .Returns((InternalItemsQuery q) => q.ItemIds.ToList());

        var section = Assert.Single(await service.GetSectionsAsync(viewer, CancellationToken.None));
        Assert.Equal(new[] { id2, id1 }, section.ItemIds);
        Assert.Equal(viewer.Id.ToString("N"), handler.LastUserId);

        await service.GetSectionsAsync(viewer, CancellationToken.None);
        Assert.Equal(1, handler.Calls);

        // Visibility is per viewer: another viewer with different rules sees the third title too.
        var otherSection = Assert.Single(await service.GetSectionsAsync(other, CancellationToken.None));
        Assert.Equal(new[] { id2, id3, id1 }, otherSection.ItemIds);

        _plugin.Configuration.ShowFriendsPopular = false;
        Assert.Empty(await service.GetSectionsAsync(viewer, CancellationToken.None));
    }

    [Fact]
    public async Task Viewer_OlderFriendWithoutPopular_IsSkippedQuietly_AndNotAskedAgainSoon()
    {
        var friend = new RemoteServer { Id = "old", Name = "Old", Url = "http://old.local", ApiKey = "k", Enabled = true, Kind = ServerKind.Jellyfin };
        _plugin.Configuration.RemoteServers.Add(friend);
        var handler = new PopularHandler(Array.Empty<Guid>()) { NotFound = true };
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(friend.Url) }));
        var (service, _) = Create(factory);

        Assert.Empty(await service.GetSectionsAsync(NewUser("viewer"), CancellationToken.None));
        Assert.Empty(await service.GetSectionsAsync(NewUser("another"), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Viewer_FailingFriend_IsNotAskedAgainOnEveryHomeLoad()
    {
        var friend = new RemoteServer { Id = "slow", Name = "Slow", Url = "http://slow.local", ApiKey = "k", Enabled = true, Kind = ServerKind.Jellyfin };
        _plugin.Configuration.RemoteServers.Add(friend);
        var handler = new PopularHandler(Array.Empty<Guid>()) { ServerError = true };
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(friend.Url) }));
        var (service, _) = Create(factory);
        var viewer = NewUser("viewer");

        Assert.Empty(await service.GetSectionsAsync(viewer, CancellationToken.None));
        Assert.Empty(await service.GetSectionsAsync(viewer, CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Owner_ServesTheStaleAnswerWhileOneRefreshRuns()
    {
        var alice = NewUser("alice");
        _users.Setup(u => u.GetUsers()).Returns(new[] { alice });
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie" };
        _library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        using var release = new ManualResetEventSlim(false);
        var scans = 0;
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() =>
        {
            if (Interlocked.Increment(ref scans) > 1)
            {
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return new List<BaseItem> { movie };
        });
        Played(alice, movie, 6);
        var (service, _) = Create();
        Assert.Single(service.GetSharedPopular(Caller(), null).Items!);

        // Age the cache past its lifetime, then ask from several threads at once.
        service.OwnerClock = () => DateTime.UtcNow.AddHours(1);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = Enumerable.Range(0, 4).Select(_ => Task.Run(() => service.GetSharedPopular(Caller(), null))).ToArray();
        Task.WaitAll(results, TimeSpan.FromSeconds(5));
        Assert.All(results, r => Assert.Single(r.Result.Items!));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        release.Set();
        Thread.Sleep(300);
        Assert.Equal(2, scans);
    }

    [Fact]
    public void Routes_PeerIsForFriends_ViewerNeedsSignIn()
    {
        var type = typeof(Jellyfin.Plugin.Federation.Api.FederationController);
        Assert.NotNull(type.GetMethod("GetPeerPopular")!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false).SingleOrDefault());
        var viewer = type.GetMethod("GetFriendPopular")!;
        Assert.Single(viewer.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false));
        Assert.Empty(viewer.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false));
    }

    private sealed class PopularHandler : HttpMessageHandler
    {
        private readonly Guid[] _ids;
        private int _calls;

        public PopularHandler(Guid[] ids) => _ids = ids;

        public bool NotFound { get; set; }

        public bool ServerError { get; set; }

        public int Calls => _calls;

        public string? LastUserId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/Plugins/Federation/Peer/Popular", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref _calls);
            LastUserId = request.Headers.TryGetValues(RemoteServerClient.RemoteUserIdHeader, out var ids) ? ids.First() : null;
            if (ServerError)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (NotFound)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var items = string.Join(",", _ids.Select((id, i) => "{\"id\":\"" + id.ToString("N") + "\",\"viewers\":" + (10 - i) + "}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"shared\":true,\"items\":[" + items + "]}", Encoding.UTF8, "application/json")
            });
        }
    }
}
