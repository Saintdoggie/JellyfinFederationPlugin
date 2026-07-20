using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationCacheSerializationTests
{
    private static FederationItemCache CreateCache(string path)
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        cache.Initialize(path);
        return cache;
    }

    [Fact]
    public async Task SaveAndLoad_RoundtripsEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), "federation-cache-test-" + Guid.NewGuid() + ".json");
        try
        {
            var cache = CreateCache(path);
            cache.UpsertByProviderId("Movies", "imdb", "tt100", MakeItem("Test Movie", "tt100"), "srvA", Guid.NewGuid(), 0, "Movie");
            cache.UpsertRaw("TV", "srvB", Guid.NewGuid(), MakeItem("Test Show"), 0, "Series");
            await cache.SaveAsync();

            var cache2 = CreateCache(path);
            var all = cache2.GetAllEntries().ToList();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.MappingName == "Movies" && e.Sources.Count == 1);
            Assert.Contains(all, e => e.MappingName == "TV");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_NoPath_DoesNotThrow()
    {
        var cache = CreateCache(string.Empty);
        cache.UpsertByProviderId("Movies", "imdb", "tt1", MakeItem("A", "tt1"), "s1", Guid.NewGuid(), 0, "Movie");
        await cache.SaveAsync();
    }

    private static BaseItemDto MakeItem(string name, string? imdb = null)
    {
        var dto = new BaseItemDto { Name = name, Type = Jellyfin.Data.Enums.BaseItemKind.Movie };
        if (imdb != null)
        {
            dto.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["imdb"] = imdb };
        }
        return dto;
    }
}
