using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
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

    [Fact]
    public void FederatedIds_RequiresLoggedInUser_NotAnonymousOrElevation()
    {
        var method = typeof(FederationController).GetMethod(nameof(FederationController.GetFederatedIds));
        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.True(string.IsNullOrEmpty(authorize.Policy));
    }

    [Fact]
    public void GloballyDisabledIds_RequiresElevation_NotAnonymous()
    {
        var method = typeof(FederationController).GetMethod(nameof(FederationController.GetGloballyDisabledIds));
        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("RequiresElevation", authorize.Policy);
    }

    [Fact]
    public void SanitizeServer_IncludesBlockedItemIdsOnOutgoingAndIncomingUserRules()
    {
        var server = new RemoteServer
        {
            Id = "friend-1",
            Name = "Bob",
            Url = "http://bob.example",
            RemoteUserAccessRules =
            {
                new RemoteUserAccessRule
                {
                    RemoteUserId = "kid",
                    RemoteUserName = "Kiddo",
                    Mode = RemoteUserAccessMode.AllLibraries,
                    BlockedItemIds = new List<string> { "item-a", "item-b" }
                }
            },
            FriendUserAccessRules =
            {
                new RemoteUserAccessRule
                {
                    RemoteUserId = "local-user",
                    RemoteUserName = "Me",
                    Mode = RemoteUserAccessMode.AllLibraries,
                    BlockedItemIds = new List<string> { "item-c" }
                }
            }
        };

        var method = typeof(FederationController).GetMethod("SanitizeServer", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var sanitized = method!.Invoke(null, new object[] { server });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(sanitized));
        var root = doc.RootElement;

        var outgoing = Assert.Single(root.GetProperty("RemoteUserAccessRules").EnumerateArray());
        Assert.Equal(new[] { "item-a", "item-b" }, Strings(outgoing.GetProperty("BlockedItemIds")));

        var incoming = Assert.Single(root.GetProperty("FriendUserAccessRules").EnumerateArray());
        Assert.Equal(new[] { "item-c" }, Strings(incoming.GetProperty("BlockedItemIds")));
    }

    private static string[] Strings(JsonElement array)
        => array.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
}
