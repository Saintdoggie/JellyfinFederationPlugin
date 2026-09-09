using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationCacheRemapTests
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

    [Fact]
    public void RemapMapping_RewritesProviderAndRawKeys()
    {
        var cache = CreateCache();
        var providerId = Guid.NewGuid();
        var rawId = Guid.NewGuid();
        cache.UpsertByProviderId("Federated Movies", "imdb", "tt1", MakeItem("A", "tt1"), "s", providerId, 0, "Movie");
        cache.UpsertRaw("Federated Movies", "s", rawId, MakeItem("B"), 0, "Movie");

        var remapped = cache.RemapMapping("Federated Movies", "Movies");

        Assert.Equal(2, remapped);
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.GetEntryByKey("Federated Movies/imdb:tt1"));
        Assert.Null(cache.GetEntryByKey("Federated Movies/raw/s/" + rawId));
        var provider = cache.GetEntryByKey("Movies/imdb:tt1");
        var raw = cache.GetEntryByKey("Movies/raw/s/" + rawId);
        Assert.NotNull(provider);
        Assert.NotNull(raw);
        Assert.Equal("Movies", provider.MappingName);
        Assert.Equal("Movies", raw.MappingName);
        Assert.Equal("federation://Movies/imdb:tt1", provider.FederationPath);
        Assert.Equal("Movies/imdb:tt1", cache.TryGetLocalKeyForRemoteItem("s", providerId));
        Assert.Equal("Movies/raw/s/" + rawId, cache.TryGetLocalKeyForRemoteItem("s", rawId));
    }

    [Fact]
    public void RemapMapping_RewritesNestedParentKeys()
    {
        var cache = CreateCache();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var series = cache.UpsertByProviderId(
            "Federated Shows",
            "tvdb",
            "10",
            new BaseItemDto { Name = "Show", Type = Jellyfin.Data.Enums.BaseItemKind.Series },
            "s",
            seriesId,
            0,
            "Series");
        cache.UpsertRaw(
            "Federated Shows",
            "s",
            seasonId,
            new BaseItemDto { Name = "Season 1", Type = Jellyfin.Data.Enums.BaseItemKind.Season },
            0,
            "Season",
            parentKey: series.Key);

        cache.RemapMapping("Federated Shows", "Shows");

        var remappedSeries = cache.GetEntryByKey("Shows/tvdb:10");
        var season = Assert.Single(cache.GetAllEntries(), e => e.ItemType == "Season");
        Assert.NotNull(remappedSeries);
        Assert.Equal(remappedSeries.Key, season.ParentKey);
        Assert.Equal("Shows/raw/s/" + seasonId, season.Key);
    }

    [Fact]
    public void RemapMapping_MergesSourcesIntoExistingTargetEntry()
    {
        var cache = CreateCache();
        var idOld = Guid.NewGuid();
        var idNew = Guid.NewGuid();
        cache.UpsertByProviderId("Federated Movies", "imdb", "tt1", MakeItem("A", "tt1"), "s1", idOld, 0, "Movie");
        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("A", "tt1"), "s2", idNew, 1, "Movie");

        cache.RemapMapping("Federated Movies", "Movies");

        var entry = Assert.Single(cache.GetAllEntries());
        Assert.Equal("Movies/imdb:tt1", entry.Key);
        var sources = entry.GetSourcesSnapshot();
        Assert.Equal(2, sources.Length);
        Assert.Contains(sources, s => s.ServerId == "s1" && s.RemoteItemId == idOld);
        Assert.Contains(sources, s => s.ServerId == "s2" && s.RemoteItemId == idNew);
        Assert.Equal("Movies/imdb:tt1", cache.TryGetLocalKeyForRemoteItem("s1", idOld));
        Assert.Equal("Movies/imdb:tt1", cache.TryGetLocalKeyForRemoteItem("s2", idNew));
    }

    [Fact]
    public void RemapMapping_ThenUpsertAndPruneHitTheSameEntry()
    {
        var cache = CreateCache();
        var remoteId = Guid.NewGuid();
        var dto = MakeItem("A", "tt1");
        cache.UpsertByProviderId("Federated Movies", "imdb", "tt1", dto, "s", remoteId, 0, "Movie");

        cache.RemapMapping("Federated Movies", "Movies");
        cache.UpsertByProviderId("Movies", "imdb", "tt1", dto, "s", remoteId, 0, "Movie");

        var afterUpsert = Assert.Single(cache.GetAllEntries());
        Assert.Equal("Movies/imdb:tt1", afterUpsert.Key);

        var removed = cache.PruneServerSources("Movies", "s", new HashSet<Guid> { remoteId });
        Assert.Equal(0, removed);
        Assert.Single(cache.GetAllEntries());
        Assert.Empty(cache.GetEntriesForMapping("Federated Movies"));

        removed = cache.PruneServerSources("Movies", "s", new HashSet<Guid>());
        Assert.Equal(1, removed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RemapMapping_SameName_IsNoOp()
    {
        var cache = CreateCache();
        cache.UpsertRaw("Movies", "s", Guid.NewGuid(), MakeItem("A"), 0, "Movie");

        Assert.Equal(0, cache.RemapMapping("Movies", "Movies"));
        Assert.Equal(0, cache.RemapMapping("Movies", "movies"));
        Assert.Equal("Movies", cache.GetAllEntries().Single().MappingName);
    }

    [Fact]
    public void RewriteMappingPrefix_ReplacesOnlyTheMappingSegment()
    {
        var guid = Guid.NewGuid();
        Assert.Equal("Movies/imdb:tt1", FederationItemCache.RewriteMappingPrefix("Federated Movies/imdb:tt1", "Federated Movies", "Movies"));
        Assert.Equal("Movies/raw/s/" + guid, FederationItemCache.RewriteMappingPrefix("federated movies/raw/s/" + guid, "Federated Movies", "Movies"));
        Assert.Equal("Movies Extra/imdb:tt1", FederationItemCache.RewriteMappingPrefix("Movies Extra/imdb:tt1", "Movies", "Shows"));
        Assert.Null(FederationItemCache.RewriteMappingPrefix(null, "Movies", "Shows"));
    }
}
