using System.Net;
using System.Text;
using FederationCompanion;
using Microsoft.AspNetCore.Http;

namespace FederationCompanion.Tests;

public sealed class CompanionStreamingTests
{
    [Fact]
    public void StableStreamUrl_BindsCapabilityToExactPeerAndItem()
    {
        var peer = new JellyfinImportPeer
        {
            Id = "peer-1",
            StreamSecret = new string('A', 64)
        };
        var itemId = "11111111-1111-1111-1111-111111111111";

        var url = JellyfinImportService.BuildCompanionStreamUrl("https://companion.example/", peer, itemId);
        var capability = new Uri(url).Query["?cap=".Length..];

        Assert.DoesNotContain(peer.StreamSecret, url, StringComparison.Ordinal);
        Assert.True(JellyfinImportService.IsValidStreamCapability(peer, itemId, capability));
        Assert.False(JellyfinImportService.IsValidStreamCapability(peer, "22222222-2222-2222-2222-222222222222", capability));
        Assert.False(JellyfinImportService.IsValidStreamCapability(peer, itemId, capability[..^1] + "0"));
    }

    [Fact]
    public void StreamCapability_WithMalformedStoredSecret_FailsClosed()
    {
        var peer = new JellyfinImportPeer
        {
            Id = "peer-1",
            StreamSecret = "not-a-valid-secret"
        };

        Assert.False(JellyfinImportService.IsValidStreamCapability(
            peer,
            "11111111-1111-1111-1111-111111111111",
            new string('A', 64)));
    }

    [Fact]
    public async Task RelayStreamAsync_MintsAtPlayTimeAndPreservesRangeResponse()
    {
        var api = new RecordingHandler(request =>
        {
            Assert.Equal("FEDERATION-SECRET", request.Headers.GetValues("X-Federation-Token").Single());
            return Json(HttpStatusCode.OK, "{\"token\":\"PLAY-ONCE\",\"expiresUtc\":\"2030-01-01T00:00:00Z\"}");
        });
        var stream = new RecordingHandler(request =>
        {
            Assert.Equal("bytes=100-199", request.Headers.Range?.ToString());
            Assert.Contains("token=PLAY-ONCE", request.RequestUri!.Query, StringComparison.Ordinal);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("movie-bytes"))
            };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(100, 110, 1000);
            response.Headers.AcceptRanges.Add("bytes");
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            return response;
        });
        var service = new JellyfinImportService(new HttpClient(api), new HttpClient(stream));
        var peer = new JellyfinImportPeer
        {
            Url = "https://jellyfin.example",
            Token = "FEDERATION-SECRET"
        };
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=100-199";
        context.Response.Body = new MemoryStream();

        await service.RelayStreamAsync(
            peer,
            "11111111-1111-1111-1111-111111111111",
            context.Request,
            context.Response,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal("bytes 100-110/1000", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());
        context.Response.Body.Position = 0;
        Assert.Equal("movie-bytes", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public void Export_ReportsUrlOnlyChangesSoPlexCanBeRefreshed()
    {
        var path = Path.Combine(Path.GetTempPath(), "companion-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var item = new PeerItem
            {
                Id = "11111111-1111-1111-1111-111111111111",
                Type = "Movie",
                Name = "Example",
                ProductionYear = 2026
            };

            var first = StrmExporter.Export(path, new[] { (item, "https://old.example/stream") });
            var unchanged = StrmExporter.Export(path, new[] { (item, "https://old.example/stream") });
            var replaced = StrmExporter.Export(path, new[] { (item, "https://new.example/stream") });

            Assert.Equal(new ExportResult(1, 1), first);
            Assert.Equal(new ExportResult(1, 0), unchanged);
            Assert.Equal(new ExportResult(1, 1), replaced);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response(request));
    }
}
