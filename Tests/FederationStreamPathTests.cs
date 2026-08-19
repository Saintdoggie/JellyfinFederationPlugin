using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
    private readonly Mock<MediaBrowser.Controller.Persistence.IMediaStreamRepository> _mediaStreamRepository;

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
        _mediaStreamRepository = new Mock<MediaBrowser.Controller.Persistence.IMediaStreamRepository>();
        _manager = new FederationLibraryManager(
            lm.Object,
            NullLogger<FederationLibraryManager>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            _cache,
            _bandwidthMonitor,
            _mediaStreamRepository.Object);
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

    private FederatedCacheEntry AddEntry(string itemType, Guid remoteId, string? container = null, MediaStream[]? mediaStreams = null)
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
                Container = container,
                MediaStreams = mediaStreams
            },
            0,
            itemType);

        return _cache.GetEntriesForMapping("Movies").First();
    }

    [Fact]
    public void Movie_DirectMode_NeverGetsAStaticStreamUrlAsPath_SoTheRealApiKeyNeverReachesAClient()
    {
        // Security fix: Direct mode used to stamp a URL with the remote server's
        // real, long-lived api_key embedded in the query string directly onto the
        // item's static Path - any logged-in user on this server, not just its
        // admin, could read that key straight out of dev tools/network tab and use
        // it directly against the friend's server. BuildPlaybackUrl now always
        // returns null for Direct mode (see its doc comment), so no credential-
        // bearing URL is ever persisted at sync time. The real, working, per-session
        // URL - a short-lived, single-item-scoped playback token, not the raw key -
        // is instead built live by FederationMediaSourceProvider.GetMediaSources on
        // every actual PlaybackInfo request.
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Off;
        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        Assert.True(string.IsNullOrEmpty(item.Path));
        Assert.False(item.IsShortcut);
    }

    [Fact]
    public void Movie_ProxyMode_WithPath_ResolvesLocationTypeRemote()
    {
        // Direct mode no longer stamps a static Path at all (see the test above), so
        // this LocationType assertion - which needs a real http Path to exercise -
        // is now covered against a Proxy-mode item instead.
        AddServer(StreamingMode.Proxy);
        _plugin.Configuration.ServerUrl = "https://my-server.example";
        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        // An http path resolves to Remote from BaseItem's own logic. A null path
        // resolves to Virtual instead, which is what made the web client paint
        // federated episodes as "Missing".
        Assert.Equal(LocationType.Remote, item.LocationType);
    }

    [Fact]
    public void Movie_ProxyMode_PointsAtThisServersOwnLoopbackProxyEndpoint_AndIsNotMarkedAShortcut()
    {
        AddServer(StreamingMode.Proxy);
        _plugin.Configuration.ServerUrl = "https://my-server.example";

        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        // Proxy-mode streams are fetched by this server's own transcoder, never by a
        // client directly, so the URL stamped here deliberately stays on loopback
        // rather than the public ServerUrl - going out through a public host/VPS
        // tunnel and back in to reach the same process is pure wasted latency.
        Assert.Equal(
            $"http://127.0.0.1:8096/Plugins/Federation/Stream?serverId=serverA&itemId={remoteId:N}",
            item.Path);

        // The remote api_key must never reach a client in Proxy mode.
        Assert.DoesNotContain("secret-key", item.Path);

        // Proxy URLs point back at this very server, so the source must not claim to
        // be remote - clients without remote-video support would refuse it.
        Assert.False(item.IsShortcut);
    }

    [Fact]
    public void Movie_ProxyModeWithNoConfiguredServerUrl_StillGetsALoopbackPath()
    {
        AddServer(StreamingMode.Proxy);
        _plugin.Configuration.ServerUrl = string.Empty;

        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        // The internal transcoder-facing URL never depended on the public ServerUrl
        // being configured, so an unconfigured ServerUrl (which still blocks peer
        // handshakes) should no longer block Proxy playback from working.
        Assert.Equal(
            $"http://127.0.0.1:8096/Plugins/Federation/Stream?serverId=serverA&itemId={remoteId:N}",
            item.Path);
    }

    [Fact]
    public void Audio_UsesTheAudioStreamEndpoint_NotTheVideoOne()
    {
        AddServer();
        var remoteId = Guid.NewGuid();
        var entry = AddEntry("Audio", remoteId);
        _manager.MaterializeItem(entry);

        // Direct mode never persists item.Path (see the security test above), so
        // the endpoint-selection logic is exercised against the live URL builder
        // instead - the decision itself is unrelated to the security fix.
        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);
        Assert.Contains($"/Audio/{remoteId:N}/stream", liveUrl);
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
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Off; // needs a stamped Path/Container - see the comment on the Off test above
        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid(), container: "mkv"));

        // Lets Jellyfin certify direct play immediately instead of waiting on a probe.
        Assert.Equal("mkv", item.Container);
    }

    [Fact]
    public void WanCapMode_DefaultsToAuto_AndUnclassifiedMeansNoCap_SoTheStampedPathIsUncapped()
    {
        // The default for every server, and the state of a brand-new one before the
        // background classifier has had a chance to run even once. Unclassified means
        // "no evidence a cap is needed" (WanBandwidthMonitor.GetEffectiveCapMbps
        // returns null for it, same as confirmed-local), so this is a plain,
        // never-changes-later URL - safe to stamp statically like Off, unlike the
        // confirmed-WAN/Manual cases below whose cap value can genuinely still move.
        var server = AddServer();
        var entry = AddEntry("Movie", Guid.NewGuid(), container: "mkv");
        var item = _manager.MaterializeItem(entry);

        Assert.Equal(Configuration.WanCapMode.Auto, server.WanCapMode);
        // Direct mode never persists item.Path (security fix - see the test above);
        // the WAN-cap/raw-vs-capped decision is still exercised, via Container and
        // the live URL builder.
        Assert.True(string.IsNullOrEmpty(item.Path));
        Assert.Equal("mkv", item.Container);

        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);
        Assert.Contains("Static=true", liveUrl);
        Assert.DoesNotContain("VideoBitrate", liveUrl);
    }

    [Fact]
    public void WanCapMode_Off_StreamsRawSourceUnchanged()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Off;
        server.WanMaxBitrateMbps = 12; // ignored in Off mode

        var entry = AddEntry("Movie", Guid.NewGuid(), container: "mkv");
        var item = _manager.MaterializeItem(entry);

        // Direct mode never persists item.Path (security fix), so the raw-vs-capped
        // decision is exercised via Container and the live URL builder instead.
        Assert.True(string.IsNullOrEmpty(item.Path));
        Assert.Equal("mkv", item.Container);

        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);
        Assert.Contains("Static=true", liveUrl);
        Assert.DoesNotContain("VideoBitrate", liveUrl);
    }

    [Fact]
    public void WanCapMode_Manual_RequestsATranscodedStreamFromTheRemoteInstead_OfTheRawFile()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        server.WanMaxHeight = 1080;

        var remoteId = Guid.NewGuid();
        var entry = AddEntry("Movie", remoteId, container: "mkv");
        var item = _manager.MaterializeItem(entry);

        // Manual is a fixed number, but an admin can edit it at any time, so a
        // stamped Path could go stale if they do - which is one reason (on top of
        // the security fix below) Direct mode never persists item.Path at all any
        // more: FederationMediaSourceProvider.GetMediaSources builds a fresh,
        // short-lived, single-item-scoped URL live on every request instead, so
        // staleness cannot happen. And regardless of staleness, this URL carries
        // the remote's real, long-lived api_key in its query string - it must never
        // be written to item.Path, which Jellyfin serializes straight into a
        // client-facing static media source.
        var expectedUrl =
            $"http://friend.example:8096/Videos/{remoteId:N}/stream.mp4"
                + "?api_key=secret-key&VideoCodec=h264&AudioCodec=aac&VideoBitrate=12000000&AudioBitrate=256000&MaxHeight=1080";
        Assert.True(string.IsNullOrEmpty(item.Path));
        // The URL forces h264/aac in an mp4 container regardless of the source
        // file's real one (mkv here) - Container must match what actually gets
        // served, not the remote's original file.
        Assert.Equal("mp4", item.Container);

        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);
        Assert.Equal(expectedUrl, liveUrl);
    }

    [Fact]
    public void WanCapMode_Manual_ButHeightUnset_OmitsTheHeightParamRatherThanCappingResolution()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        server.WanMaxHeight = 0;

        var entry = AddEntry("Movie", Guid.NewGuid());
        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);

        Assert.DoesNotContain("MaxHeight", liveUrl);
    }

    [Fact]
    public void WanCapMode_DoesNotApplyToAudio()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;

        var remoteId = Guid.NewGuid();
        var entry = AddEntry("Audio", remoteId);
        var item = _manager.MaterializeItem(entry);

        // Direct mode never persists item.Path (security fix), so the "cap never
        // applies to audio" decision is exercised via the live URL builder instead.
        Assert.True(string.IsNullOrEmpty(item.Path));

        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);
        Assert.Contains("Static=true", liveUrl);
        Assert.DoesNotContain("VideoBitrate", liveUrl);
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

        var entry = AddEntry("Movie", Guid.NewGuid());
        var item = _manager.MaterializeItem(entry);

        // Direct mode never persists item.Path (security fix); the decision is
        // exercised via the live URL builder instead.
        Assert.True(string.IsNullOrEmpty(item.Path));

        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);
        Assert.Contains("Static=true", liveUrl);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_ButNotYetMeasured_UsesTheConservativePlaceholderCap()
    {
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: null);

        var entry = AddEntry("Movie", Guid.NewGuid());
        var item = _manager.MaterializeItem(entry);

        // Direct mode never persists item.Path (security fix - see the api_key test
        // above); the WAN-cap decision is still exercised via Container and the
        // live URL builder below.
        Assert.True(string.IsNullOrEmpty(item.Path));
        Assert.Equal("mp4", item.Container);

        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);
        Assert.Contains("VideoBitrate=10000000", liveUrl);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_MeasuredFast_StaysUncapped()
    {
        // Measured comfortably above what any real source needs - forcing a second
        // transcode pass would cost CPU on both ends for no benefit, so direct play
        // wins even on a confirmed WAN link.
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 80.0);

        var entry = AddEntry("Movie", Guid.NewGuid());
        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);

        Assert.Contains("Static=true", liveUrl);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_MeasuredSlow_CapsToTheLargestBitrateThatFits()
    {
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 20.0);

        var remoteId = Guid.NewGuid();
        var entry = AddEntry("Movie", remoteId);
        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);

        // 20 Mbps measured * 0.85 safety margin = 17 Mbps.
        Assert.Contains("VideoBitrate=17000000", liveUrl);
    }

    [Fact]
    public void WanCapMode_Auto_ConfirmedWan_MeasuredVerySlow_ClampsToTheConfiguredFloor()
    {
        // The floor is a deliberate minimum-acceptable-quality choice, not just a
        // safety clamp - a connection that measures below it still gets this
        // bitrate requested rather than something even lower proportional to what
        // it measured.
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 2.0);

        var entry = AddEntry("Movie", Guid.NewGuid());
        var liveUrl = _manager.BuildPlaybackUrl(entry.ItemType, entry.GetPrimarySource()!);

        Assert.Contains("VideoBitrate=10000000", liveUrl);
    }

    [Fact]
    public void RemoteMediaStreams_ArePersistedOnTheItem_SoPlaybackCanCertifyDirectPlayWithoutALiveProbe()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Off;
        var remoteId = Guid.NewGuid();
        var streams = new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", Index = 1 }
        };

        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId, container: "mp4", mediaStreams: streams));

        _mediaStreamRepository.Verify(
            r => r.SaveMediaStreams(
                item.Id,
                It.Is<IReadOnlyList<MediaStream>>(s => s.Count == 2 && s[0].Codec == "h264" && s[1].Codec == "aac"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void RemoteMediaStreams_AreNotPersisted_WhenTheUrlServesAWanCappedTranscodeInstead()
    {
        // The WAN-capped Direct URL serves a forced h264/aac/mp4 transcode of the
        // source, not the raw file - saving the remote's real (often richer, e.g. 4K
        // HEVC) stream data here would describe bytes the URL doesn't actually serve.
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        var streams = new[] { new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 } };

        _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid(), container: "mkv", mediaStreams: streams));

        _mediaStreamRepository.Verify(
            r => r.SaveMediaStreams(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
