using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Friends are told when this server's catalog changes, and a receiving
/// server syncs that friend soon after, at most once per spacing.
/// </summary>
[Collection("PluginInstance")]
public sealed class FederationChangeNotifierTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public async Task ChangedTitle_TellsOnlyFriendsWhoCanSeeIt_RemovalTellsEveryone_FederatedItemsTellNoOne()
    {
        var movieLibrary = new CollectionFolder { Id = Guid.NewGuid() };
        var otherLibrary = new CollectionFolder { Id = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid(), Name = "New film" };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        library.Setup(l => l.GetCollectionFolders(movie)).Returns(new List<Folder> { movieLibrary });

        var sees = new RemoteServer { Id = "sees", Name = "Sees", Url = "http://sees.local", ApiKey = "a", Enabled = true, Kind = ServerKind.Jellyfin, ShareAllLibraries = false, SharedLibraryFolderIds = new() { movieLibrary.Id.ToString("N") } };
        var other = new RemoteServer { Id = "other", Name = "Other", Url = "http://other.local", ApiKey = "b", Enabled = true, Kind = ServerKind.Jellyfin, ShareAllLibraries = false, SharedLibraryFolderIds = new() { otherLibrary.Id.ToString("N") } };
        _plugin.Configuration.RemoteServers.Add(sees);
        _plugin.Configuration.RemoteServers.Add(other);
        var notices = new Dictionary<string, int>();
        var factory = new Mock<IRemoteServerClientFactory>();
        foreach (var friend in new[] { sees, other })
        {
            var handler = new NoticeHandler(() => { lock (notices) { notices[friend.Id] = notices.GetValueOrDefault(friend.Id) + 1; } });
            factory.Setup(f => f.GetClient(It.Is<RemoteServer>(s => s.Id == friend.Id)))
                .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(friend.Url) }));
        }

        using var notifier = new FederationChangeNotifier(NullLogger<FederationChangeNotifier>.Instance, library.Object, factory.Object, new FederationPeerAccessService(library.Object))
        {
            QuietPeriod = TimeSpan.FromHours(1)
        };

        notifier.Record(movie, removed: false);
        Assert.Equal(1, await notifier.FlushAsync(CancellationToken.None));
        Assert.Equal(1, notices.GetValueOrDefault("sees"));
        Assert.Equal(0, notices.GetValueOrDefault("other"));

        notifier.Record(new Movie { Id = Guid.NewGuid() }, removed: true);
        Assert.Equal(2, await notifier.FlushAsync(CancellationToken.None));

        var federated = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["FederationKey"] = "Movies/imdb:tt1" } };
        notifier.Record(federated, removed: false);
        notifier.Record(federated, removed: true);
        Assert.Equal(0, await notifier.FlushAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BurstOfChanges_SendsOneNotice_AfterTheQuietPeriod()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(movie);
        var friend = new RemoteServer { Id = "f", Name = "F", Url = "http://f.local", ApiKey = "a", Enabled = true, Kind = ServerKind.Jellyfin, ShareAllLibraries = true };
        _plugin.Configuration.RemoteServers.Add(friend);
        var notices = 0;
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns(new RemoteServerClient(friend, NullLogger.Instance, new HttpClient(new NoticeHandler(() => Interlocked.Increment(ref notices))) { BaseAddress = new Uri(friend.Url) }));
        using var notifier = new FederationChangeNotifier(NullLogger<FederationChangeNotifier>.Instance, library.Object, factory.Object, new FederationPeerAccessService(library.Object))
        {
            QuietPeriod = TimeSpan.FromMilliseconds(200)
        };
        notifier.Start();

        for (var i = 0; i < 50; i++)
        {
            notifier.Record(new Movie { Id = Guid.NewGuid() }, removed: false);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref notices) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref notices));
    }

    [Fact]
    public async Task Receiver_MergesNotices_AndWaitsOutARunningSync()
    {
        var sync = CreateSyncService();
        sync.NotifiedSyncSpacing = TimeSpan.Zero;
        sync.BusyRetryDelay = TimeSpan.FromMilliseconds(10);
        var calls = 0;
        var done = new TaskCompletionSource();
        sync.NotifiedSyncOverride = async (serverId, ct) =>
        {
            await Task.Delay(50, ct);
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                return new SyncResult { Success = true, Message = "A sync is already in progress; skipped" };
            }

            done.TrySetResult();
            return new SyncResult { Success = true, Message = "Refreshed 3 items from Friend" };
        };

        Assert.True(sync.OnFriendCatalogChanged("friend", CancellationToken.None));
        Assert.False(sync.OnFriendCatalogChanged("friend", CancellationToken.None));
        Assert.False(sync.OnFriendCatalogChanged("friend", CancellationToken.None));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref calls));

        // Scheduled again once the previous one finished.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!sync.OnFriendCatalogChanged("friend", CancellationToken.None) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(DateTime.UtcNow < deadline);
    }

    private static FederationSyncService CreateSyncService()
    {
        var factory = new Mock<IRemoteServerClientFactory>();
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var lm = new Mock<ILibraryManager>();
        var bandwidth = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, factory.Object);
        var manager = new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, factory.Object, cache, bandwidth, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, manager, Mock.Of<MediaBrowser.Controller.Persistence.IItemPersistenceService>());
        return new FederationSyncService(NullLogger<FederationSyncService>.Instance, manager, factory.Object, cache, persistence, bandwidth, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));
    }

    private sealed class NoticeHandler : HttpMessageHandler
    {
        private readonly Action _onNotice;

        public NoticeHandler(Action onNotice) => _onNotice = onNotice;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/Peer/CatalogChanged", StringComparison.Ordinal))
            {
                _onNotice();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
