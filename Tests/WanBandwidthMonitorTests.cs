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

    [Fact]
    public async Task MeasureBandwidth_WithSampleTitle_TimesARangeOfTheRealFile()
    {
        // A peer's BitrateTest endpoint read 23 Mbps on a link that served its
        // media at 120+ Mbps, which capped every 4K title to 20 Mbps 1080p.
        var handler = new PeerHandler(directStreamStatus: System.Net.HttpStatusCode.PartialContent);
        var server = MakeServer(ServerKind.Jellyfin, WanCapMode.Auto);
        server.Url = "https://peer.example";
        var client = new RemoteServerClient(server, NullLogger.Instance, new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri(server.Url) });
        var itemId = Guid.NewGuid();

        var mbps = await client.MeasureBandwidthMbpsAsync(CancellationToken.None, itemId);

        Assert.True(mbps > 0);
        Assert.Equal($"/Plugins/Federation/DirectStream/{itemId:N}", handler.DirectStreamPath);
        Assert.DoesNotContain("capMbps", handler.DirectStreamQuery);
        Assert.Equal("bytes=0-9999999", handler.DirectStreamRange);
        Assert.Equal(0, handler.BitrateTestCalls);
    }

    [Fact]
    public async Task MeasureBandwidth_MediaSampleRefused_FallsBackToBitrateTest()
    {
        var handler = new PeerHandler(directStreamStatus: System.Net.HttpStatusCode.Forbidden);
        var server = MakeServer(ServerKind.Jellyfin, WanCapMode.Auto);
        server.Url = "https://peer.example";
        var client = new RemoteServerClient(server, NullLogger.Instance, new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri(server.Url) });

        Assert.True(await client.MeasureBandwidthMbpsAsync(CancellationToken.None, Guid.NewGuid()) > 0);
        Assert.Equal(1, handler.BitrateTestCalls);
    }

    private sealed class PeerHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _directStreamStatus;

        public PeerHandler(System.Net.HttpStatusCode directStreamStatus) => _directStreamStatus = directStreamStatus;

        public string? DirectStreamPath { get; private set; }

        public string DirectStreamQuery { get; private set; } = string.Empty;

        public string? DirectStreamRange { get; private set; }

        public int BitrateTestCalls { get; private set; }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Equals("/Plugins/Federation/PlaybackToken", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("{\"token\":\"item-tok\",\"purpose\":\"Playback\"}", System.Text.Encoding.UTF8, "application/json")
                });
            }

            if (path.StartsWith("/Plugins/Federation/DirectStream/", StringComparison.OrdinalIgnoreCase))
            {
                DirectStreamPath = path;
                DirectStreamQuery = request.RequestUri.Query;
                DirectStreamRange = request.Headers.Range?.ToString();
                return Task.FromResult(_directStreamStatus is System.Net.HttpStatusCode.PartialContent
                    ? new System.Net.Http.HttpResponseMessage(_directStreamStatus) { Content = new System.Net.Http.ByteArrayContent(new byte[10_000_000]) }
                    : new System.Net.Http.HttpResponseMessage(_directStreamStatus));
            }

            if (path.Equals("/Plugins/Federation/Peer/BitrateTest", StringComparison.OrdinalIgnoreCase))
            {
                BitrateTestCalls++;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(new byte[10_000_000]) });
            }

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
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

    [Theory]
    [InlineData(4.0, 4)]
    [InlineData(9.5, 9)]
    [InlineData(10.0, 10)]
    [InlineData(20.0, 17)]
    [InlineData(49.0, 42)]
    public void AutoCap_NeverExceedsMeasured(double measured, int expected)
    {
        var cap = WanBandwidthMonitor.AutoCapFromMeasured(measured);
        Assert.Equal(expected, cap);
        Assert.True(cap <= measured);
    }

    [Fact]
    public void GetEffectiveCapMbps_SlowLink_IsBelowMeasured()
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        var server = MakeServer(ServerKind.Jellyfin, WanCapMode.Auto);
        monitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 4.0);
        Assert.Equal(4, monitor.GetEffectiveCapMbps(server));
    }

    [Fact]
    public void GetEffectiveCapMbps_PlaceholderLifts_AfterRepeatedFailedProbes()
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        var server = MakeServer(ServerKind.Jellyfin, WanCapMode.Auto);
        monitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: null, failedProbes: 3);
        Assert.Null(monitor.GetEffectiveCapMbps(server));
        Assert.Contains("Never measured", monitor.GetWanNote(server));
    }

    [Fact]
    public async Task Refresh_KeepsConfirmedWan_WhenClassificationFails()
    {
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        var server = MakeServer(ServerKind.Jellyfin, WanCapMode.Auto);
        server.Url = "http://no-such-host.invalid:8096";
        monitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 30.0);
        monitor.ExpireForTests(server.Id);

        await monitor.RefreshIfDueAsync(server);

        Assert.Equal(26, monitor.GetEffectiveCapMbps(server));
    }

    private sealed class DeadlineStream : System.IO.Stream
    {
        public double Clock;
        public int Reads;
        public override async Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken t) => 0;
        public override async ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken t = default)
        {
            Reads++;
            if (Reads > 40) { throw new OperationCanceledException(t); }
            Clock += 0.5;
            var n = Math.Min(m.Length, 64 * 1024);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, System.IO.SeekOrigin f) => throw new NotSupportedException();
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) { }
    }

    [Fact]
    public async Task Throughput_KeepsPartialReading_WhenDeadlineHits()
    {
        var s = new DeadlineStream();
        using var cts = new CancellationTokenSource();
        var mbps = await RemoteServerClient.MeasureSteadyThroughputMbpsAsync(s, () => s.Clock, cts.Token);
        Assert.NotNull(mbps);
        Assert.InRange(mbps!.Value, 0.5, 2.0);
    }
}
