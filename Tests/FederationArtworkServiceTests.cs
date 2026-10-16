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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedPlexPoster_ReplacesPrimaryImage_AndRecordsSourceTag(bool identicalTagFromOtherServer)
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
        item.SetProviderId(FederationArtworkService.PrimaryImageTagProviderId, identicalTagFromOtherServer
            ? $"different-plex:{remoteId:N}:{entry.Metadata.PrimaryImageTag}"
            : "/library/metadata/500/thumb/old");

        var changed = await service.SyncPrimaryImageAsync(item, entry, CancellationToken.None);

        Assert.True(changed);
        Assert.Equal($"{server.Id}:{remoteId:N}:{entry.Metadata.PrimaryImageTag}", item.GetProviderId(FederationArtworkService.PrimaryImageTagProviderId));
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
    public async Task JellyfinEpisodeImage_IsAddressedLazily_WithoutDownloading()
    {
        LazyArtwork.KeyDirectory = Path.Combine(Path.GetTempPath(), "fed-art-" + Guid.NewGuid().ToString("N"));
        LazyArtwork.ResetForTests();
        try
        {
            var server = new RemoteServer { Id = "jf-friend", Name = "Friend", Url = "https://friend.example", ApiKey = "secret", Kind = ServerKind.Jellyfin, Enabled = true };
            _plugin.Configuration.RemoteServers.Add(server);
            var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
            var remoteId = Guid.NewGuid();
            var entry = cache.UpsertRaw(
                "Shows",
                server.Id,
                remoteId,
                new BaseItemDto { Id = remoteId, Name = "Pilot", ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = "abc123" } },
                0,
                "Episode");

            var clients = new Mock<IRemoteServerClientFactory>(MockBehavior.Strict);
            var manager = new FederationLibraryManager(
                Mock.Of<ILibraryManager>(),
                NullLogger<FederationLibraryManager>.Instance,
                clients.Object,
                cache,
                new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clients.Object),
                Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
            var providerManager = new Mock<IProviderManager>(MockBehavior.Strict);
            var service = new FederationArtworkService(
                NullLogger<FederationArtworkService>.Instance,
                manager,
                new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()),
                providerManager.Object);
            var episode = new MediaBrowser.Controller.Entities.TV.Episode { Name = "Pilot" };

            Assert.True(await service.SyncPrimaryImageAsync(episode, entry, CancellationToken.None));

            var image = episode.GetImageInfo(ImageType.Primary, 0);
            Assert.NotNull(image);
            Assert.False(image!.IsLocalFile);
            Assert.Contains("/Plugins/Federation/Artwork?serverId=jf-friend&itemId=" + remoteId.ToString("N") + "&tag=abc123&sig=", image.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", image.Path, StringComparison.Ordinal);
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(image.Path).Query);
            Assert.True(LazyArtwork.Verify("jf-friend", remoteId, "abc123", query["sig"]));
            Assert.False(LazyArtwork.Verify("jf-friend", remoteId, "abc124", query["sig"]));
            Assert.False(LazyArtwork.Verify("other", remoteId, "abc123", query["sig"]));
            Assert.False(LazyArtwork.Verify("jf-friend", Guid.NewGuid(), "abc123", query["sig"]));

            // Unchanged on the next sync, and the key survives a restart.
            Assert.False(await service.SyncPrimaryImageAsync(episode, entry, CancellationToken.None));
            LazyArtwork.ResetForTests();
            Assert.True(LazyArtwork.Verify("jf-friend", remoteId, "abc123", query["sig"]));
        }
        finally
        {
            if (Directory.Exists(LazyArtwork.KeyDirectory!))
            {
                Directory.Delete(LazyArtwork.KeyDirectory!, true);
            }

            LazyArtwork.KeyDirectory = null;
            LazyArtwork.ResetForTests();
        }
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
        item.SetProviderId(FederationArtworkService.PrimaryImageTagProviderId, $"{server.Id}:{entry.GetPrimarySource()!.RemoteItemId:N}:same-tag");

        Assert.False(await service.SyncPrimaryImageAsync(item, entry, CancellationToken.None));
        Assert.Null(external.LastNativeId);
        providerManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlexPoster_WithoutCachedThumbTag_StillFetchesByNativeId()
    {
        var server = new RemoteServer
        {
            Id = "plex-notag",
            Name = "Plex",
            Kind = ServerKind.Plex,
            Enabled = true
        };
        _plugin.Configuration.RemoteServers.Add(server);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteId = Guid.NewGuid();
        var entry = cache.UpsertRaw("Movies", server.Id, remoteId, new BaseItemDto { Name = "Movie" }, 0, "Movie");
        entry.SetNativeId(server.Id, remoteId, "9626");

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
        providerManager
            .Setup(p => p.SaveImage(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<Stream>(), "image/jpeg", ImageType.Primary, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new FederationArtworkService(
            NullLogger<FederationArtworkService>.Instance,
            manager,
            new ExternalCatalogRegistry(new[] { external }),
            providerManager.Object);

        var item = new Movie { Name = "Movie" };
        MediaBrowser.Controller.Entities.BaseItemExtensions.SetImagePath(item, ImageType.Primary,
            new MediaBrowser.Model.IO.FileSystemMetadata { FullName = "/cached/old.jpg", LastWriteTimeUtc = DateTime.UtcNow });
        item.SetProviderId(FederationArtworkService.PrimaryImageTagProviderId, $"{server.Id}:{remoteId:N}:native:9626");
        Assert.True(await service.SyncPrimaryImageAsync(item, entry, CancellationToken.None));
        Assert.Equal("9626", external.LastNativeId);
        Assert.Equal($"{server.Id}:{remoteId:N}:native:9626", item.GetProviderId(FederationArtworkService.PrimaryImageTagProviderId));
        providerManager.Verify(p => p.SaveImage(
            item,
            It.IsAny<Stream>(),
            "image/jpeg",
            ImageType.Primary,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MixedItem_JellyfinPrimary_StillCopiesPlexSiblingPoster()
    {
        var jellyfin = new RemoteServer
        {
            Id = "jf-primary",
            Name = "Jellyfin friend",
            Kind = ServerKind.Jellyfin,
            Enabled = true
        };
        var plex = new RemoteServer
        {
            Id = "plex-sib",
            Name = "Plex",
            Kind = ServerKind.Plex,
            Enabled = true
        };
        _plugin.Configuration.RemoteServers.Add(jellyfin);
        _plugin.Configuration.RemoteServers.Add(plex);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var jfId = Guid.NewGuid();
        var plexId = Guid.NewGuid();
        var entry = cache.UpsertByProviderId(
            "Movies",
            "imdb",
            "tt-art-mixed",
            new BaseItemDto { Id = jfId, Name = "Shared" },
            jellyfin.Id,
            jfId,
            0,
            "Movie");
        cache.UpsertByProviderId(
            "Movies",
            "imdb",
            "tt-art-mixed",
            new BaseItemDto { Id = plexId, Name = "Shared" },
            plex.Id,
            plexId,
            1,
            "Movie");
        entry.SetNativeId(plex.Id, plexId, "9111");

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
        providerManager
            .Setup(p => p.SaveImage(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<Stream>(), "image/jpeg", ImageType.Primary, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new FederationArtworkService(
            NullLogger<FederationArtworkService>.Instance,
            manager,
            new ExternalCatalogRegistry(new[] { external }),
            providerManager.Object);

        entry.Metadata.PrimaryImageTag = "jellyfin-primary-tag";
        var item = new Movie { Name = "Shared" };
        MediaBrowser.Controller.Entities.BaseItemExtensions.SetImagePath(item, ImageType.Primary,
            new MediaBrowser.Model.IO.FileSystemMetadata { FullName = "/cached/old.jpg", LastWriteTimeUtc = DateTime.UtcNow });
        item.SetProviderId(FederationArtworkService.PrimaryImageTagProviderId, $"{plex.Id}:{plexId:N}:jellyfin-primary-tag");
        Assert.True(await service.SyncPrimaryImageAsync(item, entry, CancellationToken.None));
        Assert.Equal("9111", external.LastNativeId);
        providerManager.Verify(p => p.SaveImage(
            item,
            It.IsAny<Stream>(),
            "image/jpeg",
            ImageType.Primary,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
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
