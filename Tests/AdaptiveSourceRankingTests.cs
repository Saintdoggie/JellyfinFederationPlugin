using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;
public sealed class AdaptiveSourceRankingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void ReliableLowerQualityBeatsFlaky4KWithoutExcludingIt()
    {
        var ranking = new AdaptiveSourceRanking();
        var good = new RemoteServer { Id = "good" }; var bad = new RemoteServer { Id = "flaky" };
        for (var i = 0; i < 4; i++) { ranking.ObserveProbe(good.Id, true, 25); ranking.ObserveProbe(bad.Id, false); }
        var lower = ranking.Score(good, 8_000_000, 1080, null);
        var higher = ranking.Score(bad, 40_000_000, 2160, null);
        Assert.True(lower > higher); Assert.True(higher > 0);
    }
    [Fact]
    public void ThroughputHeadroomBeatsUnsustainableResolution()
    {
        var ranking = new AdaptiveSourceRanking();
        var first = new RemoteServer { Id = "a" }; var second = new RemoteServer { Id = "b" };
        ranking.ObserveTransfer("a", 2_000_000, TimeSpan.FromSeconds(2));
        ranking.ObserveTransfer("b", 20_000_000, TimeSpan.FromSeconds(2));
        Assert.True(ranking.Score(second, 8_000_000, 1080, null) > ranking.Score(first, 40_000_000, 2160, null));
    }
    [Fact]
    public void ManualCapRemainsCeilingEvenWithFastMeasuredLink()
    {
        var ranking = new AdaptiveSourceRanking(); var server = new RemoteServer { Id = "a" };
        ranking.ObserveTransfer("a", 100_000_000, TimeSpan.FromSeconds(1));
        Assert.True(ranking.Score(server, 40_000_000, 2160, 5) < ranking.Score(server, 8_000_000, 1080, 5));
    }
    [Fact]
    public void StaleMeasurementsRecoverAndUnknownBitrateIsStillEligible()
    {
        var clock = new Clock(); var ranking = new AdaptiveSourceRanking(clock); var server = new RemoteServer { Id = "a" };
        var baseline = ranking.Score(server, 40_000_000, 2160, null);
        ranking.ObserveProbe("a", false); ranking.ObserveTransfer("a", 1_048_576, TimeSpan.FromSeconds(10));
        Assert.True(ranking.Score(server, 40_000_000, 2160, null) < baseline);
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(baseline, ranking.Score(server, 40_000_000, 2160, null));
        Assert.InRange(ranking.Score(server, 0, 0, null), 0.01, 100);
    }
    [Fact]
    public void TinyOrInvalidSamplesDoNotChangeScores()
    {
        var ranking = new AdaptiveSourceRanking(); var server = new RemoteServer { Id = "a" };
        var before = ranking.Score(server, 10_000_000, 1080, null);
        ranking.ObserveTransfer("a", 1000, TimeSpan.FromSeconds(1));
        ranking.ObserveTransfer("a", long.MaxValue, TimeSpan.FromMilliseconds(0.1));
        ranking.ObserveTransfer("a", -100, TimeSpan.FromSeconds(2));
        Assert.Equal(before, ranking.Score(server, 10_000_000, 1080, null));
    }
    [Fact]
    public async Task SameOutageWithManyViewersCountsOnce()
    {
        var ranking = new AdaptiveSourceRanking(); var reference = new AdaptiveSourceRanking(); var server = new RemoteServer { Id = "a" };
        reference.ObserveStall("a");
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(() => ranking.ObserveStall("a"))));
        Assert.Equal(reference.Score(server, 10_000_000, 1080, null), ranking.Score(server, 10_000_000, 1080, null));
    }
    [Fact]
    public void ForgetRemovesHistory()
    {
        var ranking = new AdaptiveSourceRanking(); var server = new RemoteServer { Id = "a" };
        var before = ranking.Score(server, 10_000_000, 1080, null); ranking.ObserveProbe("a", false); ranking.Forget("a");
        Assert.Equal(before, ranking.Score(server, 10_000_000, 1080, null));
    }
    [Fact]
    public void PreparationHasPerUserAndGlobalLimitsAndCannotReleaseAnotherUsersLease()
    {
        var gate = new AdaptivePreparationGate(); var user = Guid.NewGuid(); var lease = gate.Acquire(user)!.Value;
        Assert.Null(gate.Acquire(user)); gate.Release(Guid.NewGuid(), lease); Assert.Null(gate.Acquire(user));
        for (var i = 0; i < 3; i++) Assert.NotNull(gate.Acquire(Guid.NewGuid()));
        Assert.Null(gate.Acquire(Guid.NewGuid())); gate.Release(user, lease); Assert.NotNull(gate.Acquire(Guid.NewGuid()));
    }
    [Fact]
    public void PreparationExpiresEvenWhenClientDisappears()
    {
        var clock = new Clock(); var gate = new AdaptivePreparationGate(clock); var user = Guid.NewGuid();
        Assert.NotNull(gate.Acquire(user)); clock.Now += TimeSpan.FromSeconds(26); Assert.NotNull(gate.Acquire(user));
    }
    [Fact]
    public void AdaptiveScriptLoadsBeforeWebAndMigratesIdempotently()
    {
        var html = "<html><head><script defer src='main.js'></script></head><body>" + FederationClientScriptHtml.Deferred + "</body></html>";
        var injected = FederationClientScriptHtml.Inject(html, true);
        Assert.True(injected.IndexOf(FederationClientScriptHtml.Early, StringComparison.Ordinal) < injected.IndexOf("main.js", StringComparison.Ordinal));
        Assert.Equal(injected, FederationClientScriptHtml.Inject(injected, true));
        var reverted = FederationClientScriptHtml.Inject(injected, false);
        Assert.Contains(FederationClientScriptHtml.Deferred, reverted);
        Assert.DoesNotContain(FederationClientScriptHtml.Early, reverted);
        Assert.Equal(reverted, FederationClientScriptHtml.Inject(reverted, false));
    }
}
