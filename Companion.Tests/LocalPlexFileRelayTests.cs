using System.Net;
using System.Text;
using System.Text.Json;
using FederationCompanion;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FederationCompanion.Tests;

public sealed class LocalPlexFileRelayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "companion-owned-files-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _bytes = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
    private const string Part = "/library/parts/77/file.mp4";
    public LocalPlexFileRelayTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "movie.mp4"), _bytes);
    }

    private CompanionState State() => new() {
        LocalFileRelayEnabled = true, ServerBaseUrl = "https://plex.example", ServerAccessToken = "PRIVATE-TEST-TOKEN",
        Libraries = new() { new() { SectionKey = "1", Type = "movie", Shared = true, Locations = new() { _root } } }
    };
    private byte[] Metadata(string? file = null, string section = "1", long? size = null) => JsonSerializer.SerializeToUtf8Bytes(new {
        MediaContainer = new { Metadata = new[] { new { ratingKey = "100", librarySectionID = section,
            Media = new[] { new { Part = new[] { new { key = Part, file = file ?? Path.Combine(_root, "movie.mp4"), size = size ?? _bytes.Length } } } } } } }
    });
    private static DefaultHttpContext Context(string method = "GET", string? range = null)
    {
        var result = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        result.Request.Method = method;
        result.Response.Body = new MemoryStream();
        if (range != null) result.Request.Headers.Range = range;
        return result;
    }

    [Theory]
    [InlineData("GET", null, 200, 200)]
    [InlineData("HEAD", null, 200, 0)]
    [InlineData("GET", "bytes=10-19", 206, 10)]
    [InlineData("GET", "bytes=-20", 206, 20)]
    [InlineData("GET", "bytes=200-210", 416, 0)]
    public async Task OriginalFiles_WorkWithoutPlexMediaApi_AndHonorHttpRanges(string method, string? range, int status, int length)
    {
        var state = State(); var peer = new CompanionPeer();
        var requests = new List<string>();
        var handler = new Handler(request => {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/library/metadata/100"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata()) }
                : new HttpResponseMessage(HttpStatusCode.PaymentRequired);
        });
        var relay = new PlexFederationRelay(state, new HttpClient(handler));
        var catalog = Context();
        await relay.RelayAsync(peer, "library/metadata/100", catalog.Request, catalog.Response, default);
        var stream = Context(method, range);
        await relay.RelayAsync(peer, Part, stream.Request, stream.Response, default);
        Assert.Equal(status, stream.Response.StatusCode);
        Assert.Equal(length, stream.Response.Body.Length);
        Assert.All(requests, path => Assert.Equal("/library/metadata/100", path));
        if (length > 0) {
            var body = ((MemoryStream)stream.Response.Body).ToArray();
            Assert.Equal(range == "bytes=10-19" ? _bytes[10..20] : range == "bytes=-20" ? _bytes[^20..] : _bytes, body);
        }
    }

    [Fact]
    public async Task ConsentRevocation_DownloadRestrictions_AndPrivateMoves_StillApply()
    {
        var state = State(); var section = "1"; var peer = new CompanionPeer { AllowDownloads = false };
        var relay = new PlexFederationRelay(state, new HttpClient(new Handler(_ =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(Metadata(section: section)) })));
        var catalog = Context(); await relay.RelayAsync(peer, "library/metadata/100", catalog.Request, catalog.Response, default);
        var download = Context(); download.Request.QueryString = new("?federationDownload=true");
        await relay.RelayAsync(peer, Part, download.Request, download.Response, default);
        Assert.Equal(403, download.Response.StatusCode);
        section = "2";
        var moved = Context(); await relay.RelayAsync(peer, Part, moved.Request, moved.Response, default);
        Assert.Equal(403, moved.Response.StatusCode);
        state.Libraries[0].Shared = false;
        Assert.False(LocalPlexFileRelay.TryResolvePath(state, "1", Path.Combine(_root, "movie.mp4"), out _, out _));
    }

    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("movie.mp4/../outside.mp4")]
    [InlineData("secret.txt")]
    [InlineData("import.strm")]
    public void TraversalAndNonMediaFiles_AreNotApproved(string name)
    {
        Assert.False(LocalPlexFileRelay.TryResolvePath(State(), "1", _root + "/" + name, out _, out _));
    }

    [Fact]
    public void RootPrefixCollision_IsNotInsideApprovedFolder()
    {
        Assert.False(LocalPlexFileRelay.TryResolvePath(State(), "1", _root + "-other/movie.mp4", out _, out _));
    }

    [Fact]
    public void ContainerMapping_UsesOnlyTheApprovedLibraryRoot()
    {
        var state = State(); state.Libraries[0].Locations = new() { "/plex/media" };
        state.LocalFileRootMappings.Add(new() { PlexRoot = "/plex/media", LocalRoot = _root });
        Assert.True(LocalPlexFileRelay.TryResolvePath(state, "1", "/plex/media/movie.mp4", out var root, out var file));
        Assert.Equal(_root, root); Assert.Equal(Path.Combine(_root, "movie.mp4"), file);
        Assert.False(LocalPlexFileRelay.TryResolvePath(state, "1", "/plex/private/movie.mp4", out _, out _));
    }

    [Fact]
    public async Task MissingFiles_AndStalePartSizes_NeverFallBackToPlex()
    {
        foreach (var body in new[] { Metadata(size: 201), Metadata(file: Path.Combine(_root, "missing.mp4")) }) {
            var context = Context(); await LocalPlexFileRelay.RelayAsync(State(), body, Part, context, default);
            Assert.True(context.Response.StatusCode is 404 or 409);
            Assert.Equal(0, context.Response.Body.Length);
        }
    }

    [Fact]
    public void FileAndDirectorySymlinks_AreRejected()
    {
        if (!OperatingSystem.IsLinux()) return; // Windows runtime is a separate gate.
        var link = Path.Combine(_root, "link.mp4");
        File.CreateSymbolicLink(link, Path.Combine(_root, "movie.mp4"));
        Assert.Throws<UnauthorizedAccessException>(() => LocalPlexFileRelay.OpenOwnedFile(_root, link));
        var directoryLink = Path.Combine(_root, "alias"); Directory.CreateSymbolicLink(directoryLink, _root);
        Assert.Throws<UnauthorizedAccessException>(() => LocalPlexFileRelay.OpenOwnedFile(_root, Path.Combine(directoryLink, "movie.mp4")));
    }

    [Fact]
    public void AnExplicitRootAlias_ResolvesToItsApprovedPhysicalDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var alias = _root + "-alias"; Directory.CreateSymbolicLink(alias, _root);
        try { using var file = LocalPlexFileRelay.OpenOwnedFile(alias, Path.Combine(alias, "movie.mp4")); Assert.Equal(_bytes.Length, file.Length); }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task MixedLocalLibrary_CannotReShareImportedMountedFiles()
    {
        var imported = Path.Combine(_root, "imported"); Directory.CreateDirectory(imported);
        var file = Path.Combine(imported, "friend.mp4"); File.WriteAllBytes(file, _bytes);
        var state = State(); state.MediaMountRoot = imported;
        var context = Context(); await LocalPlexFileRelay.RelayAsync(state, Metadata(file), Part, context, default);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public void ChangingPlexServer_ResetsOriginalFileConsentAndMappings()
    {
        var state = State(); state.ServerMachineIdentifier = "old";
        state.LocalFileRootMappings.Add(new() { PlexRoot = _root, LocalRoot = _root });
        CompanionLibraryPolicy.PrepareServerChange(state, "new");
        Assert.False(state.LocalFileRelayEnabled); Assert.Empty(state.LocalFileRootMappings);
    }

    [Fact]
    public void NamedPipes_AreRejectedWithoutWaitingForAWriter()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pipe = Path.Combine(_root, "pipe.mp4");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo") {
            ArgumentList = { pipe }, UseShellExecute = false
        })!;
        Assert.True(process.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
        Assert.Throws<UnauthorizedAccessException>(() => LocalPlexFileRelay.OpenOwnedFile(_root, pipe));
    }

    [Fact]
    public async Task ImportedRootAlias_CannotExposeItsPhysicalFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var alias = _root + "-import"; Directory.CreateSymbolicLink(alias, _root);
        try {
            var state = State(); state.MediaMountRoot = alias;
            var context = Context(); await LocalPlexFileRelay.RelayAsync(state, Metadata(), Part, context, default);
            Assert.Equal(403, context.Response.StatusCode); Assert.Equal(0, context.Response.Body.Length);
        } finally { Directory.Delete(alias); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"MediaContainer\":{\"Metadata\":[null]}}")]
    [InlineData("{\"MediaContainer\":{\"Metadata\":{}}}")]
    public async Task MalformedMetadata_FailsClosed(string metadata)
    {
        var context = Context();
        await LocalPlexFileRelay.RelayAsync(State(), Encoding.UTF8.GetBytes(metadata), Part, context, default);
        Assert.Equal(403, context.Response.StatusCode); Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task Cancellation_IsHonoredBeforeReadingLocalMedia()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalPlexFileRelay.RelayAsync(State(), Metadata(), Part, Context(), cancel.Token));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request));
    }
}
