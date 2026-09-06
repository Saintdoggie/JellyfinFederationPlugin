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

    [Fact]
    public void BulkDownload_DefaultsDenied_WhileOrdinaryDownloadRemainsAllowed()
    {
        var service = new FederationPeerAccessService(_library.Object);
        var friend = new RemoteServer { AllowDownloads = true };

        Assert.True(service.IsDownloadAllowedForRemoteUser(friend, null));
        Assert.False(service.IsBulkDownloadAllowedForRemoteUser(friend, null));

        friend.AllowBulkDownloads = true;
        Assert.True(service.IsBulkDownloadAllowedForRemoteUser(friend, null));
    }

    [Fact]
    public void BulkDownload_CannotOverrideFriendOrUserDownloadDenial()
    {
        var service = new FederationPeerAccessService(_library.Object);
        var friend = new RemoteServer { AllowDownloads = false, AllowBulkDownloads = true };
        Assert.False(service.IsBulkDownloadAllowedForRemoteUser(friend, null));

        friend.AllowDownloads = true;
        friend.RemoteUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = "blocked-downloader",
            AllowDownload = false
        });
        Assert.False(service.IsBulkDownloadAllowedForRemoteUser(friend, "blocked-downloader"));
    }

    [Theory]
    [InlineData(true, "FederationKey")]
    [InlineData(false, "FederationKey")]
    [InlineData(true, "federationkey")]
    public void FederatedItem_IsNeverVisibleThroughPeerAuthorization(bool shareAll, string providerKey)
    {
        var item = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), ProviderIds = new() { [providerKey] = "another-friend/item" } };
        SetItems(item);
        var friend = new RemoteServer { ShareAllLibraries = shareAll, SharedLibraryFolderIds = new() { "local-library" } };
        var service = new FederationPeerAccessService(_library.Object);
        Assert.False(service.IsItemVisible(friend, null, item.Id, "local-library"));
        Assert.False(service.IsItemVisible(friend, null, Guid.NewGuid(), "local-library"));
        item.ProviderIds.Clear();
        Assert.True(service.IsItemVisible(friend, null, item.Id, "local-library"));
    }

    [Fact]
    public void OutgoingLibraries_HideOnlyWhollyFederatedFolders()
    {
        var folder = new MediaBrowser.Model.Entities.VirtualFolderInfo { Locations = new[] { "/data/federation/friend" } };
        Assert.True(LibraryProvisioningService.IsEntirelyFederatedFolder(folder, "/data/federation"));
        folder.Locations = new[] { "/data/federation/friend", "/data/movies" };
        Assert.False(LibraryProvisioningService.IsEntirelyFederatedFolder(folder, "/data/federation"));
        folder.Locations = new[] { "/data/federation-other" };
        Assert.False(LibraryProvisioningService.IsEntirelyFederatedFolder(folder, "/data/federation"));
    }

    private void SetItems(params BaseItem[] items)
    {
        foreach (var item in items)
        {
            _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }
    }
}
