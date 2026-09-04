using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FederationCatalogTests
{
    [Fact]
    public void NewInstall_DefaultsCosmeticBadgeAndDestructiveReplacementActionsOff()
    {
        var config = new PluginConfiguration();

        Assert.False(config.ShowFederatedCloudBadges);
        Assert.False(config.PreferHigherQualityRemotes);
        Assert.False(config.EnableQualityReplacementActions);
    }

    [Fact]
    public void PrepareLocalCatalog_ExcludesFederatedItems_AndSortsNewestFirst()
    {
        var oldLocal = new Movie { Id = Guid.NewGuid(), Name = "Older", DateCreated = new DateTime(2024, 1, 1) };
        var newLocal = new Movie { Id = Guid.NewGuid(), Name = "Newer", DateCreated = new DateTime(2026, 1, 1) };
        var federated = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Remote",
            DateCreated = new DateTime(2027, 1, 1),
            ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "remote/item" }
        };

        var result = FederationController.PrepareLocalCatalog(new[] { oldLocal, federated, newLocal });

        Assert.Equal(new[] { newLocal.Id, oldLocal.Id }, result.ConvertAll(item => item.Id));
    }

    [Fact]
    public void HasEquivalentLocalCopy_PrefersProviderIds_AndFallsBackToExactTitleYearForWarningOnly()
    {
        var local = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "The Example",
            ProductionYear = 2025,
            ProviderIds = new Dictionary<string, string> { ["tmdb"] = "1234" }
        };

        Assert.True(FederationController.HasEquivalentLocalCopy(
            new BaseItemDto { Name = "Different localized title", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1234" } },
            new[] { local },
            new[] { "tmdb" }));

        Assert.True(FederationController.HasEquivalentLocalCopy(
            new BaseItemDto { Name = "The Example", ProductionYear = 2025 },
            new[] { local },
            new[] { "tmdb" }));

        Assert.False(FederationController.HasEquivalentLocalCopy(
            new BaseItemDto { Name = "The Example", ProductionYear = 2024 },
            new[] { local },
            new[] { "tmdb" }));
    }
}
