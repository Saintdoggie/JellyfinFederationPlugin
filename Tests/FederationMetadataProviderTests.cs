using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Providers;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

[Collection("PluginInstance")]
public class FederationMetadataProviderTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationItemCache _cache;
    private readonly FederationLibraryManager _manager;
    private readonly FederationMetadataProvider _provider;

    public FederationMetadataProviderTests()
    {
        _plugin = new RealPluginInstance();
        _cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var fileSystem = new Mock<MediaBrowser.Model.IO.IFileSystem>();
        fileSystem.Setup(f => f.IsPathFile(It.IsAny<string>()))
            .Returns((string p) => !(p.Contains("://", StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith("file://", StringComparison.OrdinalIgnoreCase)));
        MediaBrowser.Controller.Entities.BaseItem.FileSystem = fileSystem.Object;

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(path + "|" + type.FullName))));

        var clientFactory = Mock.Of<IRemoteServerClientFactory>();
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory);
        _manager = new FederationLibraryManager(
            lm.Object,
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory,
            _cache,
            bandwidthMonitor,
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var images = new FederationImageProvider(
            NullLogger<FederationImageProvider>.Instance,
            _manager,
            new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));
        _provider = new FederationMetadataProvider(
            NullLogger<FederationMetadataProvider>.Instance,
            _manager,
            images);
    }

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public async Task GetMetadata_UsesCachedPlexSourceData_SoTmdbDoesNotIdentify()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "plex1",
            Name = "freakbob",
            Url = "https://plex.example:32400",
            ApiKey = "token",
            Kind = ServerKind.Plex,
            Enabled = true
        });

        var dto = new BaseItemDto
        {
            Name = "Custom Cut",
            Overview = "Friend's plot.",
            ProductionYear = 1999,
            Genres = new[] { "Action" },
            People = new[]
            {
                new BaseItemPerson { Name = "Jane Doe", Role = "Lead", Type = Jellyfin.Data.Enums.PersonKind.Actor }
            }
        };
        var remoteId = Guid.NewGuid();
        var entry = _cache.UpsertRaw("Movies", "plex1", remoteId, dto, 0, "Movie");
        entry.Metadata.RemoteNativeId = "500";

        var info = new MovieInfo();
        info.ProviderIds["FederationKey"] = entry.Key;

        var result = await _provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Equal("Custom Cut", result.Item.Name);
        Assert.Equal("Friend's plot.", result.Item.Overview);
        Assert.Equal(1999, result.Item.ProductionYear);
        Assert.Contains(MetadataField.Cast, result.Item.LockedFields);
        Assert.Contains(result.People, p => p.Name == "Jane Doe");
        Assert.Equal(0, _provider.Order);
    }

    [Fact]
    public async Task GetImages_MintsImagePurposeToken_WithActingUser_AndDoesNotUsePlaybackToken()
    {
        var serverId = "image-user-" + Guid.NewGuid().ToString("N");
        var remoteId = Guid.NewGuid();
        var localUserId = Guid.NewGuid();
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = serverId,
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true
        });

        var dto = new BaseItemDto { Id = remoteId, Name = "Poster", Type = Jellyfin.Data.Enums.BaseItemKind.Movie };
        var entry = _cache.UpsertRaw("Movies", serverId, remoteId, dto, 0, "Movie");
        var item = _manager.MaterializeItem(entry);

        var handler = new ImageTokenHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://friend.example:8096") };
        var remoteClient = new RemoteServerClient(
            _plugin.Configuration.RemoteServers.Single(s => s.Id == serverId),
            NullLogger.Instance,
            httpClient);
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(It.IsAny<RemoteServer>())).Returns(remoteClient);
        clientFactory.Setup(f => f.GetClient(It.IsAny<string>())).Returns(remoteClient);

        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory.Object,
            _cache,
            new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object),
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.SetupGet(a => a.HttpContext).Returns(new DefaultHttpContext());
        var authorization = new Mock<IAuthorizationContext>();
        var authInfo = new AuthorizationInfo();
        var userType = typeof(AuthorizationInfo).GetProperty("User")!.PropertyType;
        var user = Activator.CreateInstance(userType, "alice", "auth", "reset")!;
        userType.GetProperty("Id")!.SetValue(user, localUserId);
        typeof(AuthorizationInfo).GetProperty("User")!.SetValue(authInfo, user);
        authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(authInfo);

        var images = new FederationImageProvider(
            NullLogger<FederationImageProvider>.Instance,
            manager,
            new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()),
            httpContextAccessor.Object,
            authorization.Object);

        var result = (await images.GetImages(item, CancellationToken.None)).ToList();

        Assert.Contains("\"Purpose\":\"Image\"", handler.LastPlaybackTokenBody);
        Assert.Equal(localUserId.ToString("N"), handler.LastRemoteUserId);
        var image = Assert.Single(result);
        Assert.Contains("/Plugins/Federation/Peer/Images/", image.Url);
        Assert.Contains("token=img-tok-123", image.Url);
    }

    private sealed class ImageTokenHandler : HttpMessageHandler
    {
        public string LastPlaybackTokenBody { get; private set; } = string.Empty;

        public string? LastRemoteUserId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            LastRemoteUserId = request.Headers.TryGetValues(RemoteServerClient.RemoteUserIdHeader, out var values)
                ? values.FirstOrDefault()
                : LastRemoteUserId;

            if (path.Equals("/Plugins/Federation/PlaybackToken", StringComparison.OrdinalIgnoreCase))
            {
                LastPlaybackTokenBody = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Json("{\"token\":\"img-tok-123\",\"purpose\":\"Image\"}");
            }

            if (path.StartsWith("/Plugins/Federation/Peer/Items/", StringComparison.OrdinalIgnoreCase))
            {
                return Json("{\"Id\":\"" + Guid.NewGuid() + "\",\"Name\":\"Poster\",\"ImageTags\":{\"Primary\":\"tag-1\"}}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body)
            => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
