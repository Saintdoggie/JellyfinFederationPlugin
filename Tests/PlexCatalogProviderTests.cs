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

    private const string TwoSectionsBody = "{\"MediaContainer\":{\"Directory\":["
        + "{\"key\":\"1\",\"title\":\"Movies\",\"type\":\"movie\"},"
        + "{\"key\":\"2\",\"title\":\"Home Videos (personal)\",\"type\":\"movie\"}"
        + "]}}";

    /// <summary>
    /// Regression coverage for the "a friend's declined-to-share library is
    /// hidden from the picker but still gets synced anyway" bug: unlike a
    /// Jellyfin friend (whose own server enforces sharing remotely - see
    /// FederationPeerAccessService), a Plex access token has no per-library
    /// scope, so RemoteServer.AllowedExternalLibraryIds is this side's only
    /// enforcement point.
    /// </summary>
    [Fact]
    public async Task GetLibrariesAsync_FiltersOutSectionsNotInAllowList()
    {
        PlexCatalogProvider.HttpClientOverride = new HttpClient(new ScriptedHandler(TwoSectionsBody));
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);
        var server = PlexServer();
        server.AllowedExternalLibraryIds = new System.Collections.Generic.List<string> { "1" };

        var libraries = await provider.GetLibrariesAsync(server, CancellationToken.None);

        var library = Assert.Single(libraries);
        Assert.Equal("1", library.Id);
    }

    [Fact]
    public async Task GetLibrariesAsync_ReturnsEverySection_WhenAllowListIsNull()
    {
        // Null must mean "no restriction recorded" - a server configured before
        // this field existed (or without ever using the connect-code handshake
        // that would populate it) must keep working exactly as before.
        PlexCatalogProvider.HttpClientOverride = new HttpClient(new ScriptedHandler(TwoSectionsBody));
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);
        var server = PlexServer();
        Assert.Null(server.AllowedExternalLibraryIds);

        var libraries = await provider.GetLibrariesAsync(server, CancellationToken.None);

        Assert.Equal(2, libraries.Count);
    }

    [Fact]
    public async Task GetItemsAsync_RefusesToSync_WhenLibraryNotInAllowList()
    {
        // Must return null (the existing "keep cached content" outcome), never
        // an empty list - an empty list would read as "this library is now
        // empty" and delete everything already synced from it, which would turn
        // a friend declining to share a library into a destructive data loss
        // event instead of just freezing the last-allowed state.
        PlexCatalogProvider.HttpClientOverride = new HttpClient(new ScriptedHandler(TwoSectionsBody));
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);
        var server = PlexServer();
        server.AllowedExternalLibraryIds = new System.Collections.Generic.List<string> { "1" };

        var items = await provider.GetItemsAsync(server, "2", CancellationToken.None);

        Assert.Null(items);
    }

    [Fact]
    public async Task GetAllItemsAsync_IgnoresAllowList_UnlikeGetItemsAsync()
    {
        // Browse (backed by GetAllItemsAsync) is ad-hoc exploration, not sync -
        // it must return items for a library that isn't (or isn't yet) in
        // AllowedExternalLibraryIds, otherwise a freshly-connected Plex server
        // with nothing allowed yet (the common starting state) shows a
        // permanently empty Browse tab even though nothing there requires sync
        // consent.
        const string itemsBody = "{\"MediaContainer\":{\"Metadata\":[{"
            + "\"ratingKey\":\"300\",\"title\":\"Some Movie\",\"type\":\"movie\","
            + "\"Media\":[{\"container\":\"mp4\",\"videoCodec\":\"h264\",\"width\":1920,\"height\":1080,"
            + "\"audioCodec\":\"aac\",\"audioChannels\":2,\"Part\":[{\"key\":\"/library/parts/1/1/file.mp4\"}]}]"
            + "}]}}";
        PlexCatalogProvider.HttpClientOverride = new HttpClient(new PathScriptedHandler(TwoSectionsBody, itemsBody));
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);
        var server = PlexServer();
        server.AllowedExternalLibraryIds = new System.Collections.Generic.List<string>();

        var items = await provider.GetAllItemsAsync(server, "1", CancellationToken.None);

        Assert.NotNull(items);
        Assert.Single(items!);
    }

    [Fact]
    public async Task GetItemsAsync_Syncs_WhenLibraryIsInAllowList()
    {
        const string itemsBody = "{\"MediaContainer\":{\"Metadata\":[{"
            + "\"ratingKey\":\"300\",\"title\":\"Some Movie\",\"type\":\"movie\","
            + "\"Media\":[{\"container\":\"mp4\",\"videoCodec\":\"h264\",\"width\":1920,\"height\":1080,"
            + "\"audioCodec\":\"aac\",\"audioChannels\":2,\"Part\":[{\"key\":\"/library/parts/1/1/file.mp4\"}]}]"
            + "}]}}";
        PlexCatalogProvider.HttpClientOverride = new HttpClient(new PathScriptedHandler(TwoSectionsBody, itemsBody));
        var provider = new PlexCatalogProvider(NullLogger<PlexCatalogProvider>.Instance);
        var server = PlexServer();
        server.AllowedExternalLibraryIds = new System.Collections.Generic.List<string> { "1" };

        var items = await provider.GetItemsAsync(server, "1", CancellationToken.None);

        Assert.NotNull(items);
        Assert.Single(items!);
    }

    private sealed class PathScriptedHandler : HttpMessageHandler
    {
        private readonly string _sectionsBody;
        private readonly string _itemsBody;

        public PathScriptedHandler(string sectionsBody, string itemsBody)
        {
            _sectionsBody = sectionsBody;
            _itemsBody = itemsBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path == "/library/sections" ? _sectionsBody : _itemsBody;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
