using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlaybackRegressionTests
{
    [Fact]
    public void UniversalAudio_UsesOnlyResolvedInternalUser()
    {
        Assert.True(RemoteMediaPath.TryParse("audio/0123456789abcdef0123456789abcdef/universal", out var audio));
        var user = Guid.NewGuid();
        var url = FederationRemoteTranscodeGateway.NativeUrl("http://127.0.0.1:8096/", audio!, "?AudioCodec=aac", user);

        Assert.Equal($"http://127.0.0.1:8096/audio/{audio!.ItemId:N}/universal?AudioCodec=aac&UserId={user:N}", url);

        Assert.True(RemoteMediaPath.TryParse("videos/0123456789abcdef0123456789abcdef/master.m3u8", out var video));
        Assert.DoesNotContain("UserId", FederationRemoteTranscodeGateway.NativeUrl("http://127.0.0.1:8096", video!, string.Empty, user), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(null, null)]
    public async Task ReadAheadRevalidation_DistinguishesDenialFromLinkFailure(HttpStatusCode? status, bool? expected)
    {
        using var client = new HttpClient(new StubHandler(status));
        var method = typeof(DirectLinkController).GetMethod("RevalidateReadAheadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<bool?>)method.Invoke(null, new object[] { client, "/Plugins/Federation/Peer/Play/token/master.m3u8", CancellationToken.None })!;
        Assert.Equal(expected, await task);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;

        public StubHandler(HttpStatusCode? status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _status.HasValue
                ? Task.FromResult(new HttpResponseMessage(_status.Value))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("link down"));
    }
}
