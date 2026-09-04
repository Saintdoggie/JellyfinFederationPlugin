using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers "download to server": the synchronous validation StartDownload performs
/// before handing off to a background job (item exists, is actually federated, has
/// a resolvable source, isn't already downloading). The background fetch-and-save
/// itself is not exercised here - it's a real network+disk operation better suited
/// to the same live-server verification the rest of this plugin's streaming path
/// gets, not a unit test.
/// </summary>
[Collection("PluginInstance")]
public class FederationDownloadServiceTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly Mock<ILibraryManager> _libraryManager;
    private readonly FederationLibraryManager _federationManager;
    private readonly FederationItemCache _cache;
    private readonly Mock<IRemoteServerClientFactory> _clientFactory;
    private readonly ExternalCatalogRegistry _externalCatalogs;
    private readonly FederationDownloadService _service;

    public FederationDownloadServiceTests()
    {
        _plugin = new RealPluginInstance();
        _plugin.Configuration.PreferHigherQualityRemotes = true;
        _plugin.Configuration.EnableQualityReplacementActions = true;

        _libraryManager = new Mock<ILibraryManager>();
        _clientFactory = new Mock<IRemoteServerClientFactory>();
        _cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, _clientFactory.Object);
        _federationManager = new FederationLibraryManager(_libraryManager.Object, NullLogger<FederationLibraryManager>.Instance, _clientFactory.Object, _cache, bandwidthMonitor, Moq.Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());

        _externalCatalogs = new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>());
        _service = new FederationDownloadService(_libraryManager.Object, _federationManager, _clientFactory.Object, _externalCatalogs, NullLogger<FederationDownloadService>.Instance);
    }

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public void StartDownload_InvalidItemId_Fails()
    {
        var (success, message, operationId) = _service.StartDownload("not-a-guid");

        Assert.False(success);
        Assert.Contains("Invalid item id", message);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartDownload_ItemNotFound_Fails()
    {
        var itemId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var (success, message, operationId) = _service.StartDownload(itemId.ToString());

        Assert.False(success);
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartDownload_ItemNotFederated_Fails()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string>() };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        var (success, message, operationId) = _service.StartDownload(itemId.ToString());

        Assert.False(success);
        Assert.Contains("friend's server", message);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartDownload_FederatedButNotInCache_Fails()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "Movies/raw/server-1/" + Guid.NewGuid() } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        var (success, message, operationId) = _service.StartDownload(itemId.ToString());

        Assert.False(success);
        Assert.Contains("source server", message);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartDownload_ValidFederatedItem_StartsTrackingAndReturnsOperationId()
    {
        var itemId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        var key = FederationItemCache.BuildRawKey("Movies", "server-1", remoteItemId);
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = key } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        _cache.UpsertRaw("Movies", "server-1", remoteItemId, new BaseItemDto { Name = "Some Movie", Container = "mkv" }, 0, "Movie");

        var (success, message, operationId) = _service.StartDownload(itemId.ToString());

        Assert.True(success, message);
        Assert.NotNull(operationId);
        var progress = DownloadProgressTracker.Get(operationId!);
        Assert.NotNull(progress);
        Assert.Equal("Some Movie", progress!.ItemName);
    }

    [Fact]
    public void StartDownload_AlreadyDownloading_Fails()
    {
        var itemId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        var key = FederationItemCache.BuildRawKey("Movies", "server-1", remoteItemId);
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = key } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        _cache.UpsertRaw("Movies", "server-1", remoteItemId, new BaseItemDto { Name = "Some Other Movie", Container = "mkv" }, 0, "Movie");

        DownloadProgressTracker.Start(Guid.NewGuid().ToString(), itemId.ToString(), "Some Other Movie");

        var (success, message, operationId) = _service.StartDownload(itemId.ToString());

        Assert.False(success);
        Assert.Contains("Already downloading", message);
        Assert.Null(operationId);
    }

    [Fact]
    public void CancelDownload_UnknownOperation_Fails()
    {
        var (success, message) = _service.CancelDownload(Guid.NewGuid().ToString());

        Assert.False(success);
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelDownload_KnownOperation_Succeeds()
    {
        var itemId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        var key = FederationItemCache.BuildRawKey("Movies", "server-1", remoteItemId);
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = key } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        _cache.UpsertRaw("Movies", "server-1", remoteItemId, new BaseItemDto { Name = "Cancel Me", Container = "mkv" }, 0, "Movie");

        var (started, startMessage, operationId) = _service.StartDownload(itemId.ToString());
        Assert.True(started, startMessage);

        // The background task may already have finished (no HttpClient set up
        // for "server-1", so it fails fast) by the time this runs - cancelling
        // a still-running download and cancelling one that just finished are
        // both a success from the caller's point of view, just worded
        // differently, so only the outcome is asserted here.
        var (success, message) = _service.CancelDownload(operationId!);
        Assert.True(success, message);
    }

    [Fact]
    public void GetDownloadUrl_InvalidItemId_Fails()
    {
        var (success, message, url, fileName) = _service.GetDownloadUrl("not-a-guid");

        Assert.False(success);
        Assert.Contains("Invalid item id", message);
        Assert.Null(url);
        Assert.Null(fileName);
    }

    [Fact]
    public void GetDownloadUrl_ItemNotFound_Fails()
    {
        var itemId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var (success, message, url, fileName) = _service.GetDownloadUrl(itemId.ToString());

        Assert.False(success);
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(url);
        Assert.Null(fileName);
    }

    [Fact]
    public void GetDownloadUrl_ItemNotFederated_Fails()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string>() };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        var (success, message, url, fileName) = _service.GetDownloadUrl(itemId.ToString());

        Assert.False(success);
        Assert.Contains("friend's server", message);
        Assert.Null(url);
        Assert.Null(fileName);
    }

    [Fact]
    public void GetDownloadUrl_FederatedButNotInCache_Fails()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "Movies/raw/server-1/" + Guid.NewGuid() } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        var (success, message, url, fileName) = _service.GetDownloadUrl(itemId.ToString());

        Assert.False(success);
        Assert.Contains("source server", message);
        Assert.Null(url);
        Assert.Null(fileName);
    }

    [Fact]
    public void GetDownloadUrl_ServerNotConfigured_Fails()
    {
        // Unlike StartDownload (which only optionally checks AllowDownloads on a
        // registered server and tolerates a missing one), BuildStaticPath - and
        // therefore this method - requires the server to actually be configured:
        // there is no proxy URL to build without it.
        var itemId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        var key = FederationItemCache.BuildRawKey("Movies", "server-1", remoteItemId);
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = key } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);
        _cache.UpsertRaw("Movies", "server-1", remoteItemId, new BaseItemDto { Name = "Some Movie", Container = "mkv" }, 0, "Movie");

        var (success, message, url, fileName) = _service.GetDownloadUrl(itemId.ToString());

        Assert.False(success);
        Assert.Null(url);
        Assert.Null(fileName);
    }

    [Fact]
    public void GetDownloadUrl_ValidFederatedItem_ReturnsDownloadUrlAndSanitizedFileName()
    {
        _plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "server-1", Name = "Friend", Url = "http://friend.example:8096", ApiKey = "federation-secret", Enabled = true });

        var itemId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        var key = FederationItemCache.BuildRawKey("Movies", "server-1", remoteItemId);
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = key } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);
        // "/" - unlike ":" or "?" - is invalid in a filename on every platform
        // Path.GetInvalidFileNameChars() runs on, so asserting its replacement
        // here isn't OS-dependent the way punctuation like ":" would be (that's
        // only invalid on Windows; these tests run on Linux).
        _cache.UpsertRaw("Movies", "server-1", remoteItemId, new BaseItemDto { Name = "Movie / The Sequel", Container = "mkv" }, 0, "Movie");

        var (success, message, url, fileName) = _service.GetDownloadUrl(itemId.ToString());

        Assert.True(success, message);
        Assert.NotNull(url);
        Assert.Contains("/Plugins/Federation/Stream?", url);
        Assert.Contains("download=true", url);
        Assert.Equal("Movie _ The Sequel.mkv", fileName);
        Assert.Contains(Uri.EscapeDataString(fileName!), url);
    }

    [Fact]
    public void GetDownloadUrl_ServerHasFriendUserAccessRules_Fails()
    {
        // A per-remote-user restriction can't be enforced through a static URL
        // handed straight to a browser download - same guard BuildStaticPath
        // already applies for the item.Path it stamps for Jellyfin clients.
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "server-1",
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "federation-secret",
            Enabled = true,
            FriendUserAccessRules = new List<RemoteUserAccessRule> { new RemoteUserAccessRule() }
        });

        var itemId = Guid.NewGuid();
        var remoteItemId = Guid.NewGuid();
        var key = FederationItemCache.BuildRawKey("Movies", "server-1", remoteItemId);
        var item = new Movie { Id = itemId, ProviderIds = new Dictionary<string, string> { ["FederationKey"] = key } };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);
        _cache.UpsertRaw("Movies", "server-1", remoteItemId, new BaseItemDto { Name = "Restricted Movie", Container = "mkv" }, 0, "Movie");

        var (success, message, url, fileName) = _service.GetDownloadUrl(itemId.ToString());

        Assert.False(success);
        Assert.Null(url);
        Assert.Null(fileName);
    }

    [Fact]
    public void StartQualityReplace_InvalidItemId_Fails()
    {
        var (success, message, operationId) = _service.StartQualityReplace("not-a-guid", "server-1", "remote-1", "Movie");

        Assert.False(success);
        Assert.Contains("Invalid item id", message);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartQualityReplace_ItemNotFound_Fails()
    {
        var itemId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var (success, message, operationId) = _service.StartQualityReplace(itemId.ToString(), "server-1", "remote-1", "Movie");

        Assert.False(success);
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartQualityReplace_ServerNotFound_Fails()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId, Path = "/media/Movie.mkv" };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        var (success, message, operationId) = _service.StartQualityReplace(itemId.ToString(), "no-such-server", "remote-1", "Movie");

        Assert.False(success);
        Assert.Contains("Server not found", message);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartQualityReplace_DownloadsDisabledForServer_Fails()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId, Path = "/media/Movie.mkv" };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "server-1",
            Name = "Friend",
            Url = "http://friend.example:8096",
            Enabled = true,
            AllowDownloads = false
        });

        var (success, message, operationId) = _service.StartQualityReplace(itemId.ToString(), "server-1", "remote-1", "Movie");

        Assert.False(success);
        Assert.Contains("disabled", message);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartQualityReplace_ReplacementActionsDisabled_FailsClosed()
    {
        _plugin.Configuration.EnableQualityReplacementActions = false;
        var itemId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(new Movie { Id = itemId, Path = "/media/Movie.mkv" });

        var (success, message, operationId) = _service.StartQualityReplace(itemId.ToString(), "server-1", "remote-1", "Movie");

        Assert.False(success);
        Assert.Contains("not enabled", message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(operationId);
    }

    [Fact]
    public void StartQualityReplace_FederatedOldItem_FailsClosed()
    {
        var itemId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(new Movie
        {
            Id = itemId,
            Path = "/media/Movie.mkv",
            ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "not-local" }
        });

        var (success, message, operationId) = _service.StartQualityReplace(itemId.ToString(), "server-1", "remote-1", "Movie");

        Assert.False(success);
        Assert.Contains("no longer a local", message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(operationId);
    }

    [Fact]
    public void ValidateCompletedDownload_RejectsTinyAndHtmlFiles_ButAcceptsMediaSizedBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "federation-download-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tiny = Path.Combine(root, "tiny.mkv");
            File.WriteAllBytes(tiny, new byte[16]);
            Assert.False(FederationDownloadService.ValidateCompletedDownload(tiny));

            var html = Path.Combine(root, "error.mkv");
            File.WriteAllText(html, "<!doctype html>" + new string('x', 2048));
            Assert.False(FederationDownloadService.ValidateCompletedDownload(html));

            var media = Path.Combine(root, "movie.mkv");
            var bytes = new byte[4096];
            bytes[0] = 0x1A;
            bytes[1] = 0x45;
            bytes[2] = 0xDF;
            bytes[3] = 0xA3;
            File.WriteAllBytes(media, bytes);
            Assert.True(FederationDownloadService.ValidateCompletedDownload(media));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QualityReplace_DownloadsValidatesAndCommitsBeforeDeletingExactOldItem()
    {
        var events = new ConcurrentQueue<string>();
        var item = ConfigureQualityReplacement();
        var validationCalls = 0;
        string? stagedPath = null;
        _libraryManager.Setup(l => l.DeleteItem(item, It.IsAny<DeleteOptions>()))
            .Callback(() =>
            {
                events.Enqueue("delete");
                Assert.NotNull(stagedPath);
                Assert.False(File.Exists(stagedPath));
            });

        var service = CreateQualityService(
            async (path, token) =>
            {
                stagedPath = path;
                events.Enqueue("download");
                await File.WriteAllBytesAsync(path, ValidMediaBytes(), token);
            },
            () =>
            {
                var call = Interlocked.Increment(ref validationCalls);
                events.Enqueue("validate-" + call);
                return true;
            });

        var (started, message, operationId) = service.StartQualityReplace(item.Id.ToString(), "server-1", "remote-1", "Untrusted browser name");
        Assert.True(started, message);
        var completed = await WaitForCompletion(operationId!);

        Assert.True(completed.Success, completed.Status);
        Assert.True(File.Exists(completed.DestinationPath));
        Assert.Equal(
            new[] { "validate-1", "download", "validate-2", "delete" },
            events.ToArray());
        Assert.Equal("Approved Movie", completed.ItemName);
        _libraryManager.Verify(l => l.DeleteItem(item, It.Is<DeleteOptions>(o => o.DeleteFileLocation)), Times.Once);
    }

    [Fact]
    public async Task QualityReplace_InvalidDownloadedBodyNeverDeletesAndCleansPartialFile()
    {
        var item = ConfigureQualityReplacement();
        var service = CreateQualityService(
            (path, token) => File.WriteAllTextAsync(path, "<!doctype html>" + new string('x', 2048), token),
            () => true);

        var (started, message, operationId) = service.StartQualityReplace(item.Id.ToString(), "server-1", "remote-1", item.Name);
        Assert.True(started, message);
        var completed = await WaitForCompletion(operationId!);

        Assert.False(completed.Success);
        Assert.Contains("did not look like media", completed.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(completed.DestinationPath));
        Assert.Empty(Directory.GetFiles(FederationDownloadService.GetDownloadsRoot(), "*.partial"));
        _libraryManager.Verify(l => l.DeleteItem(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task QualityReplace_CancellationNeverDeletesAndCleansPartialFile()
    {
        var item = ConfigureQualityReplacement();
        var transferStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateQualityService(
            async (path, token) =>
            {
                await File.WriteAllBytesAsync(path, ValidMediaBytes(), token);
                transferStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            () => true);

        var (started, message, operationId) = service.StartQualityReplace(item.Id.ToString(), "server-1", "remote-1", item.Name);
        Assert.True(started, message);
        await transferStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var (cancelled, cancelMessage) = service.CancelDownload(operationId!);
        Assert.True(cancelled, cancelMessage);
        var completed = await WaitForCompletion(operationId!);

        Assert.False(completed.Success);
        Assert.Contains("Cancelled", completed.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(completed.DestinationPath));
        Assert.Empty(Directory.GetFiles(FederationDownloadService.GetDownloadsRoot(), "*.partial"));
        _libraryManager.Verify(l => l.DeleteItem(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task QualityReplace_StaleApprovalAfterDownloadKeepsBothCopies()
    {
        var item = ConfigureQualityReplacement();
        var validationCalls = 0;
        var service = CreateQualityService(
            (path, token) => File.WriteAllBytesAsync(path, ValidMediaBytes(), token),
            () => Interlocked.Increment(ref validationCalls) == 1);

        var (started, message, operationId) = service.StartQualityReplace(item.Id.ToString(), "server-1", "remote-1", item.Name);
        Assert.True(started, message);
        var completed = await WaitForCompletion(operationId!);

        Assert.False(completed.Success);
        Assert.Contains("changed before replacement", completed.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(completed.DestinationPath));
        Assert.Equal(2, validationCalls);
        _libraryManager.Verify(l => l.DeleteItem(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    private Movie ConfigureQualityReplacement()
    {
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Approved Movie",
            Path = "/media/Approved Movie.mkv",
            ProviderIds = new Dictionary<string, string> { ["tmdb"] = "123" }
        };
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "server-1",
            Name = "Friend",
            Url = "http://friend.example:8096",
            Enabled = true,
            AllowDownloads = true
        });
        _libraryManager.Setup(l => l.GetItemById(item.Id)).Returns(item);
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new VirtualFolderInfo { Name = "Federation Downloads" }
        });
        return item;
    }

    private FederationDownloadService CreateQualityService(
        Func<string, CancellationToken, Task> download,
        Func<bool> validate)
    {
        return new FederationDownloadService(
            _libraryManager.Object,
            _federationManager,
            _clientFactory.Object,
            _externalCatalogs,
            NullLogger<FederationDownloadService>.Instance,
            (_, _, path, _, token) => download(path, token),
            (_, _, _) => validate());
    }

    private static byte[] ValidMediaBytes()
    {
        var bytes = new byte[4096];
        bytes[0] = 0x1A;
        bytes[1] = 0x45;
        bytes[2] = 0xDF;
        bytes[3] = 0xA3;
        return bytes;
    }

    private static async Task<DownloadProgress> WaitForCompletion(string operationId)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var progress = DownloadProgressTracker.Get(operationId);
            if (progress?.IsComplete == true)
            {
                return progress;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Download operation did not complete within five seconds.");
    }
}
