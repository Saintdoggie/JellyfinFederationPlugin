using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationBandwidthAllocatorTests
{
    private readonly FederationBandwidthAllocator _allocator = new();
    private DateTime _now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    public FederationBandwidthAllocatorTests() => _allocator.UtcNow = () => _now;

    [Fact]
    public void SessionsShareRemainingCapacity_AndRequestsForOnePlayDoNotDoubleCount()
    {
        var first = _allocator.Allocate("friend", "play-1", 20, 30);
        var duplicate = _allocator.Allocate("friend", "play-1", 25, 30);
        var second = _allocator.Allocate("friend", "play-2", 20, 30);
        var third = _allocator.Allocate("friend", "play-3", 5, 30);

        Assert.Equal(20, first.CapMbps);
        Assert.Equal(20, duplicate.CapMbps);
        Assert.Equal(10, second.CapMbps);
        Assert.False(third.Accepted);
        Assert.Equal(30, _allocator.ReservedMbps("friend"));
    }

    [Fact]
    public void StopReleasesCapacity()
    {
        _allocator.Allocate("friend", "play-1", 20, 30);
        _allocator.Release("friend", "play-1");
        Assert.Equal(0, _allocator.ReservedMbps("friend"));
        Assert.True(_allocator.Allocate("friend", "play-2", 20, 30).Accepted);
    }

    [Fact]
    public void StopWithoutHandoffReleasesEveryReservationForThatPlay()
    {
        _allocator.Allocate("friend-a", "play-1", 10, 20);
        _allocator.Allocate("friend-b", "play-1", 10, 20);
        _allocator.Allocate("friend-b", "play-2", 5, 20);
        _allocator.ReleaseEverywhere("play-1");
        Assert.Equal(0, _allocator.ReservedMbps("friend-a"));
        Assert.Equal(5, _allocator.ReservedMbps("friend-b"));
    }

    [Fact]
    public void IdleReservationExpires()
    {
        _allocator.Allocate("friend", "play-1", 20, 30);
        _now = _now.AddSeconds(91);
        Assert.Equal(0, _allocator.ReservedMbps("friend"));
        Assert.Equal(20, _allocator.Allocate("friend", "play-2", 20, 30).CapMbps);
    }

    [Fact]
    public void UncappedOrSessionlessPlaybackIsNotRejected()
    {
        Assert.True(_allocator.Allocate("friend", null, 20, 30).Accepted);
        Assert.True(_allocator.Allocate("friend", "play", null, 30).Accepted);
        Assert.True(_allocator.Allocate("friend", "play", 20, null).Accepted);
    }

    [Fact]
    public void CapacityIsIsolatedByFriend()
    {
        _allocator.Allocate("friend-a", "play", 20, 20);
        Assert.Equal(20, _allocator.Allocate("friend-b", "other", 20, 20).CapMbps);
    }
}
