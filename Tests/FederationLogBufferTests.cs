using System;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// In-memory ring used by the dashboard Log tab. Pins snapshot filtering,
/// newest-capped paging, helper mappings, and the 800-line capacity trim.
/// </summary>
public sealed class FederationLogBufferTests
{
    [Fact]
    public void Snapshot_Limit_ReturnsNewestEntries()
    {
        var buffer = new FederationLogBuffer();
        for (var i = 1; i <= 10; i++)
        {
            buffer.Append(LogLevel.Information, "Jellyfin.Plugin.Federation.Services.Sync", $"msg-{i}", null);
        }

        var snap = buffer.Snapshot(null, 0, 3);

        Assert.Equal(3, snap.Count);
        Assert.Equal(new[] { 8L, 9L, 10L }, snap.Select(e => e.Id).ToArray());
        Assert.Equal(new[] { "msg-8", "msg-9", "msg-10" }, snap.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void Snapshot_MinLevelWarning_HidesInformation()
    {
        var buffer = new FederationLogBuffer();
        buffer.Append(LogLevel.Debug, "Federation", "dbg", null);
        buffer.Append(LogLevel.Information, "Federation", "info", null);
        buffer.Append(LogLevel.Warning, "Federation", "warn", null);
        buffer.Append(LogLevel.Error, "Federation", "err", null);

        var snap = buffer.Snapshot("Warning", 0, 200);

        Assert.Equal(2, snap.Count);
        Assert.DoesNotContain(snap, e => e.Level == "Information" || e.Level == "Debug");
        Assert.Equal(new[] { "Warning", "Error" }, snap.Select(e => e.Level).ToArray());
        Assert.Equal(new[] { "warn", "err" }, snap.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void Snapshot_AfterId_ReturnsOnlyNewerIds()
    {
        var buffer = new FederationLogBuffer();
        buffer.Append(LogLevel.Information, "Federation", "a", null);
        buffer.Append(LogLevel.Information, "Federation", "b", null);
        buffer.Append(LogLevel.Information, "Federation", "c", null);

        var all = buffer.Snapshot(null, 0, 10);
        var after = buffer.Snapshot(null, all[0].Id, 10);

        Assert.Equal(3, all.Count);
        Assert.Equal(2, after.Count);
        Assert.All(after, e => Assert.True(e.Id > all[0].Id));
        Assert.Equal(new[] { "b", "c" }, after.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void Clear_EmptiesBuffer()
    {
        var buffer = new FederationLogBuffer();
        buffer.Append(LogLevel.Warning, "Federation", "keep-me-not", null);
        buffer.Append(LogLevel.Error, "Federation", "gone", null);

        buffer.Clear();

        Assert.Empty(buffer.Snapshot(null, 0, 200));
        buffer.Append(LogLevel.Information, "Federation", "after-clear", null);
        var snap = buffer.Snapshot(null, 0, 200);
        Assert.Equal("after-clear", Assert.Single(snap).Message);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "Debug")]
    [InlineData(LogLevel.Debug, "Debug")]
    [InlineData(LogLevel.Information, "Information")]
    [InlineData(LogLevel.Warning, "Warning")]
    [InlineData(LogLevel.Error, "Error")]
    [InlineData(LogLevel.Critical, "Error")]
    [InlineData(LogLevel.None, "Information")]
    public void MapLevel_MapsKnownLogLevels(LogLevel level, string expected)
        => Assert.Equal(expected, FederationLogBuffer.MapLevel(level));

    [Theory]
    [InlineData(null, "Federation")]
    [InlineData("", "Federation")]
    [InlineData("Federation", "Federation")]
    [InlineData("Jellyfin.Plugin.Federation.Services.FederationSyncService", "FederationSyncService")]
    [InlineData("A.B.", "A.B.")]
    public void ShortCategory_ReturnsLastSegmentOrFederation(string? category, string expected)
        => Assert.Equal(expected, FederationLogBuffer.ShortCategory(category!));

    [Theory]
    [InlineData("Debug", 0)]
    [InlineData("Information", 1)]
    [InlineData("Warning", 2)]
    [InlineData("Error", 3)]
    [InlineData("unknown", 1)]
    public void Rank_OrdersLevels(string level, int expected)
        => Assert.Equal(expected, FederationLogBuffer.Rank(level));

    [Fact]
    public void Append_OverCapacity_TrimsOldest()
    {
        var buffer = new FederationLogBuffer();
        var extra = 5;
        var total = FederationLogBuffer.Capacity + extra;
        for (var i = 1; i <= total; i++)
        {
            buffer.Append(LogLevel.Information, "Federation", $"n-{i}", null);
        }

        var snap = buffer.Snapshot(null, 0, FederationLogBuffer.Capacity);

        Assert.Equal(FederationLogBuffer.Capacity, snap.Count);
        Assert.Equal(extra + 1, snap[0].Id);
        Assert.Equal($"n-{extra + 1}", snap[0].Message);
        Assert.Equal(total, snap[^1].Id);
        Assert.Equal($"n-{total}", snap[^1].Message);
        Assert.DoesNotContain(snap, e => e.Message == "n-1");
    }
}
