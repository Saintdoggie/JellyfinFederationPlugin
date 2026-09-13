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
    public async Task JellyfinArtwork_RepairsExistingPoster_WithImageScopedToken_AndRetriesLocalReplacement()
    {
        var remoteId = Guid.NewGuid();
        var server = new RemoteServer { Id = "jellyfin-art", Url = "https://friend.example", ApiKey = "standing-private-test", Enabled = true };
        _plugin.Configuration.RemoteServers.Add(server);
        var entry = _cache.UpsertRaw("Movies", server.Id, remoteId, new BaseItemDto { Name = "Movie", ImageTags = new() { [ImageType.Primary] = "source-custom" } }, 0, "Movie");
        var tokenHandler = new ImageTokenHandler();
        using var clientHttp = new HttpClient(tokenHandler) { BaseAddress = new Uri(server.Url) };
        var client = new RemoteServerClient(server, NullLogger.Instance, clientHttp);
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<string>())).Returns(client);
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>())).Returns(client);
        var library = new Mock<ILibraryManager>();
        var manager = new FederationLibraryManager(library.Object, NullLogger<FederationLibraryManager>.Instance,
            factory.Object, _cache, new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory.Object), Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var item = new Movie { Id = Guid.NewGuid(), ImageInfos = new[] { new MediaBrowser.Controller.Entities.ItemImageInfo { Path = "/wrong-poster.jpg", Type = ImageType.Primary, DateModified = DateTime.UtcNow } } };
        var saves = 0;
        var posterPath = System.IO.Path.GetTempFileName();
        var provider = new Mock<IProviderManager>();
        provider.Setup(p => p.SaveImage(item, It.IsAny<System.IO.Stream>(), "image/png", ImageType.Primary, 0, It.IsAny<CancellationToken>()))
            .Returns(() => { saves++; System.IO.File.WriteAllBytes(posterPath, new byte[] { 1, 2, 3 }); item.ImageInfos = new[] { new MediaBrowser.Controller.Entities.ItemImageInfo { Path = posterPath, Type = ImageType.Primary, DateModified = DateTime.UtcNow } }; return Task.CompletedTask; });
        var artwork = new FederationArtworkService(manager, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()), provider.Object,
            Mock.Of<MediaBrowser.Controller.Persistence.IItemPersistenceService>(), NullLogger<FederationArtworkService>.Instance, library.Object);
        using var imageHttp = new HttpClient(new JellyfinPosterHandler(remoteId));
        FederationArtworkService.HttpClientOverride = imageHttp;
        try
        {
            var parent = new MediaBrowser.Controller.Entities.Folder();
            await artwork.RefreshAsync(item, entry, parent, CancellationToken.None);
            Assert.Equal(1, saves);
            Assert.Contains("\"Purpose\":\"Image\"", tokenHandler.LastPlaybackTokenBody);
            await artwork.RefreshAsync(item, entry, parent, CancellationToken.None);
            Assert.Equal(1, saves);
            item.ImageInfos[0].Path = "/another-provider.jpg";
            await artwork.RefreshAsync(item, entry, parent, CancellationToken.None);
            Assert.Equal(2, saves);
            System.IO.File.Delete(posterPath);
            await artwork.RefreshAsync(item, entry, parent, CancellationToken.None);
            Assert.Equal(3, saves);
            Assert.DoesNotContain("private-test", string.Join(',', item.ProviderIds.Values));
        }
        finally { FederationArtworkService.HttpClientOverride = null; System.IO.File.Delete(posterPath); }
    }

    private sealed class JellyfinPosterHandler(Guid id) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal($"/Plugins/Federation/Peer/Images/{id:N}/Primary", request.RequestUri!.AbsolutePath);
            Assert.Contains("token=img-tok-123", request.RequestUri.Query);
            Assert.DoesNotContain("standing-private-test", request.RequestUri.Query);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceArtwork_OnlyCommitsRevisionAfterImageDownloadSucceeds(bool success)
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "poster-source", Kind = ServerKind.Plex, Enabled = true });
        var entry = _cache.UpsertRaw("Movies", "poster-source", Guid.NewGuid(), new BaseItemDto { Name = "Custom poster", ImageTags = new() { [ImageType.Primary] = "new-upload" } }, 0, "Movie");
        entry.Metadata.RemoteNativeId = "123";
        var item = new Movie { Id = Guid.NewGuid() };
        var source = new Mock<IExternalCatalogProvider>();
        source.SetupGet(s => s.Kind).Returns(ServerKind.Plex);
        source.Setup(s => s.GetImagesAsync(It.IsAny<RemoteServer>(), "123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalImageSet("https://source.example/custom-poster?X-Plex-Token=private-test", null));
        var provider = new Mock<IProviderManager>();
        var saved = new List<byte>();
        provider.Setup(p => p.SaveImage(item, It.IsAny<System.IO.Stream>(), "image/png", ImageType.Primary, 0, It.IsAny<CancellationToken>()))
            .Returns(async (MediaBrowser.Controller.Entities.BaseItem _, System.IO.Stream stream, string mime, ImageType type, int? index, CancellationToken ct) =>
            { using var buffer = new System.IO.MemoryStream(); await stream.CopyToAsync(buffer, ct); saved.AddRange(buffer.ToArray()); });
        var persistence = new Mock<MediaBrowser.Controller.Persistence.IItemPersistenceService>();
        var library = new Mock<ILibraryManager>();
        var artwork = new FederationArtworkService(_manager, new ExternalCatalogRegistry(new[] { source.Object }), provider.Object, persistence.Object, NullLogger<FederationArtworkService>.Instance, library.Object);
        using var http = new HttpClient(new PosterHandler(success));
        FederationArtworkService.HttpClientOverride = http;
        try
        {
            await artwork.RefreshAsync(item, entry, new MediaBrowser.Controller.Entities.Folder(), CancellationToken.None);
            Assert.Equal(success, item.ProviderIds.ContainsKey(FederationArtworkService.StampKey));
            Assert.Equal(success ? new byte[] { 1, 2, 3 } : Array.Empty<byte>(), saved);
            Assert.DoesNotContain("private-test", string.Join(",", item.ProviderIds.Values));
        }
        finally { FederationArtworkService.HttpClientOverride = null; }
    }

    private sealed class PosterHandler(bool success) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/custom-poster", request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }

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
