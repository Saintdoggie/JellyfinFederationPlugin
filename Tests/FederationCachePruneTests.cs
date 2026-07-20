using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationCachePruneTests
{
    private static FederationItemCache CreateCache()
        => new(NullLogger<FederationItemCache>.Instance);

    private static BaseItemDto MakeItem(string name, string? imdb = null)
    {
        var dto = new BaseItemDto { Name = name, Type = Jellyfin.Data.Enums.BaseItemKind.Movie };
        if (imdb != null)
        {
            dto.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["imdb"] = imdb };
        }

        return dto;
    }

    private static void SeedTwoServers(FederationItemCache cache, Guid providerA, Guid providerB, Guid rawA1, Guid rawA2, Guid rawB1)
    {
        // Shared provider-id entry with one source per server.
        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("Shared Movie", "tt1"), "serverA", providerA, 0, "Movie");
        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("Shared Movie", "tt1"), "serverB", providerB, 1, "Movie");

        // Raw (non-deduped) entries: two for A, one for B.
        cache.UpsertRaw("Movies", "serverA", rawA1, MakeItem("Raw A1"), 0, "Movie");
        cache.UpsertRaw("Movies", "serverA", rawA2, MakeItem("Raw A2"), 0, "Movie");
        cache.UpsertRaw("Movies", "serverB", rawB1, MakeItem("Raw B1"), 1, "Movie");
    }

    [Fact]
    public void Prune_RemovesUnseenRawEntries_OnlyForTargetServer()
    {
        var cache = CreateCache();
        var providerA = Guid.NewGuid();
        var providerB = Guid.NewGuid();
        var rawA1 = Guid.NewGuid();
        var rawA2 = Guid.NewGuid();
        var rawB1 = Guid.NewGuid();
        SeedTwoServers(cache, providerA, providerB, rawA1, rawA2, rawB1);
        Assert.Equal(4, cache.Count);

        // Server A synced successfully but only reported the shared item: its two
        // raw entries are stale and must go. Server B is untouched.
        var removed = cache.PruneServerSources("Movies", "serverA", new HashSet<Guid> { providerA });

        Assert.Equal(2, removed);
        Assert.Equal(2, cache.Count);

        var entries = cache.GetEntriesForMapping("Movies").ToList();
        var providerEntry = entries.Single(e => e.Key.Contains("imdb:tt1", StringComparison.Ordinal));
        Assert.Equal(2, providerEntry.GetSourcesSnapshot().Length);
        Assert.Contains(entries, e => e.Key.Contains("serverB", StringComparison.Ordinal) && e.GetSourcesSnapshot().Any(s => s.RemoteItemId == rawB1));
    }

    [Fact]
    public void Prune_RemovesStaleSources_KeepsSharedEntryUntilLastSource()
    {
        var cache = CreateCache();
        var providerA = Guid.NewGuid();
        var providerB = Guid.NewGuid();
        var rawA1 = Guid.NewGuid();
        var rawA2 = Guid.NewGuid();
        var rawB1 = Guid.NewGuid();
        SeedTwoServers(cache, providerA, providerB, rawA1, rawA2, rawB1);

        // Server A reports nothing: its raw entries go, and its source on the
        // shared entry is dropped, but the entry survives via server B.
        var removedA = cache.PruneServerSources("Movies", "serverA", new HashSet<Guid>());
        Assert.Equal(2, removedA);
        var providerEntry = cache.GetEntriesForMapping("Movies").Single(e => e.Key.Contains("imdb:tt1", StringComparison.Ordinal));
        var sources = providerEntry.GetSourcesSnapshot();
        Assert.Single(sources);
        Assert.Equal("serverB", sources[0].ServerId);

        // Server B now reports nothing: the shared entry loses its last source
        // and is removed along with B's raw entry.
        var removedB = cache.PruneServerSources("Movies", "serverB", new HashSet<Guid>());
        Assert.Equal(2, removedB);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Prune_MappingNameMatchesCaseInsensitively()
    {
        var cache = CreateCache();
        var raw = Guid.NewGuid();
        cache.UpsertRaw("Movies", "serverA", raw, MakeItem("Raw"), 0, "Movie");

        var removed = cache.PruneServerSources("movies", "serverA", new HashSet<Guid>());

        Assert.Equal(1, removed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Prune_OtherMappingsUntouched()
    {
        var cache = CreateCache();
        cache.UpsertRaw("Movies", "serverA", Guid.NewGuid(), MakeItem("Movie"), 0, "Movie");
        cache.UpsertRaw("Shows", "serverA", Guid.NewGuid(), MakeItem("Show"), 0, "Series");

        var removed = cache.PruneServerSources("Movies", "serverA", new HashSet<Guid>());

        Assert.Equal(1, removed);
        var remaining = cache.GetAllEntries().ToList();
        Assert.Single(remaining);
        Assert.Equal("Shows", remaining[0].MappingName);
    }
}
