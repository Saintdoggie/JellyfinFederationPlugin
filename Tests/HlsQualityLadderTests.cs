using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The quality ladder adds lower levels to a converted title's master playlist.
/// These pin the level maths, which playlists qualify, that nothing secret or
/// foreign is introduced, and that each level reaches Jellyfin as its own conversion.
/// </summary>
[Collection("PluginInstance")]
public class HlsQualityLadderTests
{
    private const string Base = "https://owner.example/Plugins/Federation/Peer/Play/tok/videos/0123456789abcdef0123456789abcdef/main.m3u8";

    private static string Master(string query, string attributes = "BANDWIDTH=20192000,AVERAGE-BANDWIDTH=20192000,VIDEO-RANGE=SDR,CODECS=\"avc1.640033,mp4a.40.2\",RESOLUTION=3840x1608,FRAME-RATE=23.976")
        => "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-STREAM-INF:" + attributes + "\n" + Base + "?" + query + "\n";

    private const string Converted = "DeviceId=d1&MediaSourceId=abc&VideoCodec=h264&AudioCodec=aac&VideoBitrate=20000000&AudioBitrate=192000&PlaySessionId=ps1&SegmentContainer=mp4&TranscodeReasons=ContainerBitrateExceedsLimit";

    private static List<(string Inf, string Uri)> Variants(string playlist)
    {
        var lines = playlist.Split('\n');
        var found = new List<(string, string)>();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                found.Add((lines[i], lines[i + 1]));
            }
        }

        return found;
    }

    private static string Param(string uri, string key)
        => RemoteMediaPath.QueryValue(RemoteMediaPath.ParseQuery(uri.Substring(uri.IndexOf('?', StringComparison.Ordinal))), key)!;

    [Theory]
    [InlineData(20_000_000, new long[] { 11_000_000, 6_000_000, 3_000_000 })]
    [InlineData(8_000_000, new long[] { 4_400_000, 2_400_000, 1_200_000 })]
    [InlineData(3_000_000, new long[] { 1_650_000, 1_000_000 })]
    [InlineData(1_500_000, new long[] { 1_000_000 })]
    [InlineData(1_200_000, new long[] { })]
    public void LevelsStepDownWithAFloorAndNoNearDuplicates(long top, long[] expected)
    {
        var rungs = HlsQualityLadder.Plan(top, 2160);
        Assert.Equal(expected, rungs.Select(r => r.VideoBitrate).ToArray());
        Assert.Equal(Enumerable.Range(1, expected.Length).ToArray(), rungs.Select(r => r.Number).ToArray());
        Assert.All(rungs, r => Assert.True(r.VideoBitrate >= HlsQualityLadder.FloorVideoBitrate && r.VideoBitrate < top));
    }

    [Fact]
    public void PictureShrinksWithTheBitrateButNeverGrows()
    {
        Assert.Equal(new int?[] { null, 1080, 720 }, HlsQualityLadder.Plan(20_000_000, 2160).Select(r => r.MaxHeight).ToArray());
        Assert.Equal(new int?[] { null, 720, 480 }, HlsQualityLadder.Plan(8_000_000, 1080).Select(r => r.MaxHeight).ToArray());
        Assert.Equal(new int?[] { null, null }, HlsQualityLadder.Plan(3_000_000, 480).Select(r => r.MaxHeight).ToArray());

        // Unknown top height: only ever a limit that is safe for any source.
        Assert.Equal(new int?[] { null, 1080, 720 }, HlsQualityLadder.Plan(20_000_000, 0).Select(r => r.MaxHeight).ToArray());
    }

    [Fact]
    public void AConvertedMasterGetsLowerLevelsAfterItsOwn()
    {
        var master = Master(Converted);
        var expanded = HlsQualityLadder.Expand(master);
        var variants = Variants(expanded);

        Assert.Equal(4, variants.Count);
        Assert.Equal(Variants(master)[0], variants[0]);
        Assert.StartsWith("#EXTM3U\n#EXT-X-VERSION:7\n", expanded, StringComparison.Ordinal);
        Assert.EndsWith("\n", expanded, StringComparison.Ordinal);

        Assert.Equal(new[] { "11000000", "6000000", "3000000" }, variants.Skip(1).Select(v => Param(v.Uri, "VideoBitrate")).ToArray());
        Assert.Equal(new[] { "1", "2", "3" }, variants.Skip(1).Select(v => Param(v.Uri, HlsQualityLadder.RungParameter)).ToArray());
        Assert.All(variants.Skip(1), v =>
        {
            Assert.StartsWith(Base + "?", v.Uri, StringComparison.Ordinal);
            Assert.Equal("false", Param(v.Uri, "AllowVideoStreamCopy"));
            Assert.Equal("ps1", Param(v.Uri, "PlaySessionId"));
            Assert.Equal("d1", Param(v.Uri, "DeviceId"));
            Assert.Equal("192000", Param(v.Uri, "AudioBitrate"));
            Assert.Contains("CODECS=\"avc1.640033,mp4a.40.2\"", v.Inf, StringComparison.Ordinal);
            Assert.Contains("FRAME-RATE=23.976", v.Inf, StringComparison.Ordinal);
        });

        Assert.Contains("BANDWIDTH=11192000,AVERAGE-BANDWIDTH=11192000", variants[1].Inf, StringComparison.Ordinal);
        Assert.Contains("RESOLUTION=3840x1608", variants[1].Inf, StringComparison.Ordinal);
        Assert.Contains("BANDWIDTH=6192000", variants[2].Inf, StringComparison.Ordinal);
        Assert.Contains("RESOLUTION=2580x1080", variants[2].Inf, StringComparison.Ordinal);
        Assert.Equal("1080", Param(variants[2].Uri, "MaxHeight"));
        Assert.Equal("1920", Param(variants[2].Uri, "MaxWidth"));
        Assert.Contains("RESOLUTION=1720x720", variants[3].Inf, StringComparison.Ordinal);
        Assert.Equal("720", Param(variants[3].Uri, "MaxHeight"));
        Assert.Equal("1280", Param(variants[3].Uri, "MaxWidth"));
    }

    [Fact]
    public void ExpandingTwiceChangesNothing()
    {
        var once = HlsQualityLadder.Expand(Master(Converted));
        Assert.Same(once, HlsQualityLadder.Expand(once));
    }

    [Theory]
    [InlineData("VideoCodec=h264&VideoBitrate=20000000")]                                            // nothing says the video is converted
    [InlineData("VideoCodec=h264&VideoBitrate=20000000&TranscodeReasons=AudioCodecNotSupported")]    // audio-only reason: video is copied
    [InlineData("VideoCodec=copy&VideoBitrate=20000000&AllowVideoStreamCopy=false")]                 // explicit copy
    [InlineData("VideoCodec=h264&AllowVideoStreamCopy=false")]                                       // no requested video bitrate
    [InlineData("VideoCodec=h264&VideoBitrate=900000&AllowVideoStreamCopy=false")]                   // already at the floor
    [InlineData("VideoCodec=h264&VideoBitrate=abc&AllowVideoStreamCopy=false")]
    [InlineData("VideoCodec=h264&VideoBitrate=20000000&AllowVideoStreamCopy=false&FedRung=1")]       // already a level
    public void PlaylistsThatDoNotQualifyAreReturnedUntouched(string query)
    {
        var master = Master(query);
        Assert.Same(master, HlsQualityLadder.Expand(master));
    }

    [Fact]
    public void OtherShapesAreReturnedUntouched()
    {
        Assert.Same(string.Empty, HlsQualityLadder.Expand(string.Empty));
        var media = "#EXTM3U\n#EXTINF:3.0,\nhls1/main/0.mp4?VideoBitrate=20000000&AllowVideoStreamCopy=false\n#EXT-X-ENDLIST\n";
        Assert.Same(media, HlsQualityLadder.Expand(media));
        var noQuery = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n" + Base + "\n";
        Assert.Same(noQuery, HlsQualityLadder.Expand(noQuery));
        var dangling = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n";
        Assert.Same(dangling, HlsQualityLadder.Expand(dangling));

        // Jellyfin's own ladder (several variants) is left alone.
        var two = Master(Converted) + "#EXT-X-STREAM-INF:BANDWIDTH=18192000\n" + Base + "?" + Converted.Replace("20000000", "18000000", StringComparison.Ordinal) + "\n";
        Assert.Same(two, HlsQualityLadder.Expand(two));
    }

    [Theory]
    [InlineData("VideoCodec=h264&VideoBitrate=20000000&AllowVideoStreamCopy=false", null)]
    [InlineData("VideoCodec=h264&VideoBitrate=20000000&allowvideostreamcopy=False", null)]
    [InlineData("VideoCodec=h264&VideoBitrate=20000000&TranscodeReasons=AudioCodecNotSupported%2CVideoCodecNotSupported", null)]
    [InlineData("VideoCodec=h264&VideoBitrate=20000000&TranscodeReasons=ContainerBitrateExceedsLimit", null)]
    [InlineData("VideoCodec=h264&VideoBitrate=20000000", 93_000_000L)]                               // a 93 Mbps file cannot be copied into 20 Mbps
    public void AnyClearSignThatVideoIsConvertedQualifies(string query, long? sourceBitrate)
        => Assert.Equal(4, Variants(HlsQualityLadder.Expand(Master(query), sourceBitrate)).Count);

    [Fact]
    public void ASourceSmallerThanTheRequestIsNotProofOfConversion()
    {
        var master = Master("VideoCodec=h264&VideoBitrate=20000000");
        Assert.Same(master, HlsQualityLadder.Expand(master, 8_000_000));
    }

    [Fact]
    public void ExistingLimitsAreNeverRaisedAndUntouchedParametersKeepTheirExactText()
    {
        var query = "videoBitrate=8000000&MaxHeight=480&MaxWidth=854&AllowVideoStreamCopy=false&Tag=a%2Bb%3D%3D&SubtitleMethod=Encode";
        var variants = Variants(HlsQualityLadder.Expand(Master(query, "BANDWIDTH=8192000,RESOLUTION=854x480")));
        Assert.Equal(4, variants.Count);
        Assert.All(variants.Skip(1), v =>
        {
            Assert.Equal("480", Param(v.Uri, "MaxHeight"));
            Assert.Equal("854", Param(v.Uri, "MaxWidth"));
            Assert.Contains("Tag=a%2Bb%3D%3D", v.Uri, StringComparison.Ordinal);
            Assert.Contains("RESOLUTION=854x480", v.Inf, StringComparison.Ordinal);
            Assert.DoesNotContain("VideoBitrate=", v.Uri, StringComparison.Ordinal);
        });
        Assert.Contains("videoBitrate=4400000", variants[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAttributesAndWindowsLineEndingsAreHandled()
    {
        var master = ("#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=5000000\r\n" + Base + "?VideoBitrate=6000000&AllowVideoStreamCopy=false\r\n");
        var variants = Variants(HlsQualityLadder.Expand(master));
        Assert.Equal(4, variants.Count);

        // BANDWIDTH below the video bitrate: fall back to a plain audio allowance.
        Assert.Contains("BANDWIDTH=3492000", variants[1].Inf, StringComparison.Ordinal);
        Assert.DoesNotContain("RESOLUTION", variants[1].Inf, StringComparison.Ordinal);
        Assert.Equal("720", Param(variants[2].Uri, "MaxHeight"));
        Assert.DoesNotContain('\r', HlsQualityLadder.Expand(master));
    }

    [Fact]
    public void LevelsSurviveBothAddressRewriters()
    {
        // Owner gateway: Jellyfin's relative playlist becomes gateway addresses and
        // identity/credential parameters are stripped; the level marker must stay.
        var native = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=20192000\nmain.m3u8?VideoBitrate=11000000&AllowVideoStreamCopy=false&FedRung=1&api_key=SECRET&ApiKey=SECRET2\n";
        var gateway = HlsPlaylistRewriter.Rewrite(native, "/videos/0123456789abcdef0123456789abcdef/master.m3u8", (target, query) =>
            "https://owner.example/Plugins/Federation/Peer/Play/tok/" + target.ToNativePath(target.ItemId, target.MediaSourceId) + RemoteMediaPath.FilterQuery(RemoteMediaPath.ParseQuery(query)));
        Assert.Contains("FedRung=1", gateway, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", gateway, StringComparison.Ordinal);

        // Receiver: friend addresses in an expanded master are all rebuilt, and the
        // nested playlists of every level go through this server's relay.
        var rewritten = HandoffPlaylistLinks.Rewrite(HlsQualityLadder.Expand(Master(Converted)), "https://owner.example", "/relay?u=");
        var uris = Variants(rewritten).Select(v => v.Uri).ToList();
        Assert.Equal(4, uris.Count);
        Assert.All(uris, u => Assert.StartsWith("/relay?u=https%3A%2F%2Fowner.example%2FPlugins%2FFederation%2FPeer%2FPlay%2Ftok%2F", u, StringComparison.Ordinal));
        Assert.Equal(3, uris.Count(u => Uri.UnescapeDataString(u).Contains("FedRung=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("6", 6)]
    [InlineData("0", null)]
    [InlineData("7", null)]
    [InlineData("-1", null)]
    [InlineData("1x", null)]
    [InlineData("001", null)]
    [InlineData("", null)]
    [InlineData(" 2", null)]
    public void OnlySmallWholeLevelNumbersAreAccepted(string raw, int? expected)
    {
        var query = new List<KeyValuePair<string, StringValues>> { new("fedrung", raw) };
        Assert.Equal(expected, HlsQualityLadder.RungOf(query));
        Assert.Null(HlsQualityLadder.RungOf(new List<KeyValuePair<string, StringValues>>()));
    }

    [Fact]
    public void EachLevelGetsItsOwnUserAgentSoJellyfinKeepsItsFilesApart()
    {
        Assert.Equal("FederationRung/2", HlsQualityLadder.RungUserAgent(null, 2));
        Assert.Equal("Mozilla/5.0 FederationRung/2", HlsQualityLadder.RungUserAgent("Mozilla/5.0", 2));

        // Stamping again (a re-entrant filter) replaces, never stacks.
        Assert.Equal("Mozilla/5.0 FederationRung/3", HlsQualityLadder.RungUserAgent("Mozilla/5.0 FederationRung/2", 3));
        Assert.NotEqual(HlsQualityLadder.RungUserAgent("A", 1), HlsQualityLadder.RungUserAgent("A", 2));

        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "ExoPlayer";
        context.Request.QueryString = new QueryString("?VideoBitrate=1&FedRung=2");
        HlsQualityLadder.ApplyRungUserAgent(context.Request);
        Assert.Equal("ExoPlayer FederationRung/2", context.Request.Headers.UserAgent.ToString());

        // The top level and bad values leave the request exactly as it came.
        foreach (var query in new[] { "?VideoBitrate=1", "?FedRung=99", "?FedRung=abc" })
        {
            var plain = new DefaultHttpContext();
            plain.Request.Headers.UserAgent = "ExoPlayer";
            plain.Request.QueryString = new QueryString(query);
            HlsQualityLadder.ApplyRungUserAgent(plain.Request);
            Assert.Equal("ExoPlayer", plain.Request.Headers.UserAgent.ToString());
        }
    }

    [Fact]
    public void CapabilityIsLearnedPerFriendAndOffUntilAdvertised()
    {
        PeerTranscodeCapability.Reset();
        try
        {
            Assert.False(PeerTranscodeCapability.HasQualityLadder("owner"));
            Assert.False(PeerTranscodeCapability.HasQualityLadder(null));
            PeerTranscodeCapability.RecordQualityLadder("owner", true);
            Assert.True(PeerTranscodeCapability.HasQualityLadder("OWNER"));
            Assert.False(PeerTranscodeCapability.HasQualityLadder("other"));
            PeerTranscodeCapability.RecordQualityLadder("owner", false);
            Assert.False(PeerTranscodeCapability.HasQualityLadder("owner"));
        }
        finally
        {
            PeerTranscodeCapability.Reset();
        }
    }

    private static async Task<string> FetchMasterAsync(bool settingOn, bool friendSupports)
    {
        var previous = Plugin.Instance;
        var config = Plugin.Instance?.Configuration;
        var before = config?.AdaptiveQualityLadder;
        PeerTranscodeCapability.Reset();
        try
        {
            if (config != null)
            {
                config.AdaptiveQualityLadder = settingOn;
            }

            PeerTranscodeCapability.RecordQualityLadder("owner", friendSupports);
            var filter = new FederationRemoteTranscodeFilter(
                null!, null!, null!, null!, null!, null!, new RemoteTranscodeHandoffs(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationRemoteTranscodeFilter>.Instance)
            {
                LinkClientOverride = _ => null,
                Send = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(Master(Converted), System.Text.Encoding.UTF8, "application/vnd.apple.mpegurl")
                })
            };
            var handoff = new FederationRemoteTranscodeFilter.Handoff(
                Base.Replace("main.m3u8", "master.m3u8", StringComparison.Ordinal),
                "owner", "Owner", Guid.NewGuid(), "src", "https://owner.example",
                RemoteMediaKind.HlsMaster, "https://owner.example", true);
            var (result, status) = await filter.FetchAsync(handoff, true, "/relay?u=", CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, status);
            return System.Text.Encoding.UTF8.GetString(Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(result).FileContents);
        }
        finally
        {
            if (config != null && before.HasValue)
            {
                config.AdaptiveQualityLadder = before.Value;
            }

            PeerTranscodeCapability.Reset();
            Assert.Same(previous, Plugin.Instance);
        }
    }

    [Fact]
    public async Task HandedOffMasterGetsLevelsOnlyWhenTheSwitchIsOnAndTheFriendSupportsThem()
    {
        if (Plugin.Instance?.Configuration == null)
        {
            // Without a plugin instance the switch reads as off: nothing is added.
            Assert.Single(Variants(await FetchMasterAsync(true, true)));
            return;
        }

        Assert.Equal(4, Variants(await FetchMasterAsync(true, true)).Count);
        Assert.Single(Variants(await FetchMasterAsync(true, false)));
        Assert.Single(Variants(await FetchMasterAsync(false, true)));
    }
}
