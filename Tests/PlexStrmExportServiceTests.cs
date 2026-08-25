using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers <see cref="PlexStrmExportService"/>: writing federated movies/episodes as
/// <c>.strm</c> files (plain text files containing just the item's proxy stream URL)
/// under a configurable export directory so a different media server (Plex) can scan
/// and stream them without this server ever downloading the content.
/// </summary>
[Collection("PluginInstance")]
public class PlexStrmExportServiceTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationItemCache _cache;
    private readonly FederationLibraryManager _federationManager;
    private readonly string _exportPath;

    public PlexStrmExportServiceTests()
    {
        _plugin = new RealPluginInstance();
        _exportPath = Path.Combine(Path.GetTempPath(), "federation-strm-tests-" + Guid.NewGuid().ToString("N"));

        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "serverA",
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true
        });

        _cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Moq.Mock.Of<IRemoteServerClientFactory>());
        _federationManager = new FederationLibraryManager(
            Moq.Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Moq.Mock.Of<IRemoteServerClientFactory>(),
            _cache,
            bandwidthMonitor,
            Moq.Mock.Of<IMediaStreamRepository>());
    }

    public void Dispose()
    {
        _plugin.Dispose();
        if (Directory.Exists(_exportPath))
        {
            Directory.Delete(_exportPath, recursive: true);
        }
    }

    private void EnableExport()
    {
        _plugin.Configuration.EnablePlexStrmExport = true;
        _plugin.Configuration.PlexStrmExportPath = _exportPath;
    }

    private FederatedCacheEntry AddMovie(string name, int year, Guid? remoteId = null)
    {
        var item = new BaseItemDto { Name = name, ProductionYear = year };
        return _cache.UpsertRaw("Movies", "serverA", remoteId ?? Guid.NewGuid(), item, serverPriority: 0, itemType: "Movie");
    }

    private FederatedCacheEntry AddEpisode(string series, int season, int episode, string title, Guid? remoteId = null)
    {
        var item = new BaseItemDto
        {
            Name = title,
            SeriesName = series,
            ParentIndexNumber = season,
            IndexNumber = episode
        };
        return _cache.UpsertRaw("Shows", "serverA", remoteId ?? Guid.NewGuid(), item, serverPriority: 0, itemType: "Episode");
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_Movie_WritesStrmFileContainingProxyUrl()
    {
        EnableExport();
        AddMovie("Johnny English", 2003);
        var service = new PlexStrmExportService(NullLogger<PlexStrmExportService>.Instance, _federationManager);

        await service.ExportAsync(System.Threading.CancellationToken.None);

        var expectedPath = Path.Combine(_exportPath, "Movies", "Johnny English (2003)", "Johnny English (2003).strm");
        Assert.True(File.Exists(expectedPath));
        var content = (await File.ReadAllTextAsync(expectedPath)).Trim();
        Assert.Contains("/Plugins/Federation/Stream?serverId=serverA&itemId=", content);
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_Episode_WritesNestedStrmFile()
    {
        EnableExport();
        AddEpisode("Dexter", 1, 2, "Crocodile");
        var service = new PlexStrmExportService(NullLogger<PlexStrmExportService>.Instance, _federationManager);

        await service.ExportAsync(System.Threading.CancellationToken.None);

        var expectedPath = Path.Combine(_exportPath, "Shows", "Dexter", "Season 01", "Dexter - S01E02 - Crocodile.strm");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_ServerWithFriendUserAccessRules_SkipsSource()
    {
        EnableExport();
        _plugin.Configuration.RemoteServers.Single(s => s.Id == "serverA").FriendUserAccessRules.Add(new RemoteUserAccessRule());
        AddMovie("Restricted Movie", 2020);
        var service = new PlexStrmExportService(NullLogger<PlexStrmExportService>.Instance, _federationManager);

        await service.ExportAsync(System.Threading.CancellationToken.None);

        // The export directory itself still gets created, but no .strm file for a
        // source whose per-remote-user access can't be enforced through a static
        // file another server reads directly off disk.
        Assert.Empty(Directory.EnumerateFiles(_exportPath, "*.strm", SearchOption.AllDirectories));
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_RemovesStaleFileAndEmptyDirectory_WhenEntryNoLongerPresent()
    {
        EnableExport();
        AddMovie("Keep Me", 2021);
        var removedId = Guid.NewGuid();
        AddMovie("Remove Me", 2019, removedId);
        var service = new PlexStrmExportService(NullLogger<PlexStrmExportService>.Instance, _federationManager);
        await service.ExportAsync(System.Threading.CancellationToken.None);

        var keepPath = Path.Combine(_exportPath, "Movies", "Keep Me (2021)", "Keep Me (2021).strm");
        var removedFolder = Path.Combine(_exportPath, "Movies", "Remove Me (2019)");
        var removedPath = Path.Combine(removedFolder, "Remove Me (2019).strm");
        Assert.True(File.Exists(keepPath));
        Assert.True(File.Exists(removedPath));

        // Simulate the item disappearing from an upstream server on the next sync.
        _cache.PruneServerSources("Movies", "serverA", new[] { _federationManager.GetAllEntries().Single(e => e.Metadata.Name == "Keep Me").GetPrimarySource()!.RemoteItemId });

        await service.ExportAsync(System.Threading.CancellationToken.None);

        Assert.True(File.Exists(keepPath));
        Assert.False(File.Exists(removedPath));
        Assert.False(Directory.Exists(removedFolder));
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_Disabled_DoesNotTouchExportDirectory()
    {
        // EnableExport() intentionally not called - EnablePlexStrmExport stays at
        // its default of false.
        _plugin.Configuration.PlexStrmExportPath = _exportPath;
        AddMovie("Should Not Export", 2022);
        var service = new PlexStrmExportService(NullLogger<PlexStrmExportService>.Instance, _federationManager);

        await service.ExportAsync(System.Threading.CancellationToken.None);

        Assert.False(Directory.Exists(_exportPath));
    }
}
