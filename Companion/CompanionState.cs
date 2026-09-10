using System.Security.Cryptography;
using System.Text.Json;

namespace FederationCompanion;

/// <summary>
/// Everything this app remembers between runs: the Plex credential it minted
/// for itself and which of the user's own libraries they've chosen to make
/// available to federated Jellyfin servers. Persisted as a single JSON file
/// next to the executable - this app has no database, and does not need one
/// at this scale (one Plex account, a handful of libraries and peers).
/// </summary>
public sealed class CompanionState
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    /// <summary>
    /// Stable per-install identifier Plex requires on every request. Generated
    /// once and kept forever - changing it would make Plex treat this app as
    /// a brand new device, invalidating the existing sign-in.
    /// </summary>
    public string ClientIdentifier { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Protects every browser/admin API when the same Kestrel listener is
    /// exposed through Funnel for server-to-server linking and streaming.
    /// Supplied by the local UI as a header and never returned by an API.
    /// </summary>
    public string AdminAccessKey { get; set; } = CompanionSecrets.Create();

    /// <summary>Read-only media mount credential, separate from administration.</summary>
    public string MediaAccessKey { get; set; } = CompanionSecrets.Create();

    /// <summary>Mounted media root as seen by Plex; never the .strm export directory.</summary>
    public string? PlexMountRoot { get; set; }

    public string? MediaMountRoot { get; set; }

    public bool AutoStartMediaMount { get; set; }

    /// <summary>Explicit owner choice; fresh installs do not download/run mount helpers.</summary>
    public bool MediaMountSetupAccepted { get; set; }

    /// <summary>
    /// The signed-in Plex account's auth token, or null when not yet signed in.
    /// This is an account-level token (from the OAuth PIN flow), not a
    /// per-server one - <see cref="PlexClient"/> resolves the actual server
    /// token via Plex.tv's resource list.
    /// </summary>
    public string? PlexAccountToken { get; set; }

    /// <summary>
    /// The Plex Media Server this app manages, resolved from the account's
    /// own server list after sign-in - the user is assumed to be signed in
    /// with the same Plex account that owns (or has access to) that server.
    /// </summary>
    public string? ServerBaseUrl { get; set; }

    public string? ServerAccessToken { get; set; }

    public string? ServerName { get; set; }

    public string? ServerMachineIdentifier { get; set; }

    /// <summary>
    /// This app's own externally-reachable address (a Tailscale Funnel
    /// hostname in the common case) - what a Jellyfin Federation server
    /// calls to complete a connect-code exchange. Set once, during the
    /// Tailscale setup step; unrelated to <see cref="ServerBaseUrl"/>, which
    /// is Plex's own address and may differ (its own Funnel port, or a
    /// private tailnet address if the Jellyfin peer happens to share this
    /// user's tailnet).
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Optional URL Plex itself should use to fetch imported <c>.strm</c>
    /// streams from this app (can be plain http on a LAN hostname). Separate
    /// from <see cref="PublicUrl"/>, which must stay a public https address
    /// for off-tailnet Jellyfin friends claiming a share code.
    /// </summary>
    public string? PlaybackBaseUrl { get; set; }

    /// <summary>
    /// Path of the import folder as Plex's own process sees it, when that
    /// differs from this app's <see cref="JellyfinImportPeer.ExportPath"/>
    /// (Companion and Plex in different containers sharing a volume).
    /// </summary>
    public string? PlexVisibleImportRoot { get; set; }

    /// <summary>
    /// Every library section this Plex server has, with whether the user has
    /// chosen to share it. Refreshed from Plex on demand; a section already
    /// present keeps its existing Shared flag, so re-scanning doesn't reset
    /// choices already made.
    /// </summary>
    public List<CompanionLibrary> Libraries { get; set; } = new();

    /// <summary>
    /// Jellyfin Federation servers this app has approved to pull from the
    /// libraries marked shared above. Empty until Phase 2 (connect-code
    /// exchange) is wired in; for now a peer is added manually by an admin
    /// who already has this app's ServerAccessToken out of band.
    /// </summary>
    public List<CompanionPeer> Peers { get; set; } = new();

    /// <summary>
    /// Jellyfin Federation servers this app imports content *from*, via a
    /// connect code the Jellyfin admin generated in their own Companion tab -
    /// the mirror image of <see cref="Peers"/> (servers that import *from*
    /// this app's own Plex library).
    /// </summary>
    public List<JellyfinImportPeer> ImportPeers { get; set; } = new();

    /// <summary>
    /// Pool invites from Jellyfin friends, waiting for the Plex owner to
    /// Accept. Delivered to <c>/api/pools/invite</c> using a peer token.
    /// </summary>
    public List<CompanionPoolInvite> IncomingPoolInvites { get; set; } = new();

    private static string PathOnDisk => Path.Combine(AppContext.BaseDirectory, "companion-state.json");

    public static async Task<CompanionState> LoadAsync()
    {
        try
        {
            if (File.Exists(PathOnDisk))
            {
                RestrictStateFilePermissions(PathOnDisk);
                CompanionState? loaded;
                await using (var stream = File.OpenRead(PathOnDisk))
                {
                    loaded = await JsonSerializer.DeserializeAsync<CompanionState>(stream).ConfigureAwait(false);
                }

                if (loaded != null)
                {
                    var migrated = false;
                    if (!CompanionSecrets.IsValid(loaded.AdminAccessKey))
                    {
                        loaded.AdminAccessKey = CompanionSecrets.Create();
                        migrated = true;
                    }

                    foreach (var peer in loaded.ImportPeers)
                    {
                        // Older state files predate the stable Companion relay.
                        // Give each peer its own unguessable signing secret on
                        // first load; it is never returned by an API response.
                        if (!CompanionSecrets.IsValid(peer.StreamSecret))
                        {
                            peer.StreamSecret = CompanionSecrets.Create();
                            migrated = true;
                        }
                    }

                    foreach (var peer in loaded.Peers)
                    {
                        if (!CompanionSecrets.IsValid(peer.AccessToken))
                        {
                            peer.AccessToken = CompanionSecrets.Create();
                            migrated = true;
                        }
                    }

                    if (migrated)
                    {
                        await loaded.SaveAsync().ConfigureAwait(false);
                    }

                    return loaded;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt state file - start fresh rather than crash-looping on
            // every startup. The user just has to sign into Plex again.
        }

        return new CompanionState();
    }

    public async Task SaveAsync()
    {
        await SaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Write to a temp file and swap in, so a crash or power loss
            // mid-write never leaves a half-written, unparseable state file
            // behind - this file is the only record of the user's sign-in
            // and sharing choices.
            var tempPath = PathOnDisk + ".tmp";
            await using (var stream = PrivateFile.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, this, JsonOpts).ConfigureAwait(false);
            }

            RestrictStateFilePermissions(tempPath);

            File.Move(tempPath, PathOnDisk, overwrite: true);
        }
        finally
        {
            SaveLock.Release();
        }
    }

    private static void RestrictStateFilePermissions(string path) => PrivateFile.Restrict(path);

}

