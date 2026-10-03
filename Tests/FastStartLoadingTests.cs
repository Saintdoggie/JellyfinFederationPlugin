using System;
using System.Collections.Generic;
using System.Linq;
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
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The loading experience for slow federated starts: the projection, the fun-fact
/// source, the daily link-speed sample behind it, and the endpoints that serve them.
/// </summary>
[Collection("PluginInstance")]
public class FastStartLoadingTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();

    public void Dispose()
    {
        FunFactService.HttpClientOverride = null;
        _plugin.Dispose();
    }

    // ---- StartEstimator ----

    [Fact]
    public void Estimate_SlowLink_MatchesWhatWasMeasuredOnTheLiveServer()
    {
        // 93.2 Mbps remux over a ~7.5 Mbps link took 75-86 s on the real server.
        var e = StartEstimator.Estimate("freakbob", 93_243_000, 7.5);
        Assert.InRange(e.Seconds!.Value, 75, 90);
        Assert.Contains("freakbob", e.Reason);
        Assert.Contains("93", e.Reason);
        Assert.Contains("7.5", e.Reason);
    }

    [Fact]
    public void Estimate_GigabitFriend_IsQuickAndSaysNothingAlarming()
    {
        var e = StartEstimator.Estimate("netflix++", 93_243_000, 900);
        Assert.InRange(e.Seconds!.Value, 3, StartEstimator.QuickSeconds);
        Assert.Contains("quickly", e.Reason);
    }

    [Fact]
    public void Estimate_UnknownLink_HasNoNumberButExplainsWhy()
    {
        var e = StartEstimator.Estimate("Friend", 50_000_000, null);
        Assert.Null(e.Seconds);
        Assert.Contains("not been measured", e.Reason);
        Assert.Equal(50, e.BitrateMbps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void Estimate_UnknownBitrate_HasNoNumber(long? bitrate)
    {
        var e = StartEstimator.Estimate("Friend", bitrate, 100);
        Assert.Null(e.Seconds);
        Assert.Contains("Friend", e.Reason);
    }

    [Fact]
    public void Estimate_IsBoundedAndBlankServerGetsAName()
    {
        Assert.Equal(900, StartEstimator.Estimate("x", 150_000_000, 0.05).Seconds);
        Assert.Equal((int)StartEstimator.FixedOverheadSeconds, StartEstimator.Estimate("x", 1_000_000, 10_000).Seconds);
        Assert.Contains("the source server", StartEstimator.Estimate(" ", 1_000_000, 100).Reason);
    }

    [Fact]
    public void Estimate_GetsLongerAsTheLinkGetsSlower()
    {
        var previous = 0;
        foreach (var mbps in new[] { 200, 100, 50, 20, 10, 5, 2 })
        {
            var seconds = StartEstimator.Estimate("x", 93_000_000, mbps).Seconds!.Value;
            Assert.True(seconds >= previous);
            previous = seconds;
        }
    }

    // ---- FunFactService ----

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public CountingHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_respond());
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static FunFactService NewFacts() => new(NullLogger<FunFactService>.Instance);

    [Fact]
    public async Task FunFact_UsesTheApiTextWhenAvailable()
    {
        var handler = new CountingHandler(() => Json("{\"text\":\"Peter Falk had a glass eye.\",\"id\":\"1\"}"));
        FunFactService.HttpClientOverride = new HttpClient(handler);

        Assert.Equal("Peter Falk had a glass eye.", await NewFacts().GetAsync());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"other\":1}")]
    [InlineData(HttpStatusCode.OK, "{\"text\":\"   \"}")]
    public async Task FunFact_FallsBackWhenTheApiMisbehaves(HttpStatusCode code, string body)
    {
        FunFactService.HttpClientOverride = new HttpClient(new CountingHandler(() => Json(body, code)));
        Assert.False(string.IsNullOrWhiteSpace(await NewFacts().GetAsync()));
    }

    [Fact]
    public async Task FunFact_FallsBackWhenTheApiThrows()
    {
        FunFactService.HttpClientOverride = new HttpClient(new ThrowingHandler());
        Assert.False(string.IsNullOrWhiteSpace(await NewFacts().GetAsync()));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no network");
    }

    [Fact]
    public async Task FunFact_DisabledInSettings_NeverContactsTheApi()
    {
        _plugin.Configuration.LoadingFunFacts = false;
        var handler = new CountingHandler(() => Json("{\"text\":\"should not appear\"}"));
        FunFactService.HttpClientOverride = new HttpClient(handler);

        var fact = await NewFacts().GetAsync();

        Assert.Equal(0, handler.Calls);
        Assert.NotEqual("should not appear", fact);
        Assert.False(string.IsNullOrWhiteSpace(fact));
    }

    [Fact]
    public async Task FunFact_ManyViewersAtOnce_CallTheApiOnlyOnce()
    {
        var handler = new CountingHandler(() => Json("{\"text\":\"One fact.\"}"));
        FunFactService.HttpClientOverride = new HttpClient(handler);
        var facts = NewFacts();

        var results = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            results.Add(await facts.GetAsync());
        }

        Assert.Equal(1, handler.Calls);
        Assert.All(results, r => Assert.Equal("One fact.", r));
    }

    [Fact]
    public async Task FunFact_FallbacksRotate()
    {
        _plugin.Configuration.LoadingFunFacts = false;
        var facts = NewFacts();
        var seen = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            seen.Add(await facts.GetAsync());
        }

        Assert.True(seen.Count >= 4);
    }

    [Fact]
    public void FunFactClean_StripsControlCharactersAndBoundsLength()
    {
        Assert.Equal("ab", FunFactService.Clean("a\u0000b\n"));
        Assert.Null(FunFactService.Clean("  \t "));
        Assert.Null(FunFactService.Clean(null));
        var cleaned = FunFactService.Clean(new string('x', 1000))!;
        Assert.True(cleaned.Length <= 280);
        Assert.EndsWith("…", cleaned);
    }

    // ---- daily link-speed sample ----

    private static (WanBandwidthMonitor Monitor, Mock<IExternalCatalogProvider> Provider, RemoteServer Server) PlexMonitor(bool withImportedItem = true)
    {
        var server = new RemoteServer { Id = "plex1", Name = "freakbob", Kind = ServerKind.Plex, Enabled = true, Url = "http://plex.example:32400" };
        var provider = new Mock<IExternalCatalogProvider>();
        provider.SetupGet(p => p.Kind).Returns(ServerKind.Plex);

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        if (withImportedItem)
        {
            var remoteId = Guid.NewGuid();
            cache.UpsertRaw("Movies", "plex1", remoteId, new BaseItemDto { Id = remoteId, Name = "Movie" }, 0, "Movie");
            cache.GetEntriesForMapping("Movies").First().SetNativeId("plex1", remoteId, "100");
        }

        var monitor = new WanBandwidthMonitor(
            NullLogger<WanBandwidthMonitor>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            new ExternalCatalogRegistry(new[] { provider.Object }),
            cache);
        return (monitor, provider, server);
    }

    private static void Reading(Mock<IExternalCatalogProvider> provider, double? mbps)
        => provider.Setup(p => p.MeasureBandwidthMbpsAsync(It.IsAny<RemoteServer>(), "100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mbps);

    [Fact]
    public async Task LinkSample_PlexServer_IsMeasuredFromAnAlreadyImportedFile()
    {
        var (monitor, provider, server) = PlexMonitor();
        Reading(provider, 7.6);

        await monitor.MeasureLinkIfDueAsync(server);

        Assert.Equal(7.6, monitor.GetMeasuredLinkMbps("plex1"));
        provider.Verify(p => p.MeasureBandwidthMbpsAsync(server, "100", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LinkSample_IsTakenOncePerDay()
    {
        var (monitor, provider, server) = PlexMonitor();
        Reading(provider, 7.6);

        await monitor.MeasureLinkIfDueAsync(server);
        await monitor.MeasureLinkIfDueAsync(server);
        await monitor.MeasureLinkIfDueAsync(server);

        provider.Verify(p => p.MeasureBandwidthMbpsAsync(It.IsAny<RemoteServer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LinkSample_IsRetakenWhenAServerComesBackOnline()
    {
        var (monitor, provider, server) = PlexMonitor();
        Reading(provider, 7.6);
        await monitor.MeasureLinkIfDueAsync(server);

        Reading(provider, 31.0);
        monitor.InvalidateMeasurement("plex1");
        await monitor.MeasureLinkIfDueAsync(server);

        Assert.Equal(31.0, monitor.GetMeasuredLinkMbps("plex1"));
    }

    [Fact]
    public async Task LinkSample_FailureKeepsThePreviousReading_AndDoesNotRetryImmediately()
    {
        var (monitor, provider, server) = PlexMonitor();
        Reading(provider, 7.6);
        await monitor.MeasureLinkIfDueAsync(server);

        Reading(provider, null);
        monitor.InvalidateMeasurement("plex1");
        await monitor.MeasureLinkIfDueAsync(server);
        await monitor.MeasureLinkIfDueAsync(server);

        Assert.Equal(7.6, monitor.GetMeasuredLinkMbps("plex1"));
        provider.Verify(p => p.MeasureBandwidthMbpsAsync(It.IsAny<RemoteServer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LinkSample_DisabledServer_IsNeverProbed()
    {
        var (monitor, provider, server) = PlexMonitor();
        server.Enabled = false;

        await monitor.MeasureLinkIfDueAsync(server);

        provider.Verify(p => p.MeasureBandwidthMbpsAsync(It.IsAny<RemoteServer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(monitor.GetMeasuredLinkMbps("plex1"));
    }

    [Fact]
    public async Task LinkSample_WithNothingImportedFromTheServer_ReadsNothing()
    {
        var (monitor, provider, server) = PlexMonitor(withImportedItem: false);
        await monitor.MeasureLinkIfDueAsync(server);

        provider.Verify(p => p.MeasureBandwidthMbpsAsync(It.IsAny<RemoteServer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(monitor.GetMeasuredLinkMbps("plex1"));
    }

    [Fact]
    public async Task LinkSample_ProviderThrowing_NeverEscapes()
    {
        var (monitor, provider, server) = PlexMonitor();
        provider.Setup(p => p.MeasureBandwidthMbpsAsync(It.IsAny<RemoteServer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await monitor.MeasureLinkIfDueAsync(server);

        Assert.Null(monitor.GetMeasuredLinkMbps("plex1"));
    }

    [Fact]
    public async Task LinkSample_JellyfinServerWithoutAClient_IsHarmless()
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        await monitor.MeasureLinkIfDueAsync(new RemoteServer { Id = "j1", Kind = ServerKind.Jellyfin, Enabled = true });
        Assert.Null(monitor.GetMeasuredLinkMbps("j1"));
    }

    // ---- endpoints ----

    private (FederationLoadingController Controller, Movie Item, FederatedCacheEntry Entry) NewController(int bitrate, double? linkMbps)
    {
        var server = new RemoteServer { Id = "serverA", Name = "freakbob", Url = "http://friend.example:8096", ApiKey = "k", Enabled = true };
        _plugin.Configuration.RemoteServers.Add(server);

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(path + "|" + type.FullName))));
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        if (linkMbps.HasValue)
        {
            monitor.SeedLinkForTests("serverA", linkMbps.Value);
        }

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var manager = new FederationLibraryManager(
            lm.Object,
            NullLogger<FederationLibraryManager>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            cache,
            monitor,
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        var remoteId = Guid.NewGuid();
        cache.UpsertRaw(
            "Movies",
            "serverA",
            remoteId,
            new BaseItemDto
            {
                Id = remoteId,
                Name = "Movie",
                MediaSources = new[] { new MediaBrowser.Model.Dto.MediaSourceInfo { Bitrate = bitrate } },
            },
            0,
            "Movie");
        var entry = cache.GetEntriesForMapping("Movies").First();

        var item = new Movie
        {
            Id = Guid.NewGuid(),
            ProviderIds = new Dictionary<string, string> { ["FederationKey"] = entry.Key },
        };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetItemById(item.Id)).Returns(item);
        libraryManager.Setup(x => x.GetItemById(It.Is<Guid>(g => g != item.Id))).Returns((MediaBrowser.Controller.Entities.BaseItem?)new Movie { Id = Guid.NewGuid() });

        var controller = new FederationLoadingController(libraryManager.Object, manager, monitor, NewFacts(), new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));
        return (controller, item, entry);
    }

    private static T Body<T>(ActionResult<object> result, string property)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value!;
        return (T)value.GetType().GetProperty(property)!.GetValue(value)!;
    }

    [Fact]
    public void StartEstimate_ForASlowFederatedTitle_ProjectsTheWaitAndNamesTheFriend()
    {
        var (controller, item, _) = NewController(93_000_000, 7.5);

        var result = controller.GetStartEstimate(item.Id);

        Assert.True(Body<bool>(result, "federated"));
        Assert.InRange(Body<int?>(result, "seconds")!.Value, 70, 90);
        Assert.Contains("freakbob", Body<string>(result, "reason"));
        Assert.Equal("freakbob", Body<string>(result, "serverName"));
    }

    [Fact]
    public void StartEstimate_WithoutAMeasurementYet_HasNoSecondsButStillExplains()
    {
        var (controller, item, _) = NewController(93_000_000, null);

        var result = controller.GetStartEstimate(item.Id);

        Assert.True(Body<bool>(result, "federated"));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(ok.Value!.GetType().GetProperty("seconds")!.GetValue(ok.Value));
        Assert.Contains("not been measured", Body<string>(result, "reason"));
    }

    [Fact]
    public void StartEstimate_ForALocalTitle_IsNotFederated()
    {
        var (controller, _, _) = NewController(93_000_000, 7.5);

        var result = controller.GetStartEstimate(Guid.NewGuid());

        Assert.False(Body<bool>(result, "federated"));
    }

    [Fact]
    public async Task FunFactEndpoint_ReturnsText()
    {
        _plugin.Configuration.LoadingFunFacts = false;
        var (controller, _, _) = NewController(1, null);

        var result = await controller.GetFunFact(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(Body<string>(result, "text")));
    }

    [Fact]
    public void ClientSettings_AdvertisesTheOverlaySwitch_OnByDefault()
    {
        Assert.True(_plugin.Configuration.LoadingOverlay);
        Assert.True(_plugin.Configuration.LoadingFunFacts);
        Assert.True(_plugin.Configuration.FastStartProbing);
    }

    [Fact]
    public async Task TranscodeProbe_ForALocalTitle_IsNotFound()
    {
        var (controller, _, _) = NewController(93_000_000, 7.5);
        var result = await controller.ProbePlexTranscode(Guid.NewGuid());
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task TranscodeProbe_ForAJellyfinPeerTitle_IsRejected()
    {
        var (controller, item, _) = NewController(93_000_000, 7.5);
        var result = await controller.ProbePlexTranscode(item.Id);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
