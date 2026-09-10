using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PublicCallbackConnectionTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:1::")]
    public void RejectsNonPublicDnsAnswers(string ip)
        => Assert.False(PublicCallbackConnection.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void AllowsPublicUnicast(string ip)
        => Assert.True(PublicCallbackConnection.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task MixedPublicAndPrivateDnsFailsBeforeAnyConnection()
    {
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await PublicCallbackConnection.ConnectResolvedAsync(
                new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Loopback }, 80, CancellationToken.None));
        Assert.False(FederationFriendService.DefaultVerifyHandler.UseProxy);
        Assert.NotNull(FederationFriendService.DefaultVerifyHandler.ConnectCallback);
    }
}
