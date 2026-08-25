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
/// gate that closes it, and - just as importantly - the line between "confirmed
/// absent" and "could not tell", since only the former may delete content.
/// </summary>
public class FederationPluginDetectionTests
{
    /// <summary>
    /// Peer-probe results are cached statically per server id (a RemoteServerClient
    /// is constructed per call, so the cache cannot live on the instance). Every
    /// test therefore gets a freshly generated server id, so one test's cached
    /// verdict can never leak into another's.
    /// </summary>
    private static RemoteServer NewServer() => new()
    {
        Id = "server-" + Guid.NewGuid().ToString("N"),
        Name = "Remote",
        Url = "http://fake.local",
        ApiKey = "key",
        UserId = "user1",
        Enabled = true,
        WanCapMode = WanCapMode.Off
    };

    private static (FederationSyncService Sync, FederationItemCache Cache, LibraryMapping Mapping, PluginConfiguration Config) BuildHarness(
        RemoteServer server,
        HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(server.Url) };
        var remoteClient = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(It.IsAny<RemoteServer>())).Returns(remoteClient);

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager);
        var syncService = new FederationSyncService(
            NullLogger<FederationSyncService>.Instance,
            libraryManager,
            clientFactory.Object,
            cache,
            persistence,
            bandwidthMonitor,
            new Mock<IServiceProvider>().Object,
            new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));

        var mapping = new LibraryMapping
        {
            LocalLibraryName = "Movies",
            MediaType = "Movie",
            Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource>
            {
                new RemoteLibrarySource { ServerId = server.Id, RemoteLibraryId = "lib1", RemoteLibraryName = "Movies" }
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

    private static Guid SeedExistingItem(FederationItemCache cache, LibraryMapping mapping, string serverId)
    {
        var movieId = Guid.NewGuid();
        cache.UpsertRaw(mapping.LocalLibraryName, serverId, movieId, new BaseItemDto { Id = movieId, Name = "Old Movie" }, 0, "Movie");
        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));
        return movieId;
    }

    [Fact]
    public async Task RemoteConfirmedWithoutFederationPlugin_RemovesItsItems()
    {
        var server = NewServer();
        var (sync, cache, mapping, config) = BuildHarness(server, new ScriptedHandler(FederationProbe.NotFound, aliveProbeSucceeds: true));
        SeedExistingItem(cache, mapping, server.Id);

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Empty(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    [Fact]
    public async Task RemoteWithFederationPlugin_SyncsNormally()
    {
        var server = NewServer();
        var (sync, cache, mapping, config) = BuildHarness(server, new ScriptedHandler(FederationProbe.Ok, aliveProbeSucceeds: true));

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    /// <summary>
    /// The regression this whole tri-state exists for. A Cloudflare Tunnel (or any
    /// reverse proxy) in front of a briefly-down origin answers 502 for every path,
    /// including this plugin's own route. Reading that as "Federation was
    /// uninstalled" would delete the remote's entire federated library over an
    /// outage that resolves itself minutes later.
    /// </summary>
    [Fact]
    public async Task RemoteAnswering502_KeepsItsItems()
    {
        var server = NewServer();
        var (sync, cache, mapping, config) = BuildHarness(server, new ScriptedHandler(FederationProbe.BadGateway, aliveProbeSucceeds: false));
        SeedExistingItem(cache, mapping, server.Id);

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    [Fact]
    public async Task RemoteUnreachable_KeepsItsItems()
    {
        var server = NewServer();
        var (sync, cache, mapping, config) = BuildHarness(server, new ScriptedHandler(FederationProbe.Throws, aliveProbeSucceeds: false));
        SeedExistingItem(cache, mapping, server.Id);

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    /// <summary>
    /// A misrouted tunnel or a proxy pointed at the wrong origin 404s every path,
    /// which looks identical to "plugin not installed" if the 404 is trusted on its
    /// own. It is only an absence if the address is also serving a live Jellyfin.
    /// </summary>
    [Fact]
    public async Task Remote404sEverything_IncludingSystemInfo_KeepsItsItems()
    {
        var server = NewServer();
        var (sync, cache, mapping, config) = BuildHarness(server, new ScriptedHandler(FederationProbe.NotFound, aliveProbeSucceeds: false));
        SeedExistingItem(cache, mapping, server.Id);

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    [Fact]
    public async Task RemoteBehindAnAuthGateAnswering403_KeepsItsItems()
    {
        var server = NewServer();
        var (sync, cache, mapping, config) = BuildHarness(server, new ScriptedHandler(FederationProbe.Forbidden, aliveProbeSucceeds: false));
        SeedExistingItem(cache, mapping, server.Id);

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Single(cache.GetEntriesForMapping(mapping.LocalLibraryName));
    }

    private enum FederationProbe
    {
        Ok,
        NotFound,
        BadGateway,
        Forbidden,
        Throws
    }

    /// <summary>
    /// Answers the three request shapes a sync makes, independently: this plugin's
    /// own <c>Plugins/Federation/Config</c> route, the <c>System/Info/Public</c>
    /// liveness confirmation, and ordinary <c>/Items</c> queries. Separating the
    /// first two is the point - the interesting cases are precisely those where
    /// they disagree.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly FederationProbe _probe;
        private readonly bool _aliveProbeSucceeds;

        public ScriptedHandler(FederationProbe probe, bool aliveProbeSucceeds)
        {
            _probe = probe;
            _aliveProbeSucceeds = aliveProbeSucceeds;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("Plugins/Federation/Config", StringComparison.OrdinalIgnoreCase))
            {
                return _probe switch
                {
                    FederationProbe.Ok => Respond(HttpStatusCode.OK),
                    FederationProbe.NotFound => Respond(HttpStatusCode.NotFound),
                    FederationProbe.BadGateway => Respond(HttpStatusCode.BadGateway),
                    FederationProbe.Forbidden => Respond(HttpStatusCode.Forbidden),
                    _ => throw new HttpRequestException("connection refused")
                };
            }

            if (path.Contains("System/Info/Public", StringComparison.OrdinalIgnoreCase))
            {
                return Respond(_aliveProbeSucceeds ? HttpStatusCode.OK : HttpStatusCode.BadGateway);
            }

            var body = $"{{\"Items\":[{{\"Id\":\"{Guid.NewGuid()}\",\"Name\":\"New Movie\",\"Type\":\"Movie\"}}],\"TotalRecordCount\":1}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static Task<HttpResponseMessage> Respond(HttpStatusCode status)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
