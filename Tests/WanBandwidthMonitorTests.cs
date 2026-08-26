using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers <see cref="WanBandwidthMonitor.GetEffectiveCapMbps"/>'s per-<see cref="ServerKind"/>
/// decisions, in particular that Auto mode - which relies on a Jellyfin-only bandwidth
/// probe endpoint - never gets stuck permanently capping a non-Jellyfin (Plex) peer at
/// its "still waiting to find out" placeholder, while Manual (an explicit value the user
/// sets themselves) works for any <see cref="ServerKind"/> since it needs no probe at all.
/// </summary>
public class WanBandwidthMonitorTests
{
    private static RemoteServer MakeServer(ServerKind kind, WanCapMode mode, int maxBitrateMbps = 0)
    {
        return new RemoteServer
        {
            Id = Guid.NewGuid().ToString(),
            Url = "http://friend.example:8096",
            Kind = kind,
            WanCapMode = mode,
            WanMaxBitrateMbps = maxBitrateMbps,
            Enabled = true
        };
    }

    [Fact]
    public void GetEffectiveCapMbps_AutoModePlexServer_IsAlwaysUncapped()
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        var server = MakeServer(ServerKind.Plex, WanCapMode.Auto);

        // Even once confirmed as a WAN link (the case that would otherwise settle
        // permanently on PendingMeasurementCapMbps, since Plex has no bandwidth
        // probe endpoint to ever supply a real MeasuredMbps value).
        monitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: null);

        Assert.Null(monitor.GetEffectiveCapMbps(server));
    }

    [Fact]
    public void GetEffectiveCapMbps_AutoModeJellyfinServer_UsesPendingPlaceholderUntilMeasured()
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        var server = MakeServer(ServerKind.Jellyfin, WanCapMode.Auto);
        monitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: null);

        // Unchanged baseline behavior for a Jellyfin peer - only Plex's Auto
        // handling changed.
        Assert.Equal(10, monitor.GetEffectiveCapMbps(server));
    }

    [Theory]
    [InlineData(ServerKind.Jellyfin)]
    [InlineData(ServerKind.Plex)]
    public void GetEffectiveCapMbps_ManualMode_AppliesForAnyServerKind(ServerKind kind)
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        var server = MakeServer(kind, WanCapMode.Manual, maxBitrateMbps: 5);

        Assert.Equal(5, monitor.GetEffectiveCapMbps(server));
    }

    [Fact]
    public void GetEffectiveCapMbps_OffMode_IsUncappedRegardlessOfServerKind()
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        var server = MakeServer(ServerKind.Plex, WanCapMode.Off, maxBitrateMbps: 5);

        Assert.Null(monitor.GetEffectiveCapMbps(server));
    }

    [Fact]
    public async Task RefreshIfDueAsync_AutoModePlexServer_NeverProbesForBandwidth()
    {
        // A Plex server has no /Playback/BitrateTest-equivalent endpoint - probing
        // it would just be a wasted, always-failing HTTP round trip every cycle.
        // Auto being a no-op for Plex (see GetEffectiveCapMbps) means this should
        // never even attempt to resolve a client for it.
        var clientFactory = new Mock<IRemoteServerClientFactory>(MockBehavior.Strict);
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var server = MakeServer(ServerKind.Plex, WanCapMode.Auto);

        await monitor.RefreshIfDueAsync(server, CancellationToken.None);

        clientFactory.VerifyNoOtherCalls();
    }
}
