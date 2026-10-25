using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// A title on several friends' servers should play from the one that starts soonest
/// when the first-priority copy would blow the start budget, and otherwise stay in the
/// owner's priority order.
/// </summary>
[Collection("PluginInstance")]
public class FasterSourceTests : IDisposable
{
    private readonly WanBandwidthMonitor _monitor = new(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
    private readonly PluginConfiguration _config = new();

    public FasterSourceTests()
    {
        FederationItemPersistenceService.BandwidthOverride = _monitor;
    }

    public void Dispose() => FederationItemPersistenceService.BandwidthOverride = null;

    private RemoteServer Server(string id, double? linkMbps, StreamingMode mode = StreamingMode.Proxy)
    {
        var server = new RemoteServer { Id = id, Name = id, Enabled = true, Url = "http://" + id + ".example", StreamingMode = mode, WanCapMode = WanCapMode.Off };
        _config.RemoteServers.Add(server);
        if (linkMbps.HasValue)
        {
            _monitor.SeedLinkForTests(id, linkMbps.Value);
        }

        return server;
    }

    // Sources in priority order: (serverId, bitrate in Mbps).
    private static FederatedCacheEntry Entry(params (string Server, double Mbps)[] sources)
    {
        var entry = new FederatedCacheEntry();
        var priority = 0;
        foreach (var (server, mbps) in sources)
        {
            var remoteId = Guid.NewGuid();
            entry.AddSource(server, remoteId, priority);
            entry.UpdateFromRemote(
                new BaseItemDto
                {
                    Name = "Movie",
                    MediaSources = new[] { new MediaSourceInfo { Bitrate = (int)(mbps * 1_000_000) } },
                },
                server,
                remoteId,
                priority);
            priority++;
        }

        return entry;
    }

    private string? Pick(FederatedCacheEntry entry, params string[] offline)
        => FederationItemPersistenceService.FirstEnabledSource(entry, _config, offline.Length == 0 ? null : new HashSet<string>(offline))?.ServerId;

    [Fact]
    public void SlowFirstChoice_LosesToAFastAlternative()
    {
        // plex-source (8 Mbps link) holding a 93 Mbps remux vs a gigabit friend: ~74 s vs ~5 s.
        Server("plex-source", 8);
        Server("gig", 900);

        Assert.Equal("gig", Pick(Entry(("plex-source", 93), ("gig", 83))));
    }

    [Fact]
    public void MissingAggregateBitrate_FallsBackToPositiveStreamBitrateTotal()
    {
        Server("plex-source", 8);
        Server("gig", 900);
        var entry = Entry(("plex-source", 93), ("gig", 83));
        var sources = entry.GetSourcesSnapshot();
        sources[0].Bitrate = 0;
        sources[0].MediaStreams = new[]
        {
            new MediaStream { BitRate = 90_000_000 },
            new MediaStream { BitRate = 3_000_000 },
            new MediaStream { BitRate = -93_000_000 },
        };
        sources[1].Bitrate = null;
        sources[1].MediaStreams = new[]
        {
            new MediaStream { BitRate = 80_000_000 },
            new MediaStream { BitRate = 3_000_000 },
        };

        Assert.Equal("gig", Pick(entry));
    }

    [Fact]
    public void FirstChoiceThatStartsWithinTheBudget_StaysInPriorityOrder()
    {
        // 12.7 Mbps over 7.6 Mbps is ~14 s: fine, even though the other copy is faster.
        Server("plex-source", 7.6);
        Server("gig", 900);

        Assert.Equal("plex-source", Pick(Entry(("plex-source", 12.7), ("gig", 83))));
    }

    [Fact]
    public void UnmeasuredAlternative_IsNeverPreferredOnAGuess()
    {
        Server("plex-source", 8);
        Server("unknown", null);

        Assert.Equal("plex-source", Pick(Entry(("plex-source", 93), ("unknown", 20))));
    }

    [Fact]
    public void UnmeasuredFirstChoice_IsNeverDemoted()
    {
        Server("unknown", null);
        Server("gig", 900);

        Assert.Equal("unknown", Pick(Entry(("unknown", 93), ("gig", 83))));
    }

    [Fact]
    public void NearTheBudget_DoesNotFlipBetweenServers()
    {
        // ~31 s vs ~29 s: not enough of an improvement to switch.
        Server("a", 20.7);   // 6 x 93 / 20.7 + 4 = ~31
        Server("b", 21.6);   // 6 x 93 / 21.6 + 4 = ~29.8

        Assert.Equal("a", Pick(Entry(("a", 93), ("b", 93))));
    }

    [Fact]
    public void WhenEverySourceIsSlow_TheClearlyQuickestWins()
    {
        Server("slow", 4);      // ~143 s
        Server("less", 10);     // ~60 s

        Assert.Equal("less", Pick(Entry(("slow", 93), ("less", 93))));
    }

    [Fact]
    public void WhenEverySourceIsSlow_ButNoneClearlyQuicker_PriorityOrderStands()
    {
        Server("a", 4);
        Server("b", 4.4);

        Assert.Equal("a", Pick(Entry(("a", 93), ("b", 93))));
    }

    [Fact]
    public void OfflineSources_AreNeverChosen_EvenIfTheyWouldBeFastest()
    {
        Server("plex-source", 8);
        Server("gig", 900);

        Assert.Equal("plex-source", Pick(Entry(("plex-source", 93), ("gig", 83)), "gig"));
    }

    [Fact]
    public void DisabledSources_AreNeverChosen()
    {
        Server("plex-source", 8);
        Server("gig", 900).Enabled = false;

        Assert.Equal("plex-source", Pick(Entry(("plex-source", 93), ("gig", 83))));
    }

    [Fact]
    public void WithoutAMonitor_PriorityOrderIsUntouched()
    {
        Server("plex-source", 8);
        Server("gig", 900);
        FederationItemPersistenceService.BandwidthOverride = null;

        Assert.Equal("plex-source", Pick(Entry(("plex-source", 93), ("gig", 83))));
    }

    [Fact]
    public void SingleSource_IsReturnedAsIs()
    {
        Server("plex-source", 1);
        Assert.Equal("plex-source", Pick(Entry(("plex-source", 93))));
    }

    [Fact]
    public void NoPlayableSource_IsNull()
    {
        Server("a", 8).Enabled = false;
        Assert.Null(Pick(Entry(("a", 93))));
    }

    // ---- the projection itself ----

    [Fact]
    public void ProjectedStart_NeedsBothABitrateAndAMeasuredLink()
    {
        var server = Server("s", null);
        Assert.Null(_monitor.ProjectedStartSeconds(server, 93_000_000));

        _monitor.SeedLinkForTests("s", 8);
        Assert.Null(_monitor.ProjectedStartSeconds(server, null));
        Assert.InRange(_monitor.ProjectedStartSeconds(server, 93_000_000)!.Value, 73, 76);
    }

    [Fact]
    public void ProjectedStart_ForADirectModePeerUsesTheCapNotTheFile()
    {
        // slow-peer: Direct mode, Auto cap, ~2 Mbps link. The peer sends a capped stream,
        // so a 90 Mbps file must not be projected as if it were all crossing the link.
        var server = Server("slow-peer", null, StreamingMode.Direct);
        server.WanCapMode = WanCapMode.Manual;
        server.WanMaxBitrateMbps = 10;
        _monitor.SeedLinkForTests("slow-peer", 20);

        Assert.Equal(10_000_000, _monitor.EffectiveSourceBitrate(server, 90_000_000));
        Assert.Equal(10_000_000, _monitor.EffectiveSourceBitrate(server, null));
        Assert.Equal(5_000_000, _monitor.EffectiveSourceBitrate(server, 5_000_000));
        Assert.InRange(_monitor.ProjectedStartSeconds(server, 90_000_000)!.Value, 6.9, 7.1);
    }

    [Fact]
    public void ProjectedSeconds_AgreesWithTheEstimateShownToViewers()
    {
        var projected = StartEstimator.ProjectedSeconds(93_243_000, 7.5)!.Value;
        Assert.Equal((int)Math.Round(projected), StartEstimator.Estimate("x", 93_243_000, 7.5).Seconds);
        Assert.Null(StartEstimator.ProjectedSeconds(null, 7.5));
        Assert.Null(StartEstimator.ProjectedSeconds(93_000_000, null));
        Assert.Null(StartEstimator.ProjectedSeconds(93_000_000, 0));
    }
}
