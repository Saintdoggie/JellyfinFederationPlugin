using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The remote-transcode handoff hands a viewer's transcode to the server that
/// owns the file. These pin what may cross (and what never may): the native
/// paths, the credentials stripped from every forwarded query and playlist URI,
/// the friend's limits, and the receiver's memory of grants and capability.
/// </summary>
public class RemoteTranscodeHandoffTests
{
    private static readonly Guid Item = Guid.Parse("0123456789abcdef0123456789abcdef");
    private static readonly Guid Source = Guid.Parse("fedcba9876543210fedcba9876543210");

    [Theory]
    [InlineData("/videos/0123456789abcdef0123456789abcdef/master.m3u8", RemoteMediaKind.HlsMaster, false)]
    [InlineData("/Videos/01234567-89AB-CDEF-0123-456789ABCDEF/main.m3u8", RemoteMediaKind.HlsVariant, false)]
    [InlineData("videos/0123456789abcdef0123456789abcdef/hls1/main/0.ts", RemoteMediaKind.HlsSegment, false)]
    [InlineData("videos/0123456789abcdef0123456789abcdef/hls1/main/-1.mp4", RemoteMediaKind.HlsSegment, false)]
    [InlineData("videos/0123456789abcdef0123456789abcdef/stream", RemoteMediaKind.Stream, false)]
    [InlineData("videos/0123456789abcdef0123456789abcdef/stream.mkv", RemoteMediaKind.Stream, false)]
    [InlineData("Audio/0123456789abcdef0123456789abcdef/universal", RemoteMediaKind.UniversalAudio, true)]
    [InlineData("audio/0123456789abcdef0123456789abcdef/master.m3u8", RemoteMediaKind.HlsMaster, true)]
    [InlineData("Videos/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210/Subtitles/3/Stream.vtt", RemoteMediaKind.Subtitle, false)]
    [InlineData("Videos/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210/Subtitles/3/0/Stream.ass", RemoteMediaKind.Subtitle, false)]
    [InlineData("Videos/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210/Subtitles/3/subtitles.m3u8", RemoteMediaKind.SubtitlePlaylist, false)]
    [InlineData("Videos/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210/Attachments/2", RemoteMediaKind.Attachment, false)]
    public void AllowedShapes_Parse(string path, RemoteMediaKind kind, bool audio)
    {
        Assert.True(RemoteMediaPath.TryParse(path, out var parsed));
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(audio, parsed.Audio);
        Assert.Equal(Item, parsed.ItemId);
    }

    [Theory]
    [InlineData("videos/0123456789abcdef0123456789abcdef/../../System/Info")]
    [InlineData("videos/0123456789abcdef0123456789abcdef/master.m3u8/../x")]
    [InlineData("Items/0123456789abcdef0123456789abcdef/PlaybackInfo")]
    [InlineData("videos/0123456789abcdef0123456789abcdef/universal")]
    [InlineData("videos/notanid/master.m3u8")]
    [InlineData("videos/0123456789abcdef0123456789abcdef/live.m3u8")]
    [InlineData("videos/0123456789abcdef0123456789abcdef/AdditionalParts")]
    [InlineData("Users/0123456789abcdef0123456789abcdef/Items")]
    [InlineData("videos/0123456789abcdef0123456789abcdef\\master.m3u8")]
    [InlineData("")]
    public void EverythingElse_IsRefused(string path)
        => Assert.False(RemoteMediaPath.TryParse(path, out _));

    [Fact]
    public void NativePath_SwapsIds_KeepsSuffix()
    {
        Assert.True(RemoteMediaPath.TryParse("/Videos/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210/Subtitles/3/0/Stream.vtt", out var subtitle));
        var remote = Guid.NewGuid();
        Assert.Equal($"videos/{remote:N}/{remote:N}/Subtitles/3/0/Stream.vtt", subtitle!.ToNativePath(remote, remote));

        Assert.True(RemoteMediaPath.TryParse("/Audio/0123456789abcdef0123456789abcdef/hls1/main/4.aac", out var segment));
        Assert.Equal($"audio/{remote:N}/hls1/main/4.aac", segment!.ToNativePath(remote, null));
    }

