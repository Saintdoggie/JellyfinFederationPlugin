using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Local suppression list ("hide this friend's item from my own library" - see
/// Configuration.PluginConfiguration.HiddenFederatedItemIds): purely additive,
/// receiving-side filtering, the opposite direction from per-friend sharing
/// permissions. These tests pin
/// <see cref="FederationItemPersistenceService.IsHidden"/>, the static helper
/// <see cref="Services.FederationItemPersistenceService.ReconcileMappingAsync"/>
/// folds into both the "should I create this" and "should I delete this
/// already-existing item" decisions - the full reconciliation pass itself needs
/// a live ILibraryManager and isn't unit-testable the way this extracted piece
/// is (see FederationDisabledSourceTests for the same pattern applied to
/// FirstEnabledSource).
/// </summary>
public class FederationHiddenItemTests
{
    private static FederatedCacheEntry EntryWithKey(string key)
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteId = Guid.NewGuid();
        var entry = cache.UpsertByProviderId(
            "Movies",
            "imdb",
            key,
            new BaseItemDto { Id = remoteId, Name = "Some Movie" },
            "serverA",
            remoteId,
            0,
            "Movie");

        return entry;
    }

    [Fact]
    public void KeyOnHideList_IsHidden()
    {
        var entry = EntryWithKey("tt1111111");
        var hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Key };

        Assert.True(FederationItemPersistenceService.IsHidden(entry, hiddenKeys));
    }

    [Fact]
    public void KeyNotOnHideList_IsNotHidden()
    {
        var entry = EntryWithKey("tt2222222");
        var hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "movies/imdb:tt9999999" };

        Assert.False(FederationItemPersistenceService.IsHidden(entry, hiddenKeys));
    }

    [Fact]
    public void EmptyHideList_NothingIsHidden()
    {
        var entry = EntryWithKey("tt3333333");

        Assert.False(FederationItemPersistenceService.IsHidden(entry, new HashSet<string>()));
    }

    [Fact]
    public void HideListMatch_IsCaseInsensitive()
    {
        var entry = EntryWithKey("tt4444444");
        var hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Key.ToUpperInvariant() };

        Assert.True(FederationItemPersistenceService.IsHidden(entry, hiddenKeys));
    }

    [Fact]
    public void NullEntry_IsNeverHidden()
    {
        var hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "movies/imdb:tt1234567" };

        Assert.False(FederationItemPersistenceService.IsHidden(null, hiddenKeys));
    }
}
