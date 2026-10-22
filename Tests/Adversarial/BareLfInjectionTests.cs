using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests.Adversarial;

/// <summary>
/// Attack (adversarial review, 2026-10-22): a friend smuggles extra headers through a
/// bare LF inside one header value. Kestrel splits on bare LF, so an injected
/// <c>X-Forwarded-For</c> or a second <c>X-Federation-Link-Friend</c> reached
/// Jellyfin and unbound the request from its link. The gate now refuses any bare CR
/// or LF and forwards only headers on its short list.
/// </summary>
public class BareLfInjectionTests
{
    private const string Injected = "GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-Federation-Token: t\r\nX-A: b\nX-Forwarded-For: 10.0.0.5\nX-Federation-Link-Friend: other\r\n\r\n";

    [Theory]
    [InlineData(Injected)]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-A: b\rX-Forwarded-For: 10.0.0.5\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\nX-Forwarded-For: 10.0.0.5\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-A: b\n\nGET /System/Info HTTP/1.1\r\n\r\n")]
    public void BareCrOrLf_IsRefused(string head)
    {
        Assert.Null(PairedRequestGate.Parse(Encoding.ASCII.GetBytes(head), true, out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public async Task OverARealKestrel_AnHonestRequestIsBoundToItsLink_AndHeadersOffTheListNeverArrive()
    {
        var seen = new TaskCompletionSource<(string? Friend, string[] Names)>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapFallback(ctx =>
        {
            seen.TrySetResult((PairedLinkContext.FriendId(ctx.Request), ctx.Request.Headers.Keys.ToArray()));
            return ctx.Response.WriteAsync("ok");
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;

        var head = "GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-Federation-Token: t\r\nX-Forwarded-For: 10.0.0.5\r\nCF-Connecting-IP: 10.0.0.5\r\nX-Emby-Token: stolen\r\nCookie: a=b\r\n"
            + PairedLinkContext.FriendHeader + ": other\r\n\r\n";
        var gate = PairedRequestGate.Parse(Encoding.ASCII.GetBytes(head), true, out _)!;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await tcp.GetStream().WriteAsync(gate.BuildForward("127.0.0.1:" + port, "/", "friend-1", "203.0.113.9"));
        await tcp.GetStream().ReadAsync(new byte[256]);
        var (friend, names) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("friend-1", friend);
        foreach (var name in new[] { "X-Forwarded-For", "CF-Connecting-IP", "X-Emby-Token", "Cookie" })
        {
            Assert.DoesNotContain(name, names, StringComparer.OrdinalIgnoreCase);
        }

        await app.StopAsync();
    }
}
