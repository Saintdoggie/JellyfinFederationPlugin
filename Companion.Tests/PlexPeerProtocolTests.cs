using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FederationCompanion;
using Microsoft.AspNetCore.Http;

namespace FederationCompanion.Tests;

public sealed class PlexPeerProtocolTests
{
    [Fact]
    public async Task PlexToPlex_ImportsOwnedMovieAndEpisode_AndPreservesRangeAndRevocation()
    {
        var state = new CompanionState { ServerBaseUrl = "http://plex.test", ServerAccessToken = "private-plex-test" };
        state.Libraries.AddRange(new[] { new CompanionLibrary { SectionKey = "1", Title = "Movies", Type = "movie", Shared = true },
            new CompanionLibrary { SectionKey = "2", Title = "Shows", Type = "show", Shared = true },
            new CompanionLibrary { SectionKey = "3", Title = "Imported", Type = "movie", Shared = true } });
        state.ImportPeers.Add(new JellyfinImportPeer { PlexMovieSectionKey = "3" });
        var peer = new CompanionPeer { CompanionConnection = true };
        state.Peers.Add(peer);
        var section = "1";
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("private-plex-test", request.Headers.GetValues("X-Plex-Token").Single());
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/library/sections") return Json(new { MediaContainer = new { Directory = new[] {
                new { key = "1", title = "Movies", type = "movie" }, new { key = "2", title = "Shows", type = "show" }, new { key = "3", title = "Imported", type = "movie" } } } });
            var episode = path.Contains("/2/all") || path.Contains("/metadata/200");
            if (path.Contains("/all") || path.Contains("/metadata/")) return Json(new { MediaContainer = new { Metadata = new[] {
                new { ratingKey = episode ? "200" : "100", title = episode ? "Episode" : "Movie", type = episode ? "episode" : "movie", librarySectionID = episode ? "2" : section,
                    parentIndex = 3, index = 7, grandparentTitle = "Source series", Media = new[] { new { container = "mp4", Part = new[] { new { key = "/library/parts/77/file.mp4", size = 100L } } } } } } } });
            Assert.Equal("/library/parts/77/file.mp4", path);
            Assert.Equal("bytes=10-29", request.Headers.Range!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Enumerable.Range(10, 20).Select(x => (byte)x).ToArray()) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 29, 100);
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        }));
        var protocol = new PlexPeerProtocol(state, new PlexFederationRelay(state, http));
        Assert.Null(protocol.Authenticate("wrong"));
        Assert.Same(peer, protocol.Authenticate(peer.AccessToken));
        peer.PendingConnection = true; Assert.Null(protocol.Authenticate(peer.AccessToken)); peer.PendingConnection = false;
        var libraries = await protocol.LibrariesAsync(peer, default);
        Assert.Equal(new[] { "1", "2" }, libraries.Select(l => l.Id));
        var movies = await protocol.ItemsAsync(peer, "1", "Movie", 0, 200, default);
        var episodes = await protocol.ItemsAsync(peer, "2", "Episode", 0, 200, default);
        Assert.Equal(3, episodes[0].ParentIndexNumber); Assert.Equal(7, episodes[0].IndexNumber);
        Assert.Equal(2, MediaMount.BuildFiles(movies.Concat(episodes)).Count);
        Assert.DoesNotContain("private-plex-test", JsonSerializer.Serialize(movies));
        var grant = await protocol.MintAsync(peer, movies[0].Id, default);
        var stream = Context(); stream.Request.Headers.Range = "bytes=10-29";
        await protocol.StreamAsync(movies[0].Id, grant.Token, stream, default);
        Assert.Equal(206, stream.Response.StatusCode); Assert.Equal("bytes 10-29/100", stream.Response.Headers.ContentRange);
        Assert.Equal(Enumerable.Range(10, 20).Select(x => (byte)x), ((MemoryStream)stream.Response.Body).ToArray());
        var head = Context(); head.Request.Method = "HEAD"; head.Request.Headers.Range = "bytes=10-29";
        await protocol.StreamAsync(movies[0].Id, grant.Token, head, default);
        Assert.Equal(206, head.Response.StatusCode); Assert.Equal(0, head.Response.Body.Length);
        // The exact old capability must fail if the item moves into an imported library.
        section = "3";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => protocol.StreamAsync(movies[0].Id, grant.Token, Context(), default));
        section = "1"; state.Libraries[0].Shared = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => protocol.MintAsync(peer, movies[0].Id, default));
        state.Peers.Clear(); var revoked = Context();
        await protocol.StreamAsync(movies[0].Id, grant.Token, revoked, default);
        Assert.Equal(403, revoked.Response.StatusCode);
    }

    [Fact]
    public async Task PlaybackCapabilities_AreItemBound_Expiring_AndRevokedByCredentialRotation()
    {
        var peer = new CompanionPeer { CompanionConnection = true };
        var state = new CompanionState { Peers = new() { peer } };
        using var http = new HttpClient(new Handler(_ => throw new Exception("Invalid capabilities must not reach Plex.")));
        var protocol = new PlexPeerProtocol(state, new PlexFederationRelay(state, http));
        var item = PlexPeerProtocol.ItemId("100");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var signature = PlexPeerProtocol.Signature(peer, item, expiry);
        foreach (var pair in new[] { (PlexPeerProtocol.ItemId("200"), expiry + "." + signature), (item, "0." + signature), (item, expiry + ".bad") })
        {
            var context = Context(); await protocol.StreamAsync(pair.Item1, pair.Item2, context, default); Assert.Equal(403, context.Response.StatusCode);
        }
        peer.AccessToken = CompanionSecrets.Create();
        var rotated = Context(); await protocol.StreamAsync(item, expiry + "." + signature, rotated, default); Assert.Equal(403, rotated.Response.StatusCode);
        Assert.Null(PlexPeerProtocol.NativeId(Guid.NewGuid().ToString()));
        Assert.Throws<JsonException>(() => PlexPeerProtocol.ItemId("../bad"));
    }

    private static DefaultHttpContext Context() { var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Response.Body = new MemoryStream(); return context; }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request)); }
}
