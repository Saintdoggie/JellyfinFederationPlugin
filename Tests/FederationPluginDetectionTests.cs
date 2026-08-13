using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Federation depends entirely on the remote's stock Jellyfin API (Items, Users,
/// PlaybackInfo, ...), so nothing about a normal sync ever proves the remote is
/// actually running the Federation plugin rather than just being some other
/// reachable Jellyfin server - it would happily keep pulling from one even after
/// the owner uninstalled Federation there. These tests cover the peer-detection
/// gate added to close that: a remote that doesn't answer
/// <c>Plugins/Federation/Config</c> has its items actively removed rather than
/// left stale, and nothing new is created from it.
/// </summary>
public class FederationPluginDetectionTests
{
    private static (FederationSyncService Sync, FederationItemCache Cache, LibraryMapping Mapping, PluginConfiguration Config) BuildHarness(RemoteServerClient remoteClient, RemoteServer server)
    {
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(It.IsAny<RemoteServer>())).Returns(remoteClient);

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor);
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidthMonitor);

        var mapping = new LibraryMapping
        {
            LocalLibraryName = "Movies",
            MediaType = "Movie",
            Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = "serverA", RemoteLibraryId = "lib1", RemoteLibraryName = "Movies" }
            }
        };
        var config = new PluginConfiguration
        {
            EnableDedup = false,
            RemoteServers = new List<RemoteServer> { server }
        };

        return (syncService, cache, mapping, config);
    }

    private static async Task InvokeRefreshMappingAsync(FederationSyncService sync, LibraryMapping mapping, PluginConfiguration config)
    {
        var method = typeof(FederationSyncService).GetMethod("RefreshMappingAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(sync, new object?[] { mapping, config, CancellationToken.None, null })!;
    }

    [Fact]
    public async Task RefreshMapping_RemoteWithoutFederationPlugin_RemovesExistingItems_AndCreatesNone()
    {
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "key", UserId = "user1", Enabled = true, WanCapMode = WanCapMode.Off };
        var httpClient = new HttpClient(new PluginProbeFakeHandler(pluginInstalled: false))
        {
            BaseAddress = new Uri("http://fake.local")
        };
        var remoteClient = new RemoteServerClient(server, NullLogger.Instance, httpClient);
        var (sync, cache, mapping, config) = BuildHarness(remoteClient, server);

        // Simulate an item this server already populated on an earlier, successful
        // sync (back when it did have the plugin installed).
        var movieId = Guid.NewGuid();
        cache.UpsertRaw(mapping.LocalLibraryName, "serverA", movieId, new BaseItemDto { Id = movieId, Name = "Old Movie" }, 0, "Movie");
        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Empty(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    [Fact]
    public async Task RefreshMapping_RemoteWithFederationPlugin_SyncsNormally()
    {
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "key", UserId = "user1", Enabled = true, WanCapMode = WanCapMode.Off };
        var httpClient = new HttpClient(new PluginProbeFakeHandler(pluginInstalled: true))
        {
            BaseAddress = new Uri("http://fake.local")
        };
        var remoteClient = new RemoteServerClient(server, NullLogger.Instance, httpClient);
        var (sync, cache, mapping, config) = BuildHarness(remoteClient, server);

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    /// <summary>
    /// Answers <c>Plugins/Federation/Config</c> per <paramref name="pluginInstalled"/>
    /// (200 or 404) and, when reached, a single-movie <c>/Items</c> response for
    /// everything else - so a test can tell "nothing was fetched because the
    /// plugin-presence check short-circuited" apart from "the plugin check passed
    /// but the item fetch itself returned nothing."
    /// </summary>
    private sealed class PluginProbeFakeHandler : HttpMessageHandler
    {
        private readonly bool _pluginInstalled;

        public PluginProbeFakeHandler(bool pluginInstalled)
        {
            _pluginInstalled = pluginInstalled;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("Plugins/Federation/Config", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(_pluginInstalled ? HttpStatusCode.OK : HttpStatusCode.NotFound));
            }

            var body = $"{{\"Items\":[{{\"Id\":\"{Guid.NewGuid()}\",\"Name\":\"New Movie\",\"Type\":\"Movie\"}}],\"TotalRecordCount\":1}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
