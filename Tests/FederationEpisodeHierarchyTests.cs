using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationEpisodeHierarchyTests
{
    private static FederationItemCache CreateCache() => new(NullLogger<FederationItemCache>.Instance);

    private static BaseItemDto MakeSeries(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        Type = Jellyfin.Data.Enums.BaseItemKind.Series
    };

    [Fact]
    public void TryGetLocalKeyForRemoteItem_ReturnsNull_BeforeUpsert()
    {
        var cache = CreateCache();
        Assert.Null(cache.TryGetLocalKeyForRemoteItem("serverA", Guid.NewGuid()));
    }

    [Fact]
    public void TryGetLocalKeyForRemoteItem_ResolvesRawUpsert()
    {
        var cache = CreateCache();
        var seriesId = Guid.NewGuid();
        var entry = cache.UpsertRaw("TV", "serverA", seriesId, MakeSeries(seriesId, "Show"), 0, "Series");

        Assert.Equal(entry.Key, cache.TryGetLocalKeyForRemoteItem("serverA", seriesId));
    }

    [Fact]
    public void TryGetLocalKeyForRemoteItem_ResolvesProviderIdUpsert()
    {
        var cache = CreateCache();
        var seriesId = Guid.NewGuid();
        var dto = MakeSeries(seriesId, "Show");
        dto.ProviderIds = new Dictionary<string, string> { ["tvdb"] = "123" };

        var entry = cache.UpsertByProviderId("TV", "tvdb", "123", dto, "serverA", seriesId, 0, "Series");

        Assert.Equal(entry.Key, cache.TryGetLocalKeyForRemoteItem("serverA", seriesId));
    }

    [Fact]
    public void UpsertRaw_SetsParentKey_NullForTopLevelSetForNested()
    {
        var cache = CreateCache();
        var seriesId = Guid.NewGuid();
        var seriesEntry = cache.UpsertRaw("TV", "serverA", seriesId, MakeSeries(seriesId, "Show"), 0, "Series");

        var seasonId = Guid.NewGuid();
        var seasonEntry = cache.UpsertRaw(
            "TV",
            "serverA",
            seasonId,
            new BaseItemDto { Id = seasonId, Name = "Season 1" },
            0,
            "Season",
            parentKey: seriesEntry.Key);

        Assert.Null(seriesEntry.ParentKey);
        Assert.Equal(seriesEntry.Key, seasonEntry.ParentKey);
    }

    [Fact]
    public void FullChain_SeriesSeasonEpisode_LinksSeriesIdAndSeasonId()
    {
        var cache = CreateCache();
        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => DeterministicGuid(path + "|" + type.FullName));

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var manager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object));

        var seriesRemoteId = Guid.NewGuid();
        var seriesEntry = cache.UpsertRaw("TV", "serverA", seriesRemoteId, MakeSeries(seriesRemoteId, "Show"), 0, "Series");

        var seasonRemoteId = Guid.NewGuid();
        var seasonDto = new BaseItemDto { Id = seasonRemoteId, Name = "Season 1", IndexNumber = 1 };
        var seasonEntry = cache.UpsertRaw("TV", "serverA", seasonRemoteId, seasonDto, 0, "Season", parentKey: seriesEntry.Key);

        var episodeRemoteId = Guid.NewGuid();
        var episodeDto = new BaseItemDto
        {
            Id = episodeRemoteId,
            Name = "Pilot",
            Type = Jellyfin.Data.Enums.BaseItemKind.Episode,
            SeriesId = seriesRemoteId,
            SeasonId = seasonRemoteId,
            ParentIndexNumber = 1,
            IndexNumber = 1
        };
        var episodeEntry = cache.UpsertRaw("TV", "serverA", episodeRemoteId, episodeDto, 0, "Episode", parentKey: seasonEntry.Key);

        var seriesItem = manager.MaterializeItem(seriesEntry);
        var seasonItem = Assert.IsType<Season>(manager.MaterializeItem(seasonEntry));
        var episodeItem = Assert.IsType<Episode>(manager.MaterializeItem(episodeEntry));

        Assert.Equal(seriesItem.Id, seasonItem.SeriesId);
        Assert.Equal(seriesItem.Id, episodeItem.SeriesId);
        Assert.Equal(seasonItem.Id, episodeItem.SeasonId);
        Assert.Equal(manager.ComputeItemId(seriesEntry), seriesItem.Id);
        Assert.Equal(manager.ComputeItemId(seasonEntry), seasonItem.Id);

        // The actual mechanism Jellyfin's Shows/{id}/Seasons and Shows/{id}/Episodes
        // endpoints use to find children: Series.GetSeasons/GetEpisodes filter by
        // SeriesPresentationUniqueKey matching the series' own
        // GetPresentationUniqueKey() - not ParentId, not AncestorIds, not SeriesId.
        // Without this matching, a season/episode is a normal row that's simply
        // never found by the show/season browsing pages, regardless of how correct
        // its hierarchy ids are.
        Assert.Equal(seriesItem.PresentationUniqueKey, seasonItem.SeriesPresentationUniqueKey);
        Assert.Equal(seriesItem.PresentationUniqueKey, episodeItem.SeriesPresentationUniqueKey);
        Assert.False(string.IsNullOrEmpty(seriesItem.PresentationUniqueKey));
    }

    [Fact]
    public void MaterializedItems_AreNotVirtual_AndSeasonsCarryTheirIndexNumber()
    {
        var cache = CreateCache();
        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => DeterministicGuid(path + "|" + type.FullName));

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var manager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object));

        var seriesRemoteId = Guid.NewGuid();
        var seriesEntry = cache.UpsertRaw("TV", "serverA", seriesRemoteId, MakeSeries(seriesRemoteId, "Show"), 0, "Series");

        var seasonRemoteId = Guid.NewGuid();
        var seasonDto = new BaseItemDto { Id = seasonRemoteId, Name = "Season 2", IndexNumber = 2 };
        var seasonEntry = cache.UpsertRaw("TV", "serverA", seasonRemoteId, seasonDto, 0, "Season", parentKey: seriesEntry.Key);

        var episodeRemoteId = Guid.NewGuid();
        var episodeEntry = cache.UpsertRaw(
            "TV",
            "serverA",
            episodeRemoteId,
            new BaseItemDto
            {
                Id = episodeRemoteId,
                Name = "Pilot",
                Type = Jellyfin.Data.Enums.BaseItemKind.Episode,
                ParentIndexNumber = 2,
                IndexNumber = 1
            },
            0,
            "Episode",
            parentKey: seasonEntry.Key);

        var seriesItem = manager.MaterializeItem(seriesEntry);
        var seasonItem = Assert.IsType<Season>(manager.MaterializeItem(seasonEntry));
        var episodeItem = Assert.IsType<Episode>(manager.MaterializeItem(episodeEntry));

        // Jellyfin reads IsVirtualItem as "missing episode" and Series.GetEpisodes /
        // SetSeasonQueryOptions filter it out (query.IsMissing = false) for any user
        // without DisplayMissingEpisodes turned on - which is the default.
        Assert.False(seriesItem.IsVirtualItem);
        Assert.False(seasonItem.IsVirtualItem);
        Assert.False(episodeItem.IsVirtualItem);

        // Federated items must be instances of Jellyfin's own types, never plugin
        // subclasses of them. BaseItem.GetBaseItemKind() resolves an item's kind by
        // Enum.Parse-ing the CLR class name into BaseItemKind, so a subclass name that
        // is not a value of that enum throws - and that call sits under both
        // DtoService.AttachBasicFields and Folder.GetCachedChildren, so it takes down
        // every API response and every folder enumeration involving the item. That is
        // exactly what the 0.0.22 Federated* subclasses did. Calling it here is the
        // regression guard.
        Assert.Equal(BaseItemKind.Series, seriesItem.GetBaseItemKind());
        Assert.Equal(BaseItemKind.Season, seasonItem.GetBaseItemKind());
        Assert.Equal(BaseItemKind.Episode, episodeItem.GetBaseItemKind());

        // SeriesMetadataService creates a duplicate season for any index its episodes
        // reference that no existing season matches on IndexNumber.
        Assert.Equal(2, seasonItem.IndexNumber);
    }

    [Fact]
    public void Movie_MaterializesAsStockMovieType()
    {
        var cache = CreateCache();
        var lm = new Mock<ILibraryManager>();
        lm.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns((string path, Type type) => DeterministicGuid(path + "|" + type.FullName));

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var manager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object));

        var movieRemoteId = Guid.NewGuid();
        cache.UpsertRaw(
            "Movies",
            "serverA",
            movieRemoteId,
            new BaseItemDto { Id = movieRemoteId, Name = "Gran Turismo", Type = Jellyfin.Data.Enums.BaseItemKind.Movie },
            0,
            "Movie");

        var entry = cache.GetEntriesForMapping("Movies").FirstOrDefault();
        Assert.NotNull(entry);

        var movie = Assert.IsType<Movie>(manager.MaterializeItem(entry!));

        // See the note in the hierarchy test above: this must be a stock type so
        // GetBaseItemKind() can resolve it instead of throwing.
        Assert.Equal(BaseItemKind.Movie, movie.GetBaseItemKind());
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
