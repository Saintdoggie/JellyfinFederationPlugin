using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
public sealed class FederationSharingExpiryTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();
    private readonly Mock<ILibraryManager> _library = new();

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public void FriendSavedBeforeEndDatesExisted_HasNone()
    {
        var old = JsonSerializer.Deserialize<RemoteServer>("{\"Name\":\"Old friend\"}")!;

        Assert.Null(old.SharingExpiresUtc);
        Assert.Empty(old.LibraryShareExpiries);
        Assert.False(FederationPeerAccessService.IsSharingExpired(old));
    }

    [Fact]
    public void EndDate_EndsSharingAtThatMoment()
    {
        var end = new DateTime(2030, 5, 1, 21, 59, 59, DateTimeKind.Utc);
        var friend = new RemoteServer { SharingExpiresUtc = end };

        Assert.False(FederationPeerAccessService.IsSharingExpired(friend, end.AddSeconds(-1)));
        Assert.True(FederationPeerAccessService.IsSharingExpired(friend, end));

        // A date read back from the XML config comes back Unspecified: still UTC.
        friend.SharingExpiresUtc = DateTime.SpecifyKind(end, DateTimeKind.Unspecified);
        Assert.True(FederationPeerAccessService.IsSharingExpired(friend, end));
        friend.SharingExpiresUtc = end.ToLocalTime();
        Assert.False(FederationPeerAccessService.IsSharingExpired(friend, end.AddSeconds(-1)));
        Assert.True(FederationPeerAccessService.IsSharingExpired(friend, end));
    }

    [Fact]
    public void EndDates_SurviveTheXmlConfigRoundTrip()
    {
        var end = new DateTime(2030, 5, 1, 21, 59, 59, DateTimeKind.Utc);
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(RemoteServer));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new RemoteServer
        {
            SharingExpiresUtc = end,
            LibraryShareExpiries = new List<LibraryShareExpiry> { new() { FolderId = "movies", ExpiresUtc = end } }
        });
        var back = (RemoteServer)serializer.Deserialize(new StringReader(writer.ToString()))!;

        Assert.False(FederationPeerAccessService.IsSharingExpired(back, end.AddSeconds(-1)));
        Assert.True(FederationPeerAccessService.IsSharingExpired(back, end));
        Assert.False(FederationPeerAccessService.IsLibraryShareExpired(back, "movies", end.AddSeconds(-1)));
        Assert.True(FederationPeerAccessService.IsLibraryShareExpired(back, "movies", end));
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

    [Fact]
    public void MainSave_PreservesTheEndDatesAndConfigExposesThem()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Configuration", "FederationPluginController.cs")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "Configuration", "FederationPluginController.cs"));
        Assert.Contains("server.SharingExpiresUtc = oldServer.SharingExpiresUtc;", src);
        Assert.Contains("server.LibraryShareExpiries = oldServer.LibraryShareExpiries;", src);
        var server = new RemoteServer { SharingExpiresUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), LibraryShareExpiries = new List<LibraryShareExpiry> { new() { FolderId = "f" } } };
        var shown = FederationServerPresentation.Sanitize(server, null);
        Assert.Equal(server.SharingExpiresUtc, shown.GetType().GetProperty("SharingExpiresUtc")!.GetValue(shown));
        Assert.Same(server.LibraryShareExpiries, shown.GetType().GetProperty("LibraryShareExpiries")!.GetValue(shown));
    }

    private void SetItems(params BaseItem[] items)
    {
        foreach (var item in items)
        {
            _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }
    }
}
