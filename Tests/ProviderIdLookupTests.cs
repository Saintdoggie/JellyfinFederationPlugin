using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Dedup provider names are configured lowercase ("imdb", "tmdb", "tvdb"), but
/// Jellyfin stamps them Pascal-cased ("Imdb", "Tmdb") on both BaseItem and
/// BaseItemDto. Neither dictionary is guaranteed case-insensitive, so a plain
/// TryGetValue(configuredKey, ...) silently misses every real entry - which is
/// why dedup, both across federated servers and against content the user
/// already owns locally, never actually matched anything.
/// </summary>
public class ProviderIdLookupTests
{
    [Fact]
    public void TryGetProviderId_MatchesRegardlessOfCasing()
    {
        var providerIds = new Dictionary<string, string> { ["Imdb"] = "tt1160419" };

        Assert.True(FederationLibraryManager.TryGetProviderId(providerIds, "imdb", out var value));
        Assert.Equal("tt1160419", value);
    }

    [Fact]
    public void TryGetProviderId_MissingKey_ReturnsFalse()
    {
        var providerIds = new Dictionary<string, string> { ["Tmdb"] = "438631" };

        Assert.False(FederationLibraryManager.TryGetProviderId(providerIds, "imdb", out _));
    }

    [Fact]
    public void TryGetProviderId_EmptyValue_ReturnsFalse()
    {
        var providerIds = new Dictionary<string, string> { ["Imdb"] = string.Empty };

        Assert.False(FederationLibraryManager.TryGetProviderId(providerIds, "imdb", out _));
    }

    [Fact]
    public void TryGetProviderId_NullDictionary_ReturnsFalse()
    {
        Assert.False(FederationLibraryManager.TryGetProviderId(null, "imdb", out _));
    }
}
