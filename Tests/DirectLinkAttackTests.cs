using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Attacks a hostile friend can send over a direct link (2026-10-22 review). The
/// link's requests reach this server's Jellyfin from loopback, so anything that
/// slips a header or a route past the gate would be trusted more than it should.
/// A pairing-branch review found bare-LF header injection in the paired gate; these
/// prove the released link gate refuses it and every other splitter.
/// </summary>
public class DirectLinkAttackTests
{
    private const string Route = "/Plugins/Federation/Peer/Play/TOK/videos/0123456789abcdef0123456789abcdef/main.m3u8";

    private static DirectLinkRequestHead? Parse(string head) => DirectLinkRequestHead.Parse(Encoding.ASCII.GetBytes(head), out _);

    [Theory]
    [InlineData("X-A: b\nX-Forwarded-For: 10.0.0.5")]
    [InlineData("X-A: b\rX-Forwarded-For: 10.0.0.5")]
    [InlineData("X-A: b\n\rX-Forwarded-For: 10.0.0.5")]
    [InlineData("X-A: b\nHost: evil")]
    [InlineData("X-A: b\n\nGET /System/Info HTTP/1.1")]
    [InlineData("X-A:\nX-Federation-Direct-Link: 0")]
    [InlineData(" X-Forwarded-For: 10.0.0.5")]
    [InlineData("\tX-Forwarded-For: 10.0.0.5")]
    [InlineData("X-Forwarded-For : 10.0.0.5")]
    [InlineData("X-A: b\u0000c")]
    [InlineData("X-A: b\u000bc")]
    [InlineData("X-A: b\u000cc")]
    [InlineData("X-A: b\u007fc")]
    public void HeaderSplitting_IsRefused(string header)
    {
        Assert.Null(Parse("GET " + Route + " HTTP/1.1\r\n" + header + "\r\n\r\n"));
    }