public sealed class CompanionLibrary
{
    public string SectionKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Plex's own type string - "movie" or "show".</summary>
    public string Type { get; set; } = string.Empty;

    public List<string> Locations { get; set; } = new();

    public bool Shared { get; set; }
}

public sealed class CompanionPeer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name only - not used for anything security-relevant.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Revocable credential handed to this Jellyfin peer; never exposed by an owner API.</summary>
    public string AccessToken { get; set; } = CompanionSecrets.Create();

    /// <summary>Allows intentional one-off server-side saves by this friend.</summary>
    public bool AllowDownloads { get; set; } = true;

    /// <summary>Separate opt-in for batches larger than three items.</summary>
    public bool AllowBulkDownloads { get; set; }
}

public sealed class CompanionPoolInvite
{
    public string InviteId { get; set; } = string.Empty;

    public string PoolId { get; set; } = string.Empty;

    public string? PoolName { get; set; }

    public string? OwnerName { get; set; }

    public string FromFederationId { get; set; } = string.Empty;

    public string? CallbackUrl { get; set; }

    public string? CallbackToken { get; set; }

    public string? FederationPluginVersion { get; set; }

    public List<CompanionPoolRosterMember> Roster { get; set; } = new();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CompanionPoolRosterMember
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

public sealed class JellyfinImportPeer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>The federation token from the connect code - sent as X-Federation-Token on every Peer/* call to this friend.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Local folder this peer's .strm files are written into - point a Plex library at this path.</summary>
    public string ExportPath { get; set; } = string.Empty;

    /// <summary>
    /// Base URL Plex uses to call back into this Companion for stable relay
    /// links. Captured when the friend is connected, so a background sync does
    /// not depend on an active browser request to construct playable URLs.
    /// </summary>
    public string? PlaybackBaseUrl { get; set; }

    /// <summary>
    /// Per-peer HMAC key used to bind every public relay URL to one exact media
    /// item. This is a local credential and must never be serialized by an API
    /// response or written into a .strm file.
    /// </summary>
    public string StreamSecret { get; set; } = CompanionSecrets.Create();

    /// <summary>Which of this app's own Plex sections to refresh after a sync that changed something - null until the user picks one.</summary>
    public string? PlexSectionKey { get; set; }

    /// <summary>Auto-created (or matched) Plex movie library for this import.</summary>
    public string? PlexMovieSectionKey { get; set; }

    /// <summary>Auto-created (or matched) Plex TV library for this import.</summary>
    public string? PlexShowSectionKey { get; set; }

    /// <summary>Null preserves legacy selections; new peers explicitly choose libraries.</summary>
    public List<string>? SelectedLibraryIds { get; set; }

    public List<PeerLibrary> AvailableLibraries { get; set; } = new();

    public bool PlexRefreshPending { get; set; }

    public List<MountedMediaFile> MountedFiles { get; set; } = new();

    public List<ImportCatalogItem> ImportCatalog { get; set; } = new();

    public DateTime? LastSyncUtc { get; set; }

    public int LastItemCount { get; set; }

    public string? LastError { get; set; }
}

internal static class CompanionSecrets
{
    public static string Create()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }

        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
