using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlaybackStartupTests
{
    [Fact]
    public async Task ConcurrentMetadataRequests_PerformOneRemoteLookupAcrossClientInstances()
    {
        using var handler = new ControlledHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid") };
        var server = NewServer();
        var clients = Enumerable.Range(0, 16).Select(_ => new RemoteServerClient(server, NullLogger.Instance, http)).ToArray();
        var requests = clients.Select(c => c.GetPlaybackInfoAsync("movie", localActingUserId: "viewer")).ToArray();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        handler.Release.TrySetResult();
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelActiveLookupOrOtherWaiters()
    {
        using var handler = new ControlledHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid") };
        using var client = new RemoteServerClient(NewServer(), NullLogger.Instance, http);
        var first = client.GetPlaybackInfoAsync("movie");
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var cancelled = client.GetPlaybackInfoAsync("movie", cancellationToken: cancellation.Token);
        var survivor = client.GetPlaybackInfoAsync("movie");
        cancellation.Cancel();
        Assert.Null(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        handler.Release.TrySetResult();
        Assert.NotNull(await first);
        Assert.NotNull(await survivor);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MetadataCache_IsolatesViewersAndChangedCredentials()
    {
        using var handler = new ControlledHandler();
        handler.Release.TrySetResult();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid") };
        var server = NewServer();
        using var client = new RemoteServerClient(server, NullLogger.Instance, http);
        Assert.NotNull(await client.GetPlaybackInfoAsync("movie", localActingUserId: "allowed"));
        handler.Status = HttpStatusCode.Forbidden;
        Assert.Null(await client.GetPlaybackInfoAsync("movie", localActingUserId: "blocked"));
        server.ApiKey = "replacement-fixture-key";
        Assert.Null(await client.GetPlaybackInfoAsync("movie", localActingUserId: "allowed"));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task FailedLookup_ReleasesGateAndAllowsRetry()
    {
        using var handler = new ControlledHandler { Status = HttpStatusCode.ServiceUnavailable };
        handler.Release.TrySetResult();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid") };
        using var client = new RemoteServerClient(NewServer(), NullLogger.Instance, http);
        Assert.Null(await client.GetPlaybackInfoAsync("movie"));
        handler.Status = HttpStatusCode.OK;
        Assert.NotNull(await client.GetPlaybackInfoAsync("movie"));
        Assert.Equal(2, handler.Calls);
    }

    private static RemoteServer NewServer() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Url = "http://fixture.invalid", ApiKey = "fixture-key", Enabled = true
    };

    private sealed class ControlledHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent("{\"MediaSources\":[{\"Id\":\"fixture\",\"Container\":\"mp4\"}]}")
            };
        }
    }
}
