using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The server-wide scan for titles the admin already owns loads every movie,
/// series and episode on the server. It ran once per friend library on every
/// sync; it now runs once per sync.
/// </summary>
public sealed class LocalMatchScanTests
{
    [Fact]
    public void ServerWideScan_RunsOncePerSync()
    {
        var scans = 0;
        var lm = new Mock<ILibraryManager>();
        lm.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                scans++;
                return new List<BaseItem> { new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt0133093" } } };
            });
        var factory = new Mock<IRemoteServerClientFactory>();
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidth = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory.Object);
        var manager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, factory.Object, cache, bandwidth, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, manager, Mock.Of<MediaBrowser.Controller.Persistence.IItemPersistenceService>());
        var collect = typeof(FederationItemPersistenceService).GetMethod("LocalProviderIdsForSync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var keys = new List<string> { "imdb", "tmdb" };

        var first = (IReadOnlySet<string>)collect.Invoke(persistence, new object[] { keys })!;
        collect.Invoke(persistence, new object[] { keys });
        collect.Invoke(persistence, new object[] { keys });
        Assert.Equal(1, scans);
        Assert.Contains("imdb:tt0133093", first);

        persistence.ResetLocalMatchCache();
        collect.Invoke(persistence, new object[] { keys });
        Assert.Equal(2, scans);

        // Different dedup settings need their own scan.
        collect.Invoke(persistence, new object[] { new List<string> { "tvdb" } });
        Assert.Equal(3, scans);
    }
}
