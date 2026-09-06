using System.Net;
using System.Text;
using System.Text.Json;
using FederationCompanion;
using Microsoft.AspNetCore.Http;

namespace FederationCompanion.Tests;

public sealed class ImportReliabilityTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Items\":null}")]
    public async Task InvalidCatalog_IsFailureRatherThanSuccessfulEmptyLibrary(string body)
    {
        var service = new JellyfinImportService(new HttpClient(new Handler(_ => Json(body))));
        await Assert.ThrowsAsync<JsonException>(() => service.GetLibrariesAsync("https://friend.example", "test", CancellationToken.None));
        await Assert.ThrowsAsync<JsonException>(() => service.GetItemsAsync("https://friend.example", "test", "library", "Movie", CancellationToken.None));
    }

    [Fact]
    public async Task OlderPeerFederatedItems_AreFilteredWithoutTruncatingTheNextPage()
    {
        var calls = 0;
        var handler = new Handler(request =>
        {
            Assert.Contains("includeMediaSources=true", request.RequestUri!.Query);
            calls++;
            return Json(JsonSerializer.Serialize(new { Items = calls == 1
                ? Enumerable.Range(0, 200).Select(_ => new PeerItem { Id = Guid.NewGuid().ToString(), ProviderIds = new() { ["FederationKey"] = "remote" } }).ToList()
                : new List<PeerItem> { new() { Id = Guid.NewGuid().ToString(), Name = "Local video" } } }));
        });
        var items = await new JellyfinImportService(new HttpClient(handler)).GetItemsAsync("https://friend.example", "test", "library", "Movie", CancellationToken.None);
        Assert.Equal("Local video", Assert.Single(items).Name);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Export_PrunesOwnedFilesButPreservesForeignFilesAndEqualTitles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            var foreign = Path.Combine(root, "personal.strm");
            File.WriteAllText(foreign, "https://personal.example/video");
            var a = Item("Same name"); var b = Item("Same name");
            var export = StrmExporter.Export(root, new[] { (a, "https://companion.example/a"), (b, "https://companion.example/b") });
            Assert.Equal(2, export.ItemCount);
            Assert.Equal(3, Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Length);
            StrmExporter.Export(root, Array.Empty<(PeerItem, string)>());
            Assert.Equal(foreign, Assert.Single(Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Export_RejectsManifestTraversalBeforeDeletingAnything()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".companion-files.json"), "[\"../outside.strm\"]");
            Assert.Throws<IOException>(() => StrmExporter.Export(root, Array.Empty<(PeerItem, string)>()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MediaFiles_HaveRealExtensionsSizesAndDistinctIds()
    {
        var a = Item("Same name"); var b = Item("Same name");
        var files = MediaMount.BuildFiles(new[] { a, b });
        Assert.Equal(2, files.Select(f => f.Path).Distinct().Count());
        Assert.All(files, f => { Assert.EndsWith(".mkv", f.Path); Assert.Equal(12345, f.Size); });
        Assert.Empty(MediaMount.BuildFiles(new[] { new PeerItem { Id = Guid.NewGuid().ToString(), Type = "Movie", Name = "No stream" } }));
    }

    [Fact]
    public async Task MediaMount_RejectsMissingCredentialAndListsOnlyCommittedImports()
    {
        var peer = new JellyfinImportPeer { MountedFiles = MediaMount.BuildFiles(new[] { Item("Movie & title") }) };
        var state = new CompanionState { ImportPeers = new() { peer } };
        var jellyfin = new JellyfinImportService(new HttpClient(new Handler(_ => throw new Exception("No upstream query during listing"))));
        var denied = Context("PROPFIND");
        await MediaMount.HandleAsync(state, jellyfin, denied, null, CancellationToken.None);
        Assert.Equal(401, denied.Response.StatusCode);
        var allowed = Context("PROPFIND");
        allowed.Request.Headers.Authorization = "Bearer " + state.MediaAccessKey;
        allowed.Request.Headers["Depth"] = "1";
        await MediaMount.HandleAsync(state, jellyfin, allowed, peer.Id + "/Movies", CancellationToken.None);
        Assert.Equal(207, allowed.Response.StatusCode);
        Assert.Contains("Movie &amp; title", Encoding.UTF8.GetString(((MemoryStream)allowed.Response.Body).ToArray()));
        state.ImportPeers.Clear();
        var removed = Context("PROPFIND"); removed.Request.Headers.Authorization = "Bearer " + state.MediaAccessKey; removed.Request.Headers["Depth"] = "1";
        await MediaMount.HandleAsync(state, jellyfin, removed, peer.Id + "/Movies", CancellationToken.None);
        Assert.Equal(404, removed.Response.StatusCode);
    }

    [Fact]
    public void ImportedPlexLibrary_CannotBeResharedEvenWithSavedSharedFlag()
    {
        var state = new CompanionState { PlexMountRoot = "/mounted/friends" };
        var library = new CompanionLibrary { SectionKey = "1", Shared = true, Locations = new() { "/mounted/friends/peer/Movies" } };
        Assert.False(CompanionLibraryPolicy.IsShared(state, library));
        library.Locations = new() { "/mounted/friends-local/Movies" };
        Assert.True(CompanionLibraryPolicy.IsShared(state, library));
    }

    [Fact]
    public void CompanionUpstream_PrefersLanOverPublicAndRelay()
    {
        var server = new PlexResource { Connections = new() {
            new() { Uri = "https://public.example" }, new() { Uri = "https://relay.example", Relay = true }, new() { Uri = "http://192.168.1.5:32400", Local = true } } };
        Assert.True(PlexAuth.OrderLocalConnections(server).First().Local);
    }

    [Fact]
    public void EpisodeCatalog_PreservesSourceNumbersAndExplainsMissingMetadata()
    {
        var episode = Item("Finale"); episode.Type = "Episode"; episode.SeriesName = "Source show";
        episode.ParentIndexNumber = 1; episode.IndexNumber = 7; episode.IndexNumberEnd = 8;
        var broken = Item("Unknown episode"); broken.Type = "Episode"; broken.SeriesName = "Source show";
        var catalog = MediaMount.BuildCatalog(new[] { episode, broken });
        Assert.Contains("S01E07-E08", Assert.Single(catalog.Files).Path);
        Assert.Null(catalog.Items[0].Issue);
        Assert.Equal(1, catalog.Items[0].Season);
        Assert.Contains("number is missing", catalog.Items[1].Issue);
    }

    [Fact]
    public void SwitchingPlexServers_RequiresFreshConsentAndSectionAttachments()
    {
        var peer = new JellyfinImportPeer { PlexMovieSectionKey = "1", PlexShowSectionKey = "2", PlexSectionKey = "3" };
        var state = new CompanionState { ServerMachineIdentifier = "old", Libraries = new() { new() { SectionKey = "1", Shared = true } }, ImportPeers = new() { peer } };
        CompanionLibraryPolicy.PrepareServerChange(state, "old");
        Assert.Single(state.Libraries);
        CompanionLibraryPolicy.PrepareServerChange(state, "new");
        Assert.Empty(state.Libraries);
        Assert.Null(peer.PlexMovieSectionKey); Assert.Null(peer.PlexShowSectionKey); Assert.Null(peer.PlexSectionKey);
    }

    [Fact]
    public void MountMarker_MustBelongToThisCompanionInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".companion-mount"), "Federation Companion media mount v1\ninstance-a");
            Assert.True(MediaMount.IsMounted(root, "instance-a"));
            Assert.False(MediaMount.IsMounted(root, "instance-b"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("LPT1")]
    public void MountedShowFolders_AvoidWindowsReservedDeviceNames(string name)
    {
        var item = Item("Episode"); item.Type = "Episode"; item.SeriesName = name;
        item.ParentIndexNumber = 1; item.IndexNumber = 1;
        var file = Assert.Single(MediaMount.BuildFiles(new[] { item }));
        Assert.StartsWith("Shows/_" + name + "/", file.Path);
    }

    [Fact]
    public void Export_DoesNotOverwriteAnUnmanagedFileWithTheSameTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var item = Item("Same title");
        var path = Path.Combine(root, StrmExporter.BuildMoviePath(item)!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "https://personal.example/keep");
            StrmExporter.Export(root, new[] { (item, "https://companion.example/movie") });
            Assert.Equal("https://personal.example/keep", File.ReadAllText(path));
            Assert.Equal(2, Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Length);
            StrmExporter.Export(root, Array.Empty<(PeerItem, string)>());
            Assert.Equal(path, Assert.Single(Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static PeerItem Item(string name) => new() { Id = Guid.NewGuid().ToString(), Type = "Movie", Name = name, MediaSources = new() { new() { Container = "mkv", Size = 12345 } } };
    private static DefaultHttpContext Context(string method) { var c = new DefaultHttpContext(); c.Request.Method = method; c.Response.Body = new MemoryStream(); return c; }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request));
    }
}
