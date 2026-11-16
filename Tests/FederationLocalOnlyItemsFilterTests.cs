using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Jellyfin 12 dropped ExcludeItemTags from its items endpoint, so the library
/// "This server" tab's query was silently unfiltered and the browser hid most of
/// each page. These pin the server-side exclusion that replaces it.
/// </summary>
public class FederationLocalOnlyItemsFilterTests
{
    [Theory]
    [InlineData("Federated", true)]
    [InlineData("federated", true)]
    [InlineData("Kids,Federated", true)]
    [InlineData("Kids|Federated", true)]
    [InlineData("Kids", false)]
    [InlineData("FederatedX", false)]
    [InlineData("", false)]
    public void AsksForLocalOnly_MatchesTheMarkerTagOnly(string value, bool expected)
        => Assert.Equal(expected, FederationLocalOnlyItemsFilter.AsksForLocalOnly(new[] { value }));

    [Fact]
    public void MergeExcluded_KeepsTheQuerysOwnExclusions()
    {
        var own = Guid.NewGuid();
        var federated = Guid.NewGuid();

        Assert.Equal(new[] { own, federated }, FederationLocalOnlyItemsFilter.MergeExcluded(new[] { own }, new[] { federated, own }));
        Assert.Equal(new[] { federated }, FederationLocalOnlyItemsFilter.MergeExcluded(null, new[] { federated }));
    }

    [Theory]
    [InlineData("GetItems")]
    [InlineData("GetItemsByUserIdLegacy")]
    public async Task LocalOnlyQuery_ExcludesEveryFederatedItem(string action)
    {
        var federated = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var (context, nextCalled) = await RunAsync(action, "?ExcludeItemTags=Federated", federated);

        Assert.True(nextCalled);
        Assert.Equal(federated, (Guid[])context.ActionArguments["excludeItemIds"]!);
    }

    [Fact]
    public async Task OrdinaryQueryOrOtherAction_IsUntouched()
    {
        var federated = new[] { Guid.NewGuid() };

        var (plain, _) = await RunAsync("GetItems", "?ParentId=1", federated);
        Assert.Empty((Guid[])plain.ActionArguments["excludeItemIds"]!);

        var (other, _) = await RunAsync("GetResumeItems", "?ExcludeItemTags=Federated", federated);
        Assert.Empty((Guid[])other.ActionArguments["excludeItemIds"]!);
    }

