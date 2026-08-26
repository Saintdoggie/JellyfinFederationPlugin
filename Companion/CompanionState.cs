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

    private static string PathOnDisk => Path.Combine(AppContext.BaseDirectory, "companion-state.json");

    public static async Task<CompanionState> LoadAsync()
    {
        try
        {
            if (File.Exists(PathOnDisk))
            {
                await using var stream = File.OpenRead(PathOnDisk);
                var loaded = await JsonSerializer.DeserializeAsync<CompanionState>(stream).ConfigureAwait(false);
                if (loaded != null)
                {
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
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, this, JsonOpts).ConfigureAwait(false);
            }

            File.Move(tempPath, PathOnDisk, overwrite: true);
        }
        finally
        {
            SaveLock.Release();
        }
    }
}

public sealed class CompanionLibrary
{
    public string SectionKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Plex's own type string - "movie" or "show".</summary>
    public string Type { get; set; } = string.Empty;

    public bool Shared { get; set; }
}

public sealed class CompanionPeer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Display name only - not used for anything security-relevant.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}
