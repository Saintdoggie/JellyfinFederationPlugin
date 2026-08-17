using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
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
    private readonly FederationDownloadService _service;

    public FederationDownloadServiceTests()
    {
        _plugin = new RealPluginInstance();

        _libraryManager = new Mock<ILibraryManager>();
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        _cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        _federationManager = new FederationLibraryManager(_libraryManager.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, _cache, bandwidthMonitor);

        _service = new FederationDownloadService(_libraryManager.Object, _federationManager, NullLogger<FederationDownloadService>.Instance);
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
}
