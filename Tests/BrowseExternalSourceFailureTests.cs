using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// An offline Plex source throws from its client. Browse must answer a readable
/// 503 rather than letting it escape as Jellyfin's bare 500, which broke the
/// Downloads tab (reproduced live on 2026-10-07).
/// </summary>
public sealed class BrowseExternalSourceFailureTests
{
    [Theory]
    [MemberData(nameof(SourceFailures))]
    public void UnreachableSourceFailures_AreHandled(Exception failure)
        => Assert.True(FederationController.IsExternalSourceFailure(failure, CancellationToken.None));

    public static TheoryData<Exception> SourceFailures() => new()
    {
        new InvalidOperationException("Plex libraries could not be read. Cached items have been kept."),
        new HttpRequestException("The SSL connection could not be established"),
        new TaskCanceledException("timeout"),
        new IOException("Received an unexpected EOF")
    };

    [Fact]
    public void CallerCancellation_IsNotReportedAsAnOfflineSource()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(FederationController.IsExternalSourceFailure(new TaskCanceledException(), cts.Token));
    }

    [Fact]
    public void ProgrammingErrors_StillSurface()
        => Assert.False(FederationController.IsExternalSourceFailure(new NullReferenceException(), CancellationToken.None));

    [Fact]
    public void UnavailableResponse_Is503WithServerNameAndNoAddress()
    {
        var server = new RemoteServer { Name = "freakbob", Url = "https://freakbob.example.ts.net/plex/secret-peer" };
        var result = FederationController.ExternalSourceUnavailable(server);
        Assert.Equal(503, result.StatusCode);
        var message = (string)result.Value!.GetType().GetProperty("message")!.GetValue(result.Value)!;
        Assert.Contains("freakbob", message);
        Assert.DoesNotContain("ts.net", message);
        Assert.DoesNotContain("secret-peer", message);
    }
}

public sealed class BrowseExternalItemsCacheTests
{
    [Fact]
    public async Task SecondPage_ReusesOneLibraryFetch_AndFailuresAreNotCached()
    {
        FederationController.ClearBrowseExternalItemsCache();
        var server = new RemoteServer { Id = "plex-cache-test", Name = "Plex", Kind = ServerKind.Plex };
        var items = new System.Collections.Generic.List<Jellyfin.Plugin.Federation.Services.ExternalItem>();
        var calls = 0;
        var fail = true;
        var provider = new Moq.Mock<Jellyfin.Plugin.Federation.Services.IExternalCatalogProvider>();
        provider.Setup(p => p.GetAllItemsAsync(server, "1", Moq.It.IsAny<CancellationToken>()))
            .Returns(() => { calls++; return Task.FromResult<System.Collections.Generic.IReadOnlyList<Jellyfin.Plugin.Federation.Services.ExternalItem>?>(fail ? null : items); });

        Assert.Null(await FederationController.GetBrowseExternalItemsAsync(provider.Object, server, "1", CancellationToken.None));
        fail = false;
        Assert.Same(items, await FederationController.GetBrowseExternalItemsAsync(provider.Object, server, "1", CancellationToken.None));
        Assert.Same(items, await FederationController.GetBrowseExternalItemsAsync(provider.Object, server, "1", CancellationToken.None));
        Assert.Equal(2, calls);
        FederationController.ClearBrowseExternalItemsCache();
    }
}
