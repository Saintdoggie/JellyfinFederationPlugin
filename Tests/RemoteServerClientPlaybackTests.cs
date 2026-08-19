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
/// Covers RemoteServerClient's playback/items/item calls under the scoped
/// federation-token model: no remote user impersonation, no /Users round trip,
/// no ResolveActingUserIdAsync fallback dance - every call goes straight to this
/// plugin's own Peer/* endpoints on the remote, authenticated with the
/// federation token alone. Supersedes the pre-token-model version of this file,
/// which tested an admin-fallback user-resolution mechanism that no longer
/// exists (the whole point of the new model is that nothing here impersonates
/// any of the remote's own users any more).
/// </summary>
public class RemoteServerClientPlaybackTests
{
    [Fact]
    public async Task GetPlaybackInfoAsync_CallsPeerEndpoint_NoUserResolution()
    {
        var playbackJson = "{\"PlaySessionId\":\"abc\",\"MediaSources\":[" +
                           "{\"Id\":\"src1\",\"Path\":\"http://remote/video\",\"Container\":\"mkv\"," +
                           "\"Size\":12345,\"Bitrate\":10000000}]}";

        var handler = new FakeHttpMessageHandler(playbackJson: playbackJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var itemId = Guid.NewGuid().ToString("N");
        var result = await client.GetPlaybackInfoAsync(itemId, cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.MediaSources!);
        Assert.Equal("mkv", result.MediaSources![0].Container);
        Assert.Equal($"/Plugins/Federation/Peer/PlaybackInfo/{itemId}", handler.LastRequestedPath);
        Assert.False(handler.CalledAnyNativeUsersOrItemsEndpoint, "Must never call Jellyfin's native /Users or /Users/{id}/Items - only this plugin's own Peer/* routes.");
    }

    [Fact]
    public void GetPlaybackInfoAsync_SendsFederationTokenHeader_NotEmbyToken()
    {
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };

        // CreateDefaultHttpClient (the code path that actually sets the header in
        // production) is only exercised by the single-arg constructor - the
        // shared-HttpClient constructor used everywhere else in this file
        // bypasses it entirely, so this test targets that constructor
        // specifically.
        using var client = new RemoteServerClient(server, NullLogger.Instance);
        Assert.Contains("federation-token", GetDefaultHeaderValues(client, FederationTokenAuth.Header));
        Assert.Empty(GetDefaultHeaderValues(client, "X-Emby-Token"));
    }

