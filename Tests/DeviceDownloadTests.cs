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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Any viewer can save a friend's title to their device with their app's own
/// Download button: the button is offered when the friend allows it, the file comes
/// straight from the friend when it can, and every refusal says why.
/// </summary>
[Collection("PluginInstance")]
public class DeviceDownloadTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();
    private readonly FederationItemCache _cache = new(NullLogger<FederationItemCache>.Instance);
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Guid _remoteId = Guid.NewGuid();
    private readonly User _viewer;
    private readonly RemoteServer _friend;
    private readonly FederatedCacheEntry _entry;
    private readonly Movie _movie;

    public DeviceDownloadTests()
    {
        _viewer = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() };
        _viewer.AddDefaultPermissions();
        _viewer.SetPermission(PermissionKind.EnableContentDownloading, true);
        _friend = new RemoteServer { Id = "friend", Name = "Friend", Url = "https://friend.example", ApiKey = "k", Enabled = true, Kind = ServerKind.Jellyfin };
        _plugin.Configuration.RemoteServers.Add(_friend);
        _entry = _cache.UpsertRaw("Movies", "friend", _remoteId, new BaseItemDto { Name = "Dune", ProductionYear = 2021, Container = "mkv" }, 0, "Movie");
        _movie = new Movie { Id = Guid.NewGuid(), Name = "Dune", ProductionYear = 2021, Path = "http://127.0.0.1:8096/Plugins/Federation/Stream?x=1", ProviderIds = new Dictionary<string, string> { ["FederationKey"] = _entry.Key } };
    }

    public void Dispose() => _plugin.Dispose();

    private FederationDeviceDownloadService Service(HttpMessageHandler handler)
    {
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.Is<RemoteServer>(s => s.Id == _friend.Id)))
            .Returns(new RemoteServerClient(_friend, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(_friend.Url) }));
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory.Object);
        var manager = new FederationLibraryManager(_library.Object, NullLogger<FederationLibraryManager>.Instance, factory.Object,
            _cache, monitor, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        return new FederationDeviceDownloadService(manager, factory.Object, NullLogger<FederationDeviceDownloadService>.Instance);
    }

    [Fact]
    public void AttachmentHeader_KeepsUnicodeNames_AndNeverInjectsHeaders()
    {
        var header = DeviceDownload.AttachmentHeader("Amélie \"2001\"\r\nSet-Cookie: x.mkv");
        Assert.StartsWith("attachment", header);
        Assert.DoesNotContain("\r", header);
        Assert.DoesNotContain("\n", header);
        Assert.Contains("filename*=UTF-8''", header);
        Assert.Contains("Am%C3%A9lie", header);

        Assert.Equal("download", DeviceDownload.SafeFileName("  ..  "));
        Assert.Equal("download", DeviceDownload.SafeFileName(null));
        Assert.Equal("etcpasswd", DeviceDownload.SafeFileName("../etc/passwd"));
        var longName = DeviceDownload.SafeFileName(new string('a', 400) + ".mkv");
        Assert.True(longName.Length <= 200);
        Assert.EndsWith(".mkv", longName);
    }

    [Fact]
    public void TitleFileName_UsesYearAndTheFriendsContainer()
    {
        Assert.Equal("Dune (2021).mkv", DeviceDownload.TitleFileName("Dune", 2021, "mkv", "mp4"));
        Assert.Equal("Dune (2021).mov", DeviceDownload.TitleFileName("Dune (2021)", 2021, "mov,mp4,m4a", "mkv"));
        Assert.Equal("Dune.mkv", DeviceDownload.TitleFileName("Dune", null, "not a container!", "mkv"));

        var episode = new Episode { Name = "Pilot", SeriesName = "Show", ParentIndexNumber = 1, IndexNumber = 2 };
        Assert.Equal("Show - S01E02 - Pilot.mkv", FederationDeviceDownloadService.LocalFileName(episode, "mkv", audio: false));
        Assert.Equal("Song.flac", FederationDeviceDownloadService.LocalFileName(new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Song" }, "flac", audio: true));
    }

    [Theory]
    [InlineData(true, "https://friend.example", "https", DeviceDownloadRoute.Redirect)]
    [InlineData(true, "http://friend.example", "https", DeviceDownloadRoute.Relay)]
    [InlineData(true, "https://friend.example", "http", DeviceDownloadRoute.Redirect)]
    [InlineData(true, "http://friend.example", "http", DeviceDownloadRoute.Redirect)]
    [InlineData(false, "https://friend.example", "https", DeviceDownloadRoute.Relay)]
    [InlineData(true, "not a url", "https", DeviceDownloadRoute.Relay)]
    public void Route_RedirectsUnlessTheFriendIsOldOrWouldDowngradeAnHttpsApp(bool namesFile, string friend, string scheme, DeviceDownloadRoute expected)
        => Assert.Equal(expected, DeviceDownload.Decide(namesFile, friend, scheme).Route);

    [Fact]
    public void Route_RebuildsAnHttpFriendOnHttps_WhenItIsReachableThere()
    {
        Assert.Equal("https://friend.example:8920", DeviceDownload.HttpsForm("http://friend.example:8920"));
        Assert.Equal("https://friend.example", DeviceDownload.HttpsForm("http://friend.example"));
        Assert.Null(DeviceDownload.HttpsForm("https://friend.example"));

        var (route, baseUrl, reason) = DeviceDownload.Decide(true, "http://friend.example", "https", "https://friend.example");
        Assert.Equal(DeviceDownloadRoute.Redirect, route);
        Assert.Equal("https://friend.example", baseUrl);
        Assert.Null(reason);
        Assert.NotNull(DeviceDownload.Decide(false, "https://friend.example", "https").Reason);
        Assert.Contains("https", DeviceDownload.Decide(true, "http://friend.example", "https").Reason);
    }

    [Fact]
    public void FriendUrl_CarriesOnlyTheDownloadPermit()
    {
        var url = DeviceDownload.FriendDownloadUrl("https://friend.example/", _remoteId, "a b+c", audio: true);
        Assert.Equal($"https://friend.example/Plugins/Federation/DirectStream/{_remoteId:N}?token=a%20b%2Bc&download=true&audio=true", url);
        Assert.DoesNotContain("api_key", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendAllows_FollowsTheFriendsSwitches_AndOnlyThatViewersRule()
    {
        var other = Guid.NewGuid();
        Assert.True(FederationDeviceDownloadService.FriendAllows(_friend, _viewer.Id, "Movies", _remoteId, _cache));

        _friend.PeerAllowsDownloads = false;
        Assert.False(FederationDeviceDownloadService.FriendAllows(_friend, _viewer.Id, "Movies", _remoteId, _cache));
        _friend.PeerAllowsDownloads = null;

        _friend.FriendUserAccessRules.Add(new RemoteUserAccessRule { RemoteUserId = _viewer.Id.ToString("N"), AllowDownload = false });
        Assert.False(FederationDeviceDownloadService.FriendAllows(_friend, _viewer.Id, "Movies", _remoteId, _cache));
        Assert.True(FederationDeviceDownloadService.FriendAllows(_friend, other, "Movies", _remoteId, _cache));

        _friend.FriendUserAccessRules[0] = new RemoteUserAccessRule { RemoteUserId = _viewer.Id.ToString("N"), Mode = RemoteUserAccessMode.Blocked };
        Assert.False(FederationDeviceDownloadService.FriendAllows(_friend, _viewer.Id, "Movies", _remoteId, _cache));
        _friend.FriendUserAccessRules.Clear();

        _friend.Kind = ServerKind.Plex;
        Assert.False(FederationDeviceDownloadService.FriendAllows(_friend, _viewer.Id, "Movies", _remoteId, _cache));
        _friend.Kind = ServerKind.Jellyfin;
        _friend.Enabled = false;
        Assert.False(FederationDeviceDownloadService.FriendAllows(_friend, _viewer.Id, "Movies", _remoteId, _cache));
    }

    [Fact]
    public void CanOffer_NeedsTheViewersOwnDownloadPermission_AndAFile()
    {
        var service = Service(new PermitHandler());
        Assert.True(service.CanOffer(_viewer, _movie));

        Assert.False(service.CanOffer(_viewer, new Series { Id = Guid.NewGuid(), ProviderIds = _movie.ProviderIds }));

        _viewer.SetPermission(PermissionKind.EnableContentDownloading, false);
        Assert.False(service.CanOffer(_viewer, _movie));
    }

    [Fact]
    public async Task Plan_SendsTheViewerStraightToTheFriend_WithAViewerBoundPermit()
    {
        var handler = new PermitHandler { NamesFile = true };
        var plan = await Service(handler).PlanAsync(_viewer, _movie, "https", CancellationToken.None);

        Assert.Equal(200, plan.Status);
        Assert.Equal(DeviceDownloadRoute.Redirect, plan.Route);
        Assert.StartsWith($"https://friend.example/Plugins/Federation/DirectStream/{_remoteId:N}?token=permit-1&download=true", plan.Url);
        Assert.Equal("Dune (2021).mkv", plan.FileName);
        Assert.Equal(_viewer.Id.ToString("N"), handler.LastUserId);
        Assert.Equal("Download", handler.LastPurpose);
    }

    [Fact]
    public async Task Plan_HttpFriendIsSentToItsHttpsAddress_ForAnHttpsApp_WhenReachable()
    {
        _friend.Url = "http://plan-direct.example";
        var handler = new PermitHandler { NamesFile = true };
        var service = Service(handler);
        service.HttpsProbe = (url, _) => Task.FromResult(url == "https://plan-direct.example");

        var plan = await service.PlanAsync(_viewer, _movie, "https", CancellationToken.None);

        Assert.Equal(DeviceDownloadRoute.Redirect, plan.Route);
        Assert.StartsWith($"https://plan-direct.example/Plugins/Federation/DirectStream/{_remoteId:N}?token=permit-1&download=true", plan.Url);
        Assert.DoesNotContain("api", plan.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Null(plan.RelayReason);
    }

    [Fact]
    public async Task Plan_TriesTheDefaultHttpsPort_WhenTheExplicitPortHasNoHttps()
    {
        Assert.Equal(new[] { "https://p.example:8096", "https://p.example" }, DeviceDownload.HttpsCandidates("http://p.example:8096"));
        Assert.Single(DeviceDownload.HttpsCandidates("http://p.example"));
        Assert.Single(DeviceDownload.HttpsCandidates("http://p.example:443"));

        _friend.Url = "http://plan-port.example:8096";
        var service = Service(new PermitHandler { NamesFile = true });
        service.HttpsProbe = (url, _) => Task.FromResult(url == "https://plan-port.example");

        var plan = await service.PlanAsync(_viewer, _movie, "https", CancellationToken.None);

        Assert.Equal(DeviceDownloadRoute.Redirect, plan.Route);
        Assert.StartsWith("https://plan-port.example/Plugins/", plan.Url);
    }

    [Fact]
    public async Task Plan_RelaysWithAReason_WhenAnHttpFriendHasNoHttps()
    {
        _friend.Url = "http://plan-nohttps.example";
        var service = Service(new PermitHandler { NamesFile = true });
        service.HttpsProbe = (_, _) => Task.FromResult(false);

        var plan = await service.PlanAsync(_viewer, _movie, "https", CancellationToken.None);

        Assert.Equal(DeviceDownloadRoute.Relay, plan.Route);
        Assert.Contains("https", plan.RelayReason);

        // An http app is simply sent to the http address.
        var plain = await service.PlanAsync(_viewer, _movie, "http", CancellationToken.None);
        Assert.Equal(DeviceDownloadRoute.Redirect, plain.Route);
        Assert.StartsWith("http://plan-nohttps.example/", plain.Url);
    }

    [Fact]
    public async Task Plan_RelaysForAnOlderFriend_AndAsksTheFriendEveryTime()
    {
        var handler = new PermitHandler { NamesFile = false };
        var service = Service(handler);
        var first = await service.PlanAsync(_viewer, _movie, "https", CancellationToken.None);
        Assert.Equal(DeviceDownloadRoute.Relay, first.Route);
        Assert.Contains("older", first.RelayReason);

        // A friend that turned downloads off since is asked again, so its reason reaches the viewer.
        handler.Refuse = HttpStatusCode.Forbidden;
        var second = await service.PlanAsync(_viewer, _movie, "https", CancellationToken.None);
        Assert.Equal(403, second.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Plan_PassesTheFriendsReason_AndWhenToRetry()
    {
        var handler = new PermitHandler { Refuse = HttpStatusCode.TooManyRequests };
        var plan = await Service(handler).PlanAsync(_viewer, _movie, "https", CancellationToken.None);

        Assert.Equal(429, plan.Status);
        Assert.Equal("limit reached", plan.Error);
        Assert.Equal(1200, plan.RetryAfterSeconds);
        Assert.Null(plan.Url);

        // A refusal is never cached: the next press asks again.
        handler.Refuse = null;
        var retry = await Service(handler).PlanAsync(_viewer, _movie, "https", CancellationToken.None);
        Assert.Equal(200, retry.Status);
    }

    [Fact]
    public async Task Plan_RefusesWithoutAskingTheFriend_WhenTheyTurnedDownloadsOff()
    {
        var handler = new PermitHandler();
        _friend.PeerAllowsDownloads = false;
        var plan = await Service(handler).PlanAsync(_viewer, _movie, "https", CancellationToken.None);

        Assert.Equal(403, plan.Status);
        Assert.Equal(0, handler.Calls);

        _viewer.SetPermission(PermissionKind.EnableContentDownloading, false);
        Assert.Equal(403, (await Service(handler).PlanAsync(_viewer, _movie, "https", CancellationToken.None)).Status);
    }

    [Fact]
    public void ViewerDownloads_HaveTheirOwnAllowance_SeparateFromServerCopies()
    {
        var tokens = new FederationPlaybackTokenService();
        for (var i = 0; i < FederationPlaybackTokenService.ViewerDownloadLimit; i++)
        {
            Assert.True(tokens.TryReserveViewerDownload("friend", "alice", "item-" + i, out _));
        }

        Assert.False(tokens.TryReserveViewerDownload("friend", "alice", "one-more", out var retry));
        Assert.True(retry > TimeSpan.Zero);
        Assert.True(tokens.TryReserveViewerDownload("friend", "ALICE", "item-0", out _));
        Assert.True(tokens.TryReserveViewerDownload("friend", "bob", "one-more", out _));

        // Server copies keep the small friend-wide limit, untouched by viewers.
        Assert.True(tokens.TryReserveOrdinaryDownload("friend", "x1", out _));
        Assert.True(tokens.TryReserveOrdinaryDownload("friend", "x2", out _));
        Assert.True(tokens.TryReserveOrdinaryDownload("friend", "x3", out _));
        Assert.False(tokens.TryReserveOrdinaryDownload("friend", "x4", out _));
    }

    [Fact]
    public void ViewerDownloads_AreCappedAcrossAllOfAFriendsViewers()
    {
        var tokens = new FederationPlaybackTokenService();
        var granted = 0;
        for (var viewer = 0; viewer < 10; viewer++)
        {
            for (var item = 0; item < FederationPlaybackTokenService.ViewerDownloadLimit; item++)
            {
                if (tokens.TryReserveViewerDownload("friend", "viewer-" + viewer, "item-" + item, out _))
                {
                    granted++;
                }
            }
        }

        Assert.Equal(FederationPlaybackTokenService.ViewerDownloadFriendLimit, granted);
        Assert.True(tokens.TryReserveViewerDownload("other-friend", "viewer-0", "item-0", out _));
    }

    [Fact]
    public void ViewerPermits_LiveLongEnoughToResume_ServerCopiesStayShort()
    {
        Assert.Equal(TimeSpan.FromHours(12), FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Download, viewerBound: true));
        Assert.Equal(TimeSpan.FromMinutes(15), FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Download, viewerBound: false));
        Assert.Equal(TimeSpan.FromMinutes(15), FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.BulkDownload, viewerBound: true));
        Assert.Equal(FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Playback), FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Playback, viewerBound: true));
    }

    [Fact]
    public void CanDownloadFilter_LooksOnlyAtRefusedRemoteFiles()
    {
        BaseItemDto Dto(bool? canDownload, LocationType location, bool folder, Jellyfin.Data.Enums.MediaType type)
            => new() { Id = Guid.NewGuid(), CanDownload = canDownload, LocationType = location, IsFolder = folder, MediaType = type };

        var friendFile = Dto(false, LocationType.Remote, false, Jellyfin.Data.Enums.MediaType.Video);
        var song = Dto(false, LocationType.Remote, false, Jellyfin.Data.Enums.MediaType.Audio);
        var perViewer = Dto(false, LocationType.Virtual, false, Jellyfin.Data.Enums.MediaType.Video);
        var page = new QueryResult<BaseItemDto>(new[]
        {
            friendFile,
            song,
            perViewer,
            Dto(null, LocationType.Remote, false, Jellyfin.Data.Enums.MediaType.Video),
            Dto(true, LocationType.Remote, false, Jellyfin.Data.Enums.MediaType.Video),
            Dto(false, LocationType.FileSystem, false, Jellyfin.Data.Enums.MediaType.Video),
            Dto(false, LocationType.Remote, true, Jellyfin.Data.Enums.MediaType.Unknown),
            Dto(false, LocationType.Remote, false, Jellyfin.Data.Enums.MediaType.Book)
        });

        Assert.Equal(new[] { friendFile, song, perViewer }, FederationCanDownloadFilter.Candidates(page).ToArray());
        Assert.Equal(new[] { friendFile }, FederationCanDownloadFilter.Candidates(friendFile).ToArray());
        Assert.Empty(FederationCanDownloadFilter.Candidates("not an item"));
    }

    [Fact]
    public void DownloadFilter_MatchesOnlyJellyfinsNativeDownload()
    {
        var id = Guid.NewGuid();
        Assert.True(FederationDeviceDownloadFilter.IsDownload(Context("Library", "GetDownload", id), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(FederationDeviceDownloadFilter.IsDownload(Context("Library", "GetItemCounts", id), out _));
        Assert.False(FederationDeviceDownloadFilter.IsDownload(Context("Videos", "GetDownload", id), out _));
        Assert.False(FederationDeviceDownloadFilter.IsDownload(Context("Library", "GetDownload", Guid.Empty), out _));
    }

    [Fact]
    public void Refusal_IsJson_WithRetryAfter()
    {
        var http = new DefaultHttpContext();
        var result = Assert.IsType<ObjectResult>(FederationDeviceDownloadFilter.Refuse(http, DeviceDownloadPlan.Refused(429, "limit", 90)));
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("90", http.Response.Headers.RetryAfter.ToString());
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    private static ActionExecutingContext Context(string controller, string action, Guid itemId)
    {
        var descriptor = new ActionDescriptor { RouteValues = new Dictionary<string, string?> { ["controller"] = controller, ["action"] = action } };
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?> { ["itemId"] = itemId }, new object());
    }

    private sealed class PermitHandler : HttpMessageHandler
    {
        private int _calls;

        public bool NamesFile { get; set; } = true;

        public HttpStatusCode? Refuse { get; set; }

        public int Calls => _calls;

        public string? LastUserId { get; private set; }

        public string? LastPurpose { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/Plugins/Federation/PlaybackToken", request.RequestUri!.AbsolutePath);
            var n = Interlocked.Increment(ref _calls);
            LastUserId = request.Headers.TryGetValues(RemoteServerClient.RemoteUserIdHeader, out var ids) ? ids.First() : null;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastPurpose = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("Purpose").GetString();
            if (Refuse is { } status)
            {
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"error\":\"limit reached\",\"retryAfterSeconds\":1200}", Encoding.UTF8, "application/json")
                };
            }

            var json = "{\"token\":\"permit-" + n + "\",\"expiresUtc\":\"" + DateTime.UtcNow.AddHours(12).ToString("O") + "\",\"purpose\":\"Download\""
                + (NamesFile ? ",\"deviceDownload\":true" : string.Empty) + "}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
