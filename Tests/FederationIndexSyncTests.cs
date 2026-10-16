using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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

    /// <summary>A friend's Peer/Items with index=true and ids= support.</summary>
    private sealed class IndexedPeerHandler : HttpMessageHandler
    {
        private readonly List<(Guid Id, string Type, string Name, Guid? Series, Guid? Season, int Version)> _items = new();

        public bool SupportsIndex { get; set; } = true;

        public bool FailIndexAfterFirstPage { get; set; }

        public bool FailIds { get; set; }

        public List<Guid> Fetched { get; } = new();

        public int FullPageRequests { get; private set; }

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty);
            if (!(request.RequestUri?.AbsolutePath ?? string.Empty).Contains("/Peer/Items", StringComparison.Ordinal))
            {
                return Json("{}");
            }

            var ids = Param(query, "ids");
            if (ids != null)
            {
                if (FailIds)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
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

        private static string ItemJson((Guid Id, string Type, string Name, Guid? Series, Guid? Season, int Version) x)
        {
            var json = $"{{\"Id\":\"{x.Id}\",\"Name\":\"{x.Name}\",\"Type\":\"{x.Type}\"";
            if (x.Series is Guid series && x.Season is Guid season)
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
