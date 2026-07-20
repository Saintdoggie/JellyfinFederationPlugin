using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederatedCacheEntryTests
{
    private static FederationItemCache CreateCache()
        => new(NullLogger<FederationItemCache>.Instance);

    [Fact]
    public void UpdateFromRemote_MapsPeopleIntoMetadata()
    {
        var cache = CreateCache();
        var dto = new BaseItemDto
        {
            Name = "Movie",
            Type = BaseItemKind.Movie,
            People = new[]
            {
                new BaseItemPerson { Name = "Keanu Reeves", Role = "Neo", Type = PersonKind.Actor },
                new BaseItemPerson { Name = "Lana Wachowski", Type = PersonKind.Director }
            }
        };

        var entry = cache.UpsertRaw("Movies", "serverA", Guid.NewGuid(), dto, 0, "Movie");

        Assert.NotNull(entry.Metadata.People);
        Assert.Equal(2, entry.Metadata.People!.Count);
        var actor = entry.Metadata.People[0];
        Assert.Equal("Keanu Reeves", actor.Name);
        Assert.Equal("Neo", actor.Role);
        Assert.Equal(PersonKind.Actor.ToString(), actor.Type);
        var director = entry.Metadata.People[1];
        Assert.Equal("Lana Wachowski", director.Name);
        Assert.Equal(PersonKind.Director.ToString(), director.Type);
        Assert.True(entry.LastRefreshedUtc > DateTime.MinValue);
    }

    [Fact]
    public void Snapshot_IsDetachedFromLiveSources()
    {
        var cache = CreateCache();
        var entry = cache.UpsertRaw("Movies", "serverA", Guid.NewGuid(), MakeItem("A"), 0, "Movie");

        var snapshot = entry.Snapshot();
        entry.AddSource("serverB", Guid.NewGuid(), 1);

        Assert.Single(snapshot.Sources);
        Assert.Equal(2, entry.GetSourcesSnapshot().Length);
    }

    [Fact]
    public async Task SaveAsync_IsAtomic_LeavesNoTempFile_AndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "federation-cache-atomic-" + Guid.NewGuid() + ".json");
        try
        {
            var cache = CreateCache();
            cache.Initialize(path);
            cache.UpsertByProviderId("Movies", "imdb", "tt9", MakeItem("Atomic Movie", "tt9"), "serverA", Guid.NewGuid(), 0, "Movie");
            await cache.SaveAsync();

            Assert.False(File.Exists(path + ".tmp"), "temp file should not remain after save");
            Assert.True(File.Exists(path));

            var cache2 = CreateCache();
            cache2.Initialize(path);
            var entry = Assert.Single(cache2.GetAllEntries());
            Assert.Equal("Atomic Movie", entry.Metadata.Name);
            Assert.Equal("Movies", entry.MappingName);
            Assert.Single(entry.GetSourcesSnapshot());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(path + ".tmp"))
            {
                File.Delete(path + ".tmp");
            }
        }
    }

    [Fact]
    public void GetSourcesSnapshot_IsThreadSafeCopy()
    {
        var cache = CreateCache();
        var entry = cache.UpsertRaw("Movies", "serverA", Guid.NewGuid(), MakeItem("A"), 0, "Movie");

        var snapshot = entry.GetSourcesSnapshot();
        entry.AddSource("serverB", Guid.NewGuid(), 1);

        // The earlier snapshot must not observe the later mutation.
        Assert.Single(snapshot);
    }

    private static BaseItemDto MakeItem(string name, string? imdb = null)
    {
        var dto = new BaseItemDto { Name = name, Type = BaseItemKind.Movie };
        if (imdb != null)
        {
            dto.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["imdb"] = imdb };
        }

        return dto;
    }
}
