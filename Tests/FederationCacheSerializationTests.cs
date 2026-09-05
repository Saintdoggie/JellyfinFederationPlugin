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
    public async Task Restart_RestoresEveryExactPlaybackSourceWithoutASync()
    {
        var path = Path.Combine(Path.GetTempPath(), "federation-cache-test-" + Guid.NewGuid() + ".json");
        try
        {
            var cache = CreateCache(path);
            var remoteA = Guid.NewGuid();
            var remoteB = Guid.NewGuid();
            var episode = cache.UpsertByProviderId("TV", "tvdb", "episode-1", MakeItem("Pilot"), "a", remoteA, 0, "Episode");
            cache.UpsertByProviderId("TV", "tvdb", "episode-1", MakeItem("Pilot"), "b", remoteB, 1, "Episode");
            await cache.SaveAsync();

            var restarted = CreateCache(path);
            Assert.Equal(episode.Key, restarted.TryGetLocalKeyForRemoteItem("a", remoteA));
            Assert.Equal(episode.Key, restarted.TryGetLocalKeyForRemoteItem("b", remoteB));
            Assert.Null(restarted.TryGetLocalKeyForRemoteItem("a", remoteB));
            Assert.Null(restarted.TryGetLocalKeyForRemoteItem("stranger", remoteA));

            restarted.PruneServerSources("TV", "a", Array.Empty<Guid>());
            Assert.Null(restarted.TryGetLocalKeyForRemoteItem("a", remoteA));
            Assert.Equal(episode.Key, restarted.TryGetLocalKeyForRemoteItem("b", remoteB));
            restarted.ClearMapping("TV");
            Assert.Null(restarted.TryGetLocalKeyForRemoteItem("b", remoteB));
        }
        finally
        {
            File.Delete(path);
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
