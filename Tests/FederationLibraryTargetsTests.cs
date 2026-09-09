using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class FederationLibraryTargetsTests
{
    [Theory]
    [InlineData("Movie", "Movies")]
    [InlineData("Video", "Movies")]
    [InlineData("Series", "Shows")]
    [InlineData("Episode", "Shows")]
    [InlineData("BoxSet", "Collections")]
    public void DefaultName_UsesStockJellyfinFolders(string mediaType, string expected)
    {
        Assert.Equal(expected, FederationLibraryTargets.DefaultName(mediaType));
    }

    [Fact]
    public void Resolve_PrefersExistingShowsFolder_OverFriendsTvShowsName()
    {
        var folders = new[]
        {
            new VirtualFolderInfo { Name = "Federated Shows", CollectionType = CollectionTypeOptions.tvshows },
            new VirtualFolderInfo { Name = "Shows", CollectionType = CollectionTypeOptions.tvshows },
            new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies }
        };

        Assert.Equal("Shows", FederationLibraryTargets.Resolve("Series", folders));
        Assert.Equal("Movies", FederationLibraryTargets.Resolve("Movie", folders));
    }

    [Fact]
    public void Collapse_MovesLegacyFederatedLibrariesAndPlexSections_IntoMoviesAndShows()
    {
        var config = new PluginConfiguration
        {
            LibraryMappings = new List<LibraryMapping>
            {
                new()
                {
                    LocalLibraryName = "Federated Movies",
                    MediaType = "Movie",
                    AutoManaged = false,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "plex", RemoteLibraryId = "1" } }
                },
                new()
                {
                    LocalLibraryName = "TV Shows",
                    MediaType = "Series",
                    AutoManaged = true,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "plex", RemoteLibraryId = "2" } }
                },
                new()
                {
                    LocalLibraryName = "The PSP Experience",
                    MediaType = "Movie",
                    AutoManaged = true,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "plex", RemoteLibraryId = "3" } }
                },
                new()
                {
                    LocalLibraryName = "Anime",
                    MediaType = "Series",
                    AutoManaged = false,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "jf", RemoteLibraryId = "a" } }
                }
            }
        };

        var folders = new[]
        {
            new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies },
            new VirtualFolderInfo { Name = "Shows", CollectionType = CollectionTypeOptions.tvshows }
        };

        var retired = FederationLibraryTargets.Collapse(config, folders);

        Assert.Contains("Federated Movies", retired);
        Assert.Contains("TV Shows", retired);
        Assert.Contains("The PSP Experience", retired);
        Assert.DoesNotContain("Anime", retired);

        Assert.Contains(config.LibraryMappings, m => m.LocalLibraryName == "Movies"
            && m.RemoteLibrarySources.Any(s => s.RemoteLibraryId == "1")
            && m.RemoteLibrarySources.Any(s => s.RemoteLibraryId == "3"));
        Assert.Contains(config.LibraryMappings, m => m.LocalLibraryName == "Shows"
            && m.RemoteLibrarySources.Any(s => s.RemoteLibraryId == "2"));
        Assert.Contains(config.LibraryMappings, m => m.LocalLibraryName == "Anime");
        Assert.Equal(3, config.LibraryMappings.Count);
    }

    [Fact]
    public void Collapse_RemapsCacheKeysAndParentKeys_OntoMoviesAndShows()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var pspId = Guid.NewGuid();
        cache.UpsertByProviderId("Federated Movies", "imdb", "tt1", MakeItem("Movie"), "plex", movieId, 0, "Movie");
        cache.UpsertByProviderId("The PSP Experience", "imdb", "tt-psp", MakeItem("PSP"), "plex", pspId, 0, "Movie");
        var series = cache.UpsertByProviderId("TV Shows", "tvdb", "100", MakeItem("Show", "Series"), "plex", seriesId, 0, "Series");
        cache.UpsertRaw("TV Shows", "plex", seasonId, MakeItem("Season 1", "Season"), 0, "Season", parentKey: series.Key);
        cache.UpsertByProviderId("Anime", "tvdb", "200", MakeItem("Anime", "Series"), "jf", Guid.NewGuid(), 0, "Series");

        var config = new PluginConfiguration
        {
            LibraryMappings = new List<LibraryMapping>
            {
                new()
                {
                    LocalLibraryName = "Federated Movies",
                    MediaType = "Movie",
                    AutoManaged = false,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "plex", RemoteLibraryId = "1" } }
                },
                new()
                {
                    LocalLibraryName = "TV Shows",
                    MediaType = "Series",
                    AutoManaged = true,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "plex", RemoteLibraryId = "2" } }
                },
                new()
                {
                    LocalLibraryName = "The PSP Experience",
                    MediaType = "Movie",
                    AutoManaged = true,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "plex", RemoteLibraryId = "3" } }
                },
                new()
                {
                    LocalLibraryName = "Anime",
                    MediaType = "Series",
                    AutoManaged = false,
                    RemoteLibrarySources = { new RemoteLibrarySource { ServerId = "jf", RemoteLibraryId = "a" } }
                }
            }
        };

        FederationLibraryTargets.Collapse(config, StockFolders(), cache);

        Assert.Null(cache.GetEntryByKey("Federated Movies/imdb:tt1"));
        Assert.Null(cache.GetEntryByKey("The PSP Experience/imdb:tt-psp"));
        var movie = Assert.Single(cache.GetAllEntries(), e => e.Key == "Movies/imdb:tt1");
        Assert.Equal("Movies", movie.MappingName);
        Assert.Equal(movieId, movie.GetPrimarySource()?.RemoteItemId);
        Assert.Equal("Movies/imdb:tt1", cache.TryGetLocalKeyForRemoteItem("plex", movieId));

        var psp = cache.GetEntryByKey("Movies/imdb:tt-psp");
        Assert.NotNull(psp);
        Assert.Equal("Movies", psp.MappingName);

        var remappedSeries = cache.GetEntryByKey("Shows/tvdb:100");
        Assert.NotNull(remappedSeries);
        Assert.Equal("Shows", remappedSeries.MappingName);
        var season = Assert.Single(cache.GetAllEntries(), e => e.ItemType == "Season");
        Assert.StartsWith("Shows/", season.Key, StringComparison.Ordinal);
        Assert.Equal(remappedSeries.Key, season.ParentKey);

        var anime = cache.GetEntryByKey("Anime/tvdb:200");
        Assert.NotNull(anime);
        Assert.Equal("Anime", anime.MappingName);
    }

    [Fact]
    public void Collapse_RemapsLeftoverFederatedMoviesKeys_WhenMappingsAlreadyCollapsed()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteId = Guid.NewGuid();
        cache.UpsertByProviderId("Federated Movies", "imdb", "tt1", MakeItem("Movie"), "s", remoteId, 0, "Movie");

        var config = new PluginConfiguration
        {
            LibraryMappings =
            {
                new LibraryMapping { LocalLibraryName = "Movies", MediaType = "Movie", AutoManaged = true }
            }
        };

        var retired = FederationLibraryTargets.Collapse(config, StockFolders(), cache);

        Assert.Empty(retired);
        Assert.Null(cache.GetEntryByKey("Federated Movies/imdb:tt1"));
        var entry = cache.GetEntryByKey("Movies/imdb:tt1");
        Assert.NotNull(entry);
        Assert.Equal("Movies", entry.MappingName);
        Assert.Equal("Movies/imdb:tt1", cache.TryGetLocalKeyForRemoteItem("s", remoteId));
    }

    [Fact]
    public void RemapStaleCacheEntries_MovesOrphanedPlexSectionKeys_IntoShows()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        cache.UpsertByProviderId("TV Shows", "tvdb", "100", MakeItem("Show", "Series"), "plex", Guid.NewGuid(), 0, "Series");

        var config = new PluginConfiguration
        {
            LibraryMappings =
            {
                new LibraryMapping { LocalLibraryName = "Shows", MediaType = "Series", AutoManaged = true }
            }
        };

        var remapped = FederationLibraryTargets.RemapStaleCacheEntries(config, StockFolders(), cache);

        Assert.Equal(1, remapped);
        var entry = Assert.Single(cache.GetAllEntries());
        Assert.Equal("Shows/tvdb:100", entry.Key);
        Assert.Equal("Shows", entry.MappingName);
    }

    private static VirtualFolderInfo[] StockFolders() =>
    [
        new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies },
        new VirtualFolderInfo { Name = "Shows", CollectionType = CollectionTypeOptions.tvshows }
    ];

    private static BaseItemDto MakeItem(string name, string itemType = "Movie")
    {
        var kind = itemType switch
        {
            "Series" => Jellyfin.Data.Enums.BaseItemKind.Series,
            "Season" => Jellyfin.Data.Enums.BaseItemKind.Season,
            "Episode" => Jellyfin.Data.Enums.BaseItemKind.Episode,
            _ => Jellyfin.Data.Enums.BaseItemKind.Movie
        };
        return new BaseItemDto { Name = name, Type = kind };
    }
}
