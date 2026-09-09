using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
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
    public void Movie_DirectMode_GetsTheSecretFreeProxyUrlAsPath_SoThePlayButtonShowsAndNoCredentialLeaks()
    {
        // jellyfin-web's canPlay() hides the Play button for any non-Program item
        // with LocationType 'Virtual' - which is exactly what an empty item.Path
        // resolves to. Direct mode used to stamp no Path at all (token-security:
        // the only URL it could build carried the remote's credential), leaving
        // every Direct-mode item permanently without a Play button. The fix: the
        // static Path is now this server's own /Plugins/Federation/Stream proxy
        // gateway - anonymous, credential-free, and valid for any streaming mode -
        // so the button shows AND no credential ever reaches a client.
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Off;
        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        Assert.Equal(
            $"http://127.0.0.1:8096/Plugins/Federation/Stream?serverId=serverA&itemId={remoteId:N}&sig={_manager.CreateProxySignature("serverA", remoteId, false, null)}",
            item.Path);
        Assert.DoesNotContain("secret-key", item.Path);
        Assert.Equal(LocationType.Remote, item.LocationType);
        Assert.False(item.IsShortcut);
    }

    [Fact]
    public void Movie_DirectMode_WithUniversallyPermissiveUserRule_KeepsStaticPath()
    {
        // Merely having a rule must not remove the Play button for every user.
        // AllLibraries without a rating/item restriction allows this exact item
        // for configured and unconfigured users alike, so one shared path is safe.
        var server = AddServer();
        server.FriendUserAccessRules = new List<Configuration.RemoteUserAccessRule>
        {
            new() { RemoteUserId = Guid.NewGuid().ToString("N"), Mode = Configuration.RemoteUserAccessMode.AllLibraries }
        };
        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        Assert.False(string.IsNullOrEmpty(item.Path));
        Assert.Equal(LocationType.Remote, item.LocationType);
    }

    [Fact]
    public void Movie_DirectMode_WithRuleAllowingThisMappedLibrary_KeepsStaticPathForAllUsers()
    {
        var server = AddServer();
        server.FriendUserAccessRules = new List<Configuration.RemoteUserAccessRule>
        {
            new()
            {
                RemoteUserId = Guid.NewGuid().ToString("N"),
                Mode = Configuration.RemoteUserAccessMode.CertainLibraries,
                LibraryFolderIds = new List<string> { "remote-movies" }
            }
        };
        _plugin.Configuration.LibraryMappings.Add(new Configuration.LibraryMapping
        {
            LocalLibraryName = "Movies",
            RemoteLibrarySources = new List<Configuration.RemoteLibrarySource>
            {
                new() { ServerId = "serverA", RemoteLibraryId = "remote-movies" }
            }
        });

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.False(string.IsNullOrEmpty(item.Path));
        Assert.Equal(LocationType.Remote, item.LocationType);
    }

    [Fact]
    public void Movie_DirectMode_WithBlockedUserRule_GetsNoStaticPath()
    {
        var server = AddServer();
        server.FriendUserAccessRules = new List<Configuration.RemoteUserAccessRule>
        {
            new() { RemoteUserId = Guid.NewGuid().ToString("N"), Mode = Configuration.RemoteUserAccessMode.Blocked }
        };
        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.True(string.IsNullOrEmpty(item.Path));
    }

    [Fact]
    public void Episode_DirectMode_WithSeriesBlockedForAUser_GetsNoStaticPath()
    {
        var server = AddServer();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var otherSeriesId = Guid.NewGuid();
        var otherEpisodeId = Guid.NewGuid();

        var series = _cache.UpsertRaw("TV", "serverA", seriesId, new BaseItemDto { Id = seriesId, Name = "Show" }, 0, "Series");
        var season = _cache.UpsertRaw("TV", "serverA", seasonId, new BaseItemDto { Id = seasonId, Name = "Season 1" }, 0, "Season", parentKey: series.Key);
        var episode = _cache.UpsertRaw("TV", "serverA", episodeId, new BaseItemDto { Id = episodeId, Name = "Pilot" }, 0, "Episode", parentKey: season.Key);
        var otherSeries = _cache.UpsertRaw("TV", "serverA", otherSeriesId, new BaseItemDto { Id = otherSeriesId, Name = "Other Show" }, 0, "Series");
        var otherEpisode = _cache.UpsertRaw("TV", "serverA", otherEpisodeId, new BaseItemDto { Id = otherEpisodeId, Name = "Other Pilot" }, 0, "Episode", parentKey: otherSeries.Key);

        server.FriendUserAccessRules = new List<Configuration.RemoteUserAccessRule>
        {
            new()
            {
                RemoteUserId = Guid.NewGuid().ToString("N"),
                Mode = Configuration.RemoteUserAccessMode.AllLibraries,
                BlockedItemIds = new List<string> { seriesId.ToString("N") }
            }
        };

        Assert.True(string.IsNullOrEmpty(_manager.MaterializeItem(episode).Path));
        Assert.False(string.IsNullOrEmpty(_manager.MaterializeItem(otherEpisode).Path));
    }

    [Fact]
    public void Movie_ProxyMode_WithPath_ResolvesLocationTypeRemote()
    {
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
            $"http://127.0.0.1:8096/Plugins/Federation/Stream?serverId=serverA&itemId={remoteId:N}&sig={_manager.CreateProxySignature("serverA", remoteId, false, null)}",
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
            $"http://127.0.0.1:8096/Plugins/Federation/Stream?serverId=serverA&itemId={remoteId:N}&sig={_manager.CreateProxySignature("serverA", remoteId, false, null)}",
            item.Path);
    }

    [Fact]
    public void ProxySignature_IsScopedToServerItemMediaKindAndUser()
    {
        AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString("N");
        var signature = _manager.CreateProxySignature("serverA", itemId, false, userId);

        Assert.True(_manager.ValidateProxySignature("serverA", itemId, false, userId, signature));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, userId, null));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, userId, string.Empty));
        Assert.False(_manager.ValidateProxySignature("missing-server", itemId, false, userId, signature));
        Assert.False(_manager.ValidateProxySignature("serverA", otherItemId, false, userId, signature));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, true, userId, signature));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, Guid.NewGuid().ToString("N"), signature));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, "not-a-user-id", signature));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, userId, new string('z', 64)));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, userId, signature + "00"));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, userId, signature, download: true));
    }

    [Fact]
    public void ProxySignature_IsRevokedWhenServerCredentialChangesOrServerIsDisabled()
    {
        var server = AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var signature = _manager.CreateProxySignature("serverA", itemId, false, null);

        server.ApiKey = "rotated-key";
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, signature));

        var rotatedSignature = _manager.CreateProxySignature("serverA", itemId, false, null);
        server.Enabled = false;
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, rotatedSignature));
    }

    [Fact]
    public void ProxySignature_ValidV2_SucceedsWithinLifetime()
    {
        AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString("N");
        var mintedAt = new DateTimeOffset(2026, 9, 8, 15, 30, 0, TimeSpan.Zero);
        var signature = _manager.CreateProxySignature("serverA", itemId, false, userId, mintedAt);
        var expUnixHour = (mintedAt.ToUnixTimeSeconds() / 3600) + FederationLibraryManager.ProxySignatureLifetimeHours;
        var payload = $"v2\nserverA\n{itemId:N}\n0\n{userId}\n{expUnixHour}\n0";
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret-key"), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        Assert.Equal($"{mac}.{expUnixHour}", signature);
        Assert.True(_manager.ValidateProxySignature("serverA", itemId, false, userId, signature, mintedAt));
        Assert.True(_manager.ValidateProxySignature("serverA", itemId, false, userId, signature, mintedAt.AddHours(23)));
    }

    [Fact]
    public void ProxySignature_ExpiredV2_Fails()
    {
        AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var mintedAt = new DateTimeOffset(2026, 9, 8, 15, 30, 0, TimeSpan.Zero);
        var signature = _manager.CreateProxySignature("serverA", itemId, false, null, mintedAt);

        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, signature, mintedAt.AddHours(24)));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, signature, mintedAt.AddHours(48)));
    }

    [Fact]
    public void ProxySignature_TamperedExpiry_Fails()
    {
        AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var mintedAt = new DateTimeOffset(2026, 9, 8, 15, 30, 0, TimeSpan.Zero);
        var signature = _manager.CreateProxySignature("serverA", itemId, false, null, mintedAt);
        var separator = signature.LastIndexOf('.');
        var mac = signature.Substring(0, separator);
        var expUnixHour = long.Parse(signature.Substring(separator + 1), System.Globalization.CultureInfo.InvariantCulture);
        var tampered = $"{mac}.{expUnixHour + 48}";

        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, tampered, mintedAt));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, tampered, mintedAt.AddHours(23)));
    }

    [Fact]
    public void ProxySignature_ItemMismatch_StillFails()
    {
        AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var mintedAt = new DateTimeOffset(2026, 9, 8, 15, 30, 0, TimeSpan.Zero);
        var signature = _manager.CreateProxySignature("serverA", itemId, false, null, mintedAt);

        Assert.False(_manager.ValidateProxySignature("serverA", otherItemId, false, null, signature, mintedAt));
    }

    [Fact]
    public void ProxySignature_LegacyV1_IsPlayOnly()
    {
        AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString("N");
        var payload = $"v1\nserverA\n{itemId:N}\n0\n{userId}";
        var v1 = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret-key"), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        Assert.Equal(64, v1.Length);
        Assert.True(_manager.ValidateProxySignature("serverA", itemId, false, userId, v1));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, userId, v1, download: true));
    }

    [Fact]
    public void ProxySignature_PlayDoesNotAuthorizeDownload_AndDownloadDoesNotAuthorizePlay()
    {
        AddServer(StreamingMode.Proxy);
        var itemId = Guid.NewGuid();
        var play = _manager.CreateProxySignature("serverA", itemId, false, null);
        var download = _manager.CreateProxySignature("serverA", itemId, false, null, download: true);

        Assert.True(_manager.ValidateProxySignature("serverA", itemId, false, null, play));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, play, download: true));
        Assert.True(_manager.ValidateProxySignature("serverA", itemId, false, null, download, download: true));
        Assert.False(_manager.ValidateProxySignature("serverA", itemId, false, null, download));
        Assert.NotEqual(play, download);
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
        // Direct mode now stamps the secret-free proxy-gateway URL (see the Play
        // button test above); the raw-vs-capped decision is exercised via the live
        // URL builder below.
        Assert.Contains("/Plugins/Federation/Stream", item.Path);
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

        // Direct mode now stamps the proxy-gateway URL (Play button fix), so the
        // raw-vs-capped decision is exercised via the live URL builder instead.
        Assert.Contains("/Plugins/Federation/Stream", item.Path);
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

        // Manual is a fixed number an admin can edit at any time - one reason the
        // *stamped* Path is now the mode-independent, secret-free proxy gateway
        // rather than this capped URL: FederationMediaSourceProvider builds a
        // fresh, short-lived, single-item-scoped URL live on every request, so
        // neither staleness nor credential exposure can happen. And regardless,
        // this capped URL carries the remote's federation token in its query
        // string - it must never be written to item.Path, which Jellyfin
        // serializes straight into a client-facing static media source.
        var expectedUrl =
            $"http://friend.example:8096/Videos/{remoteId:N}/stream.mp4"
                + "?api_key=secret-key&VideoCodec=h264&AudioCodec=aac&VideoBitrate=12000000&AudioBitrate=256000&MaxHeight=1080";
        Assert.Contains("/Plugins/Federation/Stream", item.Path);
        Assert.DoesNotContain("secret-key", item.Path);
        // Every client-facing URL (stamped proxy path and provider token URL
        // alike) serves the raw source file - the capped transcode URL below is
        // internal-only - so Container matches the remote's original file, not a
        // forced mp4.
        Assert.Equal("mkv", item.Container);

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

        // Direct mode now stamps the proxy-gateway URL (Play button fix); the "cap
        // never applies to audio" decision is exercised via the live URL builder.
        Assert.Contains("/Plugins/Federation/Stream", item.Path);
        Assert.Contains("audio=true", item.Path);

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

        // Direct mode now stamps the proxy-gateway URL (Play button fix); the
        // decision is exercised via the live URL builder instead.
        Assert.Contains("/Plugins/Federation/Stream", item.Path);

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

        // Direct mode now stamps the proxy-gateway URL (Play button fix); the
        // WAN-cap decision is still exercised via the live URL builder below.
        Assert.Contains("/Plugins/Federation/Stream", item.Path);

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
    public void BuildDirectStreamCapQuery_LanOrUncapped_IsEmpty()
    {
        var server = AddServer();
        Assert.Equal(string.Empty, _manager.BuildDirectStreamCapQuery(server, isAudio: false));
    }

    [Fact]
    public void BuildDirectStreamCapQuery_ConfirmedLocalNetwork_IsEmpty()
    {
        var server = AddServer();
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: true, measuredMbps: null);

        Assert.Equal(string.Empty, _manager.BuildDirectStreamCapQuery(server, isAudio: false));
    }

    [Fact]
    public void BuildDirectStreamCapQuery_ManualWan_RequestsCapAndHeight()
    {
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        server.WanMaxHeight = 1080;

        Assert.Equal("&capMbps=12&maxHeight=1080", _manager.BuildDirectStreamCapQuery(server, isAudio: false));
        Assert.Equal(string.Empty, _manager.BuildDirectStreamCapQuery(server, isAudio: true));
    }

    [Fact]
    public void BuildDirectStreamCapQuery_AutoConfirmedWan_UsesEffectiveCap()
    {
        var server = AddServer();
        server.WanMaxHeight = 0;
        _bandwidthMonitor.SeedForTests(server.Id, isLocalNetwork: false, measuredMbps: 20.0);

        Assert.Equal("&capMbps=17", _manager.BuildDirectStreamCapQuery(server, isAudio: false));
    }

    [Fact]
    public void BuildDirectGatewayLoopbackUrl_Lan_UsesStaticTrue()
    {
        var itemId = Guid.NewGuid();
        var url = FederationLibraryManager.BuildDirectGatewayLoopbackUrl("http://127.0.0.1:8096", itemId, audio: false, capMbps: null);

        Assert.Equal($"http://127.0.0.1:8096/Videos/{itemId:N}/stream?Static=true", url);
        Assert.DoesNotContain("VideoBitrate", url);
    }

    [Fact]
    public void BuildDirectGatewayLoopbackUrl_WanCap_RequestsLowerBitrateTranscode()
    {
        var itemId = Guid.NewGuid();
        var url = FederationLibraryManager.BuildDirectGatewayLoopbackUrl(
            "http://127.0.0.1:8096",
            itemId,
            audio: false,
            capMbps: 12,
            maxHeight: 1080);

        Assert.Equal(
            $"http://127.0.0.1:8096/Videos/{itemId:N}/stream.mp4?VideoCodec=h264&AudioCodec=aac&VideoBitrate=12000000&AudioBitrate=256000&MaxHeight=1080",
            url);
        Assert.DoesNotContain("Static=true", url);
    }

    [Fact]
    public void BuildDirectGatewayLoopbackUrl_Audio_StaysStaticEvenWhenCapped()
    {
        var itemId = Guid.NewGuid();
        var url = FederationLibraryManager.BuildDirectGatewayLoopbackUrl(
            "http://127.0.0.1:8096",
            itemId,
            audio: true,
            capMbps: 12,
            maxHeight: 1080);

        Assert.Equal($"http://127.0.0.1:8096/Audio/{itemId:N}/stream?Static=true", url);
        Assert.DoesNotContain("VideoBitrate", url);
    }

    [Fact]
    public void MaterializeItem_DoesNotSaveMediaStreamsItself_BecauseTheItemDoesNotExistInTheDbYet()
    {
        // MediaStreamInfos has a foreign key on the item's own BaseItems row, which for
        // a brand-new item is only persisted later by ILibraryManager.CreateItems (see
        // FederationItemPersistenceService) - never inside MaterializeItem itself.
        // Saving here used to throw a FOREIGN KEY constraint failure on every new
        // item's first sync, every time, silently swallowed into a "fall back to a
        // live probe" warning.
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Off;
        var streams = new[] { new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0 } };

        _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid(), container: "mp4", mediaStreams: streams));

        _mediaStreamRepository.Verify(
            r => r.SaveMediaStreams(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        var entry = AddEntry("Movie", remoteId, container: "mp4", mediaStreams: streams);
        var item = _manager.MaterializeItem(entry);

        // Mirrors what FederationItemPersistenceService actually does: persist media
        // streams only once ILibraryManager.CreateItems has saved the item for real.
        var persisted = _manager.TryPersistMediaStreams(item, entry);

        Assert.True(persisted);
        _mediaStreamRepository.Verify(
            r => r.SaveMediaStreams(
                item.Id,
                It.Is<IReadOnlyList<MediaStream>>(s => s.Count == 2 && s[0].Codec == "h264" && s[1].Codec == "aac"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void RemoteMediaStreams_ArePersistedEvenWhenWanCapped_BecauseClientUrlsServeTheRawFile()
    {
        // The WAN-capped Direct URL (a forced h264/aac/mp4 transcode) is
        // internal-only - never served to any client. Every client-facing URL -
        // the stamped proxy-gateway Path and the provider's token-gated
        // DirectStream URL alike - serves the raw source file, so the remote's
        // real stream data correctly describes the bytes clients actually get.
        var server = AddServer();
        server.WanCapMode = Configuration.WanCapMode.Manual;
        server.WanMaxBitrateMbps = 12;
        var streams = new[] { new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 } };

        var entry = AddEntry("Movie", Guid.NewGuid(), container: "mkv", mediaStreams: streams);
        var item = _manager.MaterializeItem(entry);
        _manager.TryPersistMediaStreams(item, entry);

        _mediaStreamRepository.Verify(
            r => r.SaveMediaStreams(It.IsAny<Guid>(), It.Is<IReadOnlyList<MediaStream>>(s => s.Count == 1 && s[0].Codec == "hevc"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void DisabledServer_ProducesNoPath()
    {
        var server = AddServer();
        server.Enabled = false;

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.True(string.IsNullOrEmpty(item.Path));
    }

    /// <summary>
    /// Regression test: without this, a local metadata provider (TMDb, OMDb, ...)
    /// was free to run its own "identify" search against a federated item and
    /// overwrite fields this plugin already populated from the remote's own
    /// canonical data - confirmed live as dozens of Plex-sourced movies with weak
    /// source metadata all ending up mislabeled with the exact same wrong,
    /// unrelated title from one bad shared search match.
    /// </summary>
    [Fact]
    public void MaterializeItem_LocksFieldsThisPluginPopulates_SoLocalMetadataProvidersCannotOverwriteThem()
    {
        AddServer();

        var item = _manager.MaterializeItem(AddEntry("Movie", Guid.NewGuid()));

        Assert.Equal(
            FederationLibraryManager.LockedMetadataFields.OrderBy(f => f),
            item.LockedFields.OrderBy(f => f));
    }

    [Fact]
    public async Task GetMediaSources_DirectMode_UsesItemScopedPlaybackToken_NotSessionToken()
    {
        // Direct-mode Path is client-visible. A session token authorizes any
        // currently visible item, so swapping itemId on that URL would fetch a
        // different title. This pins that a known local user still gets an
        // item-scoped playback token, not RegisterUserSession.
        var serverId = "direct-client-path-" + Guid.NewGuid().ToString("N");
        var remoteId = Guid.NewGuid();
        var localUserId = Guid.NewGuid();
        var server = new RemoteServer
        {
            Id = serverId,
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true,
            StreamingMode = StreamingMode.Direct
        };
        _plugin.Configuration.RemoteServers.Add(server);

        _cache.UpsertRaw(
            "Movies",
            serverId,
            remoteId,
            new BaseItemDto { Id = remoteId, Name = "Gran Turismo", Type = Jellyfin.Data.Enums.BaseItemKind.Movie, Container = "mkv" },
            0,
            "Movie");
        var entry = _cache.GetEntriesForMapping("Movies").First(e => e.GetPrimarySource()?.ServerId == serverId);
        var item = _manager.MaterializeItem(entry);
        item.Path = "stale-so-provider-emits-direct-path";

        var handler = new DirectPathTokenHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://friend.example:8096") };
        var remoteClient = new RemoteServerClient(server, NullLogger.Instance, httpClient);
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        clientFactory.Setup(f => f.GetClient(It.IsAny<RemoteServer>())).Returns(remoteClient);
        clientFactory.Setup(f => f.GetClient(It.IsAny<string>())).Returns(remoteClient);

        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory.Object,
            _cache,
            _bandwidthMonitor,
            _mediaStreamRepository.Object);

        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.SetupGet(a => a.HttpContext).Returns(new DefaultHttpContext());
        var authorization = new Mock<IAuthorizationContext>();
        var authInfo = new AuthorizationInfo();
        var userType = typeof(AuthorizationInfo).GetProperty("User")!.PropertyType;
        var user = Activator.CreateInstance(userType, "alice", "auth", "reset")!;
        userType.GetProperty("Id")!.SetValue(user, localUserId);
        typeof(AuthorizationInfo).GetProperty("User")!.SetValue(authInfo, user);
        authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(authInfo);

        var provider = new FederationMediaSourceProvider(
            NullLogger<FederationMediaSourceProvider>.Instance,
            manager,
            httpContextAccessor.Object,
            authorization.Object,
            new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance));

        var sources = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        Assert.Equal(0, handler.RegisterUserSessionCalls);
        Assert.True(handler.PlaybackTokenCalls >= 1);
        Assert.DoesNotContain("\"Purpose\":\"Image\"", handler.LastPlaybackTokenBody);
        var path = Assert.Single(sources).Path;
        Assert.Contains($"/Plugins/Federation/DirectStream/{remoteId:N}?token=item-tok-123", path);
        Assert.DoesNotContain("session-tok-123", path);
        Assert.Equal(localUserId.ToString("N"), handler.LastRemoteUserId);
    }

    [Fact]
    public void GetInternalPlaybackBaseUrl_PrefersInternalServerUrl_OverLivePort()
    {
        _plugin.Configuration.InternalServerUrl = "http://10.0.0.5:8097/";
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 18096;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.HttpPort).Returns(12345);
        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            _cache,
            _bandwidthMonitor,
            _mediaStreamRepository.Object,
            accessor.Object,
            host.Object);

        Assert.Equal("http://10.0.0.5:8097", manager.GetInternalPlaybackBaseUrl());
    }

    [Fact]
    public void GetInternalPlaybackBaseUrl_UsesLiveKestrelPort_WhenInternalServerUrlIsBlank()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 18096;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);
        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            _cache,
            _bandwidthMonitor,
            _mediaStreamRepository.Object,
            accessor.Object);

        Assert.Equal("http://127.0.0.1:18096", manager.GetInternalPlaybackBaseUrl());
    }

    [Fact]
    public void GetInternalPlaybackBaseUrl_UsesConfiguredHttpPort_WhenNoLiveRequest()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.HttpPort).Returns(12345);
        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            _cache,
            _bandwidthMonitor,
            _mediaStreamRepository.Object,
            applicationHost: host.Object);

        Assert.Equal("http://127.0.0.1:12345", manager.GetInternalPlaybackBaseUrl());
    }

    [Fact]
    public void Movie_StampsInternalServerUrl_NotDefault8096()
    {
        AddServer(StreamingMode.Proxy);
        _plugin.Configuration.InternalServerUrl = "http://127.0.0.1:18096";
        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));

        Assert.StartsWith($"http://127.0.0.1:18096/Plugins/Federation/Stream?serverId=serverA&itemId={remoteId:N}", item.Path);
        Assert.DoesNotContain("127.0.0.1:8096", item.Path);
    }

    [Fact]
    public async Task GetMediaSources_StampedDefault8096_OnLiveNonDefaultPort_EmitsLiveUrl()
    {
        AddServer(StreamingMode.Proxy);
        var remoteId = Guid.NewGuid();
        var item = _manager.MaterializeItem(AddEntry("Movie", remoteId));
        Assert.Contains("http://127.0.0.1:8096/Plugins/Federation/Stream", item.Path);

        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 18096;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);
        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Mock.Of<IRemoteServerClientFactory>(),
            _cache,
            _bandwidthMonitor,
            _mediaStreamRepository.Object,
            accessor.Object);
        var authorization = new Mock<IAuthorizationContext>();
        authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync((AuthorizationInfo?)null);
        var provider = new FederationMediaSourceProvider(
            NullLogger<FederationMediaSourceProvider>.Instance,
            manager,
            accessor.Object,
            authorization.Object,
            new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance));

        var sources = (await provider.GetMediaSources(item, CancellationToken.None)).ToList();

        var path = Assert.Single(sources).Path;
        Assert.Contains("http://127.0.0.1:18096/Plugins/Federation/Stream", path);
        Assert.DoesNotContain("http://127.0.0.1:8096/", path);
        Assert.True(FederationLibraryManager.IsDeadDefaultLoopbackUrl(item.Path, "http://127.0.0.1:18096"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096/Plugins/Federation/Stream", "http://127.0.0.1:18096", true)]
    [InlineData("http://localhost:8096/Plugins/Federation/Stream", "http://127.0.0.1:12345", true)]
    [InlineData("http://127.0.0.1:8096/Plugins/Federation/Stream", "http://127.0.0.1:8096", false)]
    [InlineData("http://127.0.0.1:18096/Plugins/Federation/Stream", "http://127.0.0.1:18096", false)]
    public void IsDeadDefaultLoopbackUrl_OnlyWhenStamped8096DisagreesWithLivePort(string stamped, string live, bool expected)
    {
        Assert.Equal(expected, FederationLibraryManager.IsDeadDefaultLoopbackUrl(stamped, live));
    }

    private sealed class DirectPathTokenHandler : HttpMessageHandler
    {
        public int PlaybackTokenCalls { get; private set; }

        public int RegisterUserSessionCalls { get; private set; }

        public string LastPlaybackTokenBody { get; private set; } = string.Empty;

        public string? LastRemoteUserId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            LastRemoteUserId = request.Headers.TryGetValues(RemoteServerClient.RemoteUserIdHeader, out var values)
                ? values.FirstOrDefault()
                : LastRemoteUserId;

            if (path.Equals("/Plugins/Federation/PlaybackToken", StringComparison.OrdinalIgnoreCase))
            {
                PlaybackTokenCalls++;
                LastPlaybackTokenBody = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Json("{\"token\":\"item-tok-123\",\"purpose\":\"Playback\"}");
            }

            if (path.Equals("/Plugins/Federation/RegisterUserSession", StringComparison.OrdinalIgnoreCase))
            {
                RegisterUserSessionCalls++;
                return Json("{\"token\":\"session-tok-123\"}");
            }

            if (path.Contains("/Peer/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
            {
                return Json("{\"MediaSources\":[{\"Id\":\"src1\",\"Container\":\"mkv\",\"Size\":1}]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body)
            => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

}
