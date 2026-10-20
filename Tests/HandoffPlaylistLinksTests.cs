using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// A friend behind a proxy that tells it "http" writes http addresses into the
/// handoff playlists; a browser on https refuses them. The receiver puts them on
/// the address it uses for the friend, and its relay fetches only friend
/// gateway playlists.
/// </summary>
public class HandoffPlaylistLinksTests
{
    private const string Relay = "/Plugins/Federation/HandoffPlaylist?u=";

    [Fact]
    public void Master_VariantGoesThroughTheRelay_OnTheHttpsAddress()
    {
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5000000\nhttp://friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/main.m3u8?PlaySessionId=1&VideoCodec=h264";
        var rewritten = HandoffPlaylistLinks.Rewrite(master, "https://friend.example", Relay);

        Assert.DoesNotContain("http://", rewritten);
        var variantLine = rewritten.Split('\n')[2];
        Assert.StartsWith(Relay, variantLine);
        Assert.Equal(
            "https://friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/main.m3u8?PlaySessionId=1&VideoCodec=h264",
            System.Uri.UnescapeDataString(variantLine.Substring(Relay.Length)));
    }

    [Fact]
    public void Variant_SegmentsAndInitGoStraightToTheFriendOnHttps()
    {
        var variant = "#EXTM3U\n#EXT-X-MAP:URI=\"http://10.0.0.5:8096/Plugins/Federation/Peer/Play/TOK/videos/abc/hls1/main/-1.mp4?x=1\"\n#EXTINF:6.0,\nhttp://10.0.0.5:8096/Plugins/Federation/Peer/Play/TOK/videos/abc/hls1/main/0.mp4?x=1\n#EXT-X-ENDLIST\n";
        var rewritten = HandoffPlaylistLinks.Rewrite(variant, "https://friend.example/", Relay);

        Assert.Contains("#EXT-X-MAP:URI=\"https://friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/hls1/main/-1.mp4?x=1\"", rewritten);
        Assert.Contains("\nhttps://friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/hls1/main/0.mp4?x=1\n", rewritten);
        Assert.EndsWith("#EXT-X-ENDLIST\n", rewritten);
    }

    [Fact]
    public void LinesWithoutAFriendAddress_AreLeftAlone()
    {
        var playlist = "#EXTM3U\n#EXTINF:6.0,\nsegment0.ts";
        Assert.Equal(playlist, HandoffPlaylistLinks.Rewrite(playlist, "https://friend.example", Relay));
    }

    [Fact]
    public void FirstUri_PrefersTheInitSegment_ThenTheFirstEntry_OnTheFriendAddress()
    {
        var withMap = "#EXTM3U\n#EXT-X-MAP:URI=\"http://10.0.0.5:8096/Plugins/Federation/Peer/Play/T/videos/a/hls1/main/-1.mp4?x=1\"\n#EXTINF:3,\nhttp://10.0.0.5:8096/Plugins/Federation/Peer/Play/T/videos/a/hls1/main/0.mp4?x=1";
        Assert.Equal("https://friend.example/Plugins/Federation/Peer/Play/T/videos/a/hls1/main/-1.mp4?x=1", HandoffPlaylistLinks.FirstUri(withMap, "https://friend.example"));

        var ts = "#EXTM3U\n#EXTINF:3,\nhttp://x/Plugins/Federation/Peer/Play/T/videos/a/hls1/main/0.ts?x=1\n#EXTINF:3,\nhttp://x/Plugins/Federation/Peer/Play/T/videos/a/hls1/main/1.ts?x=1";
        Assert.Equal("https://friend.example/Plugins/Federation/Peer/Play/T/videos/a/hls1/main/0.ts?x=1", HandoffPlaylistLinks.FirstUri(ts, "https://friend.example/"));

        Assert.Null(HandoffPlaylistLinks.FirstUri("#EXTM3U\nsegment0.ts", "https://friend.example"));
        Assert.Null(HandoffPlaylistLinks.TakePrefetched("https://friend.example/never-fetched.m3u8"));
    }

    [Theory]
    [InlineData("https://friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/main.m3u8?a=1", true)]
    [InlineData("https://FRIEND.example/plugins/federation/peer/play/TOK/videos/abc/main.m3u8", true)]
    [InlineData("http://friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/main.m3u8", false)]
    [InlineData("https://friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/hls1/main/0.mp4", false)]
    [InlineData("https://friend.example/System/Info.m3u8", false)]
    [InlineData("https://evil.example/Plugins/Federation/Peer/Play/TOK/videos/abc/main.m3u8", false)]
    [InlineData("https://user:pw@friend.example/Plugins/Federation/Peer/Play/TOK/videos/abc/main.m3u8", false)]
    [InlineData("https://friend.example/Plugins/Federation/Peer/Play/../../System/x.m3u8", false)]
    [InlineData("https://disabled.example/Plugins/Federation/Peer/Play/TOK/videos/abc/main.m3u8", false)]
    [InlineData("file:///etc/passwd.m3u8", false)]
    [InlineData(null, false)]
    public void Relay_FetchesOnlyEnabledFriendGatewayPlaylists(string? url, bool allowed)
    {
        var servers = new List<RemoteServer>
        {
            new() { Id = "f1", Name = "friend", Url = "https://friend.example", Enabled = true, Kind = ServerKind.Jellyfin },
            new() { Id = "f2", Name = "off", Url = "https://disabled.example", Enabled = false, Kind = ServerKind.Jellyfin },
        };

        Assert.Equal(allowed, HandoffPlaylistLinks.FindRelayTarget(url, servers) != null);
    }
}
