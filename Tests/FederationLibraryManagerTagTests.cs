using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The "🌐 ServerName" tag is the only way (short of a jellyfin-web client plugin) to
/// show which remote server a federated item came from directly on the item itself.
/// </summary>
public class FederationLibraryManagerTagTests
{
    [Fact]
    public void AppendServerTag_NoExistingTags_AddsServerTag()
    {
        var result = FederationLibraryManager.AppendServerTag(null, "Friend's Server");

        Assert.Equal(new[] { "🌐 Friend's Server" }, result);
    }

    [Fact]
    public void AppendServerTag_KeepsExistingTags_AndAppendsServerTag()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "HDR", "4K" }, "Friend's Server");

        Assert.Equal(new[] { "HDR", "4K", "🌐 Friend's Server" }, result);
    }

    [Fact]
    public void AppendServerTag_ReplacesStalePreviousServerTag_InsteadOfStacking()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "🌐 Old Server", "HDR" }, "New Server");

        Assert.Equal(new[] { "HDR", "🌐 New Server" }, result);
    }

    [Fact]
    public void AppendServerTag_NullServerName_LeavesTagsUnchanged()
    {
        var result = FederationLibraryManager.AppendServerTag(new[] { "HDR" }, null);

        Assert.Equal(new[] { "HDR" }, result);
    }
}
