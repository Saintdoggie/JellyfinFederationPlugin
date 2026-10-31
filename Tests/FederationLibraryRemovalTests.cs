using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Removing a friend library must remove it everywhere: from a sync that is
/// already running, from the sync status list, and from Jellyfin.
/// </summary>
[Collection("PluginInstance")]
public sealed class FederationLibraryRemovalTests
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

    private static PluginConfiguration WithMappings(params string[] names) => new PluginConfiguration
    {
        LibraryMappings = names.Select(n => new LibraryMapping
        {
            LocalLibraryName = n,
            Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource> { new RemoteLibrarySource { ServerId = "friend", RemoteLibraryId = "lib-" + n } }
        }).ToList()
    };

    private static bool RemovedSinceSyncStarted(FederationSyncService sync, LibraryMapping mapping, PluginConfiguration syncConfig)
        => (bool)typeof(FederationSyncService).GetMethod("RemovedSinceSyncStarted", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(sync, new object[] { mapping, syncConfig })!;

    [Fact]
    public void LibraryRemovedDuringASync_IsSkippedAndItsDownloadedTitlesDropped()
    {
        using var plugin = new RealPluginInstance();
        var atStart = WithMappings("Music", "Movies");
        Plugin.Instance!.UpdateConfiguration(atStart);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteId = Guid.NewGuid();
        cache.UpsertRaw("Music", "friend", remoteId, new BaseItemDto { Id = remoteId, Name = "Song" }, 0, "Audio");
        var sync = NewSyncService(cache);
        var music = atStart.LibraryMappings[0];

        // Unchanged settings: nothing is skipped.
        Assert.False(RemovedSinceSyncStarted(sync, music, atStart));

        // The admin removes Music while the sync runs.
        Plugin.Instance.UpdateConfiguration(WithMappings("Movies"));
        Assert.True(RemovedSinceSyncStarted(sync, music, atStart));
        Assert.Empty(cache.GetEntriesForMapping("Music"));
        Assert.False(RemovedSinceSyncStarted(sync, atStart.LibraryMappings[1], atStart));

        // Switched off counts as removed.
        var off = WithMappings("Movies");
        off.LibraryMappings[0].Enabled = false;
        Plugin.Instance.UpdateConfiguration(off);
        Assert.True(RemovedSinceSyncStarted(sync, atStart.LibraryMappings[1], atStart));
    }

    [Fact]
    public void SavingSettings_DoesNotCloseConnectionsARunningSyncStillUses()
    {
        using var factory = new RemoteServerClientFactory(NullLoggerFactory.Instance);
        var server = new RemoteServer { Id = "friend", Url = "http://friend.local", ApiKey = "key" };
        var inUse = factory.GetClient(server);

        factory.InvalidateAll();
        factory.Invalidate(server.Id);

        var http = ((BoundedHttpClient)typeof(RemoteServerClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(inUse)!).InnerClient;
        http.CancelPendingRequests(); // throws ObjectDisposedException once disposed
        var fresh = ((BoundedHttpClient)typeof(RemoteServerClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(factory.GetClient(server))!).InnerClient;
        Assert.NotSame(http, fresh);
    }

    [Fact]
    public void RemovedLibrary_DisappearsFromTheSyncStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "federation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var status = new FederationSyncStatus();
            status.Initialize(Path.Combine(dir, "federation-cache.json"));
            status.BeginRun(new[]
            {
                new LibrarySyncState { Key = FederationSyncStatus.KeyFor("Music", "friend", "lib-Music"), LocalLibraryName = "Music" },
                new LibrarySyncState { Key = FederationSyncStatus.KeyFor("Movies", "friend", "lib-Movies"), LocalLibraryName = "Movies" }
            }, replaceAll: true);

            // Never while a sync runs; it owns the list then.
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FederationSyncStatus.KeyFor("Movies", "friend", "lib-Movies") };
            status.Forget(keep);
            Assert.Equal(2, status.Snapshot().Count);

            status.EndRun();
            status.Forget(keep);
            Assert.Equal(new[] { "Movies" }, status.Snapshot().Select(s => s.LocalLibraryName));

            // Also after a restart.
            var restarted = new FederationSyncStatus();
            restarted.Initialize(Path.Combine(dir, "federation-cache.json"));
            Assert.Equal(new[] { "Movies" }, restarted.Snapshot().Select(s => s.LocalLibraryName));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task PluginLibrary_IsRecognisedByItsMarkerFile_EvenUnderAnotherPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "federation-tests-" + Guid.NewGuid().ToString("N"));
        var dataPath = Path.Combine(root, "data");
        var elsewhere = Path.Combine(root, "mounted", "Music");
        Directory.CreateDirectory(Path.Combine(dataPath, "federation"));
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, ".federation-keep"), "x");
        var own = Path.Combine(root, "own music");
        Directory.CreateDirectory(own);
        using var restorer = new PluginInstanceRestorer(dataPath);
        try
        {
            var federationRoot = LibraryProvisioningService.GetFederationRoot();
            Assert.True(LibraryProvisioningService.IsOwnLocation(elsewhere, federationRoot));
            Assert.False(LibraryProvisioningService.IsOwnLocation(own, federationRoot));

            var lm = new Mock<ILibraryManager>();
            lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo { Name = "Music", Locations = new[] { elsewhere } },
                new VirtualFolderInfo { Name = "Mine", Locations = new[] { own } }
            });
            var service = new LibraryProvisioningService(lm.Object, NullLogger<LibraryProvisioningService>.Instance);

            // The stored name's case wins: Jellyfin's folder on disk is case-sensitive.
            await service.RemoveLibraryAsync("music");
            lm.Verify(x => x.RemoveVirtualFolder("Music", true), Times.Once);

            await service.RemoveLibraryAsync("Mine");
            lm.Verify(x => x.RemoveVirtualFolder("Mine", It.IsAny<bool>()), Times.Never);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
