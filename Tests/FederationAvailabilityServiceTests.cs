using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// State machine of <see cref="FederationAvailabilityService"/>: consecutive
/// failures are required before a server flips offline (one slow response
/// never hides a library), a single success flips it back immediately, and
/// flips raise the rescan hook that hides/unhides items. The HTTP probe itself
/// is not exercised here - only the transition logic via RecordResult.
/// </summary>
public sealed class FederationAvailabilityServiceTests
{
    private static RemoteServer Server(string id)
        => new() { Id = id, Name = id, Url = "http://example.local", Enabled = true };

    private static FederationAvailabilityService Service()
    {
        var clientFactory = new Mock<IRemoteServerClientFactory>(MockBehavior.Loose);
        var libraryManager = new Mock<MediaBrowser.Controller.Library.ILibraryManager>(MockBehavior.Loose);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(
            NullLogger<WanBandwidthMonitor>.Instance,
            clientFactory.Object);
        var federationManager = new FederationLibraryManager(
            libraryManager.Object,
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory.Object,
            cache,
            bandwidthMonitor,
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(
            libraryManager.Object,
            NullLogger<FederationItemPersistenceService>.Instance,
            federationManager,
            Mock.Of<MediaBrowser.Controller.Persistence.IItemPersistenceService>());
        return new FederationAvailabilityService(
            NullLogger<FederationAvailabilityService>.Instance,
            clientFactory.Object,
            new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()),
            persistence);
    }

    [Fact]
    public void FirstFailure_StaysUnknown_NotOffline()
    {
        var service = Service();

        service.RecordResult(Server("a"), false, "boom");

        Assert.Equal(ServerReachability.Unknown, service.GetAvailability("a")!.Reachability);
        Assert.False(service.IsOffline("a"));
    }

    [Fact]
    public void SecondConsecutiveFailure_FlipsOffline()
    {
        var service = Service();

        service.RecordResult(Server("a"), false, "boom");
        service.RecordResult(Server("a"), false, "boom");

        Assert.Equal(ServerReachability.Offline, service.GetAvailability("a")!.Reachability);
        Assert.True(service.IsOffline("a"));
    }

    [Fact]
    public void SingleSuccess_FlipsOnlineImmediately()
    {
        var service = Service();
        service.RecordResult(Server("a"), false, "boom");
        service.RecordResult(Server("a"), false, "boom");
        Assert.True(service.IsOffline("a"));

        service.RecordResult(Server("a"), true, null);

        Assert.Equal(ServerReachability.Online, service.GetAvailability("a")!.Reachability);
        Assert.False(service.IsOffline("a"));
    }

    [Fact]
    public async Task Flip_RaisesRescanHook_OncePerTransition()
    {
        var service = Service();
        var calls = new List<bool>();
        service.OnReachabilityChangedAsync = (serverId, online, ct) =>
        {
            calls.Add(online);
            return Task.CompletedTask;
        };

        service.RecordResult(Server("a"), true, null);
        // First success from Unknown raises (items may need re-showing).
        await Task.Delay(300);
        service.RecordResult(Server("a"), false, "x");
        await Task.Delay(100);
        // Single failure: still Unknown/Online, no flip, no hook.
        service.RecordResult(Server("a"), false, "x");
        await Task.Delay(300);

        Assert.Equal(new[] { true, false }, calls);
    }

    [Fact]
    public void DelayAfterRound_ConfirmsFirstFailureQuickly()
    {
        Assert.Equal(FederationAvailabilityService.ConfirmInterval, FederationAvailabilityService.DelayAfterRound(new[] { 1 }));
        Assert.Equal(FederationAvailabilityService.ProbeInterval, FederationAvailabilityService.DelayAfterRound(new[] { 0 }));
        Assert.Equal(FederationAvailabilityService.ProbeInterval, FederationAvailabilityService.DelayAfterRound(new[] { 2 }));
        Assert.Equal(FederationAvailabilityService.ConfirmInterval, FederationAvailabilityService.DelayAfterRound(new[] { 0, 1, 2 }));
    }

    [Fact]
    public void Forget_DropsState()
    {
        var service = Service();
        service.RecordResult(Server("a"), false, "boom");
        service.RecordResult(Server("a"), false, "boom");
        Assert.True(service.IsOffline("a"));

        service.Forget("a");

        Assert.Null(service.GetAvailability("a"));
        Assert.False(service.IsOffline("a"));
    }
}
