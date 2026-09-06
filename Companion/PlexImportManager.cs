namespace FederationCompanion;

/// <summary>
/// Turns a Jellyfin import folder into Plex libraries the owner can browse
/// from Plex itself, without walking through Plex Settings.
/// </summary>
public static class PlexImportManager
{
    public static async Task AttachAsync(
        CompanionState state,
        PlexClient plex,
        JellyfinImportPeer peer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.ServerBaseUrl) || string.IsNullOrWhiteSpace(state.ServerAccessToken))
        {
            throw new InvalidOperationException("Connect this app to Plex first, then add the friend to Plex.");
        }

        if (string.IsNullOrWhiteSpace(state.PlexMountRoot) || !MediaMount.IsMounted(state.MediaMountRoot, state.ClientIdentifier))
            throw new InvalidOperationException("Set up the media mount first. Plex cannot analyze or play a text .strm file.");
        if (peer.MountedFiles.Count == 0)
            throw new InvalidOperationException("No playable media was found. Update the friend's Federation plugin and sync again.");
        var plexRoot = state.PlexMountRoot.TrimEnd('/', '\\').Replace('\\', '/') + "/" + peer.Id;

        var movieName = $"{peer.Name} Movies (Streaming)";
        var showName = $"{peer.Name} Shows (Streaming)";
        var moviePath = $"{plexRoot}/Movies";
        var showPath = $"{plexRoot}/Shows";

        if (peer.MountedFiles.Any(f => f.Path.StartsWith("Movies/", StringComparison.Ordinal)))
        peer.PlexMovieSectionKey = await plex.EnsureSectionAsync(
            state.ServerBaseUrl, state.ServerAccessToken, movieName, "movie", moviePath, cancellationToken).ConfigureAwait(false);
        if (peer.MountedFiles.Any(f => f.Path.StartsWith("Shows/", StringComparison.Ordinal)))
        peer.PlexShowSectionKey = await plex.EnsureSectionAsync(
            state.ServerBaseUrl, state.ServerAccessToken, showName, "show", showPath, cancellationToken).ConfigureAwait(false);
        peer.PlexSectionKey ??= peer.PlexMovieSectionKey;

        foreach (var key in new[] { peer.PlexMovieSectionKey, peer.PlexShowSectionKey }.Where(k => !string.IsNullOrEmpty(k)))
        {
            try
            {
                await plex.RefreshSectionAsync(state.ServerBaseUrl, state.ServerAccessToken, key!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Library exists; Plex will scan it on its own schedule.
            }
        }
    }

    public static string PlexVisibleRoot(CompanionState state, JellyfinImportPeer peer)
    {
        if (!string.IsNullOrWhiteSpace(state.PlexVisibleImportRoot))
        {
            return PlexClient.NormalizePlexPath(Path.Combine(state.PlexVisibleImportRoot, peer.Id));
        }

        return PlexClient.MapExportPathForPlex(peer.ExportPath, null);
    }
}
