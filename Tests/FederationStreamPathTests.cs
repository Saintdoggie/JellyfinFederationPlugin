using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Regression tests for the "federated items show up but won't play" bug.
///
/// Federated items used to be created with Path left null. Jellyfin builds an item's
/// static media source from Path in BaseItem.GetVersionInfo, and with no path it
/// stamps that source Type = MediaSourceType.Placeholder with no path, container or
/// streams. Two things then go wrong at once: the client is handed a source that is
/// by definition unplayable, and MediaSourceManager.GetPlaybackMediaSources skips its
/// EnableRemoteContentProbe pass - the guard is literally
/// `mediaSources[0].Type != MediaSourceType.Placeholder` - so the codecs that would
/// have made it playable are never discovered either.
///
/// Stamping the remote stream URL on Path is what makes the static source come out as
/// a real Http source instead, so these assertions are the fix.
/// </summary>
[Collection("PluginInstance")]
public class FederationStreamPathTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationItemCache _cache;
    private readonly FederationLibraryManager _manager;
    private readonly WanBandwidthMonitor _bandwidthMonitor;

    public FederationStreamPathTests()
    {
        _plugin = new RealPluginInstance();
        _cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);

        // BaseItem.LocationType calls the static BaseItem.FileSystem, which only the
        // real server populates. Mirrors Jellyfin's own IsPathFile: anything carrying a
        // non-file:// URI scheme is not a file.
        var fileSystem = new Mock<MediaBrowser.Model.IO.IFileSystem>();
        fileSystem.Setup(f => f.IsPathFile(It.IsAny<string>()))
            .Returns((string p) => !(p.Contains("://", StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith("file://", StringComparison.OrdinalIgnoreCase)));
        MediaBrowser.Controller.Entities.BaseItem.FileSystem = fileSystem.Object;

        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(path + "|" + type.FullName))));

        _bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Mock.Of<IRemoteServerClientFactory>());
        _manager = new FederationLibraryManager(
            lm.Object,
            NullLogger<FederationLibraryManager>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            _cache,
            _bandwidthMonitor);
    }

    public void Dispose() => _plugin.Dispose();

    private RemoteServer AddServer(StreamingMode mode = StreamingMode.Direct)
    {
        var server = new RemoteServer
        {
            Id = "serverA",
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true,
            StreamingMode = mode
        };
        _plugin.Configuration.RemoteServers.Add(server);
        return server;
    }

    private FederatedCacheEntry AddEntry(string itemType, Guid remoteId, string? container = null)
    {
        _cache.UpsertRaw(
            "Movies",
            "serverA",
            remoteId,
            new BaseItemDto
            {
                Id = remoteId,
                Name = "Gran Turismo",
                Type = Jellyfin.Data.Enums.BaseItemKind.Movie,
                Container = container
            },
            0,
            itemType);

        return _cache.GetEntriesForMapping("Movies").First();
    }

    [Fact]
    public void Movie_DirectMode_GetsRemoteStreamUrlAsPath_SoTheSourceIsNotAPlaceholder()
    {
        AddServer();
        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        Assert.False(string.IsNullOrEmpty(item.Path));
        Assert.Equal(
            $"http://friend.example:8096/Videos/{remoteId:N}/stream?api_key=secret-key&Static=true",
            item.Path);

        // IsShortcut is deliberately never set: ProbeProvider.FetchShortcutInfo
        // unconditionally does File.ReadAllLines(item.Path), expecting Path to be a
        // real local .strm file rather than the URL itself, which throws on every
        // metadata refresh (see the comment in FederationLibraryManager.MaterializeItem).
        Assert.False(item.IsShortcut);
    }

    [Fact]
    public void Movie_WithPath_ResolvesLocationTypeRemote()
    {
        AddServer();
        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        // An http path resolves to Remote from BaseItem's own logic. A null path
        // resolves to Virtual instead, which is what made the web client paint
        // federated episodes as "Missing".
        Assert.Equal(LocationType.Remote, item.LocationType);
    }

    [Fact]
    public void Movie_ProxyMode_PointsAtThisServersOwnProxyEndpoint_AndIsNotMarkedAShortcut()
    {
        AddServer(StreamingMode.Proxy);
        _plugin.Configuration.ServerUrl = "https://my-server.example";

        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        Assert.Equal(
            $"https://my-server.example/Plugins/Federation/Stream?serverId=serverA&itemId={remoteId:N}",
            item.Path);

        // The remote api_key must never reach a client in Proxy mode.
        Assert.DoesNotContain("secret-key", item.Path);

        // Proxy URLs point back at this very server, so the source must not claim to
        // be remote - clients without remote-video support would refuse it.
        Assert.False(item.IsShortcut);
    }

    [Fact]
    public void Movie_ProxyModeWithNoConfiguredServerUrl_DegradesToNoPath_RatherThanABrokenOne()
    {
        AddServer(StreamingMode.Proxy);
        _plugin.Configuration.ServerUrl = string.Empty;

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        // A background sync has no incoming request to infer this server's own URL
        // from. Emitting a relative or guessed URL would be worse than leaving the
        // media source provider to supply a source at playback time, where an HTTP
        // context does exist.
        Assert.True(string.IsNullOrEmpty(item.Path));
    }

    [Fact]
    public void Audio_UsesTheAudioStreamEndpoint_NotTheVideoOne()
    {
        AddServer();
        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Audio", remoteId));

        Assert.Contains($"/Audio/{remoteId:N}/stream", item.Path);
    }

    [Theory]
    [InlineData("Series")]
    [InlineData("Season")]
    [InlineData("BoxSet")]
    public void ContainerTypes_NeverGetAStreamPath(string itemType)
    {
        AddServer();
        var item = _manager.MaterializeItem(AddEntry(itemType, Guid.NewGuid()));

        // These are folders, not playable media; giving them a stream URL would make
        // Jellyfin treat a browsable container as a media file.
        Assert.True(string.IsNullOrEmpty(item.Path));
        Assert.False(item.IsShortcut);
    }

    [Fact]
    public void Container_ReportedByTheRemote_IsStampedOnTheItem()
    {
        AddServer();
        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid(), container: "mkv"));

        // Lets Jellyfin certify direct play immediately instead of waiting on a probe.
        Assert.Equal("mkv", item.Container);
    }

    [Fact]
    public void WanCapMode_DefaultsToAuto_AndUnclassifiedMeansDirectPlay()
    {
        // The default for every server, and the state of a brand-new one before the
        // background classifier has had a chance to run even once: direct play, same
        // as if WAN capping did not exist at all. A cap is never speculative.
        var server = AddServer();
        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid(), container: "mkv"));

        Assert.Equal(Configuration.WanCapMode.Auto, server.WanCapMode);
        Assert.Contains("Static=true", item.Path);
        Assert.DoesNotContain("VideoBitrate", item.Path);
        Assert.Equal("mkv", item.Container);
    }

    [Fact]
    public void WanCapMode_Off_StreamsRawSourceUnchanged()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Off;
        server.WanMaxBitrateMbps = 12; // ignored in Off mode

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid(), container: "mkv"));

        Assert.Contains("Static=true", item.Path);
        Assert.DoesNotContain("VideoBitrate", item.Path);
        Assert.Equal("mkv", item.Container);
    }

    [Fact]
    public void WanCapMode_Manual_RequestsATranscodedStreamFromTheRemoteInstead_OfTheRawFile()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        server.WanMaxHeight = 1080;

        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId, container: "mkv"));

        Assert.Equal(
            $"http://friend.example:8096/Videos/{remoteId:N}/stream.mp4"
                + "?api_key=secret-key&VideoCodec=h264&AudioCodec=aac&VideoBitrate=12000000&AudioBitrate=256000&MaxHeight=1080",
            item.Path);

        // The URL now serves a server-side transcode, not the original mkv/HEVC file -
        // stamping the source's real container would certify direct play against bytes
        // that are not actually what gets served.
        Assert.Equal("mp4", item.Container);
    }

    [Fact]
    public void WanCapMode_Manual_ButHeightUnset_OmitsTheHeightParamRatherThanCappingResolution()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        server.WanMaxHeight = 0;

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.DoesNotContain("MaxHeight", item.Path);
    }

    [Fact]
    public void WanCapMode_DoesNotApplyToAudio()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;

        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Audio", remoteId));

        Assert.Contains("Static=true", item.Path);
        Assert.DoesNotContain("VideoBitrate", item.Path);
    }

    [Fact]
    public void WanCapMode_DoesNotApplyInProxyMode()
    {
        var server = AddServer(StreamingMode.Proxy);
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        _plugin.Configuration.ServerUrl = "https://my-server.example";

        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        // Proxy mode already routes through this server; the WAN cap only concerns
        // Direct mode's own remote-to-remote fetch.
        Assert.Contains("/Plugins/Federation/Stream", item.Path);
        Assert.DoesNotContain("VideoBitrate", item.Path);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedSameNetwork_StreamsRawSourceUnchanged()
    {
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: true, measuredMbps: null);

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.Contains("Static=true", item.Path);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_ButNotYetMeasured_UsesTheConservativePlaceholderCap()
    {
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: null);

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.Contains("VideoBitrate=10000000", item.Path);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_MeasuredFast_StaysUncapped()
    {
        // Measured comfortably above what any real source needs - forcing a second
        // transcode pass would cost CPU on both ends for no benefit, so direct play
        // wins even on a confirmed WAN link.
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 80.0);

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.Contains("Static=true", item.Path);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_MeasuredSlow_CapsToTheLargestBitrateThatFits()
    {
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 16.0);

        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        // 16 Mbps measured * 0.75 safety margin = 12 Mbps.
        Assert.Contains("VideoBitrate=12000000", item.Path);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_MeasuredVerySlow_ClampsToTheConfiguredFloor()
    {
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 2.0);

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.Contains("VideoBitrate=4000000", item.Path);
    }

    [Fact]
    public void DisabledServer_ProducesNoPath()
    {
        var server = AddServer();
        server.Enabled = false;

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.True(string.IsNullOrEmpty(item.Path));
    }
}
