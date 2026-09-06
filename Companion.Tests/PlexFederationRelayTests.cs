using System.Net;
using System.Text;
using System.Text.Json;
using FederationCompanion;
using Microsoft.AspNetCore.Http;

namespace FederationCompanion.Tests;

public sealed class PlexFederationRelayTests
{
    [Fact]
    public async Task Sections_AreFilteredToSourceOwnersCurrentSharingChoices()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("PRIVATE-PLEX-TOKEN", request.Headers.GetValues("X-Plex-Token").Single());
            return Json("""
                {"MediaContainer":{"Directory":[
                  {"key":"1","title":"Shared Movies","type":"movie"},
                  {"key":"2","title":"Private Movies","type":"movie"}
                ]}}
                """);
        });
        var (relay, peer, _) = CreateRelay(handler);
        var context = NewContext();

        await relay.RelayAsync(peer, "library/sections", context.Request, context.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var result = await ResponseJson(context);
        var directories = result.RootElement.GetProperty("MediaContainer").GetProperty("Directory");
        Assert.Single(directories.EnumerateArray());
        Assert.Equal("1", directories[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task UnsharedSection_IsRejectedWithoutContactingPlex()
    {
        var handler = new RecordingHandler(_ => throw new Xunit.Sdk.XunitException("Plex should not be contacted"));
        var (relay, peer, _) = CreateRelay(handler);
        var context = NewContext();

        await relay.RelayAsync(peer, "library/sections/2/all", context.Request, context.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SharedMetadata_AuthorizesExactPartAndPreservesRange()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/library/metadata/100")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[{"ratingKey":"100","librarySectionID":"1","Media":[{"Part":[{"key":"/library/parts/77/file.mkv"}]}]}]}}
                    """);
            }

            Assert.Equal("/library/parts/77/file.mkv", request.RequestUri.AbsolutePath);
            Assert.Equal("bytes=10-19", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("media-data"))
            };
            response.Headers.AcceptRanges.Add("bytes");
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(10, 19, 100);
            return response;
        });
        var (relay, peer, state) = CreateRelay(handler);

        var metadata = NewContext();
        await relay.RelayAsync(peer, "library/metadata/100", metadata.Request, metadata.Response, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, metadata.Response.StatusCode);

        var deniedBulk = NewContext();
        deniedBulk.Request.QueryString = new QueryString("?federationDownload=true&federationBulk=true");
        await relay.RelayAsync(peer, "library/parts/77/file.mkv", deniedBulk.Request, deniedBulk.Response, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, deniedBulk.Response.StatusCode);
        Assert.Equal(1, handler.CallCount);

        var stream = NewContext();
        stream.Request.Headers.Range = "bytes=10-19";
        await relay.RelayAsync(peer, "library/parts/77/file.mkv", stream.Request, stream.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, stream.Response.StatusCode);
        Assert.Equal("bytes", stream.Response.Headers.AcceptRanges.ToString());
        Assert.Equal("bytes 10-19/100", stream.Response.Headers.ContentRange.ToString());

        state.Libraries.Single(l => l.SectionKey == "1").Shared = false;
        var revoked = NewContext();
        await relay.RelayAsync(peer, "library/parts/77/file.mkv", revoked.Request, revoked.Response, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, revoked.Response.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task MetadataOutsideSharedLibrary_IsNeverForwarded()
    {
        var handler = new RecordingHandler(_ => Json(
            "{\"MediaContainer\":{\"Metadata\":[{\"ratingKey\":\"200\",\"librarySectionID\":\"2\"}]}}"));
        var (relay, peer, _) = CreateRelay(handler);
        var context = NewContext();

        await relay.RelayAsync(peer, "library/metadata/200", context.Request, context.Response, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task PreviouslyApprovedPart_IsRevokedWhenPlexMovesItemToPrivateLibrary()
    {
        var section = "1";
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("/library/metadata/100", request.RequestUri!.AbsolutePath);
            return Json(JsonSerializer.Serialize(new { MediaContainer = new { Metadata = new[] {
                new { ratingKey = "100", librarySectionID = section, Media = new[] { new { Part = new[] { new { key = "/library/parts/77/file.mkv" } } } } }
            } } }));
        });
        var (relay, peer, _) = CreateRelay(handler);
        var metadata = NewContext();
        await relay.RelayAsync(peer, "library/metadata/100", metadata.Request, metadata.Response, CancellationToken.None);
        Assert.Equal(200, metadata.Response.StatusCode);
        section = "2";
        var stream = NewContext();
        await relay.RelayAsync(peer, "library/parts/77/file.mkv", stream.Request, stream.Response, CancellationToken.None);
        Assert.Equal(403, stream.Response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    private static (PlexFederationRelay Relay, CompanionPeer Peer, CompanionState State) CreateRelay(HttpMessageHandler handler)
    {
        var state = new CompanionState
        {
            ServerBaseUrl = "https://plex.example:32400",
            ServerAccessToken = "PRIVATE-PLEX-TOKEN",
            Libraries = new List<CompanionLibrary>
            {
                new() { SectionKey = "1", Title = "Shared Movies", Type = "movie", Shared = true },
                new() { SectionKey = "2", Title = "Private Movies", Type = "movie", Shared = false }
            }
        };
        var peer = new CompanionPeer { Id = "peer-1" };
        return (new PlexFederationRelay(state, new HttpClient(handler)), peer, state);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonDocument> ResponseJson(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_response(request));
        }
    }
}
