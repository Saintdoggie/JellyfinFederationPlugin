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
/// With "pick the best copy automatically" on, the one selector every playback path
/// shares ranks copies with <see cref="SourceSelectionScorer"/>; with it off, the
/// previous priority-then-start-budget rule is untouched.
/// </summary>
[Collection("PluginInstance")]
public class SmartSourceSelectionTests : IDisposable
{
    private readonly WanBandwidthMonitor _monitor = new(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
    private readonly PluginConfiguration _config = new() { SmartSourceSelection = true };

    public SmartSourceSelectionTests()
    {
        FederationItemPersistenceService.BandwidthOverride = _monitor;
        FederationItemPersistenceService.SmartSelectionMemory.Reset();
        FederationItemPersistenceService.SmartSelectionMemory.UtcNow = () => DateTime.UtcNow;
    }

    public void Dispose()
    {
        FederationItemPersistenceService.BandwidthOverride = null;
        FederationItemPersistenceService.SmartSelectionMemory.Reset();
        FederationItemPersistenceService.SmartSelectionMemory.UtcNow = () => DateTime.UtcNow;
    }

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

    // Sources in priority order: (serverId, bitrate in Mbps, height).
    private static FederatedCacheEntry Entry(string key, params (string Server, double Mbps, int Height)[] sources)
    {
        var entry = new FederatedCacheEntry { Key = key };
        var priority = 0;
        foreach (var (server, mbps, height) in sources)
        {
            var remoteId = Guid.NewGuid();
            entry.AddSource(server, remoteId, priority);
            entry.UpdateFromRemote(
                new BaseItemDto
                {
                    Name = "Movie",
                    MediaSources = new[]
                    {
                        new MediaSourceInfo
                        {
                            Bitrate = (int)(mbps * 1_000_000),
                            MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Height = height, Width = height * 16 / 9 } }
                        }
                    },
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
    public void SwitchIsOffByDefault_AndOffKeepsPriorityOrder()
    {
        Assert.False(new PluginConfiguration().SmartSourceSelection);
        Assert.False(new PluginConfiguration().AdaptiveQualityLadder);

        Server("hd", 500);
        Server("uhd", 500);
        var entry = Entry("off", ("hd", 8, 1080), ("uhd", 60, 2160));
        _config.SmartSourceSelection = false;
        Assert.Equal("hd", Pick(entry));
        _config.SmartSourceSelection = true;
        Assert.Equal("uhd", Pick(entry));
    }

    [Fact]
    public void FastLinks_PreferTheBetterPictureOverPriority()
    {
        Server("hd", 300);
        Server("uhd", 300);
        Assert.Equal("uhd", Pick(Entry("a", ("hd", 8, 1080), ("uhd", 60, 2160))));
    }

    [Fact]
    public void SlowLinkToTheBigCopy_PlaysTheOneThatFits()
    {
        // Relayed through this server, the 93 Mbps original has to cross an 8 Mbps link.
        Server("uhd", 8);
        Server("hd", 40);
        Assert.Equal("hd", Pick(Entry("b", ("uhd", 93, 2160), ("hd", 8, 1080))));
    }

    [Fact]
    public void DirectFriendThatConvertsItsOwnCopy_IsNotPenalisedLikeARelay()
    {
        // Same numbers, but the friend shrinks its own file before it crosses the link.
        Server("uhd", 30, StreamingMode.Direct);
        Server("hd", 30, StreamingMode.Direct);
        Assert.Equal("uhd", Pick(Entry("c", ("hd", 4, 1080), ("uhd", 93, 2160))));
    }

    [Fact]
    public void EqualCopies_FollowFriendPriority()
    {
        Server("first", 100);
        Server("second", 100);
        Assert.Equal("first", Pick(Entry("d", ("first", 8, 1080), ("second", 8, 1080))));
    }

    [Fact]
    public void OfflineAndDisabledServersAreNeverChosen()
    {
        Server("uhd", 300);
        var hd = Server("hd", 300);
        var entry = Entry("e", ("hd", 8, 1080), ("uhd", 60, 2160));
        Assert.Equal("hd", Pick(entry, "uhd"));
        hd.Enabled = false;
        Assert.Null(Pick(entry, "uhd"));
        Assert.Equal("uhd", Pick(entry));
    }

    [Fact]
    public void SingleCopyNeedsNoScoring()
    {
        Server("only", null);
        Assert.Equal("only", Pick(Entry("f", ("only", 93, 2160))));
    }

    [Fact]
    public void ChoiceIsStickyUntilClearlyBeatenAndTheDwellHasPassed()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        FederationItemPersistenceService.SmartSelectionMemory.UtcNow = () => now;
        Server("a", 40);
        Server("b", 40);
        var entry = Entry("g", ("a", 8, 1080), ("b", 8, 1080));
        Assert.Equal("a", Pick(entry));

        // b becomes a much better copy: not yet (dwell), then yes.
        var sources = entry.GetSourcesSnapshot();
        sources[1].Bitrate = 20_000_000;
        sources[1].MediaStreams = new[] { new MediaStream { Type = MediaStreamType.Video, Height = 2160, Width = 3840 } };
        Assert.Equal("a", Pick(entry));
        now += SourceSelectionMemory.MinimumDwell;
        Assert.Equal("b", Pick(entry));

        // A small wobble back does not flip it again.
        now += SourceSelectionMemory.MinimumDwell;
        sources[1].Bitrate = 18_000_000;
        Assert.Equal("b", Pick(entry));

        // The chosen server going offline fails over at once, dwell or not.
        Assert.Equal("a", Pick(entry, "b"));
    }

    [Fact]
    public void EachTitleKeepsItsOwnMemory()
    {
        Server("a", 40);
        Server("b", 40);
        Assert.Equal("a", Pick(Entry("h1", ("a", 8, 1080), ("b", 8, 1080))));
        Assert.Equal("b", Pick(Entry("h2", ("a", 8, 1080), ("b", 30, 2160))));
    }

    [Fact]
    public void PlaybackInfoExplainsTheChoice_AndStaysQuietWhenOffOrSingle()
    {
        var uhdServer = Server("Big", 8);
        var hdServer = Server("Small", 40);
        var entry = Entry("i", ("Big", 93, 2160), ("Small", 8, 1080));
        var sources = entry.GetSourcesSnapshot();
        RemoteServer? Lookup(string id) => _config.RemoteServers.FirstOrDefault(s => s.Id == id);

        var why = SourceCandidateFactory.ExplainPlayed(entry, sources[1], Lookup, _ => false, _monitor, _config, ViewerDevice.Unknown);
        Assert.NotNull(why);
        Assert.Contains("93 Mbps", why, StringComparison.Ordinal);
        Assert.Contains("Small", why, StringComparison.Ordinal);
        Assert.DoesNotContain("example", why, StringComparison.Ordinal);

        // Playing the copy that does not rank first says so, and says how to change it.
        var other = SourceCandidateFactory.ExplainPlayed(entry, sources[0], Lookup, _ => false, _monitor, _config, ViewerDevice.Unknown);
        Assert.Contains("ranks higher", other, StringComparison.Ordinal);

        Assert.Null(SourceCandidateFactory.ExplainPlayed(entry, sources[1], Lookup, id => id == "Big", _monitor, _config, ViewerDevice.Unknown));
        Assert.Null(SourceCandidateFactory.ExplainPlayed(entry, sources[1], Lookup, _ => false, _monitor, new PluginConfiguration(), ViewerDevice.Unknown));
        Assert.NotNull(uhdServer);
        Assert.NotNull(hdServer);
    }

    [Theory]
    [InlineData("Jellyfin Android", "Pixel 9", false, ViewerClientKind.Handheld, 1080)]
    [InlineData("Swiftfin iOS", "iPhone", false, ViewerClientKind.Handheld, 1080)]
    [InlineData("Jellyfin Android TV", "SHIELD", false, ViewerClientKind.Tv, null)]
    [InlineData("Jellyfin Web", "Chrome", true, ViewerClientKind.Web, null)]
    [InlineData("Something", "Box", false, ViewerClientKind.Unknown, null)]
    [InlineData(null, null, false, ViewerClientKind.Unknown, null)]
    public void DeviceIsOnlyDescribedAsFarAsItIsKnown(string? client, string? device, bool browser, object kind, int? maxHeight)
    {
        var described = SourceCandidateFactory.Device(client, device, browser);
        Assert.Equal((ViewerClientKind)kind, described.Kind);
        Assert.Equal(maxHeight, described.MaxHeight);
        Assert.Null(described.SupportsHdr);
        Assert.Null(described.MaxBitrateMbps);
    }

    [Fact]
    public void PlayedSourceNamesTheCopyTheSelectorPlays()
    {
        Server("first", 8);
        Server("gig", 900);
        var entry = Entry("j", ("first", 93, 2160), ("gig", 83, 2160));
        var itemId = Guid.NewGuid();
        var chosen = FederationItemPersistenceService.FirstEnabledSource(entry, _config);
        Assert.Equal("gig", chosen!.ServerId);
        Assert.Equal("gig", FederationViewerReporter.PlayedSource(entry, itemId, itemId.ToString("N"), chosen)!.ServerId);
        Assert.Equal("gig", FederationViewerReporter.PlayedSource(entry, itemId, null, chosen)!.ServerId);
        Assert.Equal("first", FederationViewerReporter.PlayedSource(entry, itemId, null)!.ServerId);
        var explicitId = FederationMediaSourceProvider.BuildSourceId(entry.GetSourcesSnapshot()[0]);
        Assert.Equal("first", FederationViewerReporter.PlayedSource(entry, itemId, explicitId, chosen)!.ServerId);
    }
}
