using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationPathParsingTests
{
    [Fact]
    public void TryParsePath_ProviderForm_ParsesComponents()
    {
        var ok = FederationItemCache.TryParsePath(
            "federation://Federated Movies/imdb:tt1234567",
            out var mapping,
            out var providerName,
            out var providerId,
            out var rawServerId,
            out var rawRemoteItemId);

        Assert.True(ok);
        Assert.Equal("Federated Movies", mapping);
        Assert.Equal("imdb", providerName);
        Assert.Equal("tt1234567", providerId);
        Assert.Null(rawServerId);
        Assert.Null(rawRemoteItemId);
    }

    [Fact]
    public void TryParsePath_RawForm_ParsesServerAndItem()
    {
        var guid = Guid.NewGuid();
        var ok = FederationItemCache.TryParsePath(
            $"federation://Federated TV/raw/server-abc/{guid}",
            out var mapping,
            out var providerName,
            out var providerId,
            out var rawServerId,
            out var rawRemoteItemId);

        Assert.True(ok);
        Assert.Equal("Federated TV", mapping);
        Assert.Null(providerName);
        Assert.Null(providerId);
        Assert.Equal("server-abc", rawServerId);
        Assert.Equal(guid, rawRemoteItemId);
    }

    [Fact]
    public void TryParsePath_LegacyServerIdItemId_ReturnsFalse()
    {
        var ok = FederationItemCache.TryParsePath(
            "federation://server-abc/item-123",
            out _,
            out _,
            out _,
            out _,
            out _);

        // Legacy 2-component form is no longer a valid federation path.
        Assert.False(ok);
    }

    [Fact]
    public void TryParsePath_NonFederationPath_ReturnsFalse()
    {
        var ok = FederationItemCache.TryParsePath("/media/movies/foo.mkv", out _, out _, out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParsePath_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(FederationItemCache.TryParsePath("", out _, out _, out _, out _, out _));
        Assert.False(FederationItemCache.TryParsePath(null!, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void BuildProviderKey_RoundtripsWithPathBuilder()
    {
        var key = FederationItemCache.BuildProviderKey("Movies", "tmdb", "558");
        var path = FederationItemCache.BuildProviderPath("Movies", "tmdb", "558");
        Assert.Equal("Movies/tmdb:558", key);
        Assert.Equal("federation://Movies/tmdb:558", path);
    }

    [Fact]
    public void BuildRawKey_RoundtripsWithPathBuilder()
    {
        var guid = Guid.NewGuid();
        var key = FederationItemCache.BuildRawKey("TV", "srv1", guid);
        var path = FederationItemCache.BuildRawPath("TV", "srv1", guid);
        Assert.Equal("TV/raw/srv1/" + guid, key);
        Assert.Equal("federation://TV/raw/srv1/" + guid, path);
    }
}
