using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The Plex side of fast starts: the speed sample and the capped-transcode request.
/// All responses are hand-written; nothing here talks to a real server.
/// </summary>
public class PlexFastStartTests
{
    private const string Token = "secret-plex-token";
    private const string MetadataJson = "{\"MediaContainer\":{\"Metadata\":[{\"ratingKey\":\"100\",\"Media\":[{\"Part\":[{\"key\":\"/library/parts/9/file.mkv\"}]}]}]}}";

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _route;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) => _route = route;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_route(request));
        }
    }

    private static PlexApiClient Client(HttpMessageHandler handler)
        => new("http://plex.example:32400", Token, new HttpClient(handler), NullLogger.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(HttpStatusCode code, int count)
        => new(code) { Content = new ByteArrayContent(new byte[count]) };

    // ---- the speed sample ----

    [Fact]
    public async Task SpeedSample_ReadsAFiveMegabyteRangeFromTheMiddleOfTheFile()
    {
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/library/metadata")
            ? Json(MetadataJson)
            : Bytes(HttpStatusCode.PartialContent, 5_000_000));

        var mbps = await Client(handler).MeasureBandwidthMbpsAsync("100", CancellationToken.None);

        Assert.True(mbps > 0);
        var part = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.StartsWith("/library/parts"));
        Assert.Equal(20_000_000, part.Headers.Range!.Ranges.Single().From);
        Assert.Equal(24_999_999, part.Headers.Range.Ranges.Single().To);
    }

    [Fact]
    public async Task SpeedSample_ARangeIgnoringServer_IsNotReadAtAll()
    {
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/library/metadata")
            ? Json(MetadataJson)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream()) });

        Assert.Null(await Client(handler).MeasureBandwidthMbpsAsync("100", CancellationToken.None));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("whole file must not be read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SpeedSample_ForASmallFile_FallsBackToTheStart()
    {
        var handler = new RoutingHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath.StartsWith("/library/metadata"))
            {
                return Json(MetadataJson);
            }

            return r.Headers.Range!.Ranges.Single().From == 0
                ? Bytes(HttpStatusCode.PartialContent, 1_000_000)
                : new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        });

        Assert.True(await Client(handler).MeasureBandwidthMbpsAsync("100", CancellationToken.None) > 0);
    }

    [Fact]
    public async Task SpeedSample_UnknownItemOrUnreachableServer_IsNull()
    {
        var missing = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await Client(missing).MeasureBandwidthMbpsAsync("100", CancellationToken.None));

        var down = new RoutingHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/library/metadata")
            ? Json(MetadataJson)
            : throw new HttpRequestException("down"));
        Assert.Null(await Client(down).MeasureBandwidthMbpsAsync("100", CancellationToken.None));
    }

    // ---- the capped-transcode request ----

    [Fact]
    public void TranscodeUrl_AsksPlexForACappedTranscodeOverHttp()
    {
        var url = new Uri(Client(new RoutingHandler(_ => Json("{}"))).BuildTranscodeUrl("100", 6000, "sess1", 30));
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("/video/:/transcode/universal/start.mkv", url.AbsolutePath);
        Assert.Equal("/library/metadata/100", query["path"]);
        Assert.Equal("http", query["protocol"]);
        Assert.Equal("6000", query["maxVideoBitrate"]);
        Assert.Equal("0", query["directPlay"]);
        Assert.Equal("0", query["directStream"]);
        Assert.Equal("30", query["offset"]);
        Assert.Equal("sess1", query["session"]);
        Assert.Equal(Token, query["X-Plex-Token"]);
    }

    private static HttpResponseMessage Stream(int bytes)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[bytes]) { Headers = { ContentType = new("video/x-matroska") } },
        };

    [Fact]
    public async Task TranscodeProbe_ReportsTheStreamAndStopsTheSession()
    {
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath.Contains("/decision")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<MediaContainer generalDecisionCode=\"2000\" generalDecisionText=\"Direct play not available; Conversion OK\" />") }
            : r.RequestUri!.AbsolutePath.Contains("/start.mkv") ? Stream(2_000_000) : new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Client(handler).ProbeTranscodeAsync("100", 6000, 2, CancellationToken.None);

        Assert.Contains("generalDecisionCode=2000", result.PlexDecision);
        Assert.Equal(200, result.HttpStatus);
        Assert.NotNull(result.FirstByteMs);
        Assert.Equal(2_000_000, result.Bytes);
        Assert.Equal("video/x-matroska", result.ContentType);
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/transcode/universal/stop"));
        Assert.DoesNotContain(Token, result.Detail);
    }

    [Fact]
    public async Task TranscodeProbe_PlexRefusing_IsReportedAndStillStopsTheSession()
    {
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath.Contains("/start.mkv")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html><title>Forbidden " + Token + "</title></html>") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<MediaContainer transcodeDecisionText=\"Transcoding is not allowed for this user\" />") });

        var result = await Client(handler).ProbeTranscodeAsync("100", 6000, 2, CancellationToken.None);

        Assert.Equal(403, result.HttpStatus);
        Assert.Contains("not allowed", result.PlexDecision);
        Assert.Contains("Forbidden", result.PlexMessage);
        Assert.DoesNotContain(Token, result.PlexMessage);
        Assert.DoesNotContain(Token, result.PlexDecision);
        Assert.Null(result.FirstByteMs);
        Assert.Contains("refused", result.Detail);
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/transcode/universal/stop"));
    }

    [Fact]
    public async Task TranscodeProbe_AnAcceptedRequestWithNoData_IsNotReportedAsSuccess()
    {
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath.Contains("/start.mkv") ? Stream(0) : new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Client(handler).ProbeTranscodeAsync("100", 6000, 2, CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.Null(result.Mbps);
        Assert.Contains("no media", result.Detail);
    }

    [Fact]
    public async Task TranscodeProbe_UnreachableServer_IsReportedWithoutThrowing()
    {
        var handler = new RoutingHandler(_ => throw new HttpRequestException("down"));

        var result = await Client(handler).ProbeTranscodeAsync("100", 6000, 2, CancellationToken.None);

        Assert.Equal(0, result.HttpStatus);
        Assert.Contains("Could not reach", result.Detail);
    }
}
