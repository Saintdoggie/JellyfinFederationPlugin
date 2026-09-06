using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Providers;
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
}
