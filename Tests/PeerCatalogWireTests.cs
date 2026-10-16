using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Compact, gzipped Peer/Items pages must parse to exactly what the full,
/// uncompressed page did - the receiver only reads a fixed set of fields.
/// </summary>
public sealed class PeerCatalogWireTests
{
    private const string ItemId = "a6f6a6b1c0de4f6f9a1b2c3d4e5f6a7b";

    // Shaped like Jellyfin 12's /Users/{id}/Items output for a sync request.
    private const string FullItem = """
        {
          "Name": "The Matrix", "OriginalTitle": "The Matrix", "ServerId": "f1e2", "Id": "a6f6a6b1c0de4f6f9a1b2c3d4e5f6a7b",
          "Etag": "9c1d", "DateCreated": "2026-10-16T00:00:00.0000000Z", "CanDelete": true, "CanDownload": true,
          "Container": "mkv", "SortName": "matrix", "PremiereDate": "1999-03-30T00:00:00.0000000Z",
          "ExternalUrls": [ { "Name": "IMDb", "Url": "https://www.imdb.com/title/tt0133093" } ],
          "Path": "/srv/media/movies/The Matrix (1999).mkv", "EnableMediaSourceDisplay": true,
          "OfficialRating": "R", "Overview": "A hacker learns the truth.", "Taglines": [ "Free your mind." ],
          "Genres": [ "Action", "Science Fiction" ], "CommunityRating": 8.2, "RunTimeTicks": 81840000000,
          "ProductionYear": 1999, "ProviderIds": { "Tmdb": "603", "Imdb": "tt0133093" },
          "IsHD": false, "IsFolder": false, "ParentId": "0c1d", "Type": "Movie",
          "People": [
            { "Name": "Keanu Reeves", "Id": "11aa", "Role": "Neo", "Type": "Actor", "PrimaryImageTag": "ab12",
              "ImageBlurHashes": { "Primary": { "ab12": "dGF1bG9yZW1pcHN1bWRvbG9yc2l0YW1ldA" } } },
            { "Name": "Lana Wachowski", "Id": "22bb", "Type": "Director" }
          ],
          "Studios": [ { "Name": "Warner Bros. Pictures", "Id": "33cc" } ],
          "GenreItems": [ { "Name": "Action", "Id": "44dd" } ], "LocalTrailerCount": 0,
          "UserData": { "PlaybackPositionTicks": 0, "PlayCount": 3, "IsFavorite": true, "Played": true, "Key": "603" },
          "Tags": [ "cyberpunk" ],
          "MediaSources": [ {
            "Protocol": "File", "Id": "a6f6", "Path": "/srv/media/movies/The Matrix (1999).mkv", "Type": "Default",
            "Container": "mkv", "Size": 1234567890, "Name": "The Matrix", "IsRemote": false, "RunTimeTicks": 81840000000,
            "SupportsTranscoding": true, "SupportsDirectStream": true, "SupportsDirectPlay": true, "Bitrate": 1200000,
            "MediaStreams": [ { "Codec": "h264", "Type": "Video", "Index": 0, "Width": 1920, "Height": 1080, "IsExternal": false } ],
            "MediaAttachments": [], "Formats": [], "RequiredHttpHeaders": {}, "DefaultAudioStreamIndex": 1
          } ],
          "MediaStreams": [
            { "Codec": "h264", "Language": null, "TimeBase": "1/1000", "VideoRange": "SDR", "DisplayTitle": "1080p H264 SDR",
              "IsInterlaced": false, "BitRate": 1000000, "Height": 1080, "Width": 1920, "Type": "Video", "Index": 0, "IsExternal": false },
            { "Codec": "aac", "Language": "eng", "DisplayTitle": "English - AAC - Stereo", "Channels": 2, "SampleRate": 48000,
              "Type": "Audio", "Index": 1, "IsDefault": true, "IsExternal": false },
            { "Codec": "subrip", "Language": "ger", "DisplayTitle": "German - SUBRIP - External", "Type": "Subtitle", "Index": 2,
              "IsExternal": true, "Path": "/srv/media/movies/The Matrix (1999).de.srt" }
          ],
          "ImageTags": { "Primary": "ff01" }, "BackdropImageTags": [ "ee02" ],
          "ImageBlurHashes": { "Primary": { "ff01": "dGF1bG9yZW1pcHN1bWRvbG9yc2l0YW1ldA" } },
          "LocationType": "FileSystem", "MediaType": "Video"
        }
        """;

