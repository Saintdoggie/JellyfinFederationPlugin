using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
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

    private static async Task<(ActionExecutingContext Context, bool NextCalled)> RunAsync(
        string action,
        string query,
        Guid[] federated,
        Mock<ILibraryManager>? library = null)
    {
        if (library == null)
        {
            library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(q => q.Tags.SequenceEqual(new[] { "Federated" }) && q.Recursive)))
                .Returns(federated.ToList());
        }

        var filter = new FederationLocalOnlyItemsFilter(library.Object, NullLogger<FederationLocalOnlyItemsFilter>.Instance);
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        var descriptor = new ActionDescriptor
        {
            RouteValues = new Dictionary<string, string?> { ["controller"] = "Items", ["action"] = action }
        };
        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), descriptor),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["excludeItemIds"] = Array.Empty<Guid>() },
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
