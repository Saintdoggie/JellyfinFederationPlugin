using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Federation.Tests.Adversarial;

/// <summary>Attack: a friend smuggles extra headers through a bare LF inside one header value.</summary>
public class BareLfInjectionTests
{
    private readonly ITestOutputHelper _out;

    public BareLfInjectionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Gate_AcceptsBareLfInHeaderValue_AndForwardsItAsASecondHeaderLine()
    {
        var head = "GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-Federation-Token: t\r\nX-A: b\nX-Forwarded-For: 10.0.0.5\nX-Federation-Link-Friend: other\r\n\r\n";
        var gate = PairedRequestGate.Parse(Encoding.ASCII.GetBytes(head), true, out var refusal);
        Assert.True(gate != null, "refused: " + refusal);
        var forwarded = Encoding.ASCII.GetString(gate!.BuildForward("127.0.0.1:8096", "/", "friend-1", "203.0.113.9"));
        _out.WriteLine(forwarded.Replace("\r", "\\r").Replace("\n", "\\n\n"));
        // Splitting on bare LF as a lenient server does, the stripped headers are back.
        var lines = forwarded.Replace("\r\n", "\n").Split('\n');
        Assert.Contains(lines, l => l.StartsWith("X-Forwarded-For:", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(l => l.StartsWith("X-Federation-Link-Friend:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RealKestrel_SeesTheInjectedHeaders()
    {
        var seen = new TaskCompletionSource<Dictionary<string, string>>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapFallback(ctx =>
        {
            seen.TrySetResult(ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()));
            return ctx.Response.WriteAsync("ok");
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;

        var head = "GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-A: b\nX-Forwarded-For: 10.0.0.5\nX-Federation-Link-Friend: other\r\n\r\n";
        var gate = PairedRequestGate.Parse(Encoding.ASCII.GetBytes(head), true, out _)!;
        var forwarded = gate.BuildForward("127.0.0.1:" + port, "/", "friend-1", "203.0.113.9");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await tcp.GetStream().WriteAsync(forwarded);
        var buffer = new byte[512];
        var n = await tcp.GetStream().ReadAsync(buffer);
        _out.WriteLine(Encoding.ASCII.GetString(buffer, 0, n));
        var done = await Task.WhenAny(seen.Task, Task.Delay(3000));
        if (done != seen.Task)
        {
            _out.WriteLine("Kestrel rejected the request (bare LF not accepted).");
            return;
        }

        var headers = await seen.Task;
        foreach (var (k, v) in headers) _out.WriteLine(k + ": " + v);
        Assert.True(headers.ContainsKey("X-Forwarded-For"), "X-Forwarded-For injected past the gate");
        await app.StopAsync();
    }

    [Fact]
    public async Task InjectedDuplicateLinkHeaders_MakeTheRequestLookLikeItDidNotComeOverTheLink()
    {
        var result = new TaskCompletionSource<(string? Friend, bool SecretSeen)>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapFallback(ctx =>
        {
            result.TrySetResult((PairedLinkContext.FriendId(ctx.Request), ctx.Request.Headers.ContainsKey(PairedLinkContext.SecretHeader)));
            return ctx.Response.WriteAsync("ok");
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        async Task<string?> Send(string head)
        {
            var g = PairedRequestGate.Parse(Encoding.ASCII.GetBytes(head), true, out _)!;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await tcp.GetStream().WriteAsync(g.BuildForward("127.0.0.1:" + port, "/", "friend-1", "203.0.113.9"));
            await tcp.GetStream().ReadAsync(new byte[256]);
            return (await result.Task).Friend;
        }

        // Control: an honest request is recognised as coming over friend-1's link.
        Assert.Equal("friend-1", await Send("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-Federation-Token: t\r\n\r\n"));
        result = new TaskCompletionSource<(string?, bool)>();
        // Attack: same request with a bare-LF injected X-Federation-Link-Friend line.
        var friend = await Send("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-Federation-Token: t\r\nX-A: b\nX-Federation-Link-Friend: x\r\n\r\n");
        _out.WriteLine("FriendId with injection = " + (friend ?? "null"));
        Assert.Equal("friend-1", friend); // fails when the attack works: link binding is gone
    }
}