    [Fact]
    public async Task CompactGzippedPage_ParsesExactlyLikeTheFullPage()
    {
        var full = await ParseAsync(Page(FullItem), compress: false);

        var compactItem = JsonNode.Parse(FullItem)!.AsObject();
        PeerCatalogWire.CompactItem(compactItem);
        var handler = new PageHandler(Page(compactItem.ToJsonString()), compress: true);
        var compact = await ParseWithAsync(handler);

        Assert.Equal(Serialize(full), Serialize(compact));
        Assert.Contains("compact=true", handler.Query, StringComparison.Ordinal);
        Assert.True(handler.AcceptedGzip);
    }

    [Fact]
    public void CompactItem_DropsWhatTheReceiverNeverReads()
    {
        var item = JsonNode.Parse(FullItem)!.AsObject();
        var before = item.ToJsonString().Length;

        PeerCatalogWire.CompactItem(item);
        var json = item.ToJsonString();

        foreach (var gone in new[] { "UserData", "ImageBlurHashes", "ExternalUrls", "/srv/media", "PrimaryImageTag", "\"Path\"", "Taglines", "GenreItems" })
        {
            Assert.DoesNotContain(gone, json, StringComparison.Ordinal);
        }

        var source = Assert.Single(item["MediaSources"]!.AsArray())!.AsObject();
        Assert.Equal(new[] { "Bitrate", "Container", "RunTimeTicks", "Size" }, source.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(3, item["MediaStreams"]!.AsArray().Count);
        Assert.True(json.Length < before * 6 / 10, $"compact {json.Length} vs full {before}");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("br", false)]
    [InlineData("gzip", true)]
    [InlineData("GZIP", true)]
    [InlineData("br, gzip;q=0.5", true)]
    [InlineData("gzip;q=0", false)]
    [InlineData("gzip; q=0.000", false)]
    [InlineData("deflate, gzip ;q=1", true)]
    public void AcceptsGzip_FollowsTheHeader(string? header, bool expected)
        => Assert.Equal(expected, PeerCatalogWire.AcceptsGzip(header));

    [Fact]
    public void Encode_LeavesSmallOrUnacceptedBodiesAlone()
    {
        var small = PeerCatalogWire.Encode("{\"Items\":[]}", "gzip");
        Assert.Null(small.ContentEncoding);
        Assert.Equal("{\"Items\":[]}", Encoding.UTF8.GetString(small.Body));

        var large = Page(FullItem);
        Assert.Null(PeerCatalogWire.Encode(large, null).ContentEncoding);
        var gz = PeerCatalogWire.Encode(large, "gzip");
        Assert.Equal("gzip", gz.ContentEncoding);
        Assert.True(gz.Body.Length < Encoding.UTF8.GetByteCount(large));
    }

    [Fact]
    public async Task ReadBody_RejectsAnEncodingItCannotUndo()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        response.Content.Headers.ContentEncoding.Add("br");
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => PeerCatalogWire.ReadBodyAsync(response, CancellationToken.None));
    }

    private static string Page(string item)
        => "{\"Items\":[" + item + "],\"TotalRecordCount\":1,\"HasMore\":false,\"NextCursor\":1}";

    private static Task<List<MediaBrowser.Model.Dto.BaseItemDto>?> ParseAsync(string body, bool compress)
        => ParseWithAsync(new PageHandler(body, compress));

    private static async Task<List<MediaBrowser.Model.Dto.BaseItemDto>?> ParseWithAsync(PageHandler handler)
    {
        var server = new RemoteServer { Id = "s", Name = "Friend", Url = "http://friend.local", ApiKey = "token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(server.Url) });
        var items = await client.GetItemsAsync(mediaType: "Movie", parentId: "lib", startIndex: 0, limit: 200);
        Assert.NotNull(items);
        Assert.Equal(Guid.Parse(ItemId), Assert.Single(items!).Id);
        return items;
    }

    private static string Serialize(object? value) => JsonSerializer.Serialize(value);

    private sealed class PageHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly bool _compress;

        public PageHandler(string body, bool compress)
        {
            _body = body;
            _compress = compress;
        }

        public string Query { get; private set; } = string.Empty;

        public bool AcceptedGzip { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri?.Query ?? string.Empty;
            AcceptedGzip = request.Headers.AcceptEncoding.Any(e => e.Value == "gzip");
            var (body, encoding) = _compress
                ? PeerCatalogWire.Encode(_body, string.Join(", ", request.Headers.AcceptEncoding))
                : (Encoding.UTF8.GetBytes(_body), null);
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (encoding != null)
            {
                content.Headers.ContentEncoding.Add(encoding);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
