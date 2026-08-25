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
/// Covers <see cref="PlexCatalogProvider"/>'s thin adapter behavior over
/// <see cref="PlexApiClient"/> - translating Plex's vocabulary/paths into the
/// shapes <see cref="IExternalCatalogProvider"/> callers expect.
/// </summary>
public class PlexCatalogProviderTests : IDisposable
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly string _body;

        public ScriptedHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose() => PlexCatalogProvider.HttpClientOverride = null;

    private static RemoteServer PlexServer() => new()
    {
        Id = "plex1",
        Name = "Friend's Plex",
        Url = "https://plex.example:32400",
        ApiKey = "plex-token",
        Kind = ServerKind.Plex,
        Enabled = true
    };

    [Fact]
    public async Task GetImagesAsync_ReturnsTokenBearingUrls_ForBothThumbAndArt()
    {
        const string body = "{\"MediaContainer\":{\"Metadata\":[{" +
            "\"ratingKey\":\"100\",\"thumb\":\"/library/metadata/100/thumb/111\",\"art\":\"/library/metadata/100/art/111\"" +
            "}]}}";
        PlexCatalogProvider.HttpClientOverride = new HttpClient(new ScriptedHandler(body));
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);

        var images = await provider.GetImagesAsync(PlexServer(), "100", CancellationToken.None);

        Assert.NotNull(images);
        Assert.Equal("https://plex.example:32400/library/metadata/100/thumb/111?X-Plex-Token=plex-token", images!.PrimaryUrl);
        Assert.Equal("https://plex.example:32400/library/metadata/100/art/111?X-Plex-Token=plex-token", images.BackdropUrl);
    }

    [Fact]
    public async Task GetImagesAsync_LeavesBackdropNull_WhenSourceHasNone()
    {
        const string body = "{\"MediaContainer\":{\"Metadata\":[{" +
            "\"ratingKey\":\"200\",\"thumb\":\"/library/metadata/200/thumb/222\"" +
            "}]}}";
        PlexCatalogProvider.HttpClientOverride = new HttpClient(new ScriptedHandler(body));
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);

        var images = await provider.GetImagesAsync(PlexServer(), "200", CancellationToken.None);

        Assert.NotNull(images);
        Assert.NotNull(images!.PrimaryUrl);
        Assert.Null(images.BackdropUrl);
    }

    [Fact]
    public async Task GetImagesAsync_ReturnsNull_WhenServerHasNoCredential()
    {
        var server = PlexServer();
        server.ApiKey = string.Empty;
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);

        var images = await provider.GetImagesAsync(server, "100", CancellationToken.None);

        Assert.Null(images);
    }
}
