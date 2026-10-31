using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlaybackRouteMetricsTests
{
    private readonly PlaybackRouteMetrics _metrics = new();
    private readonly Guid _user = Guid.NewGuid();
    private DateTime _now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    public PlaybackRouteMetricsTests() => _metrics.UtcNow = () => _now;

    [Fact]
    public void MissingMeasurement_KeepsFallback()
    {
        Assert.Equal(20, _metrics.GetCapMbps(_user, "phone", "friend", "direct", 20));
        Assert.Null(_metrics.GetCapMbps(_user, "phone", "friend", "direct", null));
    }

    [Fact]
    public void MeasuredRouteCapsOnlyItsOwnDeviceAndTransport()
    {
        Transfer(40_000_000, 10_000);

        Assert.Equal(24, _metrics.GetCapMbps(_user, "phone", "friend", "direct", 50));
        Assert.Equal(24, _metrics.GetCapMbps(_user, "phone", "friend", "direct", null));
        Assert.Equal(50, _metrics.GetCapMbps(_user, "laptop", "friend", "direct", 50));
        Assert.Equal(50, _metrics.GetCapMbps(_user, "phone", "friend", "relay", 50));
        Assert.Equal(50, _metrics.GetCapMbps(Guid.NewGuid(), "phone", "friend", "direct", 50));
    }

    [Fact]
    public void StallsReduceQuickly_AndRecoveryDoesNotExceedFallback()
    {
        Transfer(40_000_000, 10_000);
        _metrics.ObserveHealth(_user, "phone", "friend", "direct", true, 0, 20, 1000);
        Assert.Equal(12, _metrics.GetCapMbps(_user, "phone", "friend", "direct", 50));

        _now = _now.AddSeconds(30);
        _metrics.ObserveHealth(_user, "phone", "friend", "direct", false, 8, 20, 1000);
        Assert.Equal(12, _metrics.GetCapMbps(_user, "phone", "friend", "direct", 50));

        _now = _now.AddMinutes(2);
        Assert.Equal(20, _metrics.GetCapMbps(_user, "phone", "friend", "direct", 20));
    }

    [Fact]
    public void OldMeasurementsAreNotUsed()
    {
        Transfer(40_000_000, 10_000);
        _now = _now.AddMinutes(6);
        Assert.Equal(20, _metrics.GetCapMbps(_user, "phone", "friend", "direct", 20));
        Assert.Null(_metrics.Snapshot(_user, "phone", "friend", "direct"));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(65536, 0)]
    [InlineData(65536, 200)]
    [InlineData(65536, 300001)]
    public void InvalidTransfersDoNotBecomeZeroBandwidth(long bytes, double duration)
    {
        Transfer(bytes, duration);
        Assert.Null(_metrics.Snapshot(_user, "phone", "friend", "direct")?.MeasuredMbps);
    }

    [Fact]
    public void TelemetryIsRateLimitedPerRoute()
    {
        Assert.True(_metrics.AcceptTelemetry(_user, "phone", "friend", "direct"));
        Assert.False(_metrics.AcceptTelemetry(_user, "phone", "friend", "direct"));
        Assert.True(_metrics.AcceptTelemetry(_user, "phone", "friend", "relay"));
        _now = _now.AddSeconds(3);
        Assert.True(_metrics.AcceptTelemetry(_user, "phone", "friend", "direct"));
    }

    private void Transfer(long bytes, double duration)
        => _metrics.ObserveTransfer(_user, "phone", "friend", "direct", bytes, duration);
}
