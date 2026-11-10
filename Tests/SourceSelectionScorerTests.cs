using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class SourceSelectionScorerTests
{
    private static readonly ViewerDevice Tv4k = new(ViewerClientKind.Tv, 2160, null, true);
    private static readonly ViewerDevice Phone = new(ViewerClientKind.Handheld, 1080, null, null);
    private static readonly ViewerDevice Web1080Sdr = new(ViewerClientKind.Web, 1080, null, false);

    private static SourceCandidate Remux4k(string server = "vps", double? link = null, int priority = 0, bool convert = true, bool atSource = true)
        => new()
        {
            Key = server + ":4k", ServerId = server, ServerName = server, BitrateBps = 93_000_000, Height = 2160, Hdr = true,
            AudioChannels = 8, Priority = priority, LinkMbps = link, ConversionAvailable = convert, ConvertsAtSource = atSource
        };

    private static SourceCandidate Hd(string server = "friend", double? link = null, int priority = 1, long bitrate = 8_000_000, bool convert = true, bool atSource = true)
        => new()
        {
            Key = server + ":hd", ServerId = server, ServerName = server, BitrateBps = bitrate, Height = 1080,
            AudioChannels = 6, Priority = priority, LinkMbps = link, ConversionAvailable = convert, ConvertsAtSource = atSource
        };

    private static string Winner(SourceSelection selection) => selection.Chosen!.Candidate.Key;

    [Fact]
    public void EmptyAndSingleCandidateAreHandled()
    {
        Assert.Null(SourceSelectionScorer.Rank(Array.Empty<SourceCandidate>(), null).Chosen);
        var only = SourceSelectionScorer.Rank(new[] { Hd(link: 50) }, null);
        Assert.Equal("friend:hd", Winner(only));
        Assert.Contains("only copy", only.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(150, 500)]
    public void FastLinksAndA4kScreenGetThe4kCopy(double vpsLink, double friendLink)
    {
        var selection = SourceSelectionScorer.Rank(new[] { Hd(link: friendLink), Remux4k(link: vpsLink) }, Tv4k);
        Assert.Equal("vps:4k", Winner(selection));
        Assert.False(selection.Chosen!.NeedsConversion);
    }

    [Fact]
    public void ASlowLinkPrefersTheCopyThatFitsOverAConverted4k()
    {
        var hd = Hd(link: 6, bitrate: 4_000_000);
        var selection = SourceSelectionScorer.Rank(new[] { Remux4k(link: 6), hd }, Tv4k);
        Assert.Equal("friend:hd", Winner(selection));
        Assert.False(selection.Chosen!.NeedsConversion);
        Assert.Contains("93 Mbps", selection.Reason, StringComparison.Ordinal);
        Assert.Contains("usable", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMediumLinkStillGives4kToA4kScreenWhenTheOwnerConverts()
    {
        // 25 Mbps carries a 4K conversion; a 4 Mbps 1080p file is a clear step down
        // on a 4K TV, but on a phone the conversion buys nothing.
        var candidates = new[] { Remux4k(link: 25), Hd(link: 25, bitrate: 4_000_000) };
        var tv = SourceSelectionScorer.Rank(candidates, Tv4k);
        Assert.Equal("vps:4k", Winner(tv));
        Assert.True(tv.Chosen!.NeedsConversion);
        Assert.Equal(2160, tv.Chosen.DeliveredHeight);
        var phone = SourceSelectionScorer.Rank(candidates, Phone).Ranked.ToDictionary(r => r.Candidate.Key);
        Assert.True(phone["friend:hd"].Quality > phone["vps:4k"].Quality);
        Assert.True(phone["friend:hd"].Total > phone["vps:4k"].Total);
    }

    [Fact]
    public void A4kCopyNobodyMayConvertLosesToAnythingThatPlays()
    {
        // 93 Mbps over 6 Mbps with no conversion allowed would only buffer.
        var selection = SourceSelectionScorer.Rank(new[] { Remux4k(link: 6, convert: false), Hd(link: 6, bitrate: 8_000_000) }, Tv4k);
        Assert.Equal("friend:hd", Winner(selection));
        Assert.True(selection.Ranked[1].WillStruggle);
        Assert.True(selection.Ranked[1].Sustain < 0.1);
    }

    [Fact]
    public void ConvertingHereMeansTheOriginalStillCrossesTheLink()
    {
        var atSource = SourceSelectionScorer.Score(Remux4k(link: 10, atSource: true), Tv4k);
        var here = SourceSelectionScorer.Score(Remux4k(link: 10, atSource: false), Tv4k);
        Assert.True(atSource.Sustain > 0.99);
        Assert.True(here.Sustain < 0.2, "a 93 Mbps original cannot be pulled through a 10 Mbps link fast enough to convert");
        Assert.True(here.ProjectedStartSeconds > atSource.ProjectedStartSeconds);
        Assert.True(atSource.Total > here.Total);
    }

    [Fact]
    public void APhoneGetsNoRewardFor4k()
    {
        // Both fit the link easily. On a 1080p screen the 4K copy shows nothing more,
        // so the two tie and friend priority decides.
        var selection = SourceSelectionScorer.Rank(new[] { Remux4k(link: 500, priority: 5), Hd(link: 500, priority: 0, bitrate: 10_000_000) }, Phone);
        Assert.Equal("friend:hd", Winner(selection));

        var onTv = SourceSelectionScorer.Rank(new[] { Remux4k(link: 500, priority: 5), Hd(link: 500, priority: 0, bitrate: 10_000_000) }, Tv4k);
        Assert.Equal("vps:4k", Winner(onTv));
    }

    [Fact]
    public void ScreenLimitIsExplainedWhenItDecides()
    {
        var device = new ViewerDevice(ViewerClientKind.Web, 720, null, false);
        var hd720 = Hd(link: 500, bitrate: 5_000_000) with { Key = "friend:720", Height = 720, Priority = 0 };
        var selection = SourceSelectionScorer.Rank(new[] { Remux4k(link: 500, priority: 3), hd720 }, device);
        Assert.Equal("friend:720", Winner(selection));
        Assert.Contains("720p", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void HdrCountsOnlyOnAScreenKnownToShowIt()
    {
        var hdr = Hd("a", 500, 1, 12_000_000) with { Key = "a:hdr", Hdr = true };
        var sdr = Hd("b", 500, 1, 12_000_000) with { Key = "b:sdr" };
        Assert.True(SourceSelectionScorer.Score(hdr, Tv4k).Quality > SourceSelectionScorer.Score(sdr, Tv4k).Quality);
        Assert.True(SourceSelectionScorer.Score(hdr, Web1080Sdr).Quality < SourceSelectionScorer.Score(sdr, Web1080Sdr).Quality);
        Assert.Equal(SourceSelectionScorer.Score(hdr, ViewerDevice.Unknown).Quality, SourceSelectionScorer.Score(sdr, ViewerDevice.Unknown).Quality);
    }

    [Fact]
    public void UnmeasuredCopiesAreNeitherPromotedNorDemotedOnAGuess()
    {
        var unmeasured = SourceSelectionScorer.Score(Remux4k(link: null), Tv4k);
        Assert.Null(unmeasured.ProjectedStartSeconds);
        Assert.False(unmeasured.NeedsConversion);
        Assert.Equal(SourceSelectionScorer.NeutralScore, unmeasured.Sustain);

        // Unmeasured 4K against an unmeasured 1080p: picture decides, as before.
        var selection = SourceSelectionScorer.Rank(new[] { Hd(link: null), Remux4k(link: null) }, ViewerDevice.Unknown);
        Assert.Equal("vps:4k", Winner(selection));

        // A measured copy known to start slowly loses to an unmeasured one.
        var slow = SourceSelectionScorer.Rank(new[] { Remux4k(link: 8, convert: false), Hd(link: null) }, ViewerDevice.Unknown);
        Assert.Equal("friend:hd", Winner(slow));
    }

    [Fact]
    public void MissingStreamDataRanksBelowADescribedCopyThatFits()
    {
        var blank = new SourceCandidate { Key = "x:blank", ServerId = "x", ServerName = "x", LinkMbps = 100, ConversionAvailable = true };
        var selection = SourceSelectionScorer.Rank(new[] { blank, Hd(link: 100) }, ViewerDevice.Unknown);
        Assert.Equal("friend:hd", Winner(selection));
    }

    [Fact]
    public void DeviceRouteAndReservationsLowerWhatIsUsable()
    {
        var c = Hd(link: 100) with { RouteMbps = 10, ReservedMbps = 4 };
        Assert.Equal((10 - 4) * SourceSelectionScorer.UsableShare, SourceSelectionScorer.UsableMbps(c, ViewerDevice.Unknown)!.Value, 3);
        Assert.Equal(3, SourceSelectionScorer.UsableMbps(c with { CapMbps = 3 }, ViewerDevice.Unknown)!.Value, 3);
        Assert.Equal(2, SourceSelectionScorer.UsableMbps(c, new ViewerDevice(ViewerClientKind.Web, null, 2))!.Value, 3);
        Assert.Null(SourceSelectionScorer.UsableMbps(Hd(link: null), ViewerDevice.Unknown));
        Assert.Equal(0.5, SourceSelectionScorer.UsableMbps(Hd(link: 5) with { ReservedMbps = 50 }, ViewerDevice.Unknown)!.Value, 3);
    }

    [Fact]
    public void AStalledOrUnreliableServerLosesToAHealthyEqual()
    {
        var healthy = Hd("a", 50, 1) with { Key = "a" };
        var stalled = Hd("b", 50, 0) with { Key = "b", RecentlyStalled = true };
        var flaky = Hd("c", 50, 0) with { Key = "c", Reliability = 0.3 };
        Assert.Equal("a", Winner(SourceSelectionScorer.Rank(new[] { stalled, healthy }, null)));
        Assert.Equal("a", Winner(SourceSelectionScorer.Rank(new[] { flaky, healthy }, null)));
    }

    [Fact]
    public void FriendPriorityDecidesBetweenEquals()
    {
        var a = Hd("a", 80, 2) with { Key = "a" };
        var b = Hd("b", 80, 1) with { Key = "b" };
        Assert.Equal("b", Winner(SourceSelectionScorer.Rank(new[] { a, b }, null)));
        var selection = SourceSelectionScorer.Rank(new[] { a with { Priority = 0 }, b }, null);
        Assert.Equal("a", Winner(selection));
        Assert.Contains("priority", selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCurrentCopyIsKeptUnlessAChallengerIsClearlyBetter()
    {
        var current = Hd("a", 80, 1, 4_000_000) with { Key = "a" };
        var slightlyBetter = Hd("b", 80, 1, 8_000_000) with { Key = "b" };
        Assert.Equal("b", Winner(SourceSelectionScorer.Rank(new[] { current, slightlyBetter }, null)));

        var kept = SourceSelectionScorer.Rank(new[] { current, slightlyBetter }, null, "a");
        Assert.Equal("a", Winner(kept));
        Assert.True(kept.KeptCurrent);
        Assert.Contains("Staying", kept.Reason, StringComparison.Ordinal);

        // A clearly better copy replaces it, but not inside the minimum dwell.
        var muchBetter = Remux4k(link: 500);
        Assert.Equal("vps:4k", Winner(SourceSelectionScorer.Rank(new[] { current, muchBetter }, Tv4k, "a")));
        Assert.Equal("a", Winner(SourceSelectionScorer.Rank(new[] { current, muchBetter }, Tv4k, "a", mayLeaveCurrent: false)));

        // A current copy that is no longer a candidate (failed, revoked) is simply gone.
        Assert.Equal("b", Winner(SourceSelectionScorer.Rank(new[] { slightlyBetter }, null, "a", mayLeaveCurrent: false)));
    }

    [Fact]
    public void ChoiceDoesNotOscillateWhenSpeedSamplesWobble()
    {
        string? current = null;
        var changes = 0;
        var links = new[] { 20.0, 22, 18, 21, 19, 23, 17, 20, 22, 18, 21, 19 };
        for (var i = 0; i < links.Length; i++)
        {
            var a = Hd("a", links[i], 1, 12_000_000) with { Key = "a" };
            var b = Hd("b", links[links.Length - 1 - i], 1, 12_000_000) with { Key = "b" };
            var winner = Winner(SourceSelectionScorer.Rank(new[] { a, b }, null, current));
            if (current != null && winner != current)
            {
                changes++;
            }

            current = winner;
        }

        Assert.Equal(0, changes);
    }

    [Fact]
    public void ThreeServersPickTheBestThatPlaysSmoothly()
    {
        // 4K remux behind a slow link, a 1080p 20 Mbps copy on a medium link that
        // cannot carry it, and a 1080p 8 Mbps copy on a link that can.
        var selection = SourceSelectionScorer.Rank(
            new[]
            {
                Remux4k("vps", 8, 0, convert: false),
                Hd("big", 15, 1, 20_000_000, convert: false) with { Key = "big" },
                Hd("fit", 30, 2, 8_000_000, convert: false) with { Key = "fit" }
            },
            Tv4k);
        Assert.Equal("fit", Winner(selection));
        Assert.Equal(new[] { "fit", "big", "vps:4k" }, selection.Ranked.Select(s => s.Candidate.Key).ToArray());
    }

    [Fact]
    public void RankingIsDeterministicAndOrderIndependent()
    {
        var list = new List<SourceCandidate> { Remux4k(link: 40), Hd("a", 40, 1) with { Key = "a" }, Hd("b", 40, 1) with { Key = "b" } };
        var forward = SourceSelectionScorer.Rank(list, Tv4k).Ranked.Select(s => s.Candidate.Key).ToArray();
        list.Reverse();
        var backward = SourceSelectionScorer.Rank(list, Tv4k).Ranked.Select(s => s.Candidate.Key).ToArray();
        Assert.Equal(forward, backward);
    }

    [Theory]
    [InlineData(null, 0.85)]
    [InlineData(5.0, 1.0)]
    [InlineData(12.0, 1.0)]
    [InlineData(30.0, 0.5)]
    [InlineData(120.0, 0.0)]
    [InlineData(600.0, 0.0)]
    public void StartScoreFollowsTheStartBudget(double? seconds, double expected)
        => Assert.Equal(expected, SourceSelectionScorer.StartScore(seconds), 3);

    [Fact]
    public void ReasonsNeverContainAddressesOrIds()
    {
        var a = Remux4k(link: 6) with { ServerName = "Anna's server" };
        var b = Hd(link: 6, bitrate: 4_000_000) with { ServerName = "VPS" };
        var reason = SourceSelectionScorer.Rank(new[] { a, b }, Tv4k).Reason;
        Assert.DoesNotContain("http", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.Key, reason, StringComparison.Ordinal);
        Assert.Contains("VPS", reason, StringComparison.Ordinal);
    }
}

public sealed class SourceSelectionMemoryTests
{
    [Fact]
    public void RemembersTheChoiceWithDwellAndExpiry()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var memory = new SourceSelectionMemory { UtcNow = () => now };
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        Assert.Equal((null, true), memory.Current(user, "dev", item));
        memory.Remember(user, "dev", item, "a", "because");
        Assert.Equal(("a", false), memory.Current(user, " DEV ", item));
        Assert.Equal("because", memory.Reason(user, "dev", item));
        Assert.Equal((null, true), memory.Current(user, "other-device", item));
        Assert.Equal((null, true), memory.Current(Guid.NewGuid(), "dev", item));

        now += SourceSelectionMemory.MinimumDwell;
        Assert.Equal(("a", true), memory.Current(user, "dev", item));

        // Seeing the same choice again does not restart its dwell.
        memory.Remember(user, "dev", item, "a", "still");
        Assert.Equal(("a", true), memory.Current(user, "dev", item));
        memory.Remember(user, "dev", item, "b", "switched");
        Assert.Equal(("b", false), memory.Current(user, "dev", item));

        now += SourceSelectionMemory.RememberFor + TimeSpan.FromSeconds(1);
        Assert.Equal((null, true), memory.Current(user, "dev", item));
        Assert.Null(memory.Reason(user, "dev", item));
    }

    [Fact]
    public void FailuresBackOffAndExpire()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var memory = new SourceSelectionMemory { UtcNow = () => now };
        Assert.False(memory.RecentlyFailed("s1"));
        memory.NoteFailure("s1");
        memory.NoteFailure(" ");
        Assert.True(memory.RecentlyFailed("S1"));
        now += SourceSelectionMemory.FailureBackOff + TimeSpan.FromSeconds(1);
        Assert.False(memory.RecentlyFailed("s1"));
    }

    [Fact]
    public void StaysBounded()
    {
        var memory = new SourceSelectionMemory();
        var user = Guid.NewGuid();
        for (var i = 0; i < 3000; i++)
        {
            memory.Remember(user, "d", Guid.NewGuid(), "k", "r");
            memory.NoteFailure("s" + i);
        }

        memory.Reset();
        Assert.Equal((null, true), memory.Current(user, "d", Guid.NewGuid()));
    }
}
