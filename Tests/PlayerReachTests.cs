using System.Net;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlayerReachTests
{
    [Theory]
    [InlineData("https://friend.example", false)]
    [InlineData("http://203.0.113.9:8096", false)]
    [InlineData("https://box.tail1234.ts.net", false)]
    [InlineData("http://box.tail1234.ts.net:8096", true)]
    [InlineData("http://192.168.1.20:8096", true)]
    [InlineData("http://10.0.0.5:8096", true)]
    [InlineData("http://172.20.0.3:8096", true)]
    [InlineData("http://100.101.102.103:8096", true)]
    [InlineData("http://127.0.0.1:8096", true)]
    [InlineData("http://[fd7a:115c:a1e0::1]:8096", true)]
    [InlineData("http://jellyfin:8096", true)]
    [InlineData("http://nas.local:8096", true)]
    [InlineData("https://media.home.arpa", true)]
    [InlineData("not a url", false)]
    public void Private_addresses_are_recognised(string url, bool expected)
        => Assert.Equal(expected, PlayerReach.IsPrivateAddress(url));

    [Theory]
    [InlineData("https://friend.example", null, true)]
    [InlineData("https://friend.example", "198.51.100.7", true)]
    [InlineData("http://192.168.1.20:8096", "198.51.100.7", false)]
    [InlineData("http://192.168.1.20:8096", null, false)]
    [InlineData("http://192.168.1.20:8096", "192.168.1.77", true)]
    [InlineData("http://192.168.1.20:8096", "192.168.2.77", false)]
    [InlineData("http://192.168.1.20:8096", "10.88.0.4", false)]
    [InlineData("http://192.168.1.20:8096", "127.0.0.1", false)]
    [InlineData("http://100.101.102.103:8096", "100.64.9.9", true)]
    [InlineData("http://100.101.102.103:8096", "192.168.1.77", false)]
    [InlineData("http://192.168.1.20:8096", "100.64.9.9", false)]
    [InlineData("http://192.168.1.20:8096", "::ffff:192.168.1.77", true)]
    [InlineData("http://nas.local:8096", "192.168.1.77", false)]
    public void A_private_address_is_only_given_to_a_player_on_the_same_network(string url, string? client, bool expected)
        => Assert.Equal(expected, PlayerReach.PlayerCanOpen(url, client == null ? null : IPAddress.Parse(client)));

    [Fact]
    public void Download_to_a_friend_known_by_a_private_address_goes_through_this_server()
    {
        var (route, _, reason) = DeviceDownload.Decide(true, "http://100.101.102.103:8096", "http");
        Assert.Equal(DeviceDownloadRoute.Relay, route);
        Assert.Contains("private address", reason);
        Assert.Equal(DeviceDownloadRoute.Relay, DeviceDownload.Decide(true, "http://192.168.1.20:8096", "https", "https://192.168.1.20:8096").Route);
        Assert.Equal(DeviceDownloadRoute.Redirect, DeviceDownload.Decide(true, "http://203.0.113.9:8096", "http").Route);
    }

    [Fact]
    public void Download_from_a_friend_set_to_proxy_goes_through_this_server()
    {
        var (route, _, reason) = DeviceDownload.Decide(true, "https://friend.example", "https", null, relayOnly: true);
        Assert.Equal(DeviceDownloadRoute.Relay, route);
        Assert.NotNull(reason);
    }

    [Fact]
    public void A_play_is_not_redirected_to_a_private_address_from_outside()
    {
        var source = new MediaSourceInfo
        {
            IsRemote = true,
            Protocol = MediaProtocol.Http,
            Path = "http://192.168.1.20:8096/Plugins/Federation/DirectStream/abc?token=t"
        };
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(source, "http", IPAddress.Parse("198.51.100.7")));
        Assert.Equal(source.Path, FederationDirectStreamRedirectFilter.RedirectTarget(source, "http", IPAddress.Parse("192.168.1.9")));
        source.Path = "http://203.0.113.9:8096/Plugins/Federation/DirectStream/abc?token=t";
        Assert.Equal(source.Path, FederationDirectStreamRedirectFilter.RedirectTarget(source, "http", null));
    }
}
