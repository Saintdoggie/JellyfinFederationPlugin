using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Index sync: a friend lists [id, hash] per title and only new or changed
/// titles are downloaded. Unchanged titles must stay, vanished ones must go,
/// and nothing may be pruned on an incomplete list.
/// </summary>
public partial class FederationSyncServiceTests
{
    [Fact]
    public async Task IndexSync_DownloadsOnlyNewAndChangedTitles_AndPrunesRemovedOnes()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 5).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        sync.IndexPageSize = 2;
        sync.IdsBatchSize = 2;

        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Equal(movies.ToHashSet(), peer.Fetched.ToHashSet());
        Assert.All(movies, id => Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, id)));
        Assert.Equal(0, peer.FullPageRequests);

        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Empty(peer.Fetched);
        Assert.All(movies, id => Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, id)));

        peer.Change(movies[1], "Movie 1 (Director's Cut)");
        var added = peer.Add("Movie", "Movie 5");
        peer.Remove(movies[3]);
        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Equal(new HashSet<Guid> { movies[1], added }, peer.Fetched.ToHashSet());
        Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, movies[3]));
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.False(CacheHasRemoteId(cache, mapping.LocalLibraryName, movies[3]));
        Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, added));
        Assert.Equal("Movie 1 (Director's Cut)", cache.GetEntriesForMapping(mapping.LocalLibraryName)
            .Single(e => e.GetSourcesSnapshot().Any(s => s.RemoteItemId == movies[1])).Metadata.Name);
    }

    [Fact]
    public async Task IndexSync_FailedIndex_KeepsEverything()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 3).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        await InvokeRefreshMappingAsync(sync, mapping, config);

        peer.Remove(movies[0]);
        peer.FailIndexAfterFirstPage = true;
        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.All(movies, id => Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, id)));
    }

    [Fact]
    public async Task IndexSync_FailedBatchFetch_KeepsEverything()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 3).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        await InvokeRefreshMappingAsync(sync, mapping, config);

        peer.Remove(movies[0]);
        peer.Change(movies[1], "Renamed");
        peer.FailIds = true;
        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, movies[0]));

        // The failed cycle must not have recorded the new hash as imported.
        peer.FailIds = false;
        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Contains(movies[1], peer.Fetched);
        Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, movies[0]));
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.False(CacheHasRemoteId(cache, mapping.LocalLibraryName, movies[0]));
    }

    [Fact]
    public async Task IndexSync_RefetchesWhenSettingsChangeOrTheCacheLostAnEntry()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 3).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, server) = CreateMovieSync(peer);
        await InvokeRefreshMappingAsync(sync, mapping, config);

        // Entry gone from the cache, hash unchanged: fetched again.
        cache.PruneServerSources(mapping.LocalLibraryName, server.Id, new HashSet<Guid> { movies[0], movies[1] });
        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Equal(new[] { movies[2] }, peer.Fetched);
        Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, movies[2]));

        // A receiver setting that shapes stored titles changed: fetch everything.
        config.IncomingFilter.BlockedGenres.Add("Horror");
        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Equal(movies.ToHashSet(), peer.Fetched.ToHashSet());
    }

    [Fact]
    public async Task IndexSync_UnchangedEpisodesKeepTheirSeasons_AndAChangedShowRefetchesOnlyItsEpisodes()
    {
        var peer = new IndexedPeerHandler();
        var showA = peer.Add("Series", "Show A");
        var showB = peer.Add("Series", "Show B");
        var seasonA = Guid.NewGuid();
        var seasonB = Guid.NewGuid();
        var episodeA = peer.Add("Episode", "A1", showA, seasonA);
        var episodeB = peer.Add("Episode", "B1", showB, seasonB);
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        mapping.MediaType = "Series";

        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Equal(new HashSet<Guid> { showA, showB, episodeA, episodeB }, peer.Fetched.ToHashSet());

        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Empty(peer.Fetched);
        foreach (var id in new[] { showA, showB, seasonA, seasonB, episodeA, episodeB })
        {
            Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, id));
        }

        peer.Change(showA, "Show A (Remastered)");
        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Equal(new HashSet<Guid> { showA, episodeA }, peer.Fetched.ToHashSet());
        Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, seasonB));
    }

    [Fact]
    public async Task ShowsGainingProviderIds_KeepEveryShowSeasonAndEpisodeKey()
    {
        // A friend's metadata refresh adds TVDB ids hours after the files appear.
        // The old key change deleted and recreated every show, season and episode.
        var peer = new IndexedPeerHandler();
        var shows = Enumerable.Range(0, 3).Select(i => peer.Add("Series", "Show " + i)).ToList();
        foreach (var show in shows)
        {
            peer.Add("Episode", "Pilot", show, Guid.NewGuid());
        }

        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        mapping.MediaType = "Series";
        config.EnableDedup = true;
        await InvokeRefreshMappingAsync(sync, mapping, config);
        var before = cache.GetEntriesForMapping(mapping.LocalLibraryName).Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(3 + 3 + 3, before.Count);

        for (var i = 0; i < shows.Count; i++)
        {
            peer.SetTvdb(shows[i], "9000" + i);
        }

        await InvokeRefreshMappingAsync(sync, mapping, config);
        var after = cache.GetEntriesForMapping(mapping.LocalLibraryName).Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task MusicLibrary_SyncsSongsInsideTheirAlbums()
    {
        var peer = new IndexedPeerHandler();
        var albumA = peer.Add("MusicAlbum", "Album A");
        var albumB = peer.Add("MusicAlbum", "Album B");
        var songsA = Enumerable.Range(1, 3).Select(i => peer.Add("Audio", "A" + i, albumA)).ToList();
        var songsB = Enumerable.Range(1, 3).Select(i => peer.Add("Audio", "B" + i, albumB)).ToList();
        var single = peer.Add("Audio", "Single");
        var (sync, cache, mapping, config, server) = CreateMovieSync(peer);
        mapping.MediaType = "MusicAlbum";

        await InvokeRefreshMappingAsync(sync, mapping, config);

        var entries = cache.GetEntriesForMapping(mapping.LocalLibraryName).ToList();
        Assert.Equal(2, entries.Count(e => e.ItemType == "MusicAlbum"));
        Assert.Equal(7, entries.Count(e => e.ItemType == "Audio"));
        var keyA = cache.TryGetLocalKeyForRemoteItem(server.Id, albumA);
        foreach (var song in songsA)
        {
            Assert.Equal(keyA, cache.GetEntryByKey(cache.TryGetLocalKeyForRemoteItem(server.Id, song)!)!.ParentKey);
        }

        Assert.Null(cache.GetEntryByKey(cache.TryGetLocalKeyForRemoteItem(server.Id, single)!)!.ParentKey);
        Assert.Equal(0, peer.IdsRequestsWithoutTypeOrLibrary);

        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Empty(peer.Fetched);

        peer.Change(albumB, "Album B (Remastered)");
        peer.Fetched.Clear();
        await InvokeRefreshMappingAsync(sync, mapping, config);
        Assert.Equal(new HashSet<Guid>(songsB.Append(albumB)), peer.Fetched.ToHashSet());
    }

    [Fact]
    public async Task LibrarySetToAudio_AlsoSyncsTheAlbums()
    {
        var peer = new IndexedPeerHandler();
        var album = peer.Add("MusicAlbum", "Album");
        var songs = Enumerable.Range(1, 3).Select(i => peer.Add("Audio", "Song " + i, album)).ToList();
        var (sync, cache, mapping, config, server) = CreateMovieSync(peer);
        mapping.MediaType = "Audio";

        await InvokeRefreshMappingAsync(sync, mapping, config);

        var albumKey = cache.TryGetLocalKeyForRemoteItem(server.Id, album);
        Assert.NotNull(albumKey);
        Assert.All(songs, id => Assert.Equal(albumKey, cache.GetEntryByKey(cache.TryGetLocalKeyForRemoteItem(server.Id, id)!)!.ParentKey));
    }

    [Fact]
    public async Task IndexSync_OlderPeerWithoutIndex_FallsBackToFullPages()
    {
        var peer = new IndexedPeerHandler { SupportsIndex = false };
        var movies = Enumerable.Range(0, 3).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);

        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.All(movies, id => Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, id)));
        Assert.True(peer.FullPageRequests > 0);
        Assert.Empty(peer.Fetched);
    }

    [Fact]
    public async Task SyncStatus_KnowsWhatWillTransferBeforeDownloading_AndEndsUpToDate()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 5).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, _, mapping, config, _) = CreateMovieSync(peer);
        sync.IdsBatchSize = 2;

        var first = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(FederationSyncStatus.Done, first.State);
        Assert.Equal("full", first.Mode);
        Assert.Equal(5, first.Total);
        Assert.Equal(5, first.ToDownload);
        Assert.Equal(5, first.Downloaded);
        Assert.True(first.BytesReceived > 0);
        Assert.NotNull(first.LastSuccessUtc);

        peer.Change(movies[0], "Changed");
        peer.Remove(movies[1]);
        var second = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(FederationSyncStatus.Done, second.State);
        Assert.Equal("changes", second.Mode);
        Assert.Equal(4, second.Total);
        Assert.Equal(1, second.ToDownload);
        Assert.Equal(1, second.ToRemove);
        Assert.Equal(4, second.LastTotal);
    }

    [Fact]
    public async Task SyncStatus_ExplainsFailuresInPlainLanguage()
    {
        var peer = new IndexedPeerHandler();
        peer.Add("Movie", "Movie");
        var (sync, _, mapping, config, server) = CreateMovieSync(peer);
        await RunWithStatusAsync(sync, mapping, config);

        peer.FailIndexAfterFirstPage = true;
        sync.IndexPageSize = 1;
        peer.Add("Movie", "Another");
        var failed = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(FederationSyncStatus.Failed, failed.State);
        Assert.Contains("answered HTTP 503", failed.Error);
        Assert.Contains("Titles already here were kept", failed.Error);
        Assert.NotNull(failed.LastSuccessUtc);

        peer.FailIndexAfterFirstPage = false;
        peer.RejectKey = true;
        var rejected = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(FederationSyncStatus.Failed, rejected.State);
        Assert.Contains("no longer accepts this server's key", rejected.Error);
        Assert.Contains("Reconnect", rejected.Error);
        peer.RejectKey = false;

        server.Enabled = false;
        var off = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(FederationSyncStatus.Failed, off.State);
        Assert.Contains("turned off", off.Error);
    }

    [Fact]
    public async Task SyncStatus_MarksOlderFriendsAsFullDownloads()
    {
        var peer = new IndexedPeerHandler { SupportsIndex = false };
        Enumerable.Range(0, 3).ToList().ForEach(i => peer.Add("Movie", "Movie " + i));
        var (sync, _, mapping, config, _) = CreateMovieSync(peer);

        var state = await RunWithStatusAsync(sync, mapping, config);

        Assert.Equal(FederationSyncStatus.Done, state.State);
        Assert.Equal("legacy", state.Mode);
        Assert.Equal(3, state.Downloaded);
        Assert.Equal(3, state.LastTotal);
    }

    [Fact]
    public void SyncStatus_LastOutcomeSurvivesARestart()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fed-status-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var cachePath = System.IO.Path.Combine(dir, "federation-cache.json");
            var status = new FederationSyncStatus();
            status.Initialize(cachePath);
            var key = FederationSyncStatus.KeyFor("Movies", "s", "lib");
            status.BeginRun(new[] { new LibrarySyncState { Key = key, ServerName = "Anna", RemoteLibraryName = "Movies", LocalLibraryName = "Movies" } }, replaceAll: true);
            status.Update(key, s => { s.Total = 7; s.State = FederationSyncStatus.Saving; });
            status.Succeed(key);
            status.EndRun();

            var restarted = new FederationSyncStatus();
            restarted.Initialize(cachePath);
            var loaded = Assert.Single(restarted.Snapshot());
            Assert.Equal(FederationSyncStatus.Done, loaded.State);
            Assert.Equal(7, loaded.LastTotal);
            Assert.NotNull(loaded.LastSuccessUtc);
            Assert.False(restarted.Running);
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SyncStatus_RequestedSyncShowsAsRunningUntilItStartsOrIsDropped()
    {
        var status = new FederationSyncStatus();
        status.MarkPending();
        Assert.True(status.Running);
        status.ClearPending();
        Assert.False(status.Running);

        status.MarkPending();
        status.BeginRun(Array.Empty<LibrarySyncState>(), replaceAll: true);
        status.ClearPending();
        Assert.True(status.Running);
        status.EndRun();
        Assert.False(status.Running);
    }

    [Fact]
    public async Task CheckPhase_AsksDifferentFriendsAtTheSameTime()
    {
        var arrivals = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource>();
        var overlapped = new System.Collections.Concurrent.ConcurrentBag<bool>();
        var peerA = new IndexedPeerHandler { OnIndexRequest = () => MeetAsync("a", "b") };
        var peerB = new IndexedPeerHandler { OnIndexRequest = () => MeetAsync("b", "a") };
        peerA.Add("Movie", "A movie");
        peerB.Add("Movie", "B movie");

        async Task MeetAsync(string me, string other)
        {
            arrivals.GetOrAdd(me, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            var otherArrived = arrivals.GetOrAdd(other, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            overlapped.Add(await Task.WhenAny(otherArrived, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == otherArrived);
        }

        var (sync, cache, mapping, config, serverA) = CreateMovieSync(peerA);
        var serverB = new RemoteServer { Id = "server-b", Name = "Friend B", Url = "http://b.local", ApiKey = "key", Enabled = true, WanCapMode = WanCapMode.Off };
        config.RemoteServers.Add(serverB);
        mapping.RemoteLibrarySources.Add(new RemoteLibrarySource { ServerId = serverB.Id, RemoteLibraryId = "lib1", RemoteLibraryName = "Movies" });
        var clientA = new RemoteServerClient(serverA, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new HttpClient(peerA) { BaseAddress = new Uri(serverA.Url) });
        var clientB = new RemoteServerClient(serverB, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new HttpClient(peerB) { BaseAddress = new Uri(serverB.Url) });
        var factory = (Moq.Mock<IRemoteServerClientFactory>)typeof(FederationSyncServiceTests).GetField("LastClientFactory", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        factory.Setup(f => f.GetClient(Moq.It.Is<RemoteServer>(s => s.Id == serverA.Id))).Returns(clientA);
        factory.Setup(f => f.GetClient(Moq.It.Is<RemoteServer>(s => s.Id == serverB.Id))).Returns(clientB);

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        await (Task)typeof(FederationSyncService).GetMethod("PlanAllAsync", flags)!.Invoke(sync, new object?[] { new List<LibraryMapping> { mapping }, config, null, CancellationToken.None })!;
        await InvokeRefreshMappingAsync(sync, mapping, config);

        Assert.Equal(2, overlapped.Count);
        Assert.All(overlapped, Assert.True);
        Assert.Equal(2, cache.GetEntriesForMapping(mapping.LocalLibraryName).Count());
    }

    [Fact]
    public async Task LargeRemoval_IsHeldUntilConfirmed()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 100).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, server) = CreateMovieSync(peer);
        await RunWithStatusAsync(sync, mapping, config);

        foreach (var gone in movies.Take(40))
        {
            peer.Remove(gone);
        }

        var first = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(40, first.WaitingRemovals);
        Assert.Equal(0, first.HeldRemovals);
        Assert.Equal(100, cache.GetEntriesForMapping(mapping.LocalLibraryName).Count());

        var second = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(40, second.HeldRemovals);
        Assert.Equal(0, second.Removed);
        Assert.Equal(100, cache.GetEntriesForMapping(mapping.LocalLibraryName).Count());

        var third = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(40, third.HeldRemovals);
        Assert.Equal(100, cache.GetEntriesForMapping(mapping.LocalLibraryName).Count());

        Assert.True(cache.ApprovePendingRemoval(FederationItemCache.RemovalKey(mapping.LocalLibraryName, server.Id)));
        var confirmed = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(40, confirmed.Removed);
        Assert.Equal(0, confirmed.HeldRemovals);
        Assert.Equal(60, cache.GetEntriesForMapping(mapping.LocalLibraryName).Count());
        Assert.False(cache.ApprovePendingRemoval(FederationItemCache.RemovalKey(mapping.LocalLibraryName, server.Id)));
    }

    [Fact]
    public async Task FullResync_EmptiesTheLibrary_RebuildsIt_AndTheNextSyncDownloadsEverything()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 100).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, server) = CreateMovieSync(peer);
        await RunWithStatusAsync(sync, mapping, config);
        foreach (var gone in movies.Take(40))
        {
            peer.Remove(gone);
        }

        await RunWithStatusAsync(sync, mapping, config);
        await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(40, cache.GetPendingRemoval(FederationItemCache.RemovalKey(mapping.LocalLibraryName, server.Id))!.HeldCount);

        var rebuilt = 0;
        await sync.ClearLibraryForResyncAsync(mapping, _ => { rebuilt++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, rebuilt);
        Assert.Empty(cache.GetEntriesForMapping(mapping.LocalLibraryName));
        Assert.Null(cache.GetPendingRemoval(FederationItemCache.RemovalKey(mapping.LocalLibraryName, server.Id)));

        // No stored hashes survive, so every title still on the friend comes back.
        peer.Fetched.Clear();
        await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(movies.Skip(40).ToHashSet(), peer.Fetched.ToHashSet());
        Assert.Equal(60, cache.GetEntriesForMapping(mapping.LocalLibraryName).Count());
    }

    [Fact]
    public async Task ModerateRemoval_IsNotHeld_OnlyDelayedOneSync()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 1000).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        await RunWithStatusAsync(sync, mapping, config);
        foreach (var gone in movies.Take(30))
        {
            peer.Remove(gone);
        }

        await RunWithStatusAsync(sync, mapping, config);
        var second = await RunWithStatusAsync(sync, mapping, config);

        Assert.Equal(30, second.Removed);
        Assert.Equal(0, second.HeldRemovals);
        Assert.Equal(970, cache.GetEntriesForMapping(mapping.LocalLibraryName).Count());
    }

    [Fact]
    public async Task TitleMissingOnce_ThenBack_IsNeverRemoved()
    {
        var peer = new IndexedPeerHandler();
        var movies = Enumerable.Range(0, 3).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        await RunWithStatusAsync(sync, mapping, config);

        peer.SupportsIndex = false;
        var hidden = movies[0];
        var all = typeof(IndexedPeerHandler).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var items = (System.Collections.IList)all.GetValue(peer)!;
        var saved = items[0];
        items.RemoveAt(0);
        await RunWithStatusAsync(sync, mapping, config);
        items.Insert(0, saved);
        peer.SupportsIndex = true;
        await RunWithStatusAsync(sync, mapping, config);
        await RunWithStatusAsync(sync, mapping, config);

        Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, hidden));
    }

    [Fact]
    public void PendingRemoval_SurvivesARestart()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fed-pending-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var path = System.IO.Path.Combine(dir, "federation-cache.json");
            var cache = new FederationItemCache(Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationItemCache>.Instance);
            cache.Initialize(path);
            var id = Guid.NewGuid();
            cache.SetPendingRemoval("Movies|s", new PendingRemoval { Missing = new HashSet<Guid> { id }, HeldCount = 1 });
            cache.SaveAsync().GetAwaiter().GetResult();

            var restarted = new FederationItemCache(Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationItemCache>.Instance);
            restarted.Initialize(path);
            var pending = restarted.GetPendingRemoval("Movies|s");
            Assert.NotNull(pending);
            Assert.Contains(id, pending!.Missing);
            Assert.True(restarted.ApprovePendingRemoval("Movies|s"));
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DownloadBatch_FailingOnce_IsRetried()
    {
        var peer = new IndexedPeerHandler { FailNextIds = 2 };
        var movies = Enumerable.Range(0, 3).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);

        var state = await RunWithStatusAsync(sync, mapping, config);

        Assert.Equal(FederationSyncStatus.Done, state.State);
        Assert.All(movies, id => Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, id)));
    }

    [Fact]
    public async Task InterruptedDownload_ResumesWithWhatIsStillMissing()
    {
        var peer = new IndexedPeerHandler { FailIdsAfter = 2 };
        var movies = Enumerable.Range(0, 10).Select(i => peer.Add("Movie", "Movie " + i)).ToList();
        var (sync, cache, mapping, config, _) = CreateMovieSync(peer);
        sync.IdsBatchSize = 2;

        var failed = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(FederationSyncStatus.Failed, failed.State);
        Assert.Equal(4, peer.Fetched.Count);

        peer.FailIdsAfter = null;
        peer.Fetched.Clear();
        var resumed = await RunWithStatusAsync(sync, mapping, config);
        Assert.Equal(FederationSyncStatus.Done, resumed.State);
        Assert.Equal(6, peer.Fetched.Count);
        Assert.Equal(6, resumed.ToDownload);
        Assert.All(movies, id => Assert.True(CacheHasRemoteId(cache, mapping.LocalLibraryName, id)));
    }

    /// <summary>The check phase, download phase and status bookkeeping SyncAllAsync runs, for one mapping.</summary>
    private static async Task<LibrarySyncState> RunWithStatusAsync(FederationSyncService sync, LibraryMapping mapping, PluginConfiguration config)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mappings = new List<LibraryMapping> { mapping };
        typeof(FederationSyncService).GetMethod("BeginStatusRun", flags)!.Invoke(sync, new object?[] { mappings, config, null });
        await (Task)typeof(FederationSyncService).GetMethod("PlanAllAsync", flags)!.Invoke(sync, new object?[] { mappings, config, null, CancellationToken.None })!;
        await InvokeRefreshMappingAsync(sync, mapping, config);
        typeof(FederationSyncService).GetMethod("MarkMappingSaved", flags)!.Invoke(sync, new object?[] { mapping });
        typeof(FederationSyncService).GetField("_currentPlans", flags)!.SetValue(sync, null);
        sync.Status.EndRun();
        return Assert.Single(sync.Status.Snapshot());
    }

    /// <summary>A friend's Peer/Items with index=true and ids= support.</summary>
    private sealed class IndexedPeerHandler : HttpMessageHandler
    {
        private readonly List<(Guid Id, string Type, string Name, Guid? Series, Guid? Season, int Version)> _items = new();
        private readonly Dictionary<Guid, string> _tvdb = new();

        public bool SupportsIndex { get; set; } = true;

        public bool FailIndexAfterFirstPage { get; set; }

        public bool FailIds { get; set; }

        /// <summary>Answer every catalog request 401, as for a key the friend no longer accepts.</summary>
        public bool RejectKey { get; set; }

        /// <summary>Fail the next this many ids= requests, then answer normally.</summary>
        public int FailNextIds { get; set; }

        /// <summary>Answer this many more ids= requests, then fail every one after.</summary>
        public int? FailIdsAfter { get; set; }

        /// <summary>Runs before the first title-list request is answered.</summary>
        public Func<Task>? OnIndexRequest { get; set; }

        private int _indexRequests;

        public List<Guid> Fetched { get; } = new();

        public int FullPageRequests { get; private set; }

        /// <summary>ids= requests that named no item type or library (Jellyfin then groups music items away).</summary>
        public int IdsRequestsWithoutTypeOrLibrary { get; private set; }

        public Guid Add(string type, string name, Guid? series = null, Guid? season = null)
        {
            var id = Guid.NewGuid();
            _items.Add((id, type, name, series, season, 0));
            return id;
        }

        public void Change(Guid id, string name)
        {
            var i = _items.FindIndex(x => x.Id == id);
            _items[i] = _items[i] with { Name = name, Version = _items[i].Version + 1 };
        }

        public void Remove(Guid id) => _items.RemoveAll(x => x.Id == id);

        /// <summary>The friend's metadata refresh found a TVDB id for this item.</summary>
        public void SetTvdb(Guid id, string tvdb)
        {
            _tvdb[id] = tvdb;
            var i = _items.FindIndex(x => x.Id == id);
            _items[i] = _items[i] with { Version = _items[i].Version + 1 };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty);
            if (!(request.RequestUri?.AbsolutePath ?? string.Empty).Contains("/Peer/Items", StringComparison.Ordinal))
            {
                return Json("{}");
            }

            if (RejectKey)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            var ids = Param(query, "ids");
            if (ids != null)
            {
                if (Param(query, "mediaType") == null || Param(query, "parentId") == null)
                {
                    IdsRequestsWithoutTypeOrLibrary++;
                }

                if (FailIds || (FailNextIds > 0 && FailNextIds-- > 0) || FailIdsAfter is <= 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                if (FailIdsAfter is > 0)
                {
                    FailIdsAfter--;
                }

                var wanted = ids.Split(',').Select(Guid.Parse).ToHashSet();
                var found = _items.Where(x => wanted.Contains(x.Id)).ToList();
                Fetched.AddRange(found.Select(x => x.Id));
                return Json(Page(found.Select(ItemJson), false, 0));
            }

            var type = Param(query, "mediaType");
            var rows = _items.Where(x => x.Type == type).ToList();
            var cursor = ParseQueryInt(query, "cursor") ?? 0;
            var limit = ParseQueryInt(query, "limit") ?? 200;
            if (query.Contains("index=true", StringComparison.Ordinal) && SupportsIndex)
            {
                if (OnIndexRequest != null && Interlocked.Increment(ref _indexRequests) == 1)
                {
                    return AnswerAfterAsync(OnIndexRequest, request, cancellationToken);
                }

                if (FailIndexAfterFirstPage && cursor > 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                var slice = rows.Skip(cursor).Take(limit).ToList();
                var next = cursor + slice.Count;
                return Json(Page(slice.Select(x => $"[\"{x.Id:N}\",\"{x.Name.GetHashCode():x}-{x.Version}\"]"), next < rows.Count, next));
            }

            FullPageRequests++;
            var start = ParseQueryInt(query, "startIndex") ?? 0;
            var page = rows.Skip(start).Take(limit).ToList();
            return Json(Page(page.Select(ItemJson), start + page.Count < rows.Count, start + page.Count, legacy: true));
        }

        private async Task<HttpResponseMessage> AnswerAfterAsync(Func<Task> wait, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await wait().ConfigureAwait(false);
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private string ItemJson((Guid Id, string Type, string Name, Guid? Series, Guid? Season, int Version) x)
        {
            var json = $"{{\"Id\":\"{x.Id}\",\"Name\":\"{x.Name}\",\"Type\":\"{x.Type}\"";
            if (_tvdb.TryGetValue(x.Id, out var tvdb))
            {
                json += $",\"ProviderIds\":{{\"Tvdb\":\"{tvdb}\"}}";
            }

            if (x.Type == "Audio" && x.Series is Guid album)
            {
                json += $",\"AlbumId\":\"{album}\",\"Album\":\"Album\"";
            }

            if (x.Type == "Episode" && x.Series is Guid series && x.Season is Guid season)
            {
                json += $",\"SeriesId\":\"{series}\",\"SeasonId\":\"{season}\",\"SeasonName\":\"Season 1\",\"ParentIndexNumber\":1,\"IndexNumber\":1";
            }

            return json + "}";
        }

        private static string Page(IEnumerable<string> items, bool hasMore, int next, bool legacy = false)
        {
            var list = items.ToList();
            var paging = legacy ? string.Empty : $",\"HasMore\":{(hasMore ? "true" : "false")},\"NextCursor\":{next}";
            return $"{{\"Items\":[{string.Join(",", list)}],\"TotalRecordCount\":{list.Count}{paging}}}";
        }

        private static string? Param(string query, string name)
            => query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == name).Select(p => p[1]).FirstOrDefault();

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