    [Theory]
    [InlineData("GET " + Route + " HTTP/1.1\nX-Forwarded-For: 10.0.0.5\r\n\r\n")]
    [InlineData("GET " + Route + "\nX-Forwarded-For: 10.0.0.5 HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + Route + "\tHTTP/1.1\r\n\r\n")]
    [InlineData("GET  " + Route + " HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + Route + " HTTP/1.1 \r\n\r\n")]
    [InlineData("GET " + Route + " HTTP/1.0\r\n\r\n")]
    [InlineData("get " + Route + " HTTP/1.1\r\n\r\n")]
    [InlineData("POST " + Route + " HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("GET http://127.0.0.1" + Route + " HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + Route + " HTTP/1.1\r\nHost: a\r\nhost: b\r\n\r\n")]
    [InlineData("GET " + Route + " HTTP/1.1\r\nContent-Length: 5\r\n\r\n")]
    [InlineData("GET " + Route + " HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("GET " + Route + " HTTP/1.1\r\ntransfer-encoding : chunked\r\n\r\n")]
    public void RequestLineAndBodyTricks_AreRefused(string head)
    {
        Assert.Null(Parse(head));
    }

    [Theory]
    [InlineData("/Plugins/Federation/Peer/Play/../../../System/Info")]
    [InlineData("/Plugins/Federation/Peer/Play/x/%2e%2e/%2e%2e/%2e%2e/System/Info")]
    [InlineData("/Plugins/Federation/Peer/Play/x/%2E%2E%2F%2E%2E%2FSystem/Info")]
    [InlineData("/Plugins/Federation/Peer/Play/x/..%5c..%5cSystem/Info")]
    [InlineData("/Plugins/Federation/Peer/Play/x\\..\\..\\System\\Info")]
    [InlineData("/Plugins/Federation/Peer/Play//System/Info")]
    [InlineData("/Plugins/Federation/Peer/Play/x/%00/System/Info")]
    [InlineData("/Plugins/Federation/Peer/Play/x#/../../System/Info")]
    [InlineData("/Plugins/Federation/Peer/Play/")]
    [InlineData("/Plugins/Federation/Peer/PlayLog")]
    [InlineData("/Plugins/Federation/Peer/PlayControl")]
    [InlineData("/Plugins/Federation/Peer/BitrateTest/../../../System/Info")]
    [InlineData("/Plugins/Federation/Configuration")]
    [InlineData("/Plugins/Federation/DirectLink/f1/Plugins/Federation/Peer/Play/x")]
    [InlineData("/System/Info")]
    [InlineData("/Users")]
    [InlineData("//Plugins/Federation/Peer/Play/x")]
    [InlineData("Plugins/Federation/Peer/Play/x")]
    [InlineData("*")]
    public void RoutesOffTheLinksShortList_AreRefused(string target)
    {
        Assert.False(DirectLinkRequestHead.IsAllowedTarget(target), target);
        Assert.Null(Parse("GET " + target + " HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public void NonAsciiBytes_AreRefused()
    {
        Assert.False(DirectLinkRequestHead.IsAllowedTarget("/Plugins/Federation/Peer/Play/x/\u00e9"));
        var head = Encoding.UTF8.GetBytes("GET /Plugins/Federation/Peer/Play/x/\u00e9 HTTP/1.1\r\nX-A: \u00e9\r\n\r\n");
        Assert.Null(DirectLinkRequestHead.Parse(head, out _));
    }

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Forwarded-Prefix")]
    [InlineData("X-Forwarded-Port")]
    [InlineData("X-Forwarded-Server")]
    [InlineData("X-Original-For")]
    [InlineData("X-Original-Host")]
    [InlineData("X-Original-Proto")]
    [InlineData("X-Real-IP")]
    [InlineData("X-Client-IP")]
    [InlineData("X-Cluster-Client-IP")]
    [InlineData("CF-Connecting-IP")]
    [InlineData("True-Client-IP")]
    [InlineData("Forwarded")]
    [InlineData("x-forwarded-for")]
    [InlineData("X-Federation-Direct-Link")]
    [InlineData("Host")]
    [InlineData("Connection")]
    [InlineData("Upgrade")]
    [InlineData("Authorization")]
    [InlineData("X-Emby-Authorization")]
    [InlineData("X-Emby-Token")]
    [InlineData("Cookie")]
    [InlineData("Origin")]
    [InlineData("X_Forwarded_For")]
    [InlineData("X-Anything-New")]
    public void ClientAddressAndRoutingHeaders_NeverReachJellyfin(string name)
    {
        var head = Parse("GET " + Route + " HTTP/1.1\r\n" + name + ": 10.0.0.5\r\n\r\n");
        Assert.NotNull(head);
        var forwarded = Encoding.ASCII.GetString(head!.BuildForward("127.0.0.1:8096", "/"));
        Assert.DoesNotContain("10.0.0.5", forwarded, StringComparison.Ordinal);
        Assert.Single(forwarded.Split("\r\n"), l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("Connection: close\r\nX-Federation-Direct-Link: 1\r\n\r\n", forwarded, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinksOwnHeaders_StillPass()
    {
        var head = Parse("GET " + Route + " HTTP/1.1\r\nRange: bytes=5-\r\nIf-Range: \"e\"\r\nAccept: */*\r\nAccept-Encoding: identity\r\nUser-Agent: ua\r\nX-Federation-Token: tok\r\n\r\n");
        var forwarded = Encoding.ASCII.GetString(head!.BuildForward("127.0.0.1:8096", "/"));
        foreach (var line in new[] { "Range: bytes=5-", "If-Range: \"e\"", "Accept: */*", "Accept-Encoding: identity", "User-Agent: ua", "X-Federation-Token: tok" })
        {
            Assert.Contains("\r\n" + line + "\r\n", forwarded, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheForwardedHead_HasExactlyTheLinesTheGateParsed()
    {
        // Whatever passes, the head written to Jellyfin splits into the same lines.
        var head = Parse("GET " + Route + "?a=1&b=%0d%0aX-Evil:1 HTTP/1.1\r\nRange: bytes=0-\r\nX-Federation-Token: tok\r\n\r\n");
        Assert.NotNull(head);
        var forwarded = Encoding.ASCII.GetString(head!.BuildForward("127.0.0.1:8096", "/jf"));
        var lines = forwarded.Split("\r\n");
        Assert.Equal("GET /jf" + Route + "?a=1&b=%0d%0aX-Evil:1 HTTP/1.1", lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("X-Evil", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain('\n', forwarded.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain('\r', forwarded.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.Equal(6, lines.Count(l => l.Length > 0));
    }
}
