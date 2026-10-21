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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Friends' favorites: a server shares which of its own titles its users favorited
/// (never who, never someone else's media, only what the asking viewer may see), and
/// shows each viewer a "Favorites on (friend)" row of titles they can open here.
/// </summary>
[Collection("PluginInstance")]
public class FriendFavoritesTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();
    private readonly FederationItemCache _cache = new(NullLogger<FederationItemCache>.Instance);
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();

    public void Dispose() => _plugin.Dispose();

    private static User NewUser(string name, bool disabled = false)
    {
        var user = new User(name, "auth", "reset") { Id = Guid.NewGuid() };
        user.AddDefaultPermissions();
        user.SetPermission(PermissionKind.IsDisabled, disabled);
        return user;
    }

    private (FederationFriendFavoritesService Service, FederationLibraryManager Manager) Create(Mock<IRemoteServerClientFactory>? factory = null)
    {
        // Jellyfin derives an item's id from its path; any stable per-path id will do here.
        _library.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string key, Type type) => new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(type.FullName + key))));
        factory ??= new Mock<IRemoteServerClientFactory>();
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory.Object);
        var manager = new FederationLibraryManager(_library.Object, NullLogger<FederationLibraryManager>.Instance, factory.Object,
            _cache, monitor, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var service = new FederationFriendFavoritesService(
            _library.Object,
            _users.Object,
            manager,
            new FederationPeerAccessService(_library.Object),
            factory.Object,
            NullLogger<FederationFriendFavoritesService>.Instance);
        return (service, manager);
    }

    [Fact]
    public void Rank_MostFavoritedFirst_ThenNewest()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var old = new DateTime(2020, 1, 1);
        var recent = new DateTime(2024, 1, 1);
        var ranked = FederationFriendFavoritesService.Rank(new[] { (a, old), (b, old), (b, old), (c, recent) });

        Assert.Equal(new[] { b, c, a }, ranked.Select(r => r.Id).ToArray());
        Assert.Equal(2, ranked[0].Count);
    }

    [Fact]
    public void MapToLocal_KeepsTheFriendsOrder_DropsUnknownAndDuplicates()
    {
        var known1 = Guid.NewGuid();
        var known2 = Guid.NewGuid();
        var local1 = Guid.NewGuid();
        var local2 = Guid.NewGuid();
        var map = new Dictionary<Guid, Guid> { [known1] = local1, [known2] = local2 };
        var mapped = FederationFriendFavoritesService.MapToLocal(
            "friend",
            new[] { new PeerFavorite(known2.ToString("N"), 3), new PeerFavorite("garbage", 2), new PeerFavorite(Guid.NewGuid().ToString("N"), 2), new PeerFavorite(known1.ToString("N"), 1), new PeerFavorite(known2.ToString("D"), 1) },
            (_, remote) => map.TryGetValue(remote, out var local) ? local : null);

        Assert.Equal(new[] { local2, local1 }, mapped);
    }

    [Fact]
    public void Owner_SharesTitlesOnly_OwnMediaOnly_AndOnlyWhatTheViewerMaySee()
    {
        var alice = NewUser("alice");
        var bob = NewUser("bob");
        var gone = NewUser("gone", disabled: true);
        _users.Setup(u => u.GetUsers()).Returns(new[] { alice, bob, gone });

        var loved = new Movie { Id = Guid.NewGuid(), Name = "Loved" };
        var liked = new Movie { Id = Guid.NewGuid(), Name = "Liked" };
        var blocked = new Movie { Id = Guid.NewGuid(), Name = "Blocked for viewer" };
        var fromAnotherFriend = new Movie { Id = Guid.NewGuid(), Name = "Federated", ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "x" } };
        var secret = new Movie { Id = Guid.NewGuid(), Name = "Only disabled user" };
        foreach (var item in new BaseItem[] { loved, liked, blocked, fromAnotherFriend, secret })
        {
            _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }

        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User == alice))).Returns(new List<BaseItem> { loved, liked, blocked, fromAnotherFriend });
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User == bob))).Returns(new List<BaseItem> { loved });
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User == gone))).Returns(new List<BaseItem> { secret });

        var viewer = Guid.NewGuid().ToString("N");
        var caller = new RemoteServer
        {
            Id = "caller",
            Name = "Caller",
            Enabled = true,
            ShareAllLibraries = true,
            RemoteUserAccessRules = { new RemoteUserAccessRule { RemoteUserId = viewer, BlockedItemIds = { blocked.Id.ToString("N") } } }
        };

        var (service, _) = Create();
        var body = service.GetSharedFavorites(caller, viewer);

        Assert.True(body.Shared);
        Assert.Equal(new[] { loved.Id.ToString("N"), liked.Id.ToString("N") }, body.Items!.Select(i => i.Id).ToArray());
        Assert.Equal(2, body.Items![0].Favorites);

        // Another viewer on the same friend has no rule, so they also get the third title.
        Assert.Contains(blocked.Id.ToString("N"), service.GetSharedFavorites(caller, Guid.NewGuid().ToString("N")).Items!.Select(i => i.Id));

        // The wire shape carries ids and counts only.
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Loved", json, StringComparison.Ordinal);

        _plugin.Configuration.ShareFavoritesWithFriends = false;
        var off = service.GetSharedFavorites(caller, viewer);
        Assert.False(off.Shared);
        Assert.Empty(off.Items!);
    }

    [Fact]
    public async Task Viewer_SeesOneRowPerFriend_OnlyTitlesTheyCanOpenHere()
    {
        var friend = new RemoteServer { Id = "friend", Name = "Friend", Url = "http://friend.local", ApiKey = "k", Enabled = true, Kind = ServerKind.Jellyfin };
        _plugin.Configuration.RemoteServers.Add(friend);
        var r1 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        var r3 = Guid.NewGuid();
        var e1 = _cache.UpsertRaw("Movies", "friend", r1, new BaseItemDto { Name = "One" }, 0, "Movie");
        var e2 = _cache.UpsertRaw("Movies", "friend", r2, new BaseItemDto { Name = "Two" }, 0, "Movie");
        var e3 = _cache.UpsertRaw("Movies", "friend", r3, new BaseItemDto { Name = "Hidden here" }, 0, "Movie");

        var handler = new FavoritesHandler(new[] { r2, Guid.NewGuid(), r3, r1 });
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.Is<RemoteServer>(s => s.Id == "friend")))
            .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(friend.Url) }));
        var (service, manager) = Create(factory);
        var id1 = manager.ComputeItemId(e1);
        var id2 = manager.ComputeItemId(e2);
        var id3 = manager.ComputeItemId(e3);

        var viewer = NewUser("viewer");
        _library.Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(q => q.User == viewer)))
            .Returns((InternalItemsQuery q) => q.ItemIds.Where(id => id != id3).ToList());

        var sections = await service.GetSectionsAsync(viewer, CancellationToken.None);

        var section = Assert.Single(sections);
        Assert.Equal("Friend", section.ServerName);
        Assert.Equal(new[] { id2, id1 }, section.ItemIds);
        Assert.Equal(viewer.Id.ToString("N"), handler.LastUserId);

        // Cached for a few minutes: a second visit doesn't ask the friend again.
        await service.GetSectionsAsync(viewer, CancellationToken.None);
        Assert.Equal(1, handler.Calls);

        _plugin.Configuration.ShowFriendsFavorites = false;
        Assert.Empty(await service.GetSectionsAsync(viewer, CancellationToken.None));
        _plugin.Configuration.ShowFriendsFavorites = true;

        friend.PeerSharesFavorites = false;
        Assert.Empty(await service.GetSectionsAsync(viewer, CancellationToken.None));
    }

    [Fact]
    public async Task Viewer_FailedAsk_IsNotRepeatedOnEveryScan()
    {
        var friend = new RemoteServer { Id = "flaky", Name = "Flaky", Url = "http://flaky.local", ApiKey = "k", Enabled = true, Kind = ServerKind.Jellyfin };
        _plugin.Configuration.RemoteServers.Add(friend);
        var calls = 0;
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(new DelegateHandler(() => { calls++; return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError); })) { BaseAddress = new Uri(friend.Url) }));
        var (service, _) = Create(factory);
        var viewer = NewUser("viewer");

        await service.GetSectionsAsync(viewer, CancellationToken.None);
        await service.GetSectionsAsync(viewer, CancellationToken.None);
        await service.GetSectionsAsync(viewer, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;

        public DelegateHandler(Func<HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder());
    }

    [Fact]
    public async Task Viewer_OlderFriendWithoutFavorites_IsSkippedQuietly_AndNotAskedAgainSoon()
    {
        var friend = new RemoteServer { Id = "old", Name = "Old", Url = "http://old.local", ApiKey = "k", Enabled = true, Kind = ServerKind.Jellyfin };
        _plugin.Configuration.RemoteServers.Add(friend);
        var handler = new FavoritesHandler(Array.Empty<Guid>()) { NotFound = true };
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(friend.Url) }));
        var (service, _) = Create(factory);
        var viewer = NewUser("viewer");

        Assert.Empty(await service.GetSectionsAsync(viewer, CancellationToken.None));
        Assert.Empty(await service.GetSectionsAsync(NewUser("another"), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void PeerRules_CarryTheFriendsDownloadAndFavoritesSwitches()
    {
        var body = System.Text.Json.JsonSerializer.Deserialize<PeerUserRulesBody>(
            "{\"rules\":[],\"allowDownloads\":false,\"shareFavorites\":true}",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.False(body!.AllowDownloads);
        Assert.True(body.ShareFavorites);

        var old = System.Text.Json.JsonSerializer.Deserialize<PeerUserRulesBody>("{\"rules\":[]}", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Null(old!.AllowDownloads);
        Assert.Null(old.ShareFavorites);
    }

    private sealed class FavoritesHandler : HttpMessageHandler
    {
        private readonly Guid[] _ids;
        private int _calls;

        public FavoritesHandler(Guid[] ids) => _ids = ids;

        public bool NotFound { get; set; }

        public int Calls => _calls;

        public string? LastUserId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/Plugins/Federation/Peer/Favorites", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref _calls);
            LastUserId = request.Headers.TryGetValues(RemoteServerClient.RemoteUserIdHeader, out var ids) ? ids.First() : null;
            if (NotFound)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var items = string.Join(",", _ids.Select((id, i) => "{\"id\":\"" + id.ToString("N") + "\",\"favorites\":" + (10 - i) + "}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"shared\":true,\"items\":[" + items + "]}", Encoding.UTF8, "application/json")
            });
        }
    }
}

/// <summary>
/// Who may call the new routes: friends (by federation token, inside the action),
/// any signed-in viewer for their own rows, and only admins for the settings.
/// </summary>
public class FriendFavoritesRouteTests
{
    private static System.Reflection.MethodInfo Method(string name)
        => typeof(Jellyfin.Plugin.Federation.Api.FederationController).GetMethod(name)
            ?? throw new InvalidOperationException(name + " not found");

    [Fact]
    public void PeerFavorites_IsForFriendsOnly()
    {
        var method = Method("GetPeerFavorites");
        Assert.NotNull(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false).SingleOrDefault());
        // The friend check itself (federation token, 401 otherwise) is
        // exercised end to end by scripts/federation_peer_smoke.py.
    }

    [Fact]
    public void ViewerRows_NeedASignIn_SettingsNeedAnAdmin()
    {
        var viewer = Method("GetFriendFavorites");
        var authorize = Assert.Single(viewer.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.True(string.IsNullOrEmpty(authorize.Policy));
        Assert.Empty(viewer.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false));

        foreach (var name in new[] { "GetFavoritesSettings", "SetFavoritesSettings" })
        {
            var admin = Assert.Single(Method(name).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
            Assert.Equal("RequiresElevation", admin.Policy);
        }
    }
}
