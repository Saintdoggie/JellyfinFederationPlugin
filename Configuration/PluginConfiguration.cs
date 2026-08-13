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
        /// Gets or sets a value indicating whether this server participates in
        /// friends-of-friends discovery. Two independent things gate on this single
        /// flag, deliberately kept as one opt-in rather than two: (1) this server will
        /// tell its own friends who its other friends are when asked (see
        /// <c>Friends/List</c>), and (2) this server will ask each of its own friends
        /// for *their* friends list and automatically send friend requests to anyone
        /// new it discovers that way. Off by default - exposing your friend list, and
        /// reaching out to strangers on your friends' behalf, are both bigger trust
        /// decisions than a single direct friendship. Consent is still preserved on
        /// the discovered side either way: an auto-sent request still needs their
        /// admin to accept it, same as a manually sent one.
        /// </summary>
        public bool AllowFriendsOfFriends { get; set; } = false;

        /// <summary>
        /// Gets or sets the list of remote Jellyfin servers.
        /// </summary>
        public List<RemoteServer> RemoteServers { get; set; } = new List<RemoteServer>();

        /// <summary>
        /// Gets or sets the virtual library mappings.
        /// </summary>
        public List<LibraryMapping> LibraryMappings { get; set; } = new List<LibraryMapping>();

        /// <summary>
        /// Gets or sets this server's own persistent federation identity, used to
        /// identify it to friends. Generated lazily (and saved) on first use rather
        /// than in this default initializer, so it stays stable even if a fresh
        /// config object is constructed more than once before ever being persisted -
        /// see <see cref="Services.FederationFriendService.GetOrCreateLocalFederationId"/>.
        /// </summary>
        public string LocalFederationId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets friend requests this server has sent that are still awaiting
        /// the other side's decision.
        /// </summary>
        public List<FriendRequest> OutgoingFriendRequests { get; set; } = new List<FriendRequest>();

        /// <summary>
        /// Gets or sets friend requests received from other servers, awaiting this
        /// server's admin to accept or reject.
        /// </summary>
        public List<FriendRequest> IncomingFriendRequests { get; set; } = new List<FriendRequest>();


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

        /// <summary>
        /// Gets or sets a value indicating whether the remote-path migration has run.
        /// V7: federated items were created with Path left null, which made
        /// BaseItem.GetVersionInfo emit a MediaSourceType.Placeholder static media
        /// source - no path, container or streams - and additionally suppressed the
        /// EnableRemoteContentProbe pass in MediaSourceManager.GetPlaybackMediaSources
        /// (it is skipped when the first source is a placeholder). Clients therefore
        /// had no usable source and reported "Unable to find a valid media source to
        /// play". 0.0.26 stamps the remote stream URL on item.Path (plus
        /// IsShortcut/ShortcutPath, the same mechanism .strm files use), so the static
        /// source is a real Http source. Existing items are only ever created or
        /// deleted by reconciliation, never updated in place, so they need one forced
        /// rebuild to pick the path up. Item ids are unchanged by this (they derive
        /// from the federation path and CLR type, neither of which changes), but local
        /// watch progress on federated items is reset, as with prior rebuilds.
        /// </summary>
        public bool MigratedRemotePathV7 { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the stock-CLR-type migration has run.
        /// V8: 0.0.22 materialized federated items under plugin subclasses
        /// (FederatedMovie, FederatedSeries, ...) so they could override LocationType.
        /// That is not survivable in Jellyfin: BaseItem.GetBaseItemKind() resolves an
        /// item's kind with <c>Enum.Parse&lt;BaseItemKind&gt;(GetType().Name)</c>, so a
        /// class name absent from that enum throws ArgumentException. The call sits
        /// under both DtoService.AttachBasicFields and Folder.GetCachedChildren, so
        /// every API response carrying a federated item failed with a 500 and every
        /// enumeration of a folder containing one threw - federated content stopped
        /// appearing at all, and reconciliation of any affected library aborted.
        /// 0.0.27 goes back to Jellyfin's own types; the LocationType override is
        /// unnecessary now that items carry a real remote URL on Path. Item ids derive
        /// from the CLR type, so all existing items are rebuilt once.
        /// </summary>
        public bool MigratedStockTypesV8 { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the IsShortcut-removal migration has
        /// run.
        /// V9: 0.0.26 set IsShortcut/ShortcutPath on Direct-mode items believing it was
        /// the same mechanism .strm files use. It is not:
        /// ProbeProvider.FetchShortcutInfo unconditionally does
        /// File.ReadAllLines(item.Path), expecting Path to be a real local .strm file
        /// rather than the URL it actually is, so it throws
        /// DirectoryNotFoundException on every metadata refresh - which, because the
        /// failure prevents MediaStreams from ever being saved, means every playback
        /// attempt re-triggers it, not just the first. 0.0.30 stops setting
        /// IsShortcut/ShortcutPath on newly created items, but reconciliation only
        /// creates and deletes items, never updates them in place (same as V4-V8), so
        /// every item already persisted under 0.0.26-0.0.29 keeps IsShortcut=true
        /// forever without this migration. Item ids are unchanged (neither the
        /// federation path nor the CLR type changed), but existing federated items are
        /// rebuilt once so the stale flag is cleared, and local watch progress on them
        /// is reset, as with prior rebuilds.
        /// </summary>
        public bool MigratedRemoveShortcutV9 { get; set; }

        /// <summary>
        /// One-time migration: rebuilds every federated item on a Direct-mode server
        /// with WAN bitrate capping enabled (WanCapMode Auto or Manual). Those items
        /// had item.Path deliberately left null (to avoid a stale cap value freezing
        /// onto it - see the comment on ResolvePlaybackUrl) - the exact
        /// null-Path-means-Placeholder problem V7 already fixed once, reintroduced
        /// for this subset of items. Jellyfin's own static media source for a
        /// null-Path item is MediaSourceType.Placeholder, which the item-detail
        /// endpoint embeds directly (it does not call this plugin's dynamic
        /// FederationMediaSourceProvider), so jellyfin-web's Details page never
        /// rendered a Play button for any of them - confirmed live via a real
        /// browser session against a federated item on 0.0.37. item.Path is now
        /// always stamped (0.0.38), but reconciliation only creates and deletes
        /// items, never updates them in place (same as V4-V9), so every affected
        /// item already persisted needs rebuilding once to pick it up. Item ids are
        /// unchanged; local watch progress on rebuilt items is reset, as with prior
        /// rebuilds.
        /// </summary>
        public bool MigratedPlaceholderPathV10 { get; set; }
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

        /// <summary>
        /// Gets or sets how a bitrate cap is chosen for Direct-mode streams from this
        /// server. Direct mode's own request to the remote (see
        /// <see cref="Services.FederationLibraryManager.BuildPlaybackUrl"/>) fetches the
        /// raw file unmodified (<c>Static=true</c>) by default - fine when both servers
        /// are on the same network, but when they are two independent servers connected
        /// only over the internet, this receiving server has to sustain the *source's
        /// own* bitrate (which can be 25+ Mbps for a 4K HDR release) pulling from the
        /// remote's upload connection before it can even start its own transcode. If
        /// that upload can't keep up - a very common asymmetric-home-internet
        /// situation, unrelated to either server's CPU/GPU - playback stutters exactly
        /// as if buffering, no matter how fast either machine is.
        ///
        /// <see cref="Services.WanBandwidthMonitor"/> implements the actual policy:
        /// direct play (the raw file, best quality, no extra transcode cost) whenever
        /// it can - same network, or not yet proven otherwise - and only asks the
        /// remote to transcode down once it has positively confirmed the link is a WAN
        /// one and measured what it can actually sustain, capping to the largest
        /// bitrate that fits rather than a blind guess.
        /// </summary>
        public WanCapMode WanCapMode { get; set; } = WanCapMode.Auto;

        /// <summary>
        /// Gets or sets the fixed bitrate cap (in Mbps) used when
        /// <see cref="WanCapMode"/> is <see cref="Configuration.WanCapMode.Manual"/>.
        /// Ignored in <see cref="Configuration.WanCapMode.Auto"/> and
        /// <see cref="Configuration.WanCapMode.Off"/>.
        /// </summary>
        public int WanMaxBitrateMbps { get; set; } = 0;

        /// <summary>
        /// Gets or sets the max output height (e.g. 1080) applied whenever a bitrate
        /// cap is in effect (Auto or Manual), or 0 for no resolution cap. Downscaling
        /// resolution alongside a bitrate cap gives noticeably better quality per bit
        /// than keeping the source's full 4K resolution squeezed into the same
        /// bitrate.
        /// </summary>
        public int WanMaxHeight { get; set; } = 1080;
    }

    /// <summary>
    /// How a Direct-mode server's WAN bitrate cap is determined.
    /// </summary>
    public enum WanCapMode
    {
        /// <summary>
        /// Default. Same-network servers (and anything not yet classified) stream the
        /// raw source file unchanged; a server confirmed to be reached only over the
        /// internet gets capped to what <see cref="Services.WanBandwidthMonitor"/>
        /// measures it can actually sustain.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Always cap Direct-mode streams from this server to the fixed
        /// <see cref="RemoteServer.WanMaxBitrateMbps"/>, regardless of network
        /// detection or measurement.
        /// </summary>
        Manual = 1,

        /// <summary>
        /// Never cap; always stream the raw source file. The original, pre-0.0.32
        /// behavior.
        /// </summary>
        Off = 2
    }

    /// <summary>
    /// A friend request between this server and another Federation-enabled server,
    /// pending on one side or the other. See
    /// <see cref="Services.FederationFriendService"/> for the request/accept protocol
    /// this drives - it replaces manually copying an API key between admins with a
    /// server-to-server handshake that mints the keys automatically.
    /// </summary>
    public class FriendRequest
    {
        /// <summary>
        /// Gets or sets the id shared by both sides of the request. Generated by
        /// whichever side sent it; the other side uses it both to identify the
        /// request and, on receipt, to verify it by calling back to
        /// <see cref="RemoteServerUrl"/> and checking the sender really has a
        /// matching outgoing request under this id.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the other server's address.
        /// </summary>
        public string RemoteServerUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the other server's display name (its FriendlyName).
        /// </summary>
        public string RemoteServerName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the other server's persistent federation id
        /// (their <see cref="PluginConfiguration.LocalFederationId"/>).
        /// </summary>
        public string RemoteServerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the API key one side generated for the other to use.
        /// On an outgoing request this is the key we minted for them; on an incoming
        /// request this is the key they minted for us. Either way, once accepted it
        /// becomes the <see cref="RemoteServer.ApiKey"/> of the resulting friend.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the request was created.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets a value indicating whether an incoming request's claimed
        /// origin was confirmed by calling back to <see cref="RemoteServerUrl"/> and
        /// finding a matching outgoing request there. Informational only - accepting
        /// an unverified request is still allowed, since the admin clicking Accept is
        /// the real trust boundary, not this check.
        /// </summary>
        public bool Verified { get; set; }
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
