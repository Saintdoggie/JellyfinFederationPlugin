using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Unticking a friend's last library in a mapping must remove what that friend
/// brought in. RefreshMappingAsync only visits servers that still have a source,
/// so a fully dropped (but still configured) server was never pruned.
/// </summary>
[Collection("PluginInstance")]
public sealed class FederationUnselectedSourcePruneTests
{
    private static FederationSyncService NewSyncService(FederationItemCache cache)
    {
        var lm = new Mock<ILibraryManager>();
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var bandwidth = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidth, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager, Mock.Of<MediaBrowser.Controller.Persistence.IItemPersistenceService>());
        return new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidth, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));
    }

    private static void Seed(FederationItemCache cache, string mapping, string serverId, string imdb)
    {
        var remoteId = Guid.NewGuid();
        cache.UpsertByProviderId(mapping, "imdb", imdb, new BaseItemDto { Id = remoteId, Name = imdb }, serverId, remoteId, 0, "Movie");
    }

    [Fact]
    public void ServerNoLongerSelectedForMapping_ItsItemsAreRemoved_OthersKept()
    {
        using var plugin = new RealPluginInstance();
        plugin.Configuration.RemoteServers = new List<RemoteServer>
        {
            new RemoteServer { Id = "keep", Enabled = true },
            new RemoteServer { Id = "dropped", Enabled = true }
        };
        plugin.Configuration.LibraryMappings = new List<LibraryMapping>
        {
            new LibraryMapping
            {
                LocalLibraryName = "Movies",
                Enabled = true,
                RemoteLibrarySources = new List<RemoteLibrarySource> { new RemoteLibrarySource { ServerId = "keep", RemoteLibraryId = "lib" } }
            }
        };

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        Seed(cache, "Movies", "keep", "tt1");
        Seed(cache, "Movies", "dropped", "tt2");
        Seed(cache, "Movies", "dropped", "tt3");

        var affected = NewSyncService(cache).PruneUnselectedServerSources();

        Assert.Equal(new[] { "Movies" }, affected);
        var remaining = cache.GetEntriesForMapping("Movies").ToList();
        Assert.Single(remaining);
        Assert.All(remaining.SelectMany(e => e.GetSourcesSnapshot()), s => Assert.Equal("keep", s.ServerId));
    }

    [Fact]
    public void MappingWithNoSources_IsLeftAlone()
    {
        using var plugin = new RealPluginInstance();
        plugin.Configuration.LibraryMappings = new List<LibraryMapping>
        {
            new LibraryMapping { LocalLibraryName = "Movies", Enabled = true }
        };

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        Seed(cache, "Movies", "someone", "tt1");

        var affected = NewSyncService(cache).PruneUnselectedServerSources();

        Assert.Empty(affected);
        Assert.Single(cache.GetEntriesForMapping("Movies"));
    }
}
