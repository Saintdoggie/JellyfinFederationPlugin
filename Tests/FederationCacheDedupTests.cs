using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationCacheDedupTests
{
    private static FederationItemCache CreateCache()
    {
        return new FederationItemCache(
            NullLogger<FederationItemCache>.Instance,
            null!);
    }

    private static BaseItemDto MakeItem(string name, string? imdb = null, string? tmdb = null)
    {
        var dto = new BaseItemDto { Name = name, Type = Jellyfin.Data.Enums.BaseItemKind.Movie };
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (imdb != null) ids["imdb"] = imdb;
        if (tmdb != null) ids["tmdb"] = tmdb;
        dto.ProviderIds = ids;
        return dto;
    }

    [Fact]
    public void UpsertByProviderId_TwoServers_MergeIntoOneEntry()
    {
        var cache = CreateCache();
        var itemIdA = Guid.NewGuid();
        var itemIdB = Guid.NewGuid();

        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("Movie A", imdb: "tt1"), "serverA", itemIdA, 0, "Movie");
        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("Movie A (serverB)", imdb: "tt1"), "serverB", itemIdB, 1, "Movie");

        var all = cache.GetAllEntries().ToList();
        Assert.Single(all);
        Assert.Equal(2, all[0].Sources.Count);
        Assert.Equal("serverA", all[0].PrimarySource?.ServerId);
        Assert.Equal(itemIdA, all[0].PrimarySource?.RemoteItemId);
    }

    [Fact]
    public void UpsertRaw_DifferentServers_CreatesSeparateEntries()
    {
        var cache = CreateCache();
        cache.UpsertRaw("Movies", "serverA", Guid.NewGuid(), MakeItem("A"), 0, "Movie");
        cache.UpsertRaw("Movies", "serverB", Guid.NewGuid(), MakeItem("B"), 0, "Movie");

        Assert.Equal(2, cache.GetAllEntries().Count());
    }

    [Fact]
    public void ClearMapping_RemovesOnlyMatchingEntries()
    {
        var cache = CreateCache();
        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("A", imdb: "tt1"), "s1", Guid.NewGuid(), 0, "Movie");
        cache.UpsertByProviderId("TV", "imdb", "tt2", MakeItem("B", imdb: "tt2"), "s1", Guid.NewGuid(), 0, "Series");

        cache.ClearMapping("Movies");

        var all = cache.GetAllEntries().ToList();
        Assert.Single(all);
        Assert.Equal("TV", all[0].MappingName);
    }

    [Fact]
    public void ProviderIdEntry_PrefersLowerPriority()
    {
        var cache = CreateCache();
        var idLow = Guid.NewGuid();
        var idHigh = Guid.NewGuid();

        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("B", imdb: "tt1"), "highPrioServer", idHigh, 5, "Movie");
        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("A", imdb: "tt1"), "lowPrioServer", idLow, 0, "Movie");

        var entry = cache.GetAllEntries().Single();
        Assert.Equal("lowPrioServer", entry.PrimarySource?.ServerId);
        Assert.Equal(idLow, entry.PrimarySource?.RemoteItemId);
    }
}
