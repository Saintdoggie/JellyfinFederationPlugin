using System;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Reconciliation rewrites an item only when the friend's metadata really
/// differs. A comparison that never settled rewrote every federated item in
/// the database on every sync - 12,600 items for one TV library.
/// </summary>
public sealed class ApplySourceMetadataTests
{
    [Fact]
    public void EmptyStudiosAsJellyfinReadsThemBack_AreNotAChange()
    {
        var episode = new Episode { Name = "Pilot", Studios = new[] { string.Empty }, Genres = Array.Empty<string>() };
        var metadata = new FederatedItemMetadata { Name = "Pilot", Studios = Array.Empty<string>(), Genres = Array.Empty<string>() };

        Assert.False(FederationItemPersistenceService.ApplySourceMetadata(episode, metadata));
    }

    [Fact]
    public void SameDateInLocalTimeAndUtc_IsNotAChange()
    {
        var utc = new DateTime(2008, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var episode = new Episode { Name = "Pilot", PremiereDate = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) };
        var metadata = new FederatedItemMetadata { Name = "Pilot", PremiereDate = utc.ToLocalTime() };

        Assert.False(FederationItemPersistenceService.ApplySourceMetadata(episode, metadata));
    }

    [Fact]
    public void RealChangesAreAppliedOnce()
    {
        var episode = new Episode { Name = "Pilot", Overview = "Old", Studios = new[] { string.Empty }, PremiereDate = new DateTime(2008, 1, 20, 0, 0, 0, DateTimeKind.Utc) };
        var metadata = new FederatedItemMetadata
        {
            Name = "Pilot (Extended)",
            Overview = "New",
            Studios = new[] { "AMC" },
            Genres = new[] { "Drama", "" },
            PremiereDate = new DateTime(2008, 1, 21, 0, 0, 0, DateTimeKind.Utc)
        };

        Assert.True(FederationItemPersistenceService.ApplySourceMetadata(episode, metadata));
        Assert.Equal("Pilot (Extended)", episode.Name);
        Assert.Equal("New", episode.Overview);
        Assert.Equal(new[] { "AMC" }, episode.Studios);
        Assert.Equal(new[] { "Drama" }, episode.Genres);
        Assert.Equal(new DateTime(2008, 1, 21, 0, 0, 0, DateTimeKind.Utc), episode.PremiereDate);

        Assert.False(FederationItemPersistenceService.ApplySourceMetadata(episode, metadata));
    }

    [Fact]
    public void MissingValuesFromTheFriendKeepWhatIsStored()
    {
        var episode = new Episode { Name = "Pilot", Overview = "Kept", ProductionYear = 2008 };

        Assert.False(FederationItemPersistenceService.ApplySourceMetadata(episode, new FederatedItemMetadata()));
        Assert.Equal("Kept", episode.Overview);
        Assert.Equal(2008, episode.ProductionYear);
    }
}
