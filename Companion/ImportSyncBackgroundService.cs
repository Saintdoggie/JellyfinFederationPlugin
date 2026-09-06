using System.Collections.Concurrent;
using System.Text.Json;

namespace FederationCompanion;

/// <summary>
/// Periodically pulls each configured <see cref="JellyfinImportPeer"/>'s
/// federated catalog and keeps a local <c>.strm</c> export in sync -
/// additions and removals on the Jellyfin side are reflected here on the next
/// tick, so a Plex library pointed at the export folder stays current without
/// the user ever running a sync by hand (though <c>/api/import/peers/{id}/sync</c>
/// still exists for "I don't want to wait").
/// </summary>
public sealed class ImportSyncBackgroundService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(30);

    private readonly CompanionState _state;
    private readonly JellyfinImportService _jellyfin;
    private readonly PlexClient _plex;
    private readonly ILogger<ImportSyncBackgroundService> _logger;

    public ImportSyncBackgroundService(CompanionState state, JellyfinImportService jellyfin, PlexClient plex, ILogger<ImportSyncBackgroundService> logger)
    {
        _state = state;
        _jellyfin = jellyfin;
        _plex = plex;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SyncInterval);
        do
        {
            await ImportSyncCoordinator.SyncAllAsync(_state, _jellyfin, _plex, _logger, stoppingToken).ConfigureAwait(false);
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// The actual sync logic, factored out of <see cref="ImportSyncBackgroundService"/>
/// so a manual "sync now" request (see <c>Program.cs</c>) can run the exact
/// same path as the periodic timer, not a hand-duplicated copy of it.
/// </summary>
public static class ImportSyncCoordinator
{
    /// <summary>
    /// A background tick and a manual "sync now" can overlap. Serializing per
    /// peer prevents two exporters from pruning each other's in-flight files
    /// and keeps the status fields coherent.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SyncLocks = new();

    public static async Task SyncAllAsync(CompanionState state, JellyfinImportService jellyfin, PlexClient plex, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var peer in state.ImportPeers.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SyncOneAsync(state, peer, jellyfin, plex, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task SyncOneAsync(CompanionState state, JellyfinImportPeer peer, JellyfinImportService jellyfin, PlexClient plex, ILogger logger, CancellationToken cancellationToken)
    {
        var syncLock = SyncLocks.GetOrAdd(peer.Id, _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!state.ImportPeers.Contains(peer)) return;

            var libraries = await jellyfin.GetLibrariesAsync(peer.Url, peer.Token, cancellationToken).ConfigureAwait(false);
            peer.AvailableLibraries = libraries;
            var entries = new List<(PeerItem Item, string Url)>();
            var playbackBaseUrl = FirstNonEmpty(state.PlaybackBaseUrl, peer.PlaybackBaseUrl, state.PublicUrl);

            if (string.IsNullOrWhiteSpace(playbackBaseUrl))
            {
                throw new InvalidOperationException("No Companion playback address is configured. Reconnect this friend after setting the public address.");
            }

            foreach (var library in libraries.Where(l => peer.SelectedLibraryIds == null || peer.SelectedLibraryIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase)))
            {
                var mediaTypes = MediaTypesFor(library.CollectionType);
                foreach (var mediaType in mediaTypes)
                {
                    var items = await jellyfin.GetItemsAsync(peer.Url, peer.Token, library.Id, mediaType, cancellationToken).ConfigureAwait(false);
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        entries.Add((item, JellyfinImportService.BuildCompanionStreamUrl(playbackBaseUrl, peer, item.Id)));
                    }
                }
            }

            var exportPath = string.IsNullOrWhiteSpace(peer.ExportPath)
                ? Path.Combine(AppContext.BaseDirectory, "imported", SafeFolderName(peer.Name))
                : peer.ExportPath;
            if (!state.ImportPeers.Contains(peer)) return;
            var mountedCatalog = MediaMount.BuildCatalog(entries.Select(e => e.Item));
            var export = StrmExporter.Export(exportPath, entries, peer.Id);
            peer.PlexRefreshPending |= !peer.MountedFiles.OrderBy(f => f.Path, StringComparer.Ordinal)
                .SequenceEqual(mountedCatalog.Files.OrderBy(f => f.Path, StringComparer.Ordinal));
            peer.ExportPath = exportPath;
            peer.MountedFiles = mountedCatalog.Files;
            peer.ImportCatalog = mountedCatalog.Items;
            peer.PlexRefreshPending |= export.ChangedFileCount > 0;

            peer.LastSyncUtc = DateTime.UtcNow;
            peer.LastItemCount = export.ItemCount;
            peer.LastError = null;

            // Plex needs a refresh when any .strm URL changes, not merely when
            // the number of files changes. Refresh every auto-created Movies/Shows
            // section plus any manually chosen section.
            var sectionKeys = new[] { peer.PlexMovieSectionKey, peer.PlexShowSectionKey, peer.PlexSectionKey }
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (peer.PlexRefreshPending && sectionKeys.Count > 0 && state.ServerBaseUrl != null && state.ServerAccessToken != null)
            {
                peer.PlexRefreshPending = false;
                foreach (var key in sectionKeys)
                {
                    try
                    {
                        await plex.RefreshSectionAsync(state.ServerBaseUrl, state.ServerAccessToken, key!, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        peer.PlexRefreshPending = true;
                        peer.LastError = "Catalog saved. Plex could not refresh its libraries; the next sync will retry.";
                        logger.LogWarning("[Companion] Plex refresh pending for {Peer}", peer.Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            peer.LastError = "Sync did not complete. Check the friend’s address, sharing permission, and export folder, then retry. The last successful catalog has been kept.";
            logger.LogWarning("[Companion] Sync failed for {Peer}: {ErrorType}", peer.Name, ex.GetType().Name);
        }
        finally
        {
            try
            {
                await state.SaveAsync().ConfigureAwait(false);
            }
            finally
            {
                syncLock.Release();
            }
        }
    }

    public static async Task RemoveAsync(CompanionState state, JellyfinImportPeer peer, bool removeFiles, CancellationToken cancellationToken)
    {
        var syncLock = SyncLocks.GetOrAdd(peer.Id, _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (removeFiles) StrmExporter.Export(peer.ExportPath, Array.Empty<(PeerItem, string)>(), peer.Id);
            state.ImportPeers.Remove(peer);
            await state.SaveAsync().ConfigureAwait(false);
        }
        finally { syncLock.Release(); }
    }

    private static IEnumerable<string> MediaTypesFor(string? collectionType)
    {
        return collectionType switch
        {
            "movies" => new[] { "Movie" },
            "tvshows" => new[] { "Episode" },
            _ => new[] { "Movie", "Episode" }
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string SafeFolderName(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "peer" : name.Trim();
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
