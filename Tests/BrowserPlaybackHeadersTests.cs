using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Web-server headers that make a browser refuse a friend's video without any
/// server error (a reverse proxy adding a second Access-Control-Allow-Origin left
/// converted episodes loading forever, the friend showing a viewer at 0:00).
/// </summary>
[Collection("BrowserPlaybackHeaders")]
public class BrowserPlaybackHeadersTests : IDisposable
{
    private const string Player = "https://media.example.net";
    private const string Peer = "https://flix.example.net";

    public BrowserPlaybackHeadersTests() => BrowserPlaybackHeaders.Reset();

    public void Dispose()
    {
        BrowserPlaybackHeaders.Reset();
        BrowserPlaybackHeaders.Send = (request, ct) => HandoffPlaylistLinks.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        BrowserPlaybackHeaders.UtcNow = () => DateTime.UtcNow;
    }

    [Fact]
    public void Cors_OneWildcardOrTheOrigin_Passes()
    {
        Assert.Null(BrowserPlaybackHeaders.CorsProblem(new[] { "*" }, Player, required: true));
        Assert.Null(BrowserPlaybackHeaders.CorsProblem(new[] { Player }, Player, required: true));
        Assert.Null(BrowserPlaybackHeaders.CorsProblem(new[] { "HTTPS://MEDIA.example.net/" }, Player, required: true));
    }

