using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>Regression tests for the integration-review fixes (data metering, redirects, download refusals).</summary>
public class ReviewFixesTests
{
    [Fact]
    public void Reserve_WithNoDataFolderYet_NeverTouchesDiskAndKeepsCounting()
    {
        string? path = null;
        var calls = 0;
        var l = new FriendDataLimits(null, () => { calls++; return path; }) { UtcNow = () => new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc) };
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(10, l.Reserve("f", 10, 0));
        }

        Assert.Equal(500, l.UsedThisMonth("f"));

        // Once the folder resolves, what was counted so far is merged and written.
        var file = Path.Combine(Path.GetTempPath(), "fed-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            path = file;
            Assert.Equal(10, l.Reserve("f", 10, 0));
            l.Flush();
            var again = new FriendDataLimits(null, () => file) { UtcNow = () => new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc) };
            Assert.Equal(510, again.UsedThisMonth("f"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Reserve_DoesNotWriteTheFileOnTheWritePath()
    {
        var file = Path.Combine(Path.GetTempPath(), "fed-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var l = new FriendDataLimits(null, () => file);
            l.Reserve("f", 10, 0);
            Assert.False(File.Exists(file));
            l.Flush();
            Assert.True(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Reserve_WithACap_NeverOvershootsUnderContention()
    {
        var l = new FriendDataLimits(null, () => null);
        long granted = 0;
        Parallel.For(0, 2000, _ => Interlocked.Add(ref granted, l.Reserve("f", 7, 5000)));
        Assert.Equal(5000, granted);
        Assert.Equal(5000, l.UsedThisMonth("f"));
    }

    [Fact]
    public void Reserve_WithoutACap_CountsEveryByteUnderContention()
    {
        var l = new FriendDataLimits(null, () => null);
        Parallel.For(0, 4000, _ => l.Reserve("f", 3, 0));
        Assert.Equal(12000, l.UsedThisMonth("f"));
    }

    [Theory]
    [InlineData("https://example.com/x", "https://www.example.com/x", true)]
    [InlineData("https://www.example.com/x", "https://example.com/x", true)]
    [InlineData("https://a.example.com/x", "https://b.example.com/x", true)]
    [InlineData("http://example.com/x", "http://www.example.com/x", false)]
    [InlineData("https://example.com/x", "https://example.org/x", false)]
    [InlineData("https://example.com/x", "https://evilexample.com/x", false)]
    [InlineData("https://foo.co.uk/x", "https://bar.co.uk/x", false)]
    [InlineData("https://foo.co.uk/x", "https://www.foo.co.uk/x", true)]
    [InlineData("https://alice.duckdns.org/x", "https://bob.duckdns.org/x", false)]
    [InlineData("https://alice.ts.net/x", "https://bob.ts.net/x", false)]
    [InlineData("https://alice.fly.dev/x", "https://bob.fly.dev/x", false)]
    [InlineData("https://alice.onrender.com/x", "https://bob.onrender.com/x", false)]
    [InlineData("https://alice.run.app/x", "https://bob.run.app/x", false)]
    [InlineData("https://alice.azurecontainer.io/x", "https://bob.azurecontainer.io/x", false)]
    [InlineData("https://10.0.0.1/x", "https://10.0.0.2/x", false)]
    [InlineData("https://example.com/x", "https://www.example.com:8443/x", false)]
    public void TokenRedirect_AllowsOnlySameSiteHttpsHopsBetweenHosts(string from, string to, bool expected)
        => Assert.Equal(expected, TokenRedirect.IsAllowedHop(new Uri(from), new Uri(to)));

    [Fact]
    public async Task TokenRedirect_FollowsApexToWww_ButNotToAnotherSite()
    {
        var seen = new System.Collections.Generic.List<string>();
        var handler = new Fake(req =>
        {
            seen.Add(req.RequestUri!.ToString() + "|" + (req.Headers.TryGetValues("X-Federation-Token", out var v) ? v.First() : "none"));
            if (req.RequestUri.Host == "example.com")
            {
                var r = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                r.Headers.Location = new Uri("https://www.example.com/p");
                return r;
            }

            if (req.RequestUri.Host == "other.net")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://evil.org/p");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        HttpRequestMessage Make(string url)
        {
            var m = new HttpRequestMessage(HttpMethod.Get, url);
            m.Headers.TryAddWithoutValidation("X-Federation-Token", "tok");
            return m;
        }

        using var ok = await TokenRedirect.SendAsync(client, () => Make("https://example.com/p"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("https://www.example.com/p|tok", seen[^1]);

        using var away = await TokenRedirect.SendAsync(client, () => Make("https://other.net/p"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Found, away.StatusCode);
        Assert.DoesNotContain(seen, s => s.Contains("evil.org"));
        Assert.Equal("evil.org", TokenRedirect.CrossHostRedirectTarget(away, new Uri("https://other.net/p")));
    }

    [Fact]
    public async Task FollowHandler_KeepsTheDefaultTokenHeaderOffOtherHosts_AndReportsThem()
    {
        var blocked = new System.Collections.Generic.List<string>();
        var evilHit = false;
        var inner = new Fake(req =>
        {
            if (req.RequestUri!.Host == "evil.org")
            {
                evilHit = true;
            }

            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.org/p");
            return r;
        });
        var handler = new TokenRedirect.FollowHandler((_, to) => blocked.Add(to.Host)) { InnerHandler = inner };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("X-Federation-Token", "tok");
        using var response = await client.GetAsync("https://friend.example/x");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.False(evilHit);
        Assert.Equal(new[] { "evil.org" }, blocked);
    }

    [Fact]
    public async Task FollowHandler_ResendsAPostBodyOnASameHostHttpsUpgrade()
    {
        var seen = new System.Collections.Generic.List<(string Url, string Method, string? Body, string? Token)>();
        var inner = new Fake(req =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            seen.Add((req.RequestUri!.ToString(), req.Method.Method, body, req.Headers.TryGetValues("X-Federation-Token", out var v) ? v.First() : null));
            if (req.RequestUri.Scheme == "http")
            {
                var r = new HttpResponseMessage(HttpStatusCode.PermanentRedirect);
                r.Headers.Location = new Uri("https://friend.example/Plugins/Federation/PlaybackToken");
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(new TokenRedirect.FollowHandler { InnerHandler = inner });
        client.DefaultRequestHeaders.Add("X-Federation-Token", "tok");
        using var response = await client.PostAsync("http://friend.example/Plugins/Federation/PlaybackToken", new StringContent("{\"ItemId\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, seen.Count);
        Assert.Equal(("https://friend.example/Plugins/Federation/PlaybackToken", "POST", "{\"ItemId\":\"x\"}", "tok"), seen[1]);
    }

    [Fact]
    public async Task Download_DataLimitRefusal_IsPermanent_StreamLimitStaysTransient()
    {
        string Path_() => Path.Combine(Path.GetTempPath(), "fed-dl-" + Guid.NewGuid().ToString("N") + ".bin");
        foreach (var (reason, permanent) in new[] { (FriendDataLimits.DataLimitReason, true), (FriendDataLimits.StreamLimitReason, false) })
        {
            using var client = new HttpClient(new Fake(_ =>
            {
                var r = new HttpResponseMessage((HttpStatusCode)429);
                r.Headers.Add(FriendDataLimits.RefusalHeader, reason);
                return r;
            }));
            var path = Path_();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => DownloadTransfer.CopyUrlToFileAsync(client, "http://peer.test/f", path, 0, null, CancellationToken.None));
            Assert.Equal(permanent, ex is DownloadRefusedException);
            Assert.Equal(!permanent, ex is HttpRequestException);
            Assert.Contains(FriendDataLimits.Describe(reason), ex.Message);
        }
    }

    private sealed class Fake : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public Fake(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
