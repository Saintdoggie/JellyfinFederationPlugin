using System;
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
}