    [Fact]
    public void Cors_TwiceOrAListOrAnotherOrigin_IsAProblem()
    {
        Assert.Contains("2 times", BrowserPlaybackHeaders.CorsProblem(new[] { "*", "*" }, Player, required: true));
        Assert.Contains("1 times", BrowserPlaybackHeaders.CorsProblem(new[] { "*, *" }, Player, required: true));
        Assert.Contains("different origin", BrowserPlaybackHeaders.CorsProblem(new[] { Peer }, Player, required: true));
        Assert.DoesNotContain(Peer, BrowserPlaybackHeaders.CorsProblem(new[] { Peer }, Player, required: true), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Player, BrowserPlaybackHeaders.CorsProblem(new[] { Peer }, Player, required: true), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cors_Missing_OnlyMattersWhenRequired()
    {
        Assert.Null(BrowserPlaybackHeaders.CorsProblem(Array.Empty<string>(), Player, required: false));
        Assert.Contains("no Access-Control-Allow-Origin", BrowserPlaybackHeaders.CorsProblem(Array.Empty<string>(), Player, required: true));
    }

    [Fact]
    public void Corp_BlocksOnlyWhatItSays()
    {
        Assert.NotNull(BrowserPlaybackHeaders.CorpProblem(new[] { "same-origin" }, Player, Peer));
        Assert.Null(BrowserPlaybackHeaders.CorpProblem(new[] { "same-site" }, Player, Peer));
        Assert.NotNull(BrowserPlaybackHeaders.CorpProblem(new[] { "same-site" }, Player, "https://flix.other.org"));
        Assert.Null(BrowserPlaybackHeaders.CorpProblem(new[] { "cross-origin" }, Player, "https://flix.other.org"));
        Assert.Null(BrowserPlaybackHeaders.CorpProblem(Array.Empty<string>(), Player, Peer));
    }

    [Theory]
    [InlineData("default-src 'self'", false)]
    [InlineData("default-src 'self'; connect-src 'self' https://flix.example.net", true)]
    [InlineData("connect-src 'self' *.example.net", true)]
    [InlineData("connect-src https:", true)]
    [InlineData("connect-src *", true)]
    [InlineData("connect-src 'self' https://flix.example.net:8920", false)]
    [InlineData("connect-src 'none'", false)]
    [InlineData("script-src 'self'", true)]
    [InlineData("img-src *", true)]
    public void Csp_ConnectSrc(string policy, bool allowed)
        => Assert.Equal(allowed, BrowserPlaybackHeaders.CspAllows(new[] { policy }, "connect-src", Peer + "/Plugins/Federation/Peer/Play/x", Player));

    [Fact]
    public void Csp_EveryPolicyMustAllow()
    {
        var policies = new[] { "connect-src *", "default-src 'self'" };
        Assert.False(BrowserPlaybackHeaders.CspAllows(policies, "connect-src", Peer + "/x", Player));
        Assert.True(BrowserPlaybackHeaders.CspAllows(Array.Empty<string>(), "connect-src", Peer + "/x", Player));
    }

    [Fact]
    public void Origin_IsSchemeHostAndNonDefaultPort()
    {
        Assert.Equal("https://media.example.net", BrowserPlaybackHeaders.Origin("https://Media.example.net/jellyfin/"));
        Assert.Equal("http://10.0.0.2:8096", BrowserPlaybackHeaders.Origin("http://10.0.0.2:8096"));
        Assert.Null(BrowserPlaybackHeaders.Origin("ftp://x"));
        Assert.Null(BrowserPlaybackHeaders.Origin(""));
    }

    [Fact]
    public async Task Check_ProxyAddingASecondHeader_FallsBackAndLogsOnce()
    {
        var friend = new FakeFriend();
        friend.Extra["Access-Control-Allow-Origin"] = "*";
        BrowserPlaybackHeaders.Send = friend.SendAsync;
        var log = new ListLogger();
        var server = Server();

        var problem = await BrowserPlaybackHeaders.ConvertedProblemAsync(server, Peer, Player, log);

        Assert.Contains("2 times", problem);
        Assert.Single(log.Warnings);
        Assert.Contains("flix", log.Warnings[0]);
        Assert.DoesNotContain(Player, log.Warnings[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Peer, log.Warnings[0], StringComparison.OrdinalIgnoreCase);
        var calls = friend.Calls;
        Assert.Equal(problem, await BrowserPlaybackHeaders.ConvertedProblemAsync(server, Peer, Player, log));
        Assert.Equal(calls, friend.Calls);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public async Task Check_CleanFriend_NoProblem_AndAFixIsNoticedAfterTheShortRecheck()
    {
        var now = DateTime.UtcNow;
        BrowserPlaybackHeaders.UtcNow = () => now;
        var friend = new FakeFriend();
        friend.Extra["Access-Control-Allow-Origin"] = "*";
        BrowserPlaybackHeaders.Send = friend.SendAsync;
        var log = new ListLogger();
        var server = Server();
        Assert.NotNull(await BrowserPlaybackHeaders.ConvertedProblemAsync(server, Peer, Player, log));

        friend.Extra.Clear();
        now += BrowserPlaybackHeaders.ProblemFor + TimeSpan.FromSeconds(1);

        Assert.Null(await BrowserPlaybackHeaders.ConvertedProblemAsync(server, Peer, Player, log));
        Assert.Single(log.Infos);
    }

    [Fact]
    public async Task Check_ProxyStrippingTheHeader_IsAProblem()
    {
        var friend = new FakeFriend { PluginOrigin = false };
        BrowserPlaybackHeaders.Send = friend.SendAsync;

        Assert.Contains("no Access-Control-Allow-Origin", await BrowserPlaybackHeaders.ConvertedProblemAsync(Server(), Peer, Player, null));
    }

    [Fact]
    public async Task Check_UnreachableFriend_IsNotAProblem()
    {
        BrowserPlaybackHeaders.Send = (_, _) => throw new HttpRequestException("down");

        var report = await BrowserPlaybackHeaders.CheckAsync(Server(), Peer, Player, null);

        Assert.Null(report.ConvertedProblem);
        Assert.Null(report.DirectPlayProblem);
    }

    [Fact]
    public async Task Check_SameOriginResourcePolicy_IsADirectPlayProblemOnly()
    {
        var friend = new FakeFriend();
        friend.Extra["Cross-Origin-Resource-Policy"] = "same-origin";
        BrowserPlaybackHeaders.Send = friend.SendAsync;

        var report = await BrowserPlaybackHeaders.CheckAsync(Server(), Peer, Player, new ListLogger());

        Assert.Null(report.ConvertedProblem);
        Assert.Contains("Cross-Origin-Resource-Policy", report.DirectPlayProblem);
    }

    [Fact]
    public void IsBrowser_WebPlayersOnly()
    {
        var web = new DefaultHttpContext().Request;
        web.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64; rv:144.0) Gecko/20100101 Firefox/144.0";
        var app = new DefaultHttpContext().Request;
        app.Headers.UserAgent = "Findroid/0.15 ExoPlayerLib/2.19";
        var xhr = new DefaultHttpContext().Request;
        xhr.Headers.Origin = Player;

        Assert.True(FederationRemoteTranscodeFilter.IsBrowser(web));
        Assert.False(FederationRemoteTranscodeFilter.IsBrowser(app));
        Assert.True(FederationRemoteTranscodeFilter.IsBrowser(xhr));
    }

    private static RemoteServer Server() => new() { Id = Guid.NewGuid().ToString(), Name = "flix", Url = Peer };

    /// <summary>A friend behind a proxy: the plugin's CORS headers plus whatever the proxy adds.</summary>
    private sealed class FakeFriend
    {
        public Dictionary<string, string> Extra { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool PluginOrigin { get; set; } = true;

        public int Calls { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var path = request.RequestUri!.AbsolutePath;
            var response = new HttpResponseMessage(path.Contains("/Peer/Play/", StringComparison.Ordinal) && request.Method == HttpMethod.Get ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            };
            if (path.Contains("/Peer/Play/", StringComparison.Ordinal))
            {
                if (PluginOrigin)
                {
                    response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
                }

                response.Headers.TryAddWithoutValidation("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            }
            else if (path.EndsWith("/System/Info/Public", StringComparison.Ordinal))
            {
                response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
            }

            foreach (var (name, value) in Extra)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public List<string> Infos { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
            else if (logLevel == LogLevel.Information)
            {
                Infos.Add(formatter(state, exception));
            }
        }
    }
}
