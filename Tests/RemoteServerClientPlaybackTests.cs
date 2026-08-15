using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RemoteServerClientPlaybackTests
{
    /// <summary>
    /// Regression test for the "Unable to find a valid media source to play" bug.
    /// GetPlaybackInfoAsync used to return null when no UserId was configured on the
    /// server, which left federated MediaSourceInfos without Container/MediaStreams
    /// and made every source unplayable (PlaybackError.NO_MEDIA_ERROR). It should
    /// now fall back to the remote's first user so stream details can still be read.
    /// </summary>
    [Fact]
    public async Task GetPlaybackInfoAsync_MissingUserId_FallsBackToFirstUser()
    {
        var usersJson = "[" +
                        "{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"user1\"}," +
                        "{\"Id\":\"22222222-2222-2222-2222-222222222222\",\"Name\":\"user2\"}" +
                        "]";
        var playbackJson = "{\"PlaySessionId\":\"abc\",\"MediaSources\":[" +
                           "{\"Id\":\"src1\",\"Path\":\"http://remote/video\",\"Container\":\"mkv\"," +
                           "\"Size\":12345,\"Bitrate\":10000000}]}";

        var handler = new FakeHttpMessageHandler(usersJson, playbackJson);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://fake.local")
        };

        // No UserId configured - the exact condition that previously broke playback.
        var server = new RemoteServer
        {
            Id = "serverA",
            Name = "Remote",
            Url = "http://fake.local",
            ApiKey = "key",
            UserId = string.Empty,
            Enabled = true
        };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        var result = await client.GetPlaybackInfoAsync(Guid.NewGuid().ToString("N"), cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.MediaSources!);
        var source = result.MediaSources![0];
        Assert.Equal("mkv", source.Container);
        Assert.Equal(10000000, source.Bitrate);

        // Confirm the fallback hit /Users first and then PlaybackInfo as the first
        // user (11111111-…), NOT an empty configured UserId.
        Assert.True(handler.CalledUsersEndpoint, "Expected the /Users endpoint to be queried for the fallback user");
        Assert.Equal("11111111-1111-1111-1111-111111111111", handler.PlaybackUserId);
    }

    /// <summary>
    /// A restricted (non-admin) user can browse/sync an item fine yet be blocked from
    /// PlaybackInfo for it (no library access, EnableMediaPlayback off) - "shows up but
    /// can't stream". Auto-resolution should prefer an administrator over whichever user
    /// happens to sort first, since admins aren't subject to those restrictions.
    /// </summary>
    [Fact]
    public async Task GetPlaybackInfoAsync_MissingUserId_PrefersAdministratorOverFirstUser()
    {
        var usersJson = "[" +
                        "{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"restricted-kid-profile\",\"Policy\":{\"IsAdministrator\":false}}," +
                        "{\"Id\":\"22222222-2222-2222-2222-222222222222\",\"Name\":\"admin\",\"Policy\":{\"IsAdministrator\":true}}" +
                        "]";
        var playbackJson = "{\"PlaySessionId\":\"abc\",\"MediaSources\":[" +
                           "{\"Id\":\"src1\",\"Path\":\"http://remote/video\",\"Container\":\"mkv\"}]}";

        var handler = new FakeHttpMessageHandler(usersJson, playbackJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        var server = new RemoteServer
        {
            // A distinct server id from the other test in this file: the resolved
            // playback user is now cached in-memory keyed by server id only (see
            // RemoteServerClient.ResolvedPlaybackUserIdCache), so sharing an id
            // would let whichever test happens to run first poison this one with
            // its own resolved user.
            Id = "serverB",
            Name = "Remote",
            Url = "http://fake.local",
            ApiKey = "key",
            UserId = string.Empty,
            Enabled = true
        };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        await client.GetPlaybackInfoAsync(Guid.NewGuid().ToString("N"), cancellationToken: CancellationToken.None);

        // The second user in the list is the admin; picking it over the first user
        // is the whole point of the fix.
        Assert.Equal("22222222-2222-2222-2222-222222222222", handler.PlaybackUserId);
    }

    /// <summary>
    /// Regression test for an ultra-review finding: the resolved fallback user
    /// used to be written straight onto <c>_server.UserId</c> - the same
    /// <see cref="RemoteServer"/> instance <c>Plugin.Instance.Configuration</c>
    /// holds - so it would get silently persisted to disk by the next unrelated
    /// <c>SaveConfiguration()</c> call (adding a server, accepting a friend
    /// request, ...), indistinguishable from an admin having configured it
    /// themselves. The resolution must stay in-memory only while still skipping
    /// the extra <c>/Users</c> round trip on a later call for the same server.
    /// </summary>
    [Fact]
    public async Task GetPlaybackInfoAsync_MissingUserId_ResolutionNeverWritesBackToServerConfig()
    {
        var usersJson = "[" +
                        "{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"user1\"}" +
                        "]";
        var playbackJson = "{\"PlaySessionId\":\"abc\",\"MediaSources\":[" +
                           "{\"Id\":\"src1\",\"Path\":\"http://remote/video\",\"Container\":\"mkv\"}]}";

        var handler = new FakeHttpMessageHandler(usersJson, playbackJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://fake.local") };

        // A globally unique id: the in-memory resolution cache is a process-wide
        // static keyed by server id, so a fixed id like "serverA" could collide
        // with unrelated tests in this same run.
        var server = new RemoteServer
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Remote",
            Url = "http://fake.local",
            ApiKey = "key",
            UserId = string.Empty,
            Enabled = true
        };
        var client = new RemoteServerClient(server, NullLogger.Instance, httpClient);

        await client.GetPlaybackInfoAsync(Guid.NewGuid().ToString("N"), cancellationToken: CancellationToken.None);
        Assert.Equal(string.Empty, server.UserId);
        Assert.Equal(1, handler.UsersEndpointCallCount);

        // A second play on the same server must still skip the /Users round trip
        // (the whole point of caching the resolution) without ever having written
        // it onto the persisted config object.
        await client.GetPlaybackInfoAsync(Guid.NewGuid().ToString("N"), cancellationToken: CancellationToken.None);
        Assert.Equal(string.Empty, server.UserId);
        Assert.Equal(1, handler.UsersEndpointCallCount);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _usersJson;
        private readonly string _playbackJson;

        public bool CalledUsersEndpoint { get; private set; }
        public int UsersEndpointCallCount { get; private set; }
        public string? PlaybackUserId { get; private set; }

        public FakeHttpMessageHandler(string usersJson, string playbackJson)
        {
            _usersJson = usersJson;
            _playbackJson = playbackJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Equals("/Users", StringComparison.OrdinalIgnoreCase))
            {
                CalledUsersEndpoint = true;
                UsersEndpointCallCount++;
                return Task.FromResult(Json(_usersJson));
            }

            if (path.Contains("PlaybackInfo", StringComparison.OrdinalIgnoreCase))
            {
                var query = request.RequestUri?.Query ?? string.Empty;
                var idx = query.IndexOf("UserId=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    PlaybackUserId = query.Substring(idx + "UserId=".Length);
                }

                return Task.FromResult(Json(_playbackJson));
            }

            return Task.FromResult(Json("{\"Items\":[]}"));
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
