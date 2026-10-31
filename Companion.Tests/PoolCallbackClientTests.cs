using System.Net;
using FederationCompanion;
using Microsoft.AspNetCore.Http;

namespace FederationCompanion.Tests;

public class PoolCallbackClientTests
{
    [Theory]
    [InlineData("https://friend.example", "https://friend.example")]
    [InlineData(" HTTPS://FRIEND.EXAMPLE:443/ ", "https://friend.example")]
    [InlineData("https://friend.example/jellyfin/", "https://friend.example/jellyfin")]
    [InlineData("https://1.1.1.1:8443/base", "https://1.1.1.1:8443/base")]
    public void NormalizeBaseUrl_AllowsCanonicalPublicHttps(string raw, string expected)
    {
        Assert.True(PoolCallbackClient.TryNormalizeBaseUrl(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("friend.example")]
    [InlineData("//friend.example")]
    [InlineData("http://friend.example")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://user:password@friend.example")]
    [InlineData("https://friend.example/path?query=value")]
    [InlineData("https://friend.example/path#fragment")]
    [InlineData("https://localhost")]
    [InlineData("https://sub.localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::ffff:127.0.0.1]")]
    public void NormalizeBaseUrl_RejectsUnsafeOrNonCanonicalTargets(string? raw)
        => Assert.False(PoolCallbackClient.TryNormalizeBaseUrl(raw, out _));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void AddressPolicy_RejectsNonPublicAddresses(string address)
        => Assert.False(PoolCallbackClient.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("2606:4700:4700::1111")]
    public void AddressPolicy_AllowsPublicUnicast(string address)
        => Assert.True(PoolCallbackClient.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void DedicatedClient_DisablesRedirectsProxyAndCookies()
    {
        using var handler = PoolCallbackClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(PoolCallbackClient.ConnectTimeout, handler.ConnectTimeout);
        Assert.NotNull(handler.ConnectCallback);

        using var client = PoolCallbackClient.CreateClient();
        Assert.Equal(PoolCallbackClient.OverallTimeout, client.Timeout);
    }

    [Fact]
    public async Task SendAsync_ChecksDnsThenUsesNormalizedUrlAndToken()
    {
        var events = new List<string>();
        using var handler = new RecordingHandler(request =>
        {
            events.Add("send");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        var result = await PoolCallbackClient.SendAsync(
            client,
            "https://friend.example/jellyfin/",
            "test-token",
            "/Plugins/Federation/Pools/AcceptNotice",
            new { InviteId = "invite" },
            CancellationToken.None,
            (host, _) =>
            {
                events.Add("dns:" + host);
                return Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") });
            });

        Assert.Equal(PoolCallbackOutcome.Success, result.Outcome);
        Assert.Equal(new[] { "dns:friend.example", "send" }, events);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://friend.example/jellyfin/Plugins/Federation/Pools/AcceptNotice", request.Uri);
        Assert.Equal("test-token", request.Token);
    }

    [Fact]
    public async Task SendAsync_RejectsMixedDnsBeforeSending()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var result = await PoolCallbackClient.SendAsync(
            client,
            "https://friend.example",
            "test-token",
            "/Plugins/Federation/Pools/AcceptNotice",
            new { InviteId = "invite" },
            CancellationToken.None,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Loopback }));

        Assert.Equal(PoolCallbackOutcome.PolicyRejected, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendAsync_ReturnsRedirectWithoutFollowingOrForwardingToken()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://redirect-target.example/collect") }
        });
        using var client = new HttpClient(handler);

        var result = await PoolCallbackClient.SendAsync(
            client,
            "https://friend.example",
            "test-token",
            "/Plugins/Federation/Pools/RejectNotice",
            new { InviteId = "invite" },
            CancellationToken.None,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }));

        Assert.Equal(PoolCallbackOutcome.HttpError, result.Outcome);
        Assert.Equal(StatusCodes.Status302Found, result.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("friend.example", new Uri(request.Uri!).Host);
        Assert.Equal("test-token", request.Token);
    }

    [Fact]
    public async Task ConnectResolvedAsync_RejectsMixedAddressesBeforeConnecting()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            PoolCallbackClient.ConnectResolvedAsync(
                new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Loopback },
                443,
                CancellationToken.None).AsTask());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<(string? Uri, string? Token)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Federation-Token", out var tokens);
            Requests.Add((request.RequestUri?.AbsoluteUri, tokens?.SingleOrDefault()));
            return Task.FromResult(response(request));
        }
    }
}
