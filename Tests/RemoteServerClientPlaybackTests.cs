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
    public async Task GetPlaybackTokenAsync_ForwardsLocalActingUserId_AsHeader()
    {
        // Without this header, the remote's IssuePlaybackToken has no way to
        // know which of the caller's local users is actually requesting
        // playback, so its own per-remote-user RemoteUserAccessRule check is
        // structurally unable to restrict anyone at the moment it actually
        // grants access - see GetPlaybackTokenAsync's own doc comment for the
        // full story. This pins that the header is genuinely sent, not just
        // documented.
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var userId = Guid.NewGuid().ToString("N");
        var (token, _) = await client.GetPlaybackTokenAsync("item-1", CancellationToken.None, localActingUserId: userId);

        Assert.Equal("tok-123", token);
        Assert.Equal("/Plugins/Federation/PlaybackToken", handler.LastRequestedPath);
        Assert.Equal(userId, handler.LastRemoteUserIdHeader);
    }

    [Fact]
    public async Task GetPlaybackTokenAsync_NoLocalActingUserId_SendsNoHeader()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        await client.GetPlaybackTokenAsync("item-1", CancellationToken.None);

        Assert.Null(handler.LastRemoteUserIdHeader);
    }

    [Fact]
    public async Task GetPlaybackTokenAsync_CachesAcrossCalls_ForSameServerItemAndUser()
    {
        // The relay/proxy path re-requests a token on every player seek/probe
        // re-open (several times per second during federated mp4 playback) - see
        // ItemPlaybackTokenCache's doc comment. Without caching, each of those
        // paid a full WAN round trip and starved the transcoder's input until
        // ffmpeg died with exit code 183.
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "server-tokcache-" + Guid.NewGuid().ToString("N"), Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var first = await client.GetPlaybackTokenAsync("item-1", CancellationToken.None);
        var second = await client.GetPlaybackTokenAsync("item-1", CancellationToken.None);
        var third = await client.GetPlaybackTokenAsync("item-1", CancellationToken.None, localActingUserId: "user-1");

        Assert.Equal("tok-123", first.Token);
        Assert.Equal("tok-123", second.Token);
        Assert.Equal("tok-123", third.Token);
        // Two mints, not three: the no-user pair shares one cache entry and the
        // user-scoped call mints its own (per-user rules apply at mint time on
        // the remote, so a user-scoped mint must not be reused for no-user and
        // vice versa).
        Assert.Equal(2, handler.PlaybackTokenCallCount);
    }

    [Fact]
    public async Task GetPlaybackTokenAsync_InvalidateItemPlaybackToken_ForcesRemint()
    {
        // A token the remote just rejected (remote restarted, friendship removed)
        // must be dropped on demand so the relay's 401/403 recovery path can get
        // a fresh one instead of pulling the dead token back out of the cache.
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "server-tokinv-" + Guid.NewGuid().ToString("N"), Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        await client.GetPlaybackTokenAsync("item-1", CancellationToken.None);
        RemoteServerClient.InvalidateItemPlaybackToken(server.Id, "item-1", null);
        await client.GetPlaybackTokenAsync("item-1", CancellationToken.None);

        Assert.Equal(2, handler.PlaybackTokenCallCount);

        // Invalidation keyed to a different item/user must not disturb this one.
        RemoteServerClient.InvalidateItemPlaybackToken(server.Id, "item-other", null);
        await client.GetPlaybackTokenAsync("item-1", CancellationToken.None);
        Assert.Equal(2, handler.PlaybackTokenCallCount);
    }

    [Fact]
    public async Task GetOrRegisterUserSessionTokenAsync_RegistersAndReturnsToken()
    {
        var handler = new FakeHttpMessageHandler(registerUserSessionJson: "{\"token\":\"session-abc\"}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var token = await client.GetOrRegisterUserSessionTokenAsync("user-1", "Alice", CancellationToken.None);

        Assert.Equal("session-abc", token);
        Assert.Equal("/Plugins/Federation/RegisterUserSession", handler.LastRequestedPath);
        Assert.Equal(1, handler.RegisterUserSessionCallCount);
    }

    [Fact]
    public async Task GetOrRegisterUserSessionTokenAsync_CachesAcrossCalls_ForSameServerAndUser()
    {
        var handler = new FakeHttpMessageHandler(registerUserSessionJson: "{\"token\":\"session-abc\"}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "server-cache-test-" + Guid.NewGuid().ToString("N"), Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var first = await client.GetOrRegisterUserSessionTokenAsync("user-1", null, CancellationToken.None);
        var second = await client.GetOrRegisterUserSessionTokenAsync("user-1", null, CancellationToken.None);

        Assert.Equal("session-abc", first);
        Assert.Equal("session-abc", second);
        Assert.Equal(1, handler.RegisterUserSessionCallCount);
    }

    [Fact]
    public async Task GetOrRegisterUserSessionTokenAsync_EmptyUserId_ReturnsNull_WithoutRequest()
    {
        var handler = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "serverA", Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var token = await client.GetOrRegisterUserSessionTokenAsync(string.Empty, null, CancellationToken.None);

        Assert.Null(token);
        Assert.Equal(0, handler.RegisterUserSessionCallCount);
    }

    [Fact]
    public async Task GetOrRegisterUserSessionTokenAsync_RejectedByRemote_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(registerUserSessionStatusCode: HttpStatusCode.Forbidden);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };
        var server = new RemoteServer { Id = "server-blocked-" + Guid.NewGuid().ToString("N"), Name = "Remote", Url = "http://fake.local", ApiKey = "federation-token", Enabled = true };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var token = await client.GetOrRegisterUserSessionTokenAsync("blocked-user", null, CancellationToken.None);

        Assert.Null(token);
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

        await client.GetItemsAsync(mediaType: "Movie", parentId: "lib-1", startIndex: 10, limit: 5, sortBy: "DateCreated", sortOrder: "Descending", cancellationToken: CancellationToken.None);

        Assert.Contains("mediaType=Movie", handler.LastRequestedQuery);
        Assert.Contains("parentId=lib-1", handler.LastRequestedQuery);
        Assert.Contains("startIndex=10", handler.LastRequestedQuery);
        Assert.Contains("limit=5", handler.LastRequestedQuery);
        Assert.Contains("sortBy=DateCreated", handler.LastRequestedQuery);
        Assert.Contains("sortOrder=Descending", handler.LastRequestedQuery);
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

        private readonly string _playbackTokenJson;
        private readonly string _registerUserSessionJson;
        private readonly HttpStatusCode _registerUserSessionStatusCode;

        public string? LastRequestedPath { get; private set; }
        public string LastRequestedQuery { get; private set; } = string.Empty;
        public bool CalledAnyNativeUsersOrItemsEndpoint { get; private set; }
        public string? LastRemoteUserIdHeader { get; private set; }
        public int RegisterUserSessionCallCount { get; private set; }
        public int PlaybackTokenCallCount { get; private set; }

        public FakeHttpMessageHandler(
            string playbackJson = "{\"MediaSources\":[]}",
            string itemsJson = "{\"Items\":[]}",
            string itemJson = "{}",
            string librariesJson = "{\"Items\":[]}",
            string usersJson = "[]",
            string systemInfoJson = "{}",
            HttpStatusCode systemInfoStatusCode = HttpStatusCode.OK,
            string playbackTokenJson = "{\"token\":\"tok-123\"}",
            string registerUserSessionJson = "{\"token\":\"session-tok-123\"}",
            HttpStatusCode registerUserSessionStatusCode = HttpStatusCode.OK)
        {
            _playbackJson = playbackJson;
            _itemsJson = itemsJson;
            _itemJson = itemJson;
            _librariesJson = librariesJson;
            _usersJson = usersJson;
            _systemInfoJson = systemInfoJson;
            _systemInfoStatusCode = systemInfoStatusCode;
            _playbackTokenJson = playbackTokenJson;
            _registerUserSessionJson = registerUserSessionJson;
            _registerUserSessionStatusCode = registerUserSessionStatusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            LastRequestedPath = path;
            LastRequestedQuery = request.RequestUri?.Query ?? string.Empty;
            LastRemoteUserIdHeader = request.Headers.TryGetValues(RemoteServerClient.RemoteUserIdHeader, out var values)
                ? values.FirstOrDefault()
                : null;

            if (path.Equals("/Users", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(path, "^/Users/[^/]+/Items"))
            {
                CalledAnyNativeUsersOrItemsEndpoint = true;
            }

            if (path.Equals("/Plugins/Federation/PlaybackToken", StringComparison.OrdinalIgnoreCase))
            {
                PlaybackTokenCallCount++;
                return Task.FromResult(Json(_playbackTokenJson));
            }

            if (path.Equals("/Plugins/Federation/RegisterUserSession", StringComparison.OrdinalIgnoreCase))
            {
                RegisterUserSessionCallCount++;
                return Task.FromResult(_registerUserSessionStatusCode == HttpStatusCode.OK
                    ? Json(_registerUserSessionJson)
                    : new HttpResponseMessage(_registerUserSessionStatusCode));
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
