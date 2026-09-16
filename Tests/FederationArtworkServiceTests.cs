using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

[Collection("PluginInstance")]
public sealed class FederationArtworkServiceTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();

    public FederationArtworkServiceTests()
    {
        MediaBrowser.Controller.Entities.BaseItem.FileSystem = Mock.Of<MediaBrowser.Model.IO.IFileSystem>();
    }

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public async Task ChangedPlexPoster_ReplacesPrimaryImage_AndRecordsSourceTag()
    {
        var server = new RemoteServer
        {
            Id = "plex-art",
            Name = "Plex friend",
            Url = "https://plex.example:32400",
            ApiKey = "secret",
            Kind = ServerKind.Plex,
            Enabled = true
        };
        _plugin.Configuration.RemoteServers.Add(server);

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteId = Guid.NewGuid();
        var entry = cache.UpsertRaw("Movies", server.Id, remoteId, new BaseItemDto { Name = "Movie" }, 0, "Movie");
        entry.Metadata.RemoteNativeId = "500";
        entry.Metadata.PrimaryImageTag = "/library/metadata/500/thumb/new";

        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(path + type.FullName))));
        var clientFactory = Mock.Of<IRemoteServerClientFactory>();
        var manager = new FederationLibraryManager(
            library.Object,
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory,
            cache,
            new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory),
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var external = new FakeExternalProvider();
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(p => p.SaveImage(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<Stream>(), "image/jpeg", ImageType.Primary, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new FederationArtworkService(
            NullLogger<FederationArtworkService>.Instance,
            manager,
            new ExternalCatalogRegistry(new[] { external }),
            providerManager.Object);

        var item = new Movie { Name = "Movie" };
        MediaBrowser.Controller.Entities.BaseItemExtensions.SetImagePath(
            item,
            ImageType.Primary,
            new MediaBrowser.Model.IO.FileSystemMetadata
            {
                FullName = "/old/poster.jpg",
                LastWriteTimeUtc = DateTime.UtcNow
            });
        item.SetProviderId(FederationArtworkService.PrimaryImageTagProviderId, "/library/metadata/500/thumb/old");

        var changed = await service.SyncPrimaryImageAsync(item, entry, CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(entry.Metadata.PrimaryImageTag, item.GetProviderId(FederationArtworkService.PrimaryImageTagProviderId));
        Assert.Equal("500", external.LastNativeId);
        providerManager.Verify(p => p.SaveImage(
            item,
            It.IsAny<Stream>(),
            "image/jpeg",
            ImageType.Primary,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnchangedPlexPoster_WithLocalImage_DoesNotFetchAgain()
    {
        var server = new RemoteServer { Id = "plex-same", Name = "Plex", Kind = ServerKind.Plex, Enabled = true };
        _plugin.Configuration.RemoteServers.Add(server);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var entry = cache.UpsertRaw("Movies", server.Id, Guid.NewGuid(), new BaseItemDto { Name = "Movie" }, 0, "Movie");
        entry.Metadata.RemoteNativeId = "501";
        entry.Metadata.PrimaryImageTag = "same-tag";

        var clients = Mock.Of<IRemoteServerClientFactory>();
        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            clients,
            cache,
            new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clients),
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var external = new FakeExternalProvider();
        var providerManager = new Mock<IProviderManager>();
        var service = new FederationArtworkService(
            NullLogger<FederationArtworkService>.Instance,
            manager,
            new ExternalCatalogRegistry(new[] { external }),
            providerManager.Object);
        var item = new Movie { Name = "Movie" };
        MediaBrowser.Controller.Entities.BaseItemExtensions.SetImagePath(
            item,
            ImageType.Primary,
            new MediaBrowser.Model.IO.FileSystemMetadata
            {
                FullName = "/cached/poster.jpg",
                LastWriteTimeUtc = DateTime.UtcNow
            });
        item.SetProviderId(FederationArtworkService.PrimaryImageTagProviderId, "same-tag");

        Assert.False(await service.SyncPrimaryImageAsync(item, entry, CancellationToken.None));
        Assert.Null(external.LastNativeId);
        providerManager.VerifyNoOtherCalls();
    }

    private sealed class FakeExternalProvider : IExternalCatalogProvider
    {
        public ServerKind Kind => ServerKind.Plex;

        public string? LastNativeId { get; private set; }

        public Task<ExternalImageSet?> GetImagesAsync(RemoteServer server, string nativeId, CancellationToken cancellationToken)
        {
            LastNativeId = nativeId;
            return Task.FromResult<ExternalImageSet?>(new ExternalImageSet(
                "https://plex.example/poster.jpg?token=internal",
                null));
        }

        public Task<HttpResponseMessage?> GetPrimaryImageResponseAsync(RemoteServer server, string nativeId, CancellationToken cancellationToken)
        {
            LastNativeId = nativeId;
            return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                }
            });
        }

        public Task<IReadOnlyList<ExternalLibrary>> GetLibrariesAsync(RemoteServer server, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ExternalLibrary>>(Array.Empty<ExternalLibrary>());

        public Task<IReadOnlyList<ExternalLibrary>> GetAllLibrariesAsync(RemoteServer server, CancellationToken cancellationToken)
            => GetLibrariesAsync(server, cancellationToken);

        public Task<IReadOnlyList<ExternalItem>?> GetItemsAsync(RemoteServer server, string libraryId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ExternalItem>?>(Array.Empty<ExternalItem>());

        public Task<IReadOnlyList<ExternalItem>?> GetAllItemsAsync(RemoteServer server, string libraryId, CancellationToken cancellationToken)
            => GetItemsAsync(server, libraryId, cancellationToken);

        public Task<string?> ResolveStreamUrlAsync(RemoteServer server, string nativeId, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string?> TestConnectionAsync(RemoteServer server, CancellationToken cancellationToken)
            => Task.FromResult<string?>(server.Name);
    }
}
