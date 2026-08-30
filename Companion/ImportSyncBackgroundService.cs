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
    /// Playback tokens minted this run, cached per item id so an unchanged
    /// item's <c>.strm</c> file isn't rewritten (churning its mtime) just
    /// because a fresh token string differs from the last one - only
    /// re-minted once within two hours of the 24h token's expiry, or for an
    /// item never seen before. Deliberately in-memory only and per-peer: a
    /// restart just re-mints everything once, which costs nothing but a
    /// slightly slower first sync.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, (string Token, DateTime ExpiresUtc)>> TokenCacheByPeer = new();

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
        if (!TokenCacheByPeer.TryGetValue(peer.Id, out var tokenCache))
        {
            tokenCache = new Dictionary<string, (string, DateTime)>();
            TokenCacheByPeer[peer.Id] = tokenCache;
        }

        try
        {
            var libraries = await jellyfin.GetLibrariesAsync(peer.Url, peer.Token, cancellationToken).ConfigureAwait(false);
            var entries = new List<(PeerItem Item, string Url)>();

            foreach (var library in libraries)
            {
                var mediaTypes = MediaTypesFor(library.CollectionType);
                foreach (var mediaType in mediaTypes)
                {
                    var items = await jellyfin.GetItemsAsync(peer.Url, peer.Token, library.Id, mediaType, cancellationToken).ConfigureAwait(false);
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!tokenCache.TryGetValue(item.Id, out var cached) || cached.ExpiresUtc < DateTime.UtcNow.AddHours(2))
                        {
                            var minted = await jellyfin.GetPlaybackTokenAsync(peer.Url, peer.Token, item.Id, cancellationToken).ConfigureAwait(false);
                            if (minted == null)
                            {
                                logger.LogWarning("[Companion] Could not mint a playback token for {Name} from {Peer} - skipping this sync", item.Name, peer.Name);
                                continue;
                            }

                            cached = minted.Value;
                            tokenCache[item.Id] = cached;
                        }

                        entries.Add((item, JellyfinImportService.BuildStreamUrl(peer.Url, item.Id, cached.Token)));
                    }
                }
            }

            var exportPath = string.IsNullOrWhiteSpace(peer.ExportPath)
                ? Path.Combine(AppContext.BaseDirectory, "imported", SafeFolderName(peer.Name))
                : peer.ExportPath;
            var previousCount = peer.LastItemCount;
            var written = StrmExporter.Export(exportPath, entries);

            peer.LastSyncUtc = DateTime.UtcNow;
            peer.LastItemCount = written;
            peer.LastError = null;

            if (written != previousCount && !string.IsNullOrEmpty(peer.PlexSectionKey) && state.ServerBaseUrl != null && state.ServerAccessToken != null)
            {
                try
                {
                    await plex.RefreshSectionAsync(state.ServerBaseUrl, state.ServerAccessToken, peer.PlexSectionKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    logger.LogWarning(ex, "[Companion] Synced {Peer} but could not trigger a Plex library refresh", peer.Name);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            peer.LastError = ex.Message;
            logger.LogWarning(ex, "[Companion] Sync failed for {Peer}", peer.Name);
        }

        await state.SaveAsync().ConfigureAwait(false);
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
