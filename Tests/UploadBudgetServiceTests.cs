using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers the pure math in <see cref="UploadBudgetService.ComputePerStreamMbps"/> -
/// dividing this server's own upload capacity across however many playback sessions
/// are currently active, so a friend's request for a bitrate the sending server
/// doesn't actually have spare capacity for gets capped down front instead of
/// stuttering mid-playback. See the "same file plays fine from my own server but
/// stutters from a friend's" investigation this plugin's WAN-cap logic already fixed
/// for the receiving side - this is the mirror case, protecting the sending side.
/// </summary>
public class UploadBudgetServiceTests
{
    [Fact]
    public void NoCapacityConfigured_ReturnsZero()
    {
        Assert.Equal(0, UploadBudgetService.ComputePerStreamMbps(0, 3));
    }

    [Fact]
    public void SingleViewer_GetsCapacityMinusSafetyMargin()
    {
        // 30 Mbps * 0.85 safety margin = 25.5 -> rounds to 26.
        Assert.Equal(26, UploadBudgetService.ComputePerStreamMbps(30, 1));
    }

    [Fact]
    public void NoActiveViewers_TreatedAsOne_NotZero()
    {
        // Dividing by zero active sessions must not throw or return an unbounded
        // value - falls back to the same result as exactly one viewer.
        Assert.Equal(26, UploadBudgetService.ComputePerStreamMbps(30, 0));
    }

    [Fact]
    public void ThreeConcurrentViewers_SplitsTheBudgetBetweenThem()
    {
        // 30 Mbps * 0.85 / 3 = 8.5 -> banker's rounding (Math.Round's default) rounds
        // a .5 tie to the nearest even number, 8 - this is the user's own example
        // (30 Mbps upload, don't commit more than that across everyone watching).
        Assert.Equal(8, UploadBudgetService.ComputePerStreamMbps(30, 3));
    }

    [Fact]
    public void ManyConcurrentViewers_ClampsToTheConfiguredFloor()
    {
        // 30 Mbps split 20 ways would compute under the 2 Mbps floor - clamped up
        // instead of handing out an unusably low per-stream limit.
        Assert.Equal(2, UploadBudgetService.ComputePerStreamMbps(30, 20));
    }

    [Fact]
    public void NeverExceedsTheConfiguredCapacityItself()
    {
        // A single viewer's margin-adjusted share can't exceed the raw capacity
        // number, even for a very small configured value.
        Assert.Equal(3, UploadBudgetService.ComputePerStreamMbps(3, 1));
    }
}
