using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

[Collection("PluginInstance")]
public sealed class FederationPeerAccessServiceTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();
    private readonly Mock<ILibraryManager> _library = new();

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public void GloballyExcludedSeries_HidesItsSeasonAndEpisode()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var season = new Season { Id = Guid.NewGuid(), ParentId = series.Id };
        var episode = new Episode { Id = Guid.NewGuid(), ParentId = season.Id };
        SetItems(series, season, episode);
        _plugin.Configuration.GloballyExcludedItemIds.Add(series.Id.ToString("N"));

        var service = new FederationPeerAccessService(_library.Object);
        var friend = new RemoteServer { ShareAllLibraries = true };

        Assert.False(service.IsItemVisible(friend, null, episode.Id, "tv-library"));
    }

    [Fact]
    public void CertainItemsSeries_AllowsItsDescendantEpisodeButNotAnotherShow()
    {
        var allowedSeries = new Series { Id = Guid.NewGuid() };
        var allowedEpisode = new Episode { Id = Guid.NewGuid(), ParentId = allowedSeries.Id };
        var otherSeries = new Series { Id = Guid.NewGuid() };
        var otherEpisode = new Episode { Id = Guid.NewGuid(), ParentId = otherSeries.Id };
        SetItems(allowedSeries, allowedEpisode, otherSeries, otherEpisode);

        var remoteUserId = "remote-viewer";
        var friend = new RemoteServer { ShareAllLibraries = true };
        friend.RemoteUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = remoteUserId,
            Mode = RemoteUserAccessMode.CertainItems,
            ItemIds = new List<string> { allowedSeries.Id.ToString("N") }
        });

        var service = new FederationPeerAccessService(_library.Object);

        Assert.True(service.IsItemVisible(friend, remoteUserId, allowedEpisode.Id, "tv-library"));
        Assert.False(service.IsItemVisible(friend, remoteUserId, otherEpisode.Id, "tv-library"));
    }

    private void SetItems(params BaseItem[] items)
    {
        foreach (var item in items)
        {
            _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }
    }
}
