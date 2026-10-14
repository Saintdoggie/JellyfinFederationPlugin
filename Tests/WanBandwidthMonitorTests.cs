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

    [Fact]
    public async Task SteadyThroughput_IgnoresSlowStart()
    {
        // 300 ms latency, a redirect and TCP slow-start made a 100 Mbps peer
        // read as ~20 Mbps when the whole request was timed.
        var clock = 0.0;
        var stream = new PacedStream(totalBytes: 10_000_000, chunk: 64 * 1024, secondsPerChunk: read => read < 1_000_000 ? 0.2 : 64 * 1024 * 8 / 100_000_000.0, advance: s => clock += s);

        var mbps = await RemoteServerClient.MeasureSteadyThroughputMbpsAsync(stream, () => clock, CancellationToken.None);

        Assert.NotNull(mbps);
        Assert.InRange(mbps!.Value, 95, 105);
    }

    [Fact]
    public async Task SteadyThroughput_SlowLink_StopsAfterTimeBudget()
    {
        var clock = 0.0;
        var stream = new PacedStream(totalBytes: 50_000_000, chunk: 64 * 1024, secondsPerChunk: _ => 64 * 1024 * 8 / 2_000_000.0, advance: s => clock += s);

        var mbps = await RemoteServerClient.MeasureSteadyThroughputMbpsAsync(stream, () => clock, CancellationToken.None);

        Assert.InRange(mbps!.Value, 1.9, 2.1);
        Assert.True(stream.Delivered < 5_000_000);
    }

    [Fact]
    public async Task SteadyThroughput_BodyShorterThanWarmup_IsNull()
    {
        var clock = 0.0;
        var stream = new PacedStream(totalBytes: 500_000, chunk: 64 * 1024, secondsPerChunk: _ => 0.01, advance: s => clock += s);

        Assert.Null(await RemoteServerClient.MeasureSteadyThroughputMbpsAsync(stream, () => clock, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://jellyfin.example.org", 308, "https://jellyfin.example.org/System/Info/Public", "https://jellyfin.example.org")]
    [InlineData("http://jellyfin.example.org", 301, "https://jellyfin.example.org/System/Info/Public", "https://jellyfin.example.org")]
    [InlineData("http://example.org/jf", 308, "https://example.org/jf/System/Info/Public", "https://example.org/jf")]
    [InlineData("http://jellyfin.example.org", 302, "https://jellyfin.example.org/System/Info/Public", null)]
    [InlineData("http://jellyfin.example.org", 308, "https://evil.example.net/System/Info/Public", null)]
    [InlineData("http://jellyfin.example.org", 308, "https://jellyfin.example.org/login", null)]
    [InlineData("http://jellyfin.example.org", 308, "http://jellyfin.example.org:8080/System/Info/Public", null)]
    [InlineData("http://jellyfin.example.org", 200, null, null)]
    public void HttpsUpgradeTarget_OnlyPermanentSameHostHttps(string configured, int status, string? location, string? expected)
    {
        var requested = new Uri(configured.TrimEnd('/') + "/System/Info/Public");
        Assert.Equal(expected, RemoteServerClientFactory.HttpsUpgradeTarget(requested, (System.Net.HttpStatusCode)status, location == null ? null : new Uri(location)));
    }

    [Fact]
    public void EffectiveUrl_UsesUpgradeOnlyForTheUrlItWasLearnedFor()
    {
        RemoteServerClientFactory.ResetHttpsUpgradesForTests();
        var server = MakeServer(ServerKind.Jellyfin, WanCapMode.Auto);
        server.Url = "http://jellyfin.example.org/";
        Assert.Equal("http://jellyfin.example.org", RemoteServerClientFactory.EffectiveUrl(server));

        RemoteServerClientFactory.SeedHttpsUpgradeForTests(server, "https://jellyfin.example.org");
        Assert.Equal("https://jellyfin.example.org", RemoteServerClientFactory.EffectiveUrl(server));

        // The admin edits the address: the old result no longer applies.
        server.Url = "http://other.example.org";
        Assert.Equal("http://other.example.org", RemoteServerClientFactory.EffectiveUrl(server));
        RemoteServerClientFactory.ResetHttpsUpgradesForTests();
    }

    private sealed class PacedStream : System.IO.Stream
    {
        private readonly long _total;
        private readonly int _chunk;
        private readonly Func<long, double> _secondsPerChunk;
        private readonly Action<double> _advance;

        public PacedStream(long totalBytes, int chunk, Func<long, double> secondsPerChunk, Action<double> advance)
        {
            _total = totalBytes;
            _chunk = chunk;
            _secondsPerChunk = secondsPerChunk;
            _advance = advance;
        }

        public long Delivered { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _total;

        public override long Position { get => Delivered; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(Math.Min(count, _chunk), _total - Delivered);
            if (n <= 0)
            {
                return 0;
            }

            _advance(_secondsPerChunk(Delivered));
            Delivered += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
