using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// An item first seen without provider ids is cached under a raw key
/// (mapping/raw/server/remoteId). Once the remote starts reporting its ids - a
/// metadata refresh there, or dedup being enabled here - the same item is
/// upserted under a provider key instead, orphaning the raw entry. Pruning could
/// not remove it, because pruning only dropped raw entries whose remote id was
/// no longer seen, and this one still is: it is the same item. The result was a
/// library listing every affected title twice.
/// </summary>
public class FederationDuplicateKeyTests
{
    private static FederationItemCache NewCache() => new(NullLogger<FederationItemCache>.Instance);

    [Fact]
    public void ItemThatGainsProviderIds_LeavesExactlyOneEntry_AfterPrune()
    {
        var cache = NewCache();
        var remoteId = Guid.NewGuid();
        var dto = new BaseItemDto { Id = remoteId, Name = "Incredibles 2" };

        // First sync: the remote reported no provider ids.
        cache.UpsertRaw("Movies", "serverA", remoteId, dto, 0, "Movie");
        Assert.Single(cache.GetEntriesForMapping("Movies"));

        // Later sync: the same remote item now reports an imdb id.
        cache.UpsertByProviderId("Movies", "imdb", "tt3606756", dto, "serverA", remoteId, 0, "Movie");
        Assert.Equal(2, cache.GetEntriesForMapping("Movies").Count());

        // The item is still present on the remote, so it is "seen" this cycle -
        // which is precisely why the stale raw copy used to survive forever.
        cache.PruneServerSources("Movies", "serverA", new HashSet<Guid> { remoteId });

        var remaining = cache.GetEntriesForMapping("Movies").ToList();
        var single = Assert.Single(remaining);
        Assert.Equal("Movies/imdb:tt3606756", single.Key);
    }

    [Fact]
    public void ItemThatLosesProviderIds_LeavesExactlyOneEntry_AfterPrune()
    {
        // The reverse direction: an item first synced while dedup matched a
        // provider id, then later re-synced without one (dedup turned off, or
        // the remote's metadata dropped the id). The stale provider-keyed entry
        // isn't a raw key, so RemoveSourcesNotIn alone can't catch it - its
        // remote id is still "seen" every sync, just under a new key.
        var cache = NewCache();
        var remoteId = Guid.NewGuid();
        var dto = new BaseItemDto { Id = remoteId, Name = "Incredibles 2" };

        cache.UpsertByProviderId("Movies", "imdb", "tt3606756", dto, "serverA", remoteId, 0, "Movie");
        Assert.Single(cache.GetEntriesForMapping("Movies"));

        cache.UpsertRaw("Movies", "serverA", remoteId, dto, 0, "Movie");
        Assert.Equal(2, cache.GetEntriesForMapping("Movies").Count());

        cache.PruneServerSources("Movies", "serverA", new HashSet<Guid> { remoteId });

        var remaining = cache.GetEntriesForMapping("Movies").ToList();
        var single = Assert.Single(remaining);
        Assert.Equal("Movies/raw/serverA/" + remoteId, single.Key);
    }

    [Fact]
    public void RawEntryStillOwningItsRemoteItem_IsKept()
    {
        var cache = NewCache();
        var remoteId = Guid.NewGuid();
        cache.UpsertRaw("Movies", "serverA", remoteId, new BaseItemDto { Id = remoteId, Name = "No Provider Ids" }, 0, "Movie");

        cache.PruneServerSources("Movies", "serverA", new HashSet<Guid> { remoteId });

        Assert.Single(cache.GetEntriesForMapping("Movies"));
    }

    [Fact]
    public void RawEntryWhoseRemoteItemVanished_IsStillRemoved()
    {
        var cache = NewCache();
        var remoteId = Guid.NewGuid();
        cache.UpsertRaw("Movies", "serverA", remoteId, new BaseItemDto { Id = remoteId, Name = "Deleted Upstream" }, 0, "Movie");

        cache.PruneServerSources("Movies", "serverA", new HashSet<Guid>());

        Assert.Empty(cache.GetEntriesForMapping("Movies"));
    }

    [Fact]
    public void RawEntryFromAnotherServer_IsUntouchedByThisServersPrune()
    {
        var cache = NewCache();
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        cache.UpsertRaw("Movies", "serverA", idA, new BaseItemDto { Id = idA, Name = "On A" }, 0, "Movie");
        cache.UpsertRaw("Movies", "serverB", idB, new BaseItemDto { Id = idB, Name = "On B" }, 0, "Movie");

        // Pruning server A with nothing seen must not touch server B's entry.
        cache.PruneServerSources("Movies", "serverA", new HashSet<Guid>());

        var remaining = cache.GetEntriesForMapping("Movies").ToList();
        var single = Assert.Single(remaining);
        Assert.Contains("serverB", single.Key, StringComparison.OrdinalIgnoreCase);
    }
}
