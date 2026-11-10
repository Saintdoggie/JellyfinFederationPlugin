using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FirstSegmentTimingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Clock UseClock()
    {
        var clock = new Clock();
        FirstSegmentTiming.Clock = clock;
        return clock;
    }

    [Fact]
    public void ReportsTheGapOnlyOncePerPlay()
    {
        var clock = UseClock();
        var session = Guid.NewGuid().ToString("N");
        FirstSegmentTiming.RecordPlaylist(session);
        clock.Now = clock.Now.AddMilliseconds(1234);
        Assert.Equal(1234, FirstSegmentTiming.CompleteSegment(session));
        Assert.Null(FirstSegmentTiming.CompleteSegment(session));
        FirstSegmentTiming.Clock = TimeProvider.System;
    }

    [Fact]
    public void IgnoresMissingIdsAndUnknownSessions()
    {
        UseClock();
        Assert.Null(FirstSegmentTiming.CompleteSegment(null));
        Assert.Null(FirstSegmentTiming.CompleteSegment(string.Empty));
        Assert.Null(FirstSegmentTiming.CompleteSegment(Guid.NewGuid().ToString("N")));
        FirstSegmentTiming.Clock = TimeProvider.System;
    }

    [Fact]
    public void AbandonedPlaysExpireInsteadOfAccumulating()
    {
        var clock = UseClock();
        var session = Guid.NewGuid().ToString("N");
        FirstSegmentTiming.RecordPlaylist(session);
        clock.Now = clock.Now.AddMinutes(11);
        // A later play triggers the sweep; the abandoned one must not report a
        // segment time long after its playlist was forgotten.
        FirstSegmentTiming.RecordPlaylist(Guid.NewGuid().ToString("N"));
        Assert.Null(FirstSegmentTiming.CompleteSegment(session));
        FirstSegmentTiming.Clock = TimeProvider.System;
    }
}
