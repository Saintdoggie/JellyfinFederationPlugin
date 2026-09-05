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

        var plexRoot = PlexVisibleRoot(state, peer);
        Directory.CreateDirectory(Path.Combine(peer.ExportPath, "Movies"));
        Directory.CreateDirectory(Path.Combine(peer.ExportPath, "Shows"));

        var movieName = $"{peer.Name} Movies";
        var showName = $"{peer.Name} Shows";
        var moviePath = $"{plexRoot}/Movies";
        var showPath = $"{plexRoot}/Shows";

        peer.PlexMovieSectionKey = await plex.EnsureSectionAsync(
            state.ServerBaseUrl, state.ServerAccessToken, movieName, "movie", moviePath, cancellationToken).ConfigureAwait(false);
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
