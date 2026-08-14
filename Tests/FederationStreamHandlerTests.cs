using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Regression coverage for the "seeking is broken / playback stalls" bug in Proxy
/// mode: <see cref="FederationStreamHandler.HandleProxyAsync"/> was reporting
/// <c>Content-Length</c> as <c>rangeStart + remote's partial length</c> instead of
/// just the remote's partial length, overstating it by exactly the seek offset on
/// every non-zero-start Range request - which is every seek, and every buffer-ahead
/// read during ordinary playback. Clients then waited for bytes that would never
/// arrive.
/// </summary>
[Collection("PluginInstance")]
public class FederationStreamHandlerTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationStreamHandler _handler;

    public FederationStreamHandlerTests()
    {
        _plugin = new RealPluginInstance();
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "serverA",
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true
        });

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Moq.Mock.Of<IRemoteServerClientFactory>());
        var federationManager = new FederationLibraryManager(
            Moq.Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Moq.Mock.Of<IRemoteServerClientFactory>(),
            cache,
            bandwidthMonitor);

        _handler = new FederationStreamHandler(NullLogger<FederationStreamHandler>.Instance, federationManager);
    }

    public void Dispose()
    {
        FederationStreamHandler.HttpClientOverride = null;
        _plugin.Dispose();
    }

    private static (HttpRequest Request, HttpResponse Response, MemoryStream Body) MakeContext(string? rangeHeader)
    {
        var context = new DefaultHttpContext();
        if (rangeHeader != null)
        {
            context.Request.Headers["Range"] = rangeHeader;
        }

        var body = new MemoryStream();
        context.Response.Body = body;
        return (context.Request, context.Response, body);
    }

    [Fact]
    public async Task Seek_MidFileRangeRequest_ContentLengthMatchesTheActualPartialBodySize()
    {
        // A 6,000,000-byte file; the client seeks and asks for everything from byte
        // 1,000,000 onward. The remote correctly returns a 206 with a Content-Length
        // of 5,000,000 (the partial length), which is exactly what this proxy must
        // also report to the client - not 6,000,000 (1,000,000 + 5,000,000), which
        // is what the bug reported.
        var remoteBytes = new byte[5_000_000];
        new Random(42).NextBytes(remoteBytes);

        FederationStreamHandler.HttpClientOverride = new HttpClient(new FakeHandler(req =>
        {
            Assert.Equal("bytes=1000000-", string.Join(",", req.Headers.GetValues("Range")));

            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remoteBytes)
            };
            resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(1_000_000, 5_999_999, 6_000_000);
            return resp;
        }));

        var (request, response, body) = MakeContext("bytes=1000000-");

        await _handler.HandleProxyAsync("serverA", Guid.NewGuid().ToString("N"), request, response, CancellationToken.None);

        Assert.Equal(5_000_000, response.ContentLength);
        Assert.Equal(remoteBytes.Length, body.Length);
    }

    [Fact]
    public async Task InitialZeroStartRequest_ContentLengthMatchesFullBody()
    {
        // The one case the bug happened to get right by coincidence (0 + length ==
        // length) - kept as a baseline so a future change can't silently break this
        // path while "fixing" the seek case.
        var remoteBytes = Encoding.UTF8.GetBytes("hello world, this is the whole file");

        FederationStreamHandler.HttpClientOverride = new HttpClient(new FakeHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(remoteBytes) };
            return resp;
        }));

        var (request, response, body) = MakeContext(null);

        await _handler.HandleProxyAsync("serverA", Guid.NewGuid().ToString("N"), request, response, CancellationToken.None);

        Assert.Equal(remoteBytes.Length, response.ContentLength);
        Assert.Equal(remoteBytes.Length, body.Length);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
