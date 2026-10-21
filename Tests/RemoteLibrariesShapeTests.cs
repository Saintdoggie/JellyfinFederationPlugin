using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class RemoteLibrariesShapeTests
{
    private sealed class Fixed : HttpMessageHandler
    {
        private readonly string _body;

        public Fixed(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    }

    private static RemoteServerClient Client(string body)
        => new(
            new RemoteServer { Id = "s", Name = "R", Url = "http://fake.local", ApiKey = "t", Enabled = true },
            NullLogger.Instance,
            new HttpClient(new Fixed(body)) { BaseAddress = new Uri("http://fake.local") });

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"error\":\"x\"}")]
    [InlineData("{\"Items\":null}")]
    [InlineData("[]")]
    public async Task UnexpectedShape_IsUnknown_NotEmpty(string body)
        => Assert.Null(await Client(body).GetLibrariesAsync());

    [Fact]
    public async Task ExplicitEmptyArray_IsAuthoritativeEmpty()
    {
        var result = await Client("{\"Items\":[]}").GetLibrariesAsync();
        Assert.NotNull(result);
        Assert.Empty(result!);
    }
}
