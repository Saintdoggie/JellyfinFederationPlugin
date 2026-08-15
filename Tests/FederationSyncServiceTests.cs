using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationSyncServiceTests
{
    /// <summary>
    /// Regression test for a bug where synthesized Season entries were deleted in
    /// the same sync pass that created them: PruneServerSources removes any raw-keyed
    /// cache entry whose remote id wasn't marked "seen" during the sync, but a
    /// Season's remote id (the episode's SeasonId) was never added to that set -
    /// only the episode's own remote id was. Every episode's ParentKey pointed at a
    /// season that no longer existed, so no episode was ever persisted.
    /// </summary>
    [Fact]
    public async Task RefreshMapping_SeasonSurvivesPruning_AndEpisodeParentKeyResolves()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        var seriesJson = $"{{\"Items\":[{{\"Id\":\"{seriesId}\",\"Name\":\"Show\",\"Type\":\"Series\"}}],\"TotalRecordCount\":1}}";
        var episodeJson = $"{{\"Items\":[{{\"Id\":\"{episodeId}\",\"Name\":\"Pilot\",\"Type\":\"Episode\",\"SeriesId\":\"{seriesId}\",\"SeasonId\":\"{seasonId}\",\"SeasonName\":\"Season 1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}}],\"TotalRecordCount\":1}}";

        var httpClient = new HttpClient(new FakeHttpMessageHandler(seriesJson, episodeJson))
        {
            BaseAddress = new Uri("http://fake.local")
        };

        // WanCapMode explicitly Off (rather than the Auto default): Auto would have
        // the sync's WAN-bandwidth refresh attempt a real DNS lookup against
        // "fake.local", which is exactly the kind of real-network dependency a unit
        // test should not have. This test is about sync mechanics, not WAN capping.
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "key", UserId = "user1", Enabled = true, WanCapMode = WanCapMode.Off };
        var remoteClient = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(It.IsAny<RemoteServer>())).Returns(remoteClient);

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor);
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor, new Mock<IServiceProvider>().Object);

        var mapping = new LibraryMapping
        {
            LocalLibraryName = "Shows",
            MediaType = "Series",
            Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = "serverA", RemoteLibraryId = "lib1", RemoteLibraryName = "Shows" }
            }
        };
        var config = new PluginConfiguration
        {
            EnableDedup = false,
            RemoteServers = new List<RemoteServer> { server }
        };

        var method = typeof(FederationSyncService).GetMethod("RefreshMappingAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(syncService, new object?[] { mapping, config, CancellationToken.None, null })!;

        var entries = cache.GetEntriesForMapping("Shows").ToList();

        var seasonEntry = entries.SingleOrDefault(e => e.ItemType == "Season");
        Assert.NotNull(seasonEntry);

        var episodeEntry = entries.Single(e => e.ItemType == "Episode");
        Assert.Equal(seasonEntry!.Key, episodeEntry.ParentKey);
        Assert.NotNull(cache.GetEntryByKey(episodeEntry.ParentKey!));
    }

    /// <summary>
    /// Regression test for an ultra-review finding: when an episode's series was
    /// not synced this cycle (fetch error, missing from this mapping's pages,
    /// etc.), <c>UpsertEpisodeSeason</c> already correctly returns null per its own
    /// docstring - but the caller used to upsert the episode anyway with a null
    /// ParentKey, which <see cref="FederationItemPersistenceService"/> then
    /// materializes as a loose item sitting at the library root with no
    /// SeriesId/SeasonId. Once created, its federation key lands in `seen` and no
    /// later sync ever revisits or removes it. The episode must be skipped
    /// entirely instead.
    /// </summary>
    [Fact]
    public async Task RefreshMapping_EpisodeWithMissingSeries_IsSkippedNotOrphaned()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        // The series page comes back empty - standing in for the series never
        // having been synced this cycle (a swallowed per-item error, a page the
        // series simply wasn't on, etc.) - while the episode page still offers an
        // episode pointing at that never-synced series.
        var seriesJson = "{\"Items\":[],\"TotalRecordCount\":0}";
        var episodeJson = $"{{\"Items\":[{{\"Id\":\"{episodeId}\",\"Name\":\"Pilot\",\"Type\":\"Episode\",\"SeriesId\":\"{seriesId}\",\"SeasonId\":\"{seasonId}\",\"SeasonName\":\"Season 1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}}],\"TotalRecordCount\":1}}";

        var httpClient = new HttpClient(new FakeHttpMessageHandler(seriesJson, episodeJson))
        {
            BaseAddress = new Uri("http://fake.local")
        };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "key", UserId = "user1", Enabled = true, WanCapMode = WanCapMode.Off };
        var remoteClient = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(It.IsAny<RemoteServer>())).Returns(remoteClient);

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor);
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor, new Mock<IServiceProvider>().Object);

        var mapping = new LibraryMapping
        {
            LocalLibraryName = "Shows",
            MediaType = "Series",
            Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = "serverA", RemoteLibraryId = "lib1", RemoteLibraryName = "Shows" }
            }
        };
        var config = new PluginConfiguration
        {
            EnableDedup = false,
            RemoteServers = new List<RemoteServer> { server }
        };

        var method = typeof(FederationSyncService).GetMethod("RefreshMappingAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(syncService, new object?[] { mapping, config, CancellationToken.None, null })!;

        var entries = cache.GetEntriesForMapping("Shows").ToList();

        // No Episode (and no synthesized Season, which only gets created as a side
        // effect of upserting a valid episode) should have been materialized.
        Assert.DoesNotContain(entries, e => e.ItemType == "Episode");
        Assert.DoesNotContain(entries, e => e.ItemType == "Season");
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _seriesJson;
        private readonly string _episodeJson;

        public FakeHttpMessageHandler(string seriesJson, string episodeJson)
        {
            _seriesJson = seriesJson;
            _episodeJson = episodeJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var body = query.Contains("IncludeItemTypes=Series", StringComparison.OrdinalIgnoreCase)
                ? _seriesJson
                : query.Contains("IncludeItemTypes=Episode", StringComparison.OrdinalIgnoreCase)
                    ? _episodeJson
                    : "{\"Items\":[],\"TotalRecordCount\":0}";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
