using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FederationPopularCollectionServiceTests
{
    [Fact]
    public void MapToLocalItems_KeepsOrderAndOnlyTitlesThisServerHas()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((path, _) => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(path))));
        var factory = new Mock<IRemoteServerClientFactory>();
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var manager = new FederationLibraryManager(
            library.Object,
            NullLogger<FederationLibraryManager>.Instance,
            factory.Object,
            cache,
            new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory.Object),
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var heat = Guid.NewGuid();
        var lost = Guid.NewGuid();
        var notPersisted = Guid.NewGuid();
        var heatEntry = cache.UpsertRaw("Movies", "bob", heat, new BaseItemDto { Id = heat, Name = "Heat" }, 0, "Movie");
        var lostEntry = cache.UpsertRaw("Shows", "bob", lost, new BaseItemDto { Id = lost, Name = "Lost" }, 0, "Series");
        cache.UpsertRaw("Movies", "bob", notPersisted, new BaseItemDto { Id = notPersisted, Name = "Gone" }, 0, "Movie");
        var heatLocal = manager.ComputeItemId(heatEntry);
        var lostLocal = manager.ComputeItemId(lostEntry);
        library.Setup(l => l.GetItemById(heatLocal)).Returns(new Movie { Id = heatLocal });
        library.Setup(l => l.GetItemById(lostLocal)).Returns(new Movie { Id = lostLocal });

        var service = new FederationPopularCollectionService(factory.Object, cache, manager, library.Object, Mock.Of<ICollectionManager>(), NullLogger<FederationPopularCollectionService>.Instance);
        var mapped = service.MapToLocalItems("bob", new List<PopularTitle>
        {
            new() { Id = heat.ToString("N"), Plays = 2 },
            new() { Id = lost.ToString("N"), Plays = 7 },
            new() { Id = notPersisted.ToString("N"), Plays = 9 },
            new() { Id = Guid.NewGuid().ToString("N"), Plays = 50 },
            new() { Id = "not-an-id", Plays = 99 }
        });

        Assert.Equal(new[] { lostLocal, heatLocal }, mapped);
        Assert.Empty(service.MapToLocalItems("someone-else", new List<PopularTitle> { new() { Id = heat.ToString("N"), Plays = 1 } }));
    }
}
