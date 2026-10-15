using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlexLibraryImportTests
{
    private static readonly PlexPathMapping[] WindowsToLinux =
    {
        new() { From = @"D:\Media", To = "/mnt/media" },
        new() { From = @"D:\Media\Movies", To = "/mnt/films" },
    };

    [Theory]
    [InlineData(@"D:\Media\Shows", "/mnt/media/Shows")]
    [InlineData(@"d:\media\Movies\4K", "/mnt/films/4K")]
    [InlineData(@"D:\Media", "/mnt/media")]
    [InlineData(@"D:\MediaExtra\Shows", @"D:\MediaExtra\Shows")]
    [InlineData("/data/movies", "/data/movies")]
    public void MapPath_UsesLongestWholeFolderPrefix(string plexPath, string expected)
        => Assert.Equal(expected, PlexLibraryImportService.MapPath(plexPath, WindowsToLinux));

    [Fact]
    public void MapPath_LinuxToWindows_UsesBackslashes()
        => Assert.Equal(@"E:\Plex\tv\Severance", PlexLibraryImportService.MapPath("/data/tv/Severance", new[] { new PlexPathMapping { From = "/data", To = @"E:\Plex\" } }));

    [Fact]
    public void MapPath_LinuxPrefixesAreCaseSensitive()
        => Assert.Equal("/Data/movies", PlexLibraryImportService.MapPath("/Data/movies", new[] { new PlexPathMapping { From = "/data", To = "/srv" } }));

    [Fact]
    public void MapPath_IgnoresBlankMappings()
        => Assert.Equal("/data/movies", PlexLibraryImportService.MapPath("/data/movies", new[] { new PlexPathMapping { From = " ", To = "/x" }, new PlexPathMapping { From = "/data", To = "" } }));

    [Fact]
    public void BuildPlan_ReportsReadyLinkedWrongTypeAndMissing()
    {
        var sections = new[]
        {
            new PlexSection("1", "Movies", "movie", new[] { "/data/movies", "/data/movies-4k" }),
            new PlexSection("2", "TV Shows", "show", new[] { "/data/tv" }),
            new PlexSection("3", "Anime", "show", new[] { "/data/anime" }),
            new PlexSection("4", "Kids", "movie", new[] { @"Z:\Kids" }),
        };
        var libraries = new List<VirtualFolderInfo>
        {
            new() { Name = "Shows", ItemId = "tv-lib", CollectionType = CollectionTypeOptions.tvshows, Locations = new[] { "/data/tv/" } },
            new() { Name = "Mixed", ItemId = "mixed-lib", CollectionType = CollectionTypeOptions.mixed, Locations = new[] { "/data" + "/anime" } },
        };
        var existing = new HashSet<string> { "/data/movies", "/data/tv", "/data/anime" };

        var plan = PlexLibraryImportService.BuildPlan(sections, null, libraries, existing.Contains);

        Assert.Equal(PlexLibraryImportService.SectionState.Ready, plan[0].State);
        Assert.Equal(new[] { true, false }, plan[0].Folders.Select(f => f.Found).ToArray());
        Assert.Equal(PlexLibraryImportService.SectionState.Linked, plan[1].State);
        Assert.Equal("tv-lib", plan[1].LibraryId);
        Assert.Equal(PlexLibraryImportService.SectionState.WrongType, plan[2].State);
        Assert.Equal(PlexLibraryImportService.SectionState.Missing, plan[3].State);
    }

    [Fact]
    public void BuildPlan_AppliesMappingsBeforeCheckingFolders()
    {
        var plan = PlexLibraryImportService.BuildPlan(
            new[] { new PlexSection("1", "Movies", "movie", new[] { @"D:\Media\Movies" }) },
            WindowsToLinux,
            new List<VirtualFolderInfo>(),
            p => p == "/mnt/films");

        var folder = Assert.Single(Assert.Single(plan).Folders);
        Assert.Equal("/mnt/films", folder.LocalPath);
        Assert.True(folder.Found);
    }

    [Fact]
    public void BuildPlan_DirectoryCheckThatThrows_CountsAsMissing()
    {
        var plan = PlexLibraryImportService.BuildPlan(
            new[] { new PlexSection("1", "Movies", "movie", new[] { "/denied" }) },
            null,
            new List<VirtualFolderInfo>(),
            _ => throw new UnauthorizedAccessException());

        Assert.Equal(PlexLibraryImportService.SectionState.Missing, Assert.Single(plan).State);
    }

    [Theory]
    [InlineData("Movies", new string[0], "Movies")]
    [InlineData("Movies", new[] { "movies" }, "Movies (Plex)")]
    [InlineData("Movies", new[] { "Movies", "Movies (Plex)" }, "Movies (Plex 2)")]
    [InlineData("  ", new string[0], "Plex")]
    public void UniqueName_NeverCollidesWithAnExistingLibrary(string title, string[] taken, string expected)
        => Assert.Equal(expected, PlexLibraryImportService.UniqueName(title, taken));

    [Fact]
    public async Task GetSections_ReadsEachLibrarysFolders()
    {
        var (service, _, _, _) = Build();
        var sections = await service.PlanAsync(null, CancellationToken.None);

        Assert.Equal(new[] { "/data/movies" }, sections[0].Folders.Select(f => f.PlexPath).ToArray());
        Assert.Equal(new[] { "/data/tv", "/data/tv2" }, sections[1].Folders.Select(f => f.PlexPath).ToArray());
    }

    [Fact]
    public async Task Import_CreatesLibrariesOverFoundFolders_TurnsPlexOn_AndScansOnce()
    {
        var (service, library, settings, folders) = Build();
        library.Setup(l => l.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()))
            .Callback<string, CollectionTypeOptions?, LibraryOptions, bool>((name, type, options, _) =>
                folders.Add(new VirtualFolderInfo { Name = name, ItemId = IdFor(name), CollectionType = type, Locations = options.PathInfos.Select(p => p.Path).ToArray() }))
            .Returns(Task.CompletedTask);

        var results = await service.ImportAsync(new[] { "1", "2" }, new List<PlexPathMapping>(), CancellationToken.None);

        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        library.Verify(l => l.AddVirtualFolder("Movies (Plex)", CollectionTypeOptions.movies, It.Is<LibraryOptions>(o => o.PathInfos.Single().Path == "/data/movies"), false), Times.Once);
        library.Verify(l => l.AddVirtualFolder("TV Shows", CollectionTypeOptions.tvshows, It.Is<LibraryOptions>(o => o.PathInfos.Single().Path == "/data/tv"), false), Times.Once);
        library.Verify(l => l.QueueLibraryScan(), Times.Once);
        Assert.Contains("1 Plex folder(s) were not found", results[1].Message);
        Assert.True(settings.Enabled);
        Assert.Equal(new[] { IdFor("Movies (Plex)"), IdFor("TV Shows") }, settings.JellyfinLibraryIds.ToArray());
    }

    [Fact]
    public async Task Import_MissingFolders_CreatesNothing_ButKeepsTheRewrites()
    {
        var (service, library, settings, _) = Build(exists: _ => false);
        var mappings = new List<PlexPathMapping> { new() { From = "/data", To = " /srv/media " } };

        var result = Assert.Single(await service.ImportAsync(new[] { "1" }, mappings, CancellationToken.None));

        Assert.False(result.Ok);
        Assert.Contains("/srv/media/movies", result.Message);
        library.Verify(l => l.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()), Times.Never);
        library.Verify(l => l.QueueLibraryScan(), Times.Never);
        Assert.False(settings.Enabled);
        Assert.Equal("/srv/media", Assert.Single(settings.PathMappings).To);
    }

    [Fact]
    public async Task Import_ExistingAllLibrariesSetting_IsNotNarrowed()
    {
        var (service, library, settings, _) = Build();
        settings.Enabled = true;
        library.Setup(l => l.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        await service.ImportAsync(new[] { "2" }, new List<PlexPathMapping>(), CancellationToken.None);

        Assert.Empty(settings.JellyfinLibraryIds);
        Assert.Empty(settings.SectionKeys);
    }

    private const string SectionsJson = """
    {"MediaContainer":{"size":3,"Directory":[
      {"key":"1","title":"Movies","type":"movie","Location":[{"id":1,"path":"/data/movies"}]},
      {"key":"2","title":"TV Shows","type":"show","Location":[{"id":2,"path":"/data/tv"},{"id":3,"path":"/data/tv2"}]},
      {"key":"3","title":"Music","type":"artist","Location":[{"id":4,"path":"/data/music"}]}
    ]}}
    """;

    private static (PlexLibraryImportService Service, Mock<ILibraryManager> Library, PlexMetadataSourceSettings Settings, List<VirtualFolderInfo> Folders) Build(Func<string, bool>? exists = null)
    {
        var settings = new PlexMetadataSourceSettings { ServerUrl = "http://plex.test:32400", Token = "t" };
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Movies", ItemId = IdFor("Movies"), CollectionType = CollectionTypeOptions.homevideos, Locations = new[] { "/home/videos" } },
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns(() => folders.ToList());
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());

        var plex = new PlexMetadataSourceService(
            NullLogger<PlexMetadataSourceService>.Instance,
            library.Object,
            new Mock<IProviderManager>().Object,
            new Mock<IFileSystem>().Object)
        {
            HttpClientOverride = new HttpClient(new SectionsHandler()),
            SettingsOverride = () => settings,
        };
        var service = new PlexLibraryImportService(NullLogger<PlexLibraryImportService>.Instance, library.Object, plex)
        {
            DirectoryExists = exists ?? (p => p is "/data/movies" or "/data/tv"),
            SettingsOverride = () => settings,
        };
        return (service, library, settings, folders);
    }

    private static string IdFor(string name)
        => new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(name))).ToString("N");

    private sealed class SectionsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsolutePath == "/library/sections"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SectionsJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
