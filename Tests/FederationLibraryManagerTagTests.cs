using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The "🌐 ServerName" tag is the only way (short of a jellyfin-web client plugin) to
/// show which remote server a federated item came from directly on the item itself.
/// The constant "Federated" tag is stamped first on every federated item so all
/// federated content - from every server - can be filtered as one group in any
/// Jellyfin client's tag filter.
/// </summary>
public class FederationLibraryManagerTagTests
{
    [Fact]
    public void AppendServerTag_NoExistingTags_AddsFederatedTagFirst_ThenServerTag()
    {
        var result = FederationLibraryManager.AppendServerTag(null, "Friend's Server");

        Assert.Equal(new[] { "Federated", "🌐 Friend's Server" }, result);
    }

    [Fact]
    public void AppendServerTag_KeepsExistingTags_AndAppendsServerTag()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "HDR", "4K" }, "Friend's Server");

        Assert.Equal(new[] { "Federated", "HDR", "4K", "🌐 Friend's Server" }, result);
    }

    [Fact]
    public void AppendServerTag_ReplacesStalePreviousServerTag_InsteadOfStacking()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "🌐 Old Server", "HDR" }, "New Server");

        Assert.Equal(new[] { "Federated", "HDR", "🌐 New Server" }, result);
    }

    [Fact]
    public void AppendServerTag_NullServerName_StillAddsFederatedTag()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "HDR" }, null);

        Assert.Equal(new[] { "Federated", "HDR" }, result);
    }

    [Fact]
    public void AppendServerTag_AlwaysPlacesFederatedTagFirst()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "HDR" }, "S");

        Assert.Equal("Federated", result[0]);
        Assert.Equal("🌐 S", result[^1]);
    }

    [Fact]
    public void AppendServerTag_DoesNotDuplicateAnExistingFederatedTag()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "Federated", "HDR" }, null);

        Assert.Single(result, t => string.Equals(t, "Federated", StringComparison.Ordinal));
        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void GetServerNameFromTags_StillResolvesServerName_AfterFederatedTagWasPrepended()
    {
        var tags = FederationLibraryManager.AppendServerTag(new[] { "HDR" }, "Friend's Server");

        Assert.Equal("Friend's Server", FederationLibraryManager.GetServerNameFromTags(tags));
    }
}