    [Fact]
    public async Task GetItemsAsync_CallsPeerEndpoint_NoUserResolution()
    {
        var itemsJson = "{\"Items\":[{\"Id\":\"33333333-3333-3333-3333-333333333333\",\"Name\":\"A Movie\"}]}";
        var handler = new FakeHttpMessageHandler(itemsJson: itemsJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var result = await client.GetItemsAsync(cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("A Movie", result[0].Name);
        Assert.Equal("/Plugins/Federation/Peer/Items", handler.LastRequestedPath);
        Assert.False(handler.CalledAnyNativeUsersOrItemsEndpoint);
    }

    [Fact]
    public async Task GetItemsAsync_ForwardsFilterParams()
    {
        var handler = new FakeHttpMessageHandler(itemsJson: "{\"Items\":[]}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        await client.GetItemsAsync(mediaType: "Movie", parentId: "lib-1", startIndex: 10, limit: 5, cancellationToken: CancellationToken.None);

        Assert.Contains("mediaType=Movie", handler.LastRequestedQuery);
        Assert.Contains("parentId=lib-1", handler.LastRequestedQuery);
        Assert.Contains("startIndex=10", handler.LastRequestedQuery);
        Assert.Contains("limit=5", handler.LastRequestedQuery);
    }

    [Fact]
    public async Task GetItemAsync_CallsPeerEndpoint_NoUserResolution()
    {
        var itemJson = "{\"Id\":\"33333333-3333-3333-3333-333333333333\",\"Name\":\"A Movie\"}";
        var handler = new FakeHttpMessageHandler(itemJson: itemJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var result = await client.GetItemAsync("33333333-3333-3333-3333-333333333333", cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("A Movie", result!.Name);
        Assert.Equal("/Plugins/Federation/Peer/Items/33333333-3333-3333-3333-333333333333", handler.LastRequestedPath);
        Assert.False(handler.CalledAnyNativeUsersOrItemsEndpoint);
    }

    [Fact]
    public async Task GetLibrariesAsync_CallsPeerEndpoint_NoUserResolution()
    {
        var librariesJson = "{\"Items\":[{\"Id\":\"lib-1\",\"Name\":\"Movies\",\"CollectionType\":\"movies\"}],\"TotalRecordCount\":1}";
        var handler = new FakeHttpMessageHandler(librariesJson: librariesJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var result = await client.GetLibrariesAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("Movies", result[0].Name);
        Assert.Equal("/Plugins/Federation/Peer/Libraries", handler.LastRequestedPath);
        Assert.False(handler.CalledAnyNativeUsersOrItemsEndpoint);
    }

    [Fact]
    public async Task GetUsersAsync_CallsPeerUsersEndpoint_NotNativeUsers()
    {
        var usersJson = "[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"someone\",\"Policy\":{\"IsAdministrator\":true}}]";
        var handler = new FakeHttpMessageHandler(usersJson: usersJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var result = await client.GetUsersAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.True(result![0].IsAdministrator);
        Assert.Equal("/Plugins/Federation/Peer/Users", handler.LastRequestedPath);
    }

    [Fact]
    public async Task GetSystemInfoDetailedAsync_CallsPeerEndpoint_NotNativeSystemInfo()
    {
        var systemInfoJson = "{\"ServerName\":\"Remote\",\"Version\":\"10.11.0\",\"Id\":\"abc\",\"FederationPluginVersion\":\"0.0.70\"}";
        var handler = new FakeHttpMessageHandler(systemInfoJson: systemInfoJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var (info, error) = await client.GetSystemInfoDetailedAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Null(error);
        Assert.Equal("Remote", info!.ServerName);
        Assert.Equal("/Plugins/Federation/Peer/SystemInfo", handler.LastRequestedPath);
    }

    [Fact]
    public async Task GetSystemInfoDetailedAsync_Unauthorized_ReportsTokenProblem_NotJellyfinPermissions()
    {
        var handler = new FakeHttpMessageHandler(systemInfoStatusCode: HttpStatusCode.Unauthorized);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "stale-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var (info, error) = await client.GetSystemInfoDetailedAsync(CancellationToken.None);

        Assert.Null(info);
        Assert.Contains("federation token", error, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetDefaultHeaderValues(RemoteServerClient client, string headerName)
    {
        var field = typeof(RemoteServerClient).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var httpClient = (HttpClient)field!.GetValue(client)!;
        return httpClient.DefaultRequestHeaders.TryGetValues(headerName, out var values) ? System.Linq.Enumerable.ToArray(values) : Array.Empty<string>();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _playbackJson;
        private readonly string _itemsJson;
        private readonly string _itemJson;
        private readonly string _librariesJson;
        private readonly string _usersJson;
        private readonly string _systemInfoJson;
        private readonly HttpStatusCode _systemInfoStatusCode;

        public string? LastRequestedPath { get; private set; }
        public string LastRequestedQuery { get; private set; } = string.Empty;
        public bool CalledAnyNativeUsersOrItemsEndpoint { get; private set; }

        public FakeHttpMessageHandler(
            string playbackJson = "{\"MediaSources\":[]}",
            string itemsJson = "{\"Items\":[]}",
            string itemJson = "{}",
            string librariesJson = "{\"Items\":[]}",
            string usersJson = "[]",
            string systemInfoJson = "{}",
            HttpStatusCode systemInfoStatusCode = HttpStatusCode.OK)
        {
            _playbackJson = playbackJson;
            _itemsJson = itemsJson;
            _itemJson = itemJson;
            _librariesJson = librariesJson;
            _usersJson = usersJson;
            _systemInfoJson = systemInfoJson;
            _systemInfoStatusCode = systemInfoStatusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            LastRequestedPath = path;
            LastRequestedQuery = request.RequestUri?.Query ?? string.Empty;

            if (path.Equals("/Users", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(path, "^/Users/[^/]+/Items"))
            {
                CalledAnyNativeUsersOrItemsEndpoint = true;
            }

            if (path.Equals("/Plugins/Federation/Peer/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/Plugins/Federation/Peer/PlaybackInfo/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(_playbackJson));
            }

            if (path.Equals("/Plugins/Federation/Peer/Items", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(_itemsJson));
            }

            if (path.StartsWith("/Plugins/Federation/Peer/Items/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(_itemJson));
            }

            if (path.Equals("/Plugins/Federation/Peer/Libraries", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(_librariesJson));
            }

            if (path.Equals("/Plugins/Federation/Peer/Users", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(_usersJson));
            }

            if (path.Equals("/Plugins/Federation/Peer/SystemInfo", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(_systemInfoStatusCode)
                {
                    Content = new StringContent(_systemInfoJson, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(Json("{}"));
        }

        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
