using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
    [InlineData(true, "FederationDownloadedFrom")]
    [InlineData(false, "federationdownloadedfrom")]
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
    public void DownloadedFileInDownloadsFolder_IsNotVisibleEvenBeforeProviderStamp()
    {
        var root = FederationDownloadService.GetDownloadsRoot();
        Assert.False(string.IsNullOrEmpty(root));
        var item = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = Guid.NewGuid(),
            Path = Path.Combine(root, "Friend Movie.mkv")
        };
        SetItems(item);
        var friend = new RemoteServer { ShareAllLibraries = true };
        var service = new FederationPeerAccessService(_library.Object);
        Assert.False(service.IsItemVisible(friend, null, item.Id, "local-library"));
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

    [Fact]
    public void SpecificSharedLibrary_MatchesItemsByTheirLibraryNotTheirPhysicalFolder()
    {
        // GetTopParent() of a real item is its library's physical folder, which
        // never equals the CollectionFolder id the library picker stores.
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var otherLibrary = new CollectionFolder { Id = Guid.NewGuid() };
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid() };
        SetItems(movie);
        _library.Setup(l => l.GetCollectionFolders(movie)).Returns(new List<Folder> { library });
        var service = new FederationPeerAccessService(_library.Object);

        var shared = new RemoteServer { ShareAllLibraries = false, SharedLibraryFolderIds = new() { library.Id.ToString() } };
        Assert.True(service.IsItemVisible(shared, null, movie.Id));

        var notShared = new RemoteServer { ShareAllLibraries = false, SharedLibraryFolderIds = new() { otherLibrary.Id.ToString() } };
        Assert.False(service.IsItemVisible(notShared, null, movie.Id));

        var perUser = new RemoteServer { ShareAllLibraries = true };
        perUser.RemoteUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = "viewer",
            Mode = RemoteUserAccessMode.CertainLibraries,
            LibraryFolderIds = new List<string> { library.Id.ToString("N") }
        });
        Assert.True(service.IsItemVisible(perUser, "viewer", movie.Id));
        perUser.RemoteUserAccessRules[0].LibraryFolderIds = new List<string> { otherLibrary.Id.ToString("N") };
        Assert.False(service.IsItemVisible(perUser, "viewer", movie.Id));
    }

    [Fact]
    public void ExpiredFriendship_HidesEveryLibraryAndItem()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        SetItems(movie);
        var service = new FederationPeerAccessService(_library.Object);
        var friend = new RemoteServer { ShareAllLibraries = true, SharingExpiresUtc = DateTime.UtcNow.AddMinutes(-1) };

        Assert.False(service.IsLibraryVisible(friend, null, "movies"));
        Assert.False(service.IsItemVisible(friend, null, movie.Id, "movies"));

        friend.SharingExpiresUtc = DateTime.UtcNow.AddDays(1);
        Assert.True(service.IsLibraryVisible(friend, null, "movies"));
        Assert.True(service.IsItemVisible(friend, null, movie.Id, "movies"));
    }

    [Fact]
    public void ExpiredLibrary_IsHiddenWhileOtherLibrariesStay_AlsoWhenSharingEverything()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var show = new Series { Id = Guid.NewGuid() };
        SetItems(movie, show);
        var service = new FederationPeerAccessService(_library.Object);
        var moviesId = Guid.NewGuid().ToString("N");
        var expiry = new List<LibraryShareExpiry> { new() { FolderId = Guid.Parse(moviesId).ToString("D"), ExpiresUtc = DateTime.UtcNow.AddMinutes(-1) } };

        foreach (var friend in new[]
        {
            new RemoteServer { ShareAllLibraries = true, LibraryShareExpiries = expiry },
            new RemoteServer { ShareAllLibraries = false, SharedLibraryFolderIds = new List<string> { moviesId, "tv" }, LibraryShareExpiries = expiry }
        })
        {
            Assert.False(service.IsLibraryVisible(friend, null, moviesId));
            Assert.False(service.IsItemVisible(friend, null, movie.Id, moviesId));
            Assert.True(service.IsLibraryVisible(friend, null, "tv"));
            Assert.True(service.IsItemVisible(friend, null, show.Id, "tv"));
        }
    }

    [Fact]
    public void ExpiredLibrary_BlocksStreamsToo_WhereTheItemAlsoResolvesToItsPhysicalFolder()
    {
        // Stream-time checks resolve every id an item can match, which also
        // includes the library's physical folder - an id that never expires.
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var physicalFolder = new CollectionFolder { Id = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid() };
        SetItems(movie);
        _library.Setup(l => l.GetCollectionFolders(movie)).Returns(new List<Folder> { library, physicalFolder });
        var service = new FederationPeerAccessService(_library.Object);
        var friend = new RemoteServer
        {
            ShareAllLibraries = true,
            LibraryShareExpiries = new List<LibraryShareExpiry> { new() { FolderId = library.Id.ToString("N"), ExpiresUtc = DateTime.UtcNow.AddMinutes(-1) } }
        };

        Assert.False(service.IsItemVisible(friend, null, movie.Id));
        friend.LibraryShareExpiries[0].ExpiresUtc = DateTime.UtcNow.AddDays(1);
        Assert.True(service.IsItemVisible(friend, null, movie.Id));
    }

    [Fact]
    public void LibraryExpiryInTheFuture_KeepsSharing()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        SetItems(movie);
        var service = new FederationPeerAccessService(_library.Object);
        var friend = new RemoteServer
        {
            ShareAllLibraries = false,
            SharedLibraryFolderIds = new List<string> { "movies" },
            LibraryShareExpiries = new List<LibraryShareExpiry> { new() { FolderId = "movies", ExpiresUtc = DateTime.UtcNow.AddDays(2) } }
        };

        Assert.True(service.IsItemVisible(friend, null, movie.Id, "movies"));
    }

    private void SetItems(params BaseItem[] items)
    {
        foreach (var item in items)
        {
            _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }
    }
}
