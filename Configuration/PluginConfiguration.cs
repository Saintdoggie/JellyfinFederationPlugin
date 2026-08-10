using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Plugin configuration for federation settings.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the local server's own reachable URL (auto-detected, overridable).
        /// </summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path where the federation cache is persisted.
        /// </summary>
        public string CachePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether duplicate items across servers are
        /// merged into a single federated item (deduplicated by provider ID).
        /// </summary>
        public bool EnableDedup { get; set; } = true;

        /// <summary>
        /// Gets or sets the provider id keys used for dedup (e.g. imdb, tmdb, tvdb).
        /// </summary>
        public List<string> DedupProviderIds { get; set; } = new List<string> { "imdb", "tmdb", "tvdb" };

        /// <summary>
        /// Gets or sets a value indicating whether virtual libraries should be
        /// auto-provisioned from the configured library mappings.
        /// </summary>
        public bool AutoProvisionLibraries { get; set; } = true;

        /// <summary>
        /// Gets or sets the refresh interval (in hours) for the background cache refresh task.
        /// </summary>
        public int RefreshIntervalHours { get; set; } = 1;

        /// <summary>
        /// Gets or sets the list of remote Jellyfin servers.
        /// </summary>
        public List<RemoteServer> RemoteServers { get; set; } = new List<RemoteServer>();

        /// <summary>
        /// Gets or sets the virtual library mappings.
        /// </summary>
        public List<LibraryMapping> LibraryMappings { get; set; } = new List<LibraryMapping>();

        /// <summary>
        /// Gets or sets a value indicating whether the one-time migration that
        /// force-recreates existing Season/Episode items in dependency order has run.
        /// Items created before 0.0.13 were saved in a single flat batch, which could
        /// leave an episode or season with incomplete ancestry (it was saved before
        /// its parent was actually persisted) - invisible to ancestor-based browsing
        /// even though it still exists. False by default so every upgrading install
        /// runs the migration exactly once; a fresh install has nothing to migrate,
        /// so it's a harmless no-op there.
        /// V2: the 0.0.14 migration (V1) could run concurrently with itself (startup
        /// sync racing the scheduled task) since nothing serialized syncs yet, which
        /// hit SQLite lock errors mid delete-recreate and could leave a mapping's
        /// nested items half-migrated while still marking V1 complete. 0.0.15 adds
        /// sync serialization and reruns once more under V2 to clean up from that.
        /// V3: the actual mechanism Jellyfin's Shows/{id}/Seasons and
        /// Shows/{id}/Episodes endpoints use is SeriesPresentationUniqueKey matching
        /// (see FederationLibraryManager.MaterializeItem), not ancestry ordering -
        /// V1/V2 fixed a real but different problem and never touched this field, so
        /// every federated season/episode was still undiscoverable from the show
        /// page. 0.0.16 sets it and reruns once more under V3 to backfill it onto
        /// whatever V1/V2 already created.
        /// V4: V3 only recreated Season/Episode items, not Series - a Series created
        /// before 0.0.16 never had its own PresentationUniqueKey explicitly set
        /// either, and its lazy fallback computation only matches what's stamped on
        /// its children when the library's EnableAutomaticSeriesGrouping option is
        /// off (unverifiable from the plugin). 0.0.17 recreates Series too under V4
        /// so the match no longer depends on that setting.
        /// </summary>
        public bool MigratedTieredCreationV4 { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the season-index/visibility
        /// migration has run.
        /// V5: items created before 0.0.20 were stamped IsVirtualItem = true, which
        /// Jellyfin treats as "missing episode" and filters out of the show page
        /// unless the user enables DisplayMissingEpisodes; their Season items also
        /// had no IndexNumber, so SeriesMetadataService created a duplicate empty
        /// season beside each real one. Recreating them picks up both fixes, and the
        /// duplicates Jellyfin already created are swept in the same pass (they carry
        /// no FederationKey, so ordinary reconciliation never touches them).
        /// </summary>
        public bool MigratedSeasonIndexV5 { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the remote-location migration has run.
        /// V6: federated items were materialized as their base Jellyfin CLR types
        /// (Episode, Season, Series, Movie, Audio...) with Path left null, which makes
        /// BaseItem.LocationType resolve to LocationType.Virtual - and the Jellyfin web
        /// client renders a "Missing" badge on any Episode with
        /// Type == Episode && LocationType == Virtual, regardless of IsVirtualItem.
        /// Federated items are remote (pathless by design), not missing. 0.0.22 creates
        /// them under dedicated subclasses (FederatedEpisode, FederatedMovie, ...) that
        /// override LocationType to Remote. Because item ids are derived from the CLR
        /// type, every existing federated item's id changes, so this migration deletes
        /// and recreates them all under the new types once. Local watch/playback progress
        /// on federated items is reset (same tradeoff as prior migrations).
        /// </summary>
        public bool MigratedRemoteLocationV6 { get; set; }
    }

    /// <summary>
    /// Streaming mode for a remote server.
    /// </summary>
    public enum StreamingMode
    {
        /// <summary>
        /// 302 redirect the client directly to the remote server (default).
        /// Exposes the remote API key to clients on the network.
        /// </summary>
        Direct = 0,

        /// <summary>
        /// Proxy the stream body through this server. Slower but never exposes the remote API key.
        /// </summary>
        Proxy = 1
    }

    /// <summary>
    /// Represents a remote Jellyfin server configuration.
    /// </summary>
    public class RemoteServer
    {
        /// <summary>
        /// Gets or sets the unique identifier for this server.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the friendly name for this server.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the server URL (e.g., http://remote-jellyfin:8096).
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the API key for authentication.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this server is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the user ID to authenticate as on the remote server.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the streaming mode to use for content from this server.
        /// </summary>
        public StreamingMode StreamingMode { get; set; } = StreamingMode.Direct;

        /// <summary>
        /// Gets or sets the priority used when picking a primary source for deduped items.
        /// Lower number = higher priority.
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Gets or sets a value indicating whether the remote server requires an api_key
        /// query parameter for unauthenticated image fetches.
        /// </summary>
        public bool RequireApiKeyForImages { get; set; } = false;
    }

    /// <summary>
    /// Represents a mapping between remote libraries and local virtual libraries.
    /// </summary>
    public class LibraryMapping
    {
        /// <summary>
        /// Gets or sets the local library name (shadow library).
        /// </summary>
        public string LocalLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the media type (Movie, Series, MusicVideo, etc.).
        /// </summary>
        public string MediaType { get; set; } = "Movie";

        /// <summary>
        /// Gets or sets the list of remote server IDs to pull content from.
        /// </summary>
        public List<string> RemoteServerIds { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the list of specific remote library sources.
        /// </summary>
        public List<RemoteLibrarySource> RemoteLibrarySources { get; set; } = new List<RemoteLibrarySource>();

        /// <summary>
        /// Gets or sets a value indicating whether this mapping is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether a virtual library should be
        /// auto-provisioned for this mapping. When false, the mapping is resolved
        /// live but no top-level library is created.
        /// </summary>
        public bool AutoProvision { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether this mapping is managed by the
        /// simplified "Choose what to share" picker on the config page. Auto-managed
        /// mappings are rebuilt whenever the picker selection is saved; custom
        /// (hand-made) mappings are left untouched.
        /// </summary>
        public bool AutoManaged { get; set; } = false;
    }

    /// <summary>
    /// Represents a specific remote library source.
    /// </summary>
    public class RemoteLibrarySource
    {
        /// <summary>
        /// Gets or sets the remote server ID.
        /// </summary>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the remote server name (for display).
        /// </summary>
        public string ServerName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the remote library ID.
        /// </summary>
        public string RemoteLibraryId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the remote library name (for display).
        /// </summary>
        public string RemoteLibraryName { get; set; } = string.Empty;
    }
}
