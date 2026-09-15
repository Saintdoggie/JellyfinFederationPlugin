using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Offline hide/unhide rule (see <see cref="FederationAvailabilityService"/>):
/// entries whose every source server is currently unreachable are hidden from
/// the local library - never created, removed if already present - while the
/// cache entries themselves are left untouched so they come back on rescan.
/// These tests pin <see cref="FederationItemPersistenceService.IsEntryOffline"/>,
/// the static helper <see cref="Services.FederationItemPersistenceService.ReconcileMappingAsync"/>
/// folds into both decisions, the same pattern as FederationHiddenItemTests.
/// </summary>
public sealed class FederationOfflineHideTests
{
    private static FederatedCacheEntry EntryFrom(string serverId)
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteId = Guid.NewGuid();
        return cache.UpsertByProviderId(
            "Movies",
            "imdb",
            "tt-" + remoteId.ToString("N"),
            new BaseItemDto { Id = remoteId, Name = "Some Movie" },
            serverId,
            remoteId,
            0,
            "Movie");
    }

    private static HashSet<string> Offline(params string[] ids)
        => new(ids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SoleSourceOffline_IsOffline()
    {
        var entry = EntryFrom("serverA");

        Assert.True(FederationItemPersistenceService.IsEntryOffline(entry, Offline("serverA")));
    }

    [Fact]
    public void SoleSourceOnline_IsNotOffline()
    {
        var entry = EntryFrom("serverA");

        Assert.False(FederationItemPersistenceService.IsEntryOffline(entry, Offline("serverB")));
    }

    [Fact]
    public void EmptyOfflineSet_NothingIsOffline()
    {
        var entry = EntryFrom("serverA");

        Assert.False(FederationItemPersistenceService.IsEntryOffline(entry, Offline()));
    }

    [Fact]
    public void NullEntry_IsNeverOffline()
    {
        Assert.False(FederationItemPersistenceService.IsEntryOffline(null, Offline("serverA")));
    }

    [Fact]
    public void DedupedEntry_StaysVisibleWhileAnySourceIsReachable()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteA = Guid.NewGuid();
        var entry = cache.UpsertByProviderId(
            "Movies",
            "imdb",
            "tt-shared",
            new BaseItemDto { Id = remoteA, Name = "Shared Movie" },
            "serverA",
            remoteA,
            0,
            "Movie");
        var remoteB = Guid.NewGuid();
        entry.AddSource("serverB", remoteB, 1);

        // serverA down, serverB up: still visible.
        Assert.False(FederationItemPersistenceService.IsEntryOffline(entry, Offline("serverA")));

        // Both down: hidden.
        Assert.True(FederationItemPersistenceService.IsEntryOffline(entry, Offline("serverA", "serverB")));
    }

    [Fact]
    public void OfflineMatch_IsCaseInsensitive()
    {
        var entry = EntryFrom("serverA");

        Assert.True(FederationItemPersistenceService.IsEntryOffline(entry, Offline("SERVERA")));
    }
}
