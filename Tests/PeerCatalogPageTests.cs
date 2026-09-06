using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class PeerCatalogPageTests
{
    [Fact]
    public async Task ForwardedRows_DoNotLeakOrHideLaterOwnedRows_AndCursorTracksRawRows()
    {
        var forwarded = new BaseItemDto { Id = Guid.NewGuid(), ProviderIds = new() { ["federationkey"] = "someone-else/item" } };
        var a = new BaseItemDto { Id = Guid.NewGuid(), Name = "A" };
        var b = new BaseItemDto { Id = Guid.NewGuid(), Name = "B" };
        var source = new[] { forwarded, a, forwarded, b };
        Task<List<BaseItemDto>?> Fetch(int start, int count) => Task.FromResult<List<BaseItemDto>?>(source.Skip(start).Take(count).ToList());
        var first = await PeerCatalogPage.ReadAsync(Fetch, 0, 1, CancellationToken.None);
        Assert.Equal(a.Id, Assert.Single(first!.Items).Id);
        Assert.Equal(2, first.NextStartIndex);
        var second = await PeerCatalogPage.ReadAsync(Fetch, first.NextStartIndex, 1, CancellationToken.None);
        Assert.Equal(b.Id, Assert.Single(second!.Items).Id);
        Assert.Equal(4, second.NextStartIndex);
    }

    [Fact]
    public async Task FailureAfterFilteredRows_IsNotAnEmptyLibrary()
    {
        var calls = 0;
        var result = await PeerCatalogPage.ReadAsync((_, _) => Task.FromResult<List<BaseItemDto>?>(++calls == 1
            ? new() { new BaseItemDto { ProviderIds = new() { ["FederationKey"] = "forwarded" } } } : null), 0, 1, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadOfForwardedItem_IsRejectedBeforeTokenOrMediaRequest()
    {
        var id = Guid.NewGuid().ToString("N");
        var handler = new ForwardedHandler(id);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://selected.example") };
        using var client = new RemoteServerClient(new RemoteServer { Id = "selected", Name = "Selected", Url = "https://selected.example" }, NullLogger.Instance, http);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadToFileAsync(id, "should-never-be-created.mkv", null, CancellationToken.None));
        Assert.Contains("not directly owned", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class ForwardedHandler(string id) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.EndsWith("/Peer/Items/" + id, request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"Id\":\"" + id + "\",\"Name\":\"Forwarded\",\"Type\":\"Movie\",\"ProviderIds\":{\"FederationKey\":\"third-party/item\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
