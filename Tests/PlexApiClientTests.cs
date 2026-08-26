using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers <see cref="PlexApiClient"/> against hand-written synthetic Plex API
/// responses (never real captured data from any actual server - see
/// FederationPluginDetectionTests for the same handler-per-path pattern).
/// </summary>
public class PlexApiClientTests
{
    private sealed class ScriptedPlexHandler : HttpMessageHandler
    {
        private readonly string? _metadataBody;

        public ScriptedPlexHandler(string? metadataBody = null)
        {
            _metadataBody = metadataBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.StartsWith("/library/metadata/", StringComparison.Ordinal))
            {
                return _metadataBody == null
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                    : Respond(_metadataBody);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Respond(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    private static PlexApiClient BuildClient(HttpMessageHandler handler, string baseUrl = "https://plex.example:32400", string token = "test-token")
    {
        var httpClient = new HttpClient(handler);
        return new PlexApiClient(baseUrl, token, httpClient, NullLogger.Instance);
    }

    [Fact]
    public async Task GetImagePathsAsync_ReturnsThumbAndArt_WhenBothPresent()
    {
        const string body = "{\"MediaContainer\":{\"Metadata\":[{" +
            "\"ratingKey\":\"100\",\"thumb\":\"/library/metadata/100/thumb/111\",\"art\":\"/library/metadata/100/art/111\"" +
            "}]}}";
        var client = BuildClient(new ScriptedPlexHandler(body));

        var result = await client.GetImagePathsAsync("100", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("/library/metadata/100/thumb/111", result!.Value.Thumb);
        Assert.Equal("/library/metadata/100/art/111", result.Value.Art);
    }

    [Fact]
    public async Task GetImagePathsAsync_ReturnsNullArt_WhenSourceHasNoBackdrop()
    {
        // Some items (e.g. many TV episodes) have a thumb but no separate art/fanart.
        const string body = "{\"MediaContainer\":{\"Metadata\":[{" +
            "\"ratingKey\":\"200\",\"thumb\":\"/library/metadata/200/thumb/222\"" +
            "}]}}";
        var client = BuildClient(new ScriptedPlexHandler(body));

        var result = await client.GetImagePathsAsync("200", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("/library/metadata/200/thumb/222", result!.Value.Thumb);
        Assert.Null(result.Value.Art);
    }

    [Fact]
    public async Task GetImagePathsAsync_ReturnsNull_WhenItemNotFound()
    {
        var client = BuildClient(new ScriptedPlexHandler(metadataBody: null));

        var result = await client.GetImagePathsAsync("missing", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void BuildStreamUrl_AppendsTokenWithQuestionMark_WhenPathHasNoExistingQuery()
    {
        var client = BuildClient(new ScriptedPlexHandler(), baseUrl: "https://plex.example:32400", token: "abc123");

        var url = client.BuildStreamUrl("/library/metadata/100/thumb/111");

        Assert.Equal("https://plex.example:32400/library/metadata/100/thumb/111?X-Plex-Token=abc123", url);
    }

    [Fact]
    public void BuildStreamUrl_EscapesToken()
    {
        var client = BuildClient(new ScriptedPlexHandler(), token: "a b&c");

        var url = client.BuildStreamUrl("/library/parts/1/1/file.mp4");

        Assert.Contains("X-Plex-Token=a%20b%26c", url, StringComparison.Ordinal);
    }

    private sealed class ScriptedSectionHandler : HttpMessageHandler
    {
        private readonly string _body;

        public ScriptedSectionHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var body = query.Contains("type=1", StringComparison.Ordinal)
                ? _body
                : "{\"MediaContainer\":{\"Metadata\":[]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Regression test: Plex reports one combined bitrate per Media entry (in
    /// kbps), which ApplyMediaDetails used to drop entirely - Jellyfin then had
    /// no idea what a Plex-sourced item's actual data rate was, which is what
    /// made its own client-side quality selector fall back to a low, generic
    /// default regardless of the source's real quality.
    /// </summary>
    [Fact]
    public async Task GetSectionItemsAsync_ConvertsPlexBitrateFromKbpsToVideoStreamBps()
    {
        const string body = "{\"MediaContainer\":{\"Metadata\":[{" +
            "\"ratingKey\":\"300\",\"title\":\"Some Movie\",\"type\":\"movie\"," +
            "\"Media\":[{\"container\":\"mp4\",\"bitrate\":2043,\"videoCodec\":\"h264\",\"width\":1920,\"height\":1080," +
            "\"audioCodec\":\"aac\",\"audioChannels\":2,\"Part\":[{\"key\":\"/library/parts/1/1/file.mp4\"}]}]" +
            "}]}}";
        var client = BuildClient(new ScriptedSectionHandler(body));

        var items = await client.GetSectionItemsAsync(new PlexSection("1", "Movies", "movie"), CancellationToken.None);

        var item = Assert.Single(items);
        var videoStream = Assert.Single(item.Dto.MediaStreams!, s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        Assert.Equal(2043 * 1000, videoStream.BitRate);
    }

    private sealed class ScriptedSectionAndDetailHandler : HttpMessageHandler
    {
        private readonly string _sectionBody;
        private readonly string _detailBody;

        public ScriptedSectionAndDetailHandler(string sectionBody, string detailBody)
        {
            _sectionBody = sectionBody;
            _detailBody = detailBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;

            string body;
            if (path.StartsWith("/library/metadata/", StringComparison.Ordinal))
            {
                body = _detailBody;
            }
            else if (query.Contains("type=1", StringComparison.Ordinal))
            {
                body = _sectionBody;
            }
            else
            {
                body = "{\"MediaContainer\":{\"Metadata\":[]}}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Regression test: the bulk section-listing endpoint (used to sync every
    /// item) never reports color/HDR/Dolby Vision data at all - only the
    /// per-item detail endpoint's Part.Stream array does, confirmed against a
    /// real 4K Dolby Vision/HDR10 remux that the listing endpoint reported no
    /// differently from an ordinary SDR file. ApplyMediaDetailsAsync fetches
    /// that detail endpoint and uses its real per-stream breakdown - both for
    /// HDR classification and for listing every audio track, not just one.
    /// </summary>
    [Fact]
    public async Task GetSectionItemsAsync_CapturesHdrDolbyVisionAndAllAudioTracks_FromDetailEndpoint()
    {
        const string sectionBody = "{\"MediaContainer\":{\"Metadata\":[{" +
            "\"ratingKey\":\"400\",\"title\":\"Some Remux\",\"type\":\"movie\"," +
            "\"Media\":[{\"container\":\"mkv\",\"bitrate\":50000,\"videoCodec\":\"hevc\",\"width\":3840,\"height\":2160," +
            "\"audioCodec\":\"truehd\",\"audioChannels\":8,\"Part\":[{\"key\":\"/library/parts/9/9/file.mkv\"}]}]" +
            "}]}}";

        const string detailBody = "{\"MediaContainer\":{\"Metadata\":[{" +
            "\"ratingKey\":\"400\",\"title\":\"Some Remux\",\"type\":\"movie\"," +
            "\"Media\":[{\"Part\":[{\"key\":\"/library/parts/9/9/file.mkv\",\"Stream\":[" +
            "{\"streamType\":1,\"codec\":\"hevc\",\"width\":3840,\"height\":2160,\"bitrate\":49000,\"bitDepth\":10," +
            "\"colorPrimaries\":\"bt2020\",\"colorSpace\":\"bt2020nc\",\"colorTrc\":\"smpte2084\",\"colorRange\":\"tv\"," +
            "\"DOVIPresent\":true,\"DOVIProfile\":8,\"DOVIBLCompatID\":1,\"DOVIBLPresent\":true,\"DOVIRPUPresent\":true,\"DOVIELPresent\":false}," +
            "{\"streamType\":2,\"codec\":\"truehd\",\"channels\":8,\"bitrate\":5000,\"languageTag\":\"ja\",\"audioChannelLayout\":\"7.1\"}," +
            "{\"streamType\":2,\"codec\":\"ac3\",\"channels\":6,\"bitrate\":448,\"languageTag\":\"en\",\"audioChannelLayout\":\"5.1\",\"selected\":true}" +
            "]}]}]" +
            "}]}}";

        var client = BuildClient(new ScriptedSectionAndDetailHandler(sectionBody, detailBody));

        var items = await client.GetSectionItemsAsync(new PlexSection("1", "Movies", "movie"), CancellationToken.None);

        var item = Assert.Single(items);
        var streams = item.Dto.MediaStreams!;

        var video = Assert.Single(streams, s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        Assert.Equal("smpte2084", video.ColorTransfer);
        Assert.Equal(8, video.DvProfile);
        Assert.Equal(Jellyfin.Data.Enums.VideoRangeType.DOVIWithHDR10, video.VideoRangeType);
        Assert.Equal(Jellyfin.Data.Enums.VideoRange.HDR, video.VideoRange);

        var audioStreams = streams.Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio).ToList();
        Assert.Equal(2, audioStreams.Count);
        Assert.Contains(audioStreams, s => s.Codec == "truehd" && s.Channels == 8);
        var acStream = Assert.Single(audioStreams, s => s.Codec == "ac3");
        Assert.True(acStream.IsDefault);
    }
}
