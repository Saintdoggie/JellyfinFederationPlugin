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
