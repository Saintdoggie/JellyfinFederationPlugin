using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Entities;
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
}
