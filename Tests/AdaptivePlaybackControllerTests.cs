using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

[Collection("PluginInstance")]
public sealed class AdaptivePlaybackControllerTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly FederationItemCache _cache = new(NullLogger<FederationItemCache>.Instance);
    private readonly AdaptivePlaybackController _controller;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly RemoteServer _server;
    private readonly FederatedCacheEntry _entry;

    public AdaptivePlaybackControllerTests()
    {
        _plugin.Configuration.EnableAdaptivePlayback = true;
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "auth", "reset") { Id = _userId };
        var info = new AuthorizationInfo(); typeof(AuthorizationInfo).GetProperty("User")!.SetValue(info, user);
        _authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(info);
        _server = new RemoteServer { Id = "peer", Enabled = true, Name = "Server 1", ApiKey = "NEVER-EXPOSE-KEY", Url = "http://private-address" };
        _plugin.Configuration.RemoteServers.Add(_server);
        var remoteId = Guid.NewGuid();
        _entry = _cache.UpsertRaw("Movies", _server.Id, remoteId, new BaseItemDto { Id = remoteId, Name = "Movie", RunTimeTicks = 7200L * 10000000 }, 0, "Movie");
        var item = new Mock<MediaBrowser.Controller.Entities.Movies.Movie>();
        item.Object.Id = _itemId; item.Object.ProviderIds = new Dictionary<string, string> { ["FederationKey"] = _entry.Key };
        item.Setup(i => i.IsVisible(user, false)).Returns(true);
        _library.Setup(l => l.GetItemById(_itemId)).Returns(item.Object);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { item.Object });
        var factory = Mock.Of<IRemoteServerClientFactory>();
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory);
        var manager = new FederationLibraryManager(_library.Object, NullLogger<FederationLibraryManager>.Instance, factory,
            _cache, monitor, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        _controller = new AdaptivePlaybackController(_authorization.Object, Mock.Of<IUserManager>(), _library.Object, manager,
            new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance), new AdaptiveSourceRanking(), new AdaptivePreparationGate());
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    }
    public void Dispose() => _plugin.Dispose();

    [Fact]
    public async Task RankingExposesOnlyAuthorizedMetadataNotPathsOrKeys()
    {
        var result = Assert.IsType<OkObjectResult>(await _controller.Sources(_itemId));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("Server 1", json); Assert.Contains("TimelineName", json);
        Assert.DoesNotContain("NEVER-EXPOSE-KEY", json); Assert.DoesNotContain("private-address", json); Assert.DoesNotContain("Path", json);
        _library.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User!.Id == _userId && q.ItemIds.Contains(_itemId))), Times.Once);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrRevokedSourceIsOmittedImmediately(bool blocked)
    {
        if (blocked) _server.FriendUserAccessRules.Add(new RemoteUserAccessRule { RemoteUserId = _userId.ToString("N"), Mode = RemoteUserAccessMode.Blocked });
        else _server.Enabled = false;
        var result = Assert.IsType<OkObjectResult>(await _controller.Sources(_itemId));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(0, json.RootElement.GetProperty("Sources").GetArrayLength());
    }
    [Fact]
    public async Task InvisibleLibraryCannotBeRankedEvenWithAnItemId()
    {
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        Assert.IsType<NotFoundResult>(await _controller.Sources(_itemId));
    }
    [Fact]
    public async Task UnsignedQueryUserCannotSupplyAuthentication()
    {
        _authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync((AuthorizationInfo)null!);
        _controller.Request.QueryString = new QueryString("?UserId=" + _userId.ToString("N"));
        Assert.IsType<UnauthorizedResult>(await _controller.Sources(_itemId));
        Assert.IsType<UnauthorizedResult>(await _controller.Prepare());
    }
    [Fact]
    public async Task FeatureDisabledRefusesNewPreparationsAndRanking()
    {
        _plugin.Configuration.EnableAdaptivePlayback = false;
        Assert.IsType<NotFoundResult>(await _controller.Sources(_itemId));
        Assert.IsType<NotFoundResult>(await _controller.Prepare());
    }
}