    [Fact]
    public void FilterQuery_DropsCredentialsAndIdentity_KeepsPlaybackParameters()
    {
        var query = RemoteMediaPath.ParseQuery("?DeviceId=d&MediaSourceId=abc&VideoCodec=h264&ApiKey=SECRET&api_key=SECRET&X-Emby-Token=SECRET&userId=u&LiveStreamId=l&PlaySessionId=p&DeviceProfileId=x");

        var filtered = RemoteMediaPath.FilterQuery(query);

        Assert.DoesNotContain("SECRET", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("userId", filtered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LiveStreamId", filtered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeviceProfileId", filtered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DeviceId=d", filtered, StringComparison.Ordinal);
        Assert.Contains("VideoCodec=h264", filtered, StringComparison.Ordinal);
        Assert.Contains("PlaySessionId=p", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiverQuery_MapsSourceToFriendItem_AndHoldsBitratesToTheCap()
    {
        var remote = Guid.NewGuid();
        var query = RemoteMediaPath.ParseQuery("?MediaSourceId=abc&VideoBitrate=40000000&AudioBitrate=192000&MaxStreamingBitrate=60000000&VideoCodec=h264");

        string Rewrite(int? cap) => RemoteMediaPath.FilterQuery(query, (k, v) => FederationRemoteTranscodeFilter.RewriteQueryValue(k, v, remote, cap, query));

        var capped = Rewrite(20);
        Assert.Contains($"MediaSourceId={remote:N}", capped, StringComparison.Ordinal);
        Assert.Contains("VideoBitrate=18808000", capped, StringComparison.Ordinal);
        Assert.Contains("MaxStreamingBitrate=19000000", capped, StringComparison.Ordinal);

        var uncapped = Rewrite(null);
        Assert.Contains("VideoBitrate=40000000", uncapped, StringComparison.Ordinal);
        Assert.Contains($"MediaSourceId={remote:N}", uncapped, StringComparison.Ordinal);

        var low = RemoteMediaPath.ParseQuery("?VideoBitrate=1000000");
        Assert.Contains("VideoBitrate=1000000", RemoteMediaPath.FilterQuery(low, (k, v) => FederationRemoteTranscodeFilter.RewriteQueryValue(k, v, remote, 20, low)), StringComparison.Ordinal);
    }

    [Fact]
    public void Playlist_AllUrisBecomeAbsoluteGatewayUrls_WithoutCredentials()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nmain.m3u8?DeviceId=d&MediaSourceId=0123456789abcdef0123456789abcdef&ApiKey=SECRET&PlaySessionId=p\n";
        const string variant = "#EXTM3U\n#EXT-X-MAP:URI=\"hls1/main/-1.mp4?ApiKey=SECRET&runtimeTicks=0\"\n#EXTINF:3,\nhls1/main/0.mp4?ApiKey=SECRET&runtimeTicks=0\n#EXT-X-ENDLIST\n";

        static string Map(RemoteMediaPath p, string q) => "https://owner.example/Plugins/Federation/Peer/Play/TOK/" + p.ToNativePath(p.ItemId, p.MediaSourceId) + RemoteMediaPath.FilterQuery(RemoteMediaPath.ParseQuery(q));

        var rewrittenMaster = HlsPlaylistRewriter.Rewrite(master, $"/videos/{Item:N}/master.m3u8", Map);
        var rewrittenVariant = HlsPlaylistRewriter.Rewrite(variant, $"/videos/{Item:N}/main.m3u8", Map);

        Assert.DoesNotContain("SECRET", rewrittenMaster, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", rewrittenVariant, StringComparison.Ordinal);
        Assert.Contains($"https://owner.example/Plugins/Federation/Peer/Play/TOK/videos/{Item:N}/main.m3u8?DeviceId=d", rewrittenMaster, StringComparison.Ordinal);
        Assert.Contains($"URI=\"https://owner.example/Plugins/Federation/Peer/Play/TOK/videos/{Item:N}/hls1/main/-1.mp4?runtimeTicks=0\"", rewrittenVariant, StringComparison.Ordinal);
        Assert.Contains($"\nhttps://owner.example/Plugins/Federation/Peer/Play/TOK/videos/{Item:N}/hls1/main/0.mp4?runtimeTicks=0\n", rewrittenVariant, StringComparison.Ordinal);
        Assert.StartsWith("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\n", rewrittenMaster, StringComparison.Ordinal);
    }

    [Fact]
    public void Playlist_UrisOutsideTheAllowList_AreDropped()
    {
        const string hostile = "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,URI=\"../../System/Configuration\"\nhttps://evil.example/x.ts\n../../Users\n";

        var rewritten = HlsPlaylistRewriter.Rewrite(hostile, $"/videos/{Item:N}/master.m3u8", (p, q) => "MAPPED");

        Assert.DoesNotContain("MAPPED", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("System", rewritten, StringComparison.Ordinal);
        Assert.Contains("URI=\"\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnerRegistry_LimitsNewSessionsPerFriend_AndOnlyControlsItsOwn()
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var registry = new RemoteTranscodeSessionRegistry { UtcNow = () => now };

        Assert.True(registry.TryTouch("friend-a", "s1", "dev", limit: 2));
        Assert.True(registry.TryTouch("friend-a", "S1", "dev", limit: 2));
        Assert.True(registry.TryTouch("friend-a", "s2", "dev", limit: 2));
        Assert.False(registry.TryTouch("friend-a", "s3", "dev", limit: 2));
        Assert.True(registry.TryTouch("friend-b", "s3", "dev", limit: 2));
        Assert.True(registry.TryTouch("friend-a", "s1", "dev", limit: 2));

        Assert.False(registry.TryPing("friend-b", "s1"));
        Assert.False(registry.TryEnd("friend-b", "s1", out _));
        Assert.True(registry.TryEnd("friend-a", "s1", out var device));
        Assert.Equal("dev", device);

        // A stopped session frees its slot at once, can't be ended or pinged
        // again, but stays known for fetching its conversion log.
        Assert.False(registry.TryEnd("friend-a", "s1", out _));
        Assert.False(registry.TryPing("friend-a", "s1"));
        Assert.True(registry.TryTouch("friend-a", "s3", "dev", limit: 2));
        registry.TryEnd("friend-a", "s3", out _);

        now += RemoteTranscodeSessionRegistry.ActiveWindow + TimeSpan.FromSeconds(1);
        Assert.True(registry.TryTouch("friend-a", "s4", "dev", limit: 2));
        Assert.True(registry.TryTouch("friend-a", "s5", "dev", limit: 2));
    }

    [Fact]
    public void OwnerRegistry_KnowsTitleAndStartForLogs_OnlyForTheFriendThatStartedIt()
    {
        var now = new DateTime(2026, 10, 20, 15, 42, 0, DateTimeKind.Utc);
        var registry = new RemoteTranscodeSessionRegistry { UtcNow = () => now };
        registry.TryTouch("friend-a", "play", "dev", 0, Item);

        now = now.AddMinutes(2);
        registry.TryTouch("friend-a", "play", "dev", 0, Guid.NewGuid());
        Assert.True(registry.TryGetForLog("friend-a", "PLAY", out var item, out var started));
        Assert.Equal(Item, item);
        Assert.Equal(now.AddMinutes(-2), started);
        Assert.False(registry.TryGetForLog("friend-b", "play", out _, out _));

        registry.TryEnd("friend-a", "play", out _);
        now += RemoteTranscodeSessionRegistry.EndedWindow - TimeSpan.FromMinutes(1);
        registry.TryTouch("friend-a", "other", "dev", 0, Item);
        Assert.True(registry.TryGetForLog("friend-a", "play", out _, out _));
        now += TimeSpan.FromMinutes(2);
        registry.TryTouch("friend-a", "other", "dev", 0, Item);
        Assert.False(registry.TryGetForLog("friend-a", "play", out _, out _));
    }

    [Fact]
    public void ReceiverHandoffs_PingSpacing_PauseChangeAlwaysRelayed_StopOnce()
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var handoffs = new RemoteTranscodeHandoffs { UtcNow = () => now };
        handoffs.Record("p1", "friend");

        Assert.Equal("friend", handoffs.TakePing("p1", false));
        Assert.Null(handoffs.TakePing("p1", false));
        Assert.Equal("friend", handoffs.TakePing("p1", true));
        now += RemoteTranscodeHandoffs.PingSpacing;
        Assert.Equal("friend", handoffs.TakePing("p1", true));
        Assert.Null(handoffs.TakePing("unknown", false));

        Assert.Equal("friend", handoffs.TakeStop("P1"));
        Assert.Null(handoffs.TakeStop("p1"));
    }

    [Fact]
    public void Capability_LearnedFromTokens_BacksOffAfterRefusal()
    {
        PeerTranscodeCapability.Reset();
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        PeerTranscodeCapability.UtcNow = () => now;
        try
        {
            Assert.False(PeerTranscodeCapability.IsAvailable("cap-test"));
            PeerTranscodeCapability.Record("cap-test", true);
            Assert.True(PeerTranscodeCapability.IsAvailable("cap-test"));
            PeerTranscodeCapability.RecordRefusal("cap-test");
            Assert.False(PeerTranscodeCapability.IsAvailable("cap-test"));
            now += PeerTranscodeCapability.RefusalBackoff;
            Assert.True(PeerTranscodeCapability.IsAvailable("cap-test"));
            PeerTranscodeCapability.Record("cap-test", false);
            Assert.False(PeerTranscodeCapability.IsAvailable("cap-test"));
        }
        finally
        {
            PeerTranscodeCapability.UtcNow = () => DateTime.UtcNow;
            PeerTranscodeCapability.Reset();
        }
    }

    [Fact]
    public void Grants_ArePerViewer_AndTokenParsesFromDirectPath()
    {
        var viewer = Guid.NewGuid();
        var other = Guid.NewGuid();
        var item = Guid.NewGuid();
        var remote = Guid.NewGuid();
        var token = PeerPlaybackGrants.TokenFromDirectPath($"https://owner.example/Plugins/Federation/DirectStream/{remote:N}?token=ABC123&capMbps=20");
        Assert.Equal("ABC123", token);
        Assert.Null(PeerPlaybackGrants.TokenFromDirectPath("http://127.0.0.1:8096/Plugins/Federation/Stream?serverId=x"));

        PeerPlaybackGrants.Record(viewer, item, item.ToString("N"), "owner", remote, token!);

        Assert.Equal("ABC123", PeerPlaybackGrants.Find(viewer, item, null)!.Token);
        Assert.Equal("ABC123", PeerPlaybackGrants.Find(viewer, item, item.ToString("D"))!.Token);
        Assert.Null(PeerPlaybackGrants.Find(other, item, null));
        PeerPlaybackGrants.Forget(viewer, item, null);
        Assert.Null(PeerPlaybackGrants.Find(viewer, item, null));
    }

    [Fact]
    public void RedactedStreams_KeepFfmpegInputIndexes_WithAnExternalSubtitle()
    {
        // Jellyfin numbers a sidecar subtitle first (0) and the file's own streams
        // after it (1, 2), then maps to ffmpeg input indexes by counting streams
        // that share a Path. Nulling every Path made the sidecar count as part of
        // the file: ffmpeg was asked for 0:1/0:2 of a two-stream file.
        var streams = new List<MediaBrowser.Model.Entities.MediaStream>
        {
            new() { Index = 0, Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, IsExternal = true, Path = "/owner/movies/x.en.srt" },
            new() { Index = 1, Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Path = "/owner/movies/x.mkv" },
            new() { Index = 2, Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Path = "/owner/movies/x.mkv" },
        };

        streams.ForEach(FederationLibraryManager.RedactStreamPath);

        Assert.All(streams, s => Assert.DoesNotContain("/owner", s.Path ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(0, MediaBrowser.Controller.MediaEncoding.EncodingHelper.FindIndex(streams, streams[1]));
        Assert.Equal(1, MediaBrowser.Controller.MediaEncoding.EncodingHelper.FindIndex(streams, streams[2]));
    }
}

public class RemoteTranscodeFilterOrderTests
{
    [Fact]
    public async Task SegmentOfLocalPlay_SkipsTheFriendLookup()
    {
        // Strict mocks: any ResolveAsync work (library or auth lookup) would throw and be swallowed,
        // so assert on the verifiable calls instead.
        var library = new Moq.Mock<MediaBrowser.Controller.Library.ILibraryManager>(Moq.MockBehavior.Strict);
        var auth = new Moq.Mock<MediaBrowser.Controller.Net.IAuthorizationContext>(Moq.MockBehavior.Strict);
        var filter = new FederationRemoteTranscodeFilter(
            auth.Object,
            Moq.Mock.Of<MediaBrowser.Controller.Library.IUserManager>(),
            library.Object,
            Moq.Mock.Of<MediaBrowser.Controller.Library.IMediaSourceManager>(),
            null!,
            null!,
            new RemoteTranscodeHandoffs(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationRemoteTranscodeFilter>.Instance);

        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = $"/videos/{Guid.NewGuid()}/hls1/main/0.ts";
        http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?PlaySessionId=local1");
        var descriptor = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor
        {
            RouteValues = new Dictionary<string, string?> { ["controller"] = "DynamicHls", ["action"] = "GetHlsVideoSegment" }
        };
        var context = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
            new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), descriptor),
            new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(context, new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(), new object()));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
        library.VerifyNoOtherCalls();
        auth.VerifyNoOtherCalls();
    }
}

public class RemoteTranscodeLimitKeyTests
{
    [Fact]
    public void MissingPlaySessionId_IsStillCountedWhenALimitIsSet()
    {
        var item = Guid.NewGuid();
        var registry = new RemoteTranscodeSessionRegistry();
        var k1 = RemoteMediaPath.SessionKeyForLimit(null, "dev1", item, 1);
        Assert.NotNull(k1);
        Assert.True(registry.TryTouch("friend", k1!, "dev1", 1, item));

        // Same play's next request joins it; a different play is over the limit of 1.
        Assert.True(registry.TryTouch("friend", RemoteMediaPath.SessionKeyForLimit(null, "dev1", item, 1)!, "dev1", 1, item));
        Assert.False(registry.TryTouch("friend", RemoteMediaPath.SessionKeyForLimit(null, "dev2", item, 1)!, "dev2", 1, item));
    }

    [Fact]
    public void NoLimit_MissingPlaySessionId_IsNotTracked()
        => Assert.Null(RemoteMediaPath.SessionKeyForLimit(null, "d", Guid.NewGuid(), 0));

    [Fact]
    public void PlaySessionId_IsUsedAsIs()
        => Assert.Equal("abc", RemoteMediaPath.SessionKeyForLimit("abc", "d", Guid.NewGuid(), 3));
}
