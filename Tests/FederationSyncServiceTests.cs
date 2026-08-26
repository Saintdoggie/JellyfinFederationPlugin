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
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));

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
    /// Regression test: the same real show, synced from two different federated
    /// servers, used to end up with two parallel season/episode trees under the
    /// one (correctly provider-id-deduped) series - every episode showing up
    /// twice - because seasons and episodes were always keyed by (server, remote
    /// id) with no merge step of their own, unlike series/movies. Confirmed
    /// live: a show present on both a Jellyfin peer and a Plex friend had every
    /// episode duplicated. Seasons and episodes now merge by (series key, season
    /// number) and (season key, episode number) respectively, so a season/
    /// episode already seen from one server gains the second server as an
    /// additional source on the same entry instead of a sibling duplicate.
    /// </summary>
    [Fact]
    public async Task RefreshMapping_SameShowFromTwoServers_MergesSeasonAndEpisodeInsteadOfDuplicating()
    {
        var seriesIdA = Guid.NewGuid();
        var seasonIdA = Guid.NewGuid();
        var episodeIdA = Guid.NewGuid();
        var seriesIdB = Guid.NewGuid();
        var seasonIdB = Guid.NewGuid();
        var episodeIdB = Guid.NewGuid();

        var seriesJsonA = $"{{\"Items\":[{{\"Id\":\"{seriesIdA}\",\"Name\":\"Test Show\",\"Type\":\"Series\",\"ProviderIds\":{{\"Imdb\":\"tt9999999\"}}}}],\"TotalRecordCount\":1}}";
        var episodeJsonA = $"{{\"Items\":[{{\"Id\":\"{episodeIdA}\",\"Name\":\"Pilot\",\"Type\":\"Episode\",\"SeriesId\":\"{seriesIdA}\",\"SeasonId\":\"{seasonIdA}\",\"SeasonName\":\"Season 1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}}],\"TotalRecordCount\":1}}";
        var seriesJsonB = $"{{\"Items\":[{{\"Id\":\"{seriesIdB}\",\"Name\":\"Test Show\",\"Type\":\"Series\",\"ProviderIds\":{{\"Imdb\":\"tt9999999\"}}}}],\"TotalRecordCount\":1}}";
        var episodeJsonB = $"{{\"Items\":[{{\"Id\":\"{episodeIdB}\",\"Name\":\"Pilot\",\"Type\":\"Episode\",\"SeriesId\":\"{seriesIdB}\",\"SeasonId\":\"{seasonIdB}\",\"SeasonName\":\"Season 1\",\"ParentIndexNumber\":1,\"IndexNumber\":1}}],\"TotalRecordCount\":1}}";

        var serverA = new RemoteServer { Id = "serverA", Name = "RemoteA", Url = "http://fake-a.local", ApiKey = "key", UserId = "user1", Enabled = true, WanCapMode = WanCapMode.Off };
        var serverB = new RemoteServer { Id = "serverB", Name = "RemoteB", Url = "http://fake-b.local", ApiKey = "key", UserId = "user1", Enabled = true, WanCapMode = WanCapMode.Off };

        var httpClientA = new HttpClient(new FakeHttpMessageHandler(seriesJsonA, episodeJsonA)) { BaseAddress = new Uri("http://fake-a.local") };
        var httpClientB = new HttpClient(new FakeHttpMessageHandler(seriesJsonB, episodeJsonB)) { BaseAddress = new Uri("http://fake-b.local") };
        var remoteClientA = new RemoteServerClient(serverA, NullLogger.Instance, httpClientA);
        var remoteClientB = new RemoteServerClient(serverB, NullLogger.Instance, httpClientB);

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(serverA)).Returns(remoteClientA);
        clientFactory.Setup(f => f.GetClient(serverB)).Returns(remoteClientB);

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));

        var mapping = new LibraryMapping
        {
            LocalLibraryName = "Shows",
            MediaType = "Series",
            Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = "serverA", RemoteLibraryId = "lib1", RemoteLibraryName = "Shows" },
                new RemoteLibrarySource { ServerId = "serverB", RemoteLibraryId = "lib1", RemoteLibraryName = "Shows" }
            }
        };
        var config = new PluginConfiguration
        {
            EnableDedup = true,
            DedupProviderIds = new List<string> { "imdb" },
            RemoteServers = new List<RemoteServer> { serverA, serverB }
        };

        var method = typeof(FederationSyncService).GetMethod("RefreshMappingAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(syncService, new object?[] { mapping, config, CancellationToken.None, null })!;

        var entries = cache.GetEntriesForMapping("Shows").ToList();

        var seriesEntries = entries.Where(e => e.ItemType == "Series").ToList();
        var seasonEntries = entries.Where(e => e.ItemType == "Season").ToList();
        var episodeEntries = entries.Where(e => e.ItemType == "Episode").ToList();

        Assert.Single(seriesEntries);
        Assert.Equal(2, seriesEntries[0].Sources.Count);

        var season = Assert.Single(seasonEntries);
        Assert.Equal(2, season.Sources.Count);

        var episode = Assert.Single(episodeEntries);
        Assert.Equal(2, episode.Sources.Count);
        Assert.Equal(season.Key, episode.ParentKey);
    }

    /// <summary>
    /// Regression test for a bug where PruneServerSources - scoped only by
    /// (mapping, serverId), with no notion of which RemoteLibrarySource a cached
    /// item came from - ran once per source instead of once per server: a
    /// mapping with two sources from the same friend's server (e.g. two of their
    /// folders both feeding this one local library) had each source's prune
    /// step delete whatever the OTHER source had just synced moments earlier in
    /// the very same cycle, since it was never in that source's own "seen" set.
    /// Both sources' content must survive a single refresh of this mapping.
    /// </summary>
    [Fact]
    public async Task RefreshMapping_TwoSourcesFromSameServer_NeitherPrunesTheOther()
    {
        var movieAId = Guid.NewGuid();
        var movieBId = Guid.NewGuid();

        var movieAJson = $"{{\"Items\":[{{\"Id\":\"{movieAId}\",\"Name\":\"Movie A\",\"Type\":\"Movie\"}}],\"TotalRecordCount\":1}}";
        var movieBJson = $"{{\"Items\":[{{\"Id\":\"{movieBId}\",\"Name\":\"Movie B\",\"Type\":\"Movie\"}}],\"TotalRecordCount\":1}}";

        var httpClient = new HttpClient(new ParentIdScriptedHandler(("lib1", movieAJson), ("lib2", movieBJson)))
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
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));

        var mapping = new LibraryMapping
        {
            LocalLibraryName = "Movies",
            MediaType = "Movie",
            Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = "serverA", RemoteLibraryId = "lib1", RemoteLibraryName = "Folder 1" },
                new RemoteLibrarySource { ServerId = "serverA", RemoteLibraryId = "lib2", RemoteLibraryName = "Folder 2" }
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

        var entries = cache.GetEntriesForMapping("Movies").ToList();

        Assert.Contains(entries, e => e.Metadata.Name == "Movie A");
        Assert.Contains(entries, e => e.Metadata.Name == "Movie B");
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
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));

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

    /// <summary>
    /// Regression test for a bug confirmed live in production: every other prune
    /// of a server's cache entries only ever runs as part of actively syncing
    /// that specific server, or an explicit admin delete
    /// (FederationController.DeleteServer's own call to PruneServerSources). Once
    /// a server disappeared from config by any other means (a raw config save
    /// that dropped a RemoteServers entry without going through DeleteServer, in
    /// the incident this pins), its cache entries - and the materialized items
    /// backed by them - were never revisited again: the main sync loop only ever
    /// iterates currently-configured servers, so a fully-removed server's
    /// leftovers just sat there forever, showing content from a friend that
    /// wasn't even connected anymore. PruneOrphanedServerSources runs once at the
    /// start of every SyncAllAsync specifically to catch this, regardless of how
    /// the server disappeared.
    /// </summary>
    [Fact]
    public void PruneOrphanedServerSources_RemovesEntries_ForServerNoLongerInConfig()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var lm = new Mock<ILibraryManager>();
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));

        // "gone-server" is not present in the RemoteServers list passed below - the
        // scenario for a friend removed by any means, not just DeleteServer.
        cache.UpsertRaw("Movies", "gone-server", Guid.NewGuid(), new MediaBrowser.Model.Dto.BaseItemDto { Name = "Orphaned Movie" }, 0, "Movie");
        cache.UpsertRaw("Movies", "still-here", Guid.NewGuid(), new MediaBrowser.Model.Dto.BaseItemDto { Name = "Still Shared Movie" }, 0, "Movie");

        var mappings = new List<LibraryMapping>
        {
            new LibraryMapping { LocalLibraryName = "Movies", MediaType = "Movie", Enabled = true }
        };
        var remoteServers = new List<RemoteServer>
        {
            new RemoteServer { Id = "still-here", Name = "Still Connected Friend", Enabled = true }
        };

        var method = typeof(FederationSyncService).GetMethod("PruneOrphanedServerSources", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(syncService, new object?[] { mappings, remoteServers });

        var entries = cache.GetEntriesForMapping("Movies").ToList();
        Assert.DoesNotContain(entries, e => e.Sources.Any(s => s.ServerId == "gone-server"));
        Assert.Contains(entries, e => e.Sources.Any(s => s.ServerId == "still-here"));
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
            var body = query.Contains("mediaType=Series", StringComparison.OrdinalIgnoreCase)
                ? _seriesJson
                : query.Contains("mediaType=Episode", StringComparison.OrdinalIgnoreCase)
                    ? _episodeJson
                    : "{\"Items\":[],\"TotalRecordCount\":0}";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Scripts a response body by the request's parentId query parameter,
    /// standing in for two different remote library folders on the same
    /// server. Anything else (including the Federation-peer-status probe)
    /// gets an empty-items 200 OK, which is all ProbeFederationPeerStatusAsync
    /// looks at.
    /// </summary>
    private sealed class ParentIdScriptedHandler : HttpMessageHandler
    {
        private readonly (string ParentId, string Body)[] _scripts;

        public ParentIdScriptedHandler(params (string ParentId, string Body)[] scripts)
        {
            _scripts = scripts;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var body = "{\"Items\":[],\"TotalRecordCount\":0}";
            foreach (var (parentId, scriptedBody) in _scripts)
            {
                if (query.Contains($"parentId={parentId}", StringComparison.OrdinalIgnoreCase))
                {
                    body = scriptedBody;
                    break;
                }
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