    [Fact]
    public async Task LibraryFailure_LeavesTheRequestAlone()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>())).Throws(new InvalidOperationException());
        var (context, nextCalled) = await RunAsync("GetItems", "?ExcludeItemTags=Federated", Array.Empty<Guid>(), library);

        Assert.True(nextCalled);
        Assert.Empty((Guid[])context.ActionArguments["excludeItemIds"]!);
    }

    [Fact]
    public void NarrowTo_KeepsTheQuerysOwnIds_AndIsNeverEmpty()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(new[] { a, b }, FederationLocalOnlyItemsFilter.NarrowTo(null, new[] { a, b }));
        Assert.Equal(new[] { b }, FederationLocalOnlyItemsFilter.NarrowTo(new[] { b, Guid.NewGuid() }, new[] { a, b }));

        // An empty id list would mean "no filter" to Jellyfin and show every title.
        Assert.Equal(new[] { FederationLocalOnlyItemsFilter.NoMatch }, FederationLocalOnlyItemsFilter.NarrowTo(null, Array.Empty<Guid>()));
        Assert.Equal(new[] { FederationLocalOnlyItemsFilter.NoMatch }, FederationLocalOnlyItemsFilter.NarrowTo(new[] { Guid.NewGuid() }, new[] { a }));
    }

    /// <summary>
    /// A server's tab used to hide cards in the browser, page by page. The server
    /// now answers it, and a title owned here that the server also has belongs
    /// to its tab although its one card is the own one.
    /// </summary>
    [Fact]
    public async Task ServerTabQuery_IsHeldToThatServersTitles_IncludingOwnedOnes()
    {
        FederationLocalOnlyItemsFilter.ResetMemo();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "federation-origin-test-" + Guid.NewGuid() + ".json");
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        cache.Initialize(path);
        MediaBrowser.Model.Dto.BaseItemDto Dto(string name, string imdb) => new()
        {
            Name = name,
            Type = Jellyfin.Data.Enums.BaseItemKind.Movie,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["imdb"] = imdb }
        };
        var fromVps = cache.UpsertByProviderId("Movies", "imdb", "tt1", Dto("Only there", "tt1"), "vps", Guid.NewGuid(), 0, "Movie");
        var fromFriend = cache.UpsertByProviderId("Movies", "imdb", "tt2", Dto("Friend's", "tt2"), "friend", Guid.NewGuid(), 0, "Movie");
        cache.UpsertByProviderId("Movies", "imdb", "tt3", Dto("Owned and there", "tt3"), "vps", Guid.NewGuid(), 0, "Movie");

        Movie Item(string? federationKey, string? imdb)
        {
            var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
            if (federationKey != null) { movie.ProviderIds["FederationKey"] = federationKey; }
            if (imdb != null) { movie.ProviderIds["imdb"] = imdb; }
            return movie;
        }

        var shownFromVps = Item(fromVps.Key, "tt1");
        var shownFromFriend = Item(fromFriend.Key, "tt2");
        var ownedAlsoOnVps = Item(null, "tt3");
        var ownedOnlyHere = Item(null, "tt4");
        var parent = Guid.NewGuid();
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.Recursive && q.ParentId == parent)))
            .Returns(new List<BaseItem> { shownFromVps, shownFromFriend, ownedAlsoOnVps, ownedOnlyHere });

        var config = new Jellyfin.Plugin.Federation.Configuration.PluginConfiguration
        {
            RemoteServers = new List<Jellyfin.Plugin.Federation.Configuration.RemoteServer>
            {
                new() { Id = "vps", Name = "ubuntu-vps", Enabled = true },
                new() { Id = "friend", Name = "Friend", Enabled = true }
            },
            LibraryMappings = new List<Jellyfin.Plugin.Federation.Configuration.LibraryMapping>
            {
                new() { LocalLibraryName = "Movies", Enabled = true }
            }
        };

        var arguments = new Dictionary<string, object?>
        {
            ["excludeItemIds"] = Array.Empty<Guid>(),
            ["ids"] = Array.Empty<Guid>(),
            ["parentId"] = (Guid?)parent,
            ["includeItemTypes"] = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
            ["startIndex"] = (int?)200
        };
        var (context, nextCalled) = await RunAsync("GetItems", "?federationOrigin=Ubuntu-VPS", Array.Empty<Guid>(), library, arguments, cache, config);

        Assert.True(nextCalled);
        Assert.Equal(new[] { shownFromVps.Id, ownedAlsoOnVps.Id }, (Guid[])context.ActionArguments["ids"]!);
        Assert.Equal(0, context.ActionArguments["startIndex"]);
        Assert.Empty((Guid[])context.ActionArguments["excludeItemIds"]!);

        // A name no enabled server has shows nothing rather than everything.
        var (unknown, _) = await RunAsync("GetItems", "?federationOrigin=Nobody", Array.Empty<Guid>(), library, new Dictionary<string, object?> { ["ids"] = Array.Empty<Guid>() }, cache, config);
        Assert.Equal(new[] { FederationLocalOnlyItemsFilter.NoMatch }, (Guid[])unknown.ActionArguments["ids"]!);

        // Not a library list: untouched.
        var (other, _) = await RunAsync("GetResumeItems", "?federationOrigin=Ubuntu-VPS", Array.Empty<Guid>(), library, new Dictionary<string, object?> { ["ids"] = Array.Empty<Guid>() }, cache, config);
        Assert.Empty((Guid[])other.ActionArguments["ids"]!);
    }

    /// <summary>
    /// Titles of an offline server used to be hidden by the browser after the page
    /// had been filled, which left short pages. The server leaves them out.
    /// </summary>
    [Fact]
    public void OfflineServersTitles_AreLeftOutOfLibraryQueries_ButNotUncheckedOnes()
    {
        FederationLocalOnlyItemsFilter.ResetMemo();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "federation-offline-test-" + Guid.NewGuid() + ".json");
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        cache.Initialize(path);
        MediaBrowser.Model.Dto.BaseItemDto Dto(string name, string imdb) => new()
        {
            Name = name,
            Type = Jellyfin.Data.Enums.BaseItemKind.Movie,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["imdb"] = imdb }
        };
        var onlyOffline = cache.UpsertByProviderId("Movies", "imdb", "tt1", Dto("Only on the offline one", "tt1"), "down", Guid.NewGuid(), 0, "Movie");
        var both = cache.UpsertByProviderId("Movies", "imdb", "tt2", Dto("On both", "tt2"), "down", Guid.NewGuid(), 0, "Movie");
        cache.UpsertByProviderId("Movies", "imdb", "tt2", Dto("On both", "tt2"), "up", Guid.NewGuid(), 1, "Movie");
        var onlyOff = cache.UpsertByProviderId("Movies", "imdb", "tt3", Dto("On the switched-off one", "tt3"), "off", Guid.NewGuid(), 0, "Movie");
        var unchecked_ = cache.UpsertByProviderId("Movies", "imdb", "tt4", Dto("Not checked yet", "tt4"), "new", Guid.NewGuid(), 0, "Movie");
        var ids = new Dictionary<FederatedCacheEntry, Guid>
        {
            [onlyOffline] = Guid.NewGuid(), [both] = Guid.NewGuid(), [onlyOff] = Guid.NewGuid(), [unchecked_] = Guid.NewGuid()
        };
        var config = new Jellyfin.Plugin.Federation.Configuration.PluginConfiguration
        {
            RemoteServers = new List<Jellyfin.Plugin.Federation.Configuration.RemoteServer>
            {
                new() { Id = "down", Name = "Down", Enabled = true },
                new() { Id = "up", Name = "Up", Enabled = true },
                new() { Id = "off", Name = "Off", Enabled = false },
                new() { Id = "new", Name = "New", Enabled = true }
            }
        };
        var filter = new FederationLocalOnlyItemsFilter(new Mock<ILibraryManager>().Object, NullLogger<FederationLocalOnlyItemsFilter>.Instance)
        {
            CacheOverride = cache,
            Settings = () => config,
            IsOffline = id => id == "down",
            ItemIdOf = entry => ids[entry]
        };

        Assert.Equal(new[] { ids[onlyOffline], ids[onlyOff] }.OrderBy(g => g), filter.UnavailableItemIds().OrderBy(g => g));

        // Everybody up: nothing is added to any query.
        FederationLocalOnlyItemsFilter.ResetMemo();
        config.RemoteServers[2].Enabled = true;
        filter.IsOffline = _ => false;
        Assert.Empty(filter.UnavailableItemIds());
        FederationLocalOnlyItemsFilter.ResetMemo();
        try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
    }

    /// <summary>
    /// The home page's rows have no id list to narrow, so the answer is narrowed:
    /// more is asked for, the row keeps only what the chosen tab holds (an episode
    /// goes by its show) and is cut back to the asked length.
    /// </summary>
    [Fact]
    public async Task HomeRows_AreNarrowedOnTheServer_ForServerTabAndThisServerTab()
    {
        FederationLocalOnlyItemsFilter.ResetMemo();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "federation-home-test-" + Guid.NewGuid() + ".json");
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        cache.Initialize(path);
        var fromVps = cache.UpsertByProviderId("Movies", "imdb", "tt1", new MediaBrowser.Model.Dto.BaseItemDto
        {
            Name = "Only there",
            Type = Jellyfin.Data.Enums.BaseItemKind.Movie,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["imdb"] = "tt1" }
        }, "vps", Guid.NewGuid(), 0, "Movie");
        var vpsShow = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FederationKey"] = fromVps.Key, ["imdb"] = "tt1" } };
        var own = new Movie { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["imdb"] = "tt9" } };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { vpsShow, own });
        library.Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(q => q.Tags.SequenceEqual(new[] { "Federated" }) && q.Recursive)))
            .Returns(new List<Guid> { vpsShow.Id });
        var config = new Jellyfin.Plugin.Federation.Configuration.PluginConfiguration
        {
            RemoteServers = new List<Jellyfin.Plugin.Federation.Configuration.RemoteServer> { new() { Id = "vps", Name = "ubuntu-vps", Enabled = true } },
            LibraryMappings = new List<Jellyfin.Plugin.Federation.Configuration.LibraryMapping> { new() { LocalLibraryName = "Movies", Enabled = true } }
        };
        var filter = new FederationLocalOnlyItemsFilter(library.Object, NullLogger<FederationLocalOnlyItemsFilter>.Instance) { CacheOverride = cache, Settings = () => config };
        filter.ItemIdOf = _ => Guid.NewGuid();

        MediaBrowser.Model.Dto.BaseItemDto Card(Guid id, Guid? series = null) => new() { Id = id, SeriesId = series };
        var episodeOfVps = Card(Guid.NewGuid(), vpsShow.Id);
        var ownEpisode = Card(Guid.NewGuid(), own.Id);
        var row = new List<MediaBrowser.Model.Dto.BaseItemDto> { Card(own.Id), Card(vpsShow.Id), ownEpisode, episodeOfVps, Card(vpsShow.Id), Card(vpsShow.Id) };

        async Task<(object? Value, object? Limit)> Ask(string controller, string action, string query, object value, int? limit)
        {
            var http = new DefaultHttpContext();
            http.Request.QueryString = new QueryString(query);
            var descriptor = new ActionDescriptor { RouteValues = new Dictionary<string, string?> { ["controller"] = controller, ["action"] = action } };
            var arguments = new Dictionary<string, object?> { ["limit"] = limit };
            var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), new List<IFilterMetadata>(), arguments, new object());
            var result = new Microsoft.AspNetCore.Mvc.ObjectResult(value);
            await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()) { Result = result }));
            return (result.Value, arguments["limit"]);
        }

        // The server's tab: its titles and its shows' episodes, cut to the 3 asked for; 60 were asked of Jellyfin.
        var (latest, widened) = await Ask("UserLibrary", "GetLatestMedia", "?federationOrigin=Ubuntu-VPS", row, 3);
        Assert.Equal(60, widened);
        Assert.Equal(new[] { vpsShow.Id, episodeOfVps.Id, vpsShow.Id }, ((IEnumerable<MediaBrowser.Model.Dto.BaseItemDto>)latest!).Select(d => d.Id));

        // "This server": nothing shown from another server, in a paged answer too.
        var (resume, _) = await Ask("Items", "GetResumeItems", "?ExcludeItemTags=Federated", new MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Model.Dto.BaseItemDto>(0, row.Count, row), 12);
        var page = (MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Model.Dto.BaseItemDto>)resume!;
        Assert.Equal(new[] { own.Id, ownEpisode.Id }, page.Items.Select(d => d.Id));
        Assert.Equal(2, page.TotalRecordCount);

        // No tab chosen and nothing offline: the answer and the limit are Jellyfin's own.
        var (untouched, limit) = await Ask("TvShows", "GetNextUp", string.Empty, row, 3);
        Assert.Same(row, untouched);
        Assert.Equal(3, limit);

        // Another endpoint with the same parameter is left alone.
        var (other, _) = await Ask("UserLibrary", "GetItem", "?federationOrigin=Ubuntu-VPS", row, 3);
        Assert.Same(row, other);

        // A search under the server's tab: Jellyfin ignores the id list there, so the answer is narrowed.
        var (search, _) = await Ask("Items", "GetItems", "?federationOrigin=Ubuntu-VPS&searchTerm=the", new MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Model.Dto.BaseItemDto>(0, row.Count, row), 100);
        var hits = (MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Model.Dto.BaseItemDto>)search!;
        Assert.Equal(new[] { vpsShow.Id, episodeOfVps.Id, vpsShow.Id, vpsShow.Id }, hits.Items.Select(d => d.Id));
        Assert.Equal(4, hits.TotalRecordCount);

        // A library page (no search) keeps Jellyfin's own paged answer: it was asked by id.
        var paged = new MediaBrowser.Model.Querying.QueryResult<MediaBrowser.Model.Dto.BaseItemDto>(0, row.Count, row);
        var (library1, _) = await Ask("Items", "GetItems", "?federationOrigin=Ubuntu-VPS", paged, 100);
        Assert.Same(paged, library1);

        Assert.Equal(300, FederationLocalOnlyItemsFilter.WidenedLimit(100));
    }

    private static async Task<(ActionExecutingContext Context, bool NextCalled)> RunAsync(
        string action,
        string query,
        Guid[] federated,
        Mock<ILibraryManager>? library = null,
        Dictionary<string, object?>? arguments = null,
        FederationItemCache? cache = null,
        Jellyfin.Plugin.Federation.Configuration.PluginConfiguration? config = null)
    {
        if (library == null)
        {
            library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(q => q.Tags.SequenceEqual(new[] { "Federated" }) && q.Recursive)))
                .Returns(federated.ToList());
        }

        var filter = new FederationLocalOnlyItemsFilter(library.Object, NullLogger<FederationLocalOnlyItemsFilter>.Instance) { CacheOverride = cache };
        if (config != null)
        {
            filter.Settings = () => config;
        }

        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        var descriptor = new ActionDescriptor
        {
            RouteValues = new Dictionary<string, string?> { ["controller"] = "Items", ["action"] = action }
        };
        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), descriptor),
            new List<IFilterMetadata>(),
            arguments ?? new Dictionary<string, object?> { ["excludeItemIds"] = Array.Empty<Guid>() },
            new object());

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
        });

        return (context, nextCalled);
    }
}
