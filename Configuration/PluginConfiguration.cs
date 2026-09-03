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
        /// This is the PUBLIC address handed to peers for handshakes and Direct-mode
        /// playback URLs - it is never used for this server's own Proxy-mode stream
        /// fetches, see <see cref="InternalServerUrl"/>.
        /// </summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base URL this server's own transcoder should use to fetch
        /// a Proxy-mode federated stream from itself (see FederationLibraryManager's
        /// GetInternalPlaybackBaseUrl and FederationMediaSourceProvider's
        /// ResolveLocalServerUrl). Blank means "use loopback" - the correct default
        /// for the overwhelming majority of installs, including every VPS/tunnel/
        /// reverse-proxy setup, since that avoids ffmpeg fetching the stream back
        /// through the public route just to reach itself. Only needs setting when
        /// loopback genuinely isn't reachable from where ffmpeg runs - e.g. Jellyfin
        /// running in a container without a shared network namespace with itself
        /// (rare), or a non-default Kestrel port that can't be auto-detected during a
        /// background sync (no live request to read the port from at that point).
        /// </summary>
        public string InternalServerUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets this server's own internal relay API key: a dedicated
        /// Jellyfin ApiKey, auto-created on first use, that this server uses purely
        /// server-side to fetch its own native <c>/Videos/{id}/stream</c> or
        /// <c>/Audio/{id}/stream</c> endpoint over loopback when relaying a Direct-mode
        /// federated stream on behalf of a friend server (see
        /// <c>FederationController.DirectStream</c>). Never transmitted over the
        /// network beyond localhost, never shown in any UI or API response - unlike
        /// <see cref="ServerUrl"/>/<see cref="InternalServerUrl"/> above, this is a
        /// secret and must be excluded from <c>FederationController.Sanitize</c>.
        /// </summary>
        public string InternalRelayApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path where the federation cache is persisted.
        /// </summary>
        public string CachePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets how this server is reachable from the internet - a one-time
        /// choice made on the Pools tab, not part of the main Save form (see the
        /// preservation block in <see cref="Configuration.FederationPluginController.UpdateConfiguration"/>).
        /// <see cref="ServerConnectivityMode.PublicFacing"/> means genuinely
        /// port-forwarded to a stable address of its own (a real domain, not a
        /// Cloudflare-style tunnel) - only a server in this mode can create a new
        /// pool (see <see cref="Services.FederationFriendService.CreatePool"/>),
        /// since a pool only works if every member is reachable enough for the
        /// others to connect to it directly. <see cref="ServerConnectivityMode.Unset"/>
        /// is the default for every install predating this setting; it is treated
        /// the same as <see cref="ServerConnectivityMode.Tailscale"/> for that gate
        /// rather than as "trust it" - an install that never made the choice
        /// explicitly should not silently inherit a capability that assumes real
        /// public reachability.
        /// </summary>
        public ServerConnectivityMode ConnectivityMode { get; set; } = ServerConnectivityMode.Unset;

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
        /// Gets or sets a value indicating whether the config page should compare
        /// each locally-owned item against its federated dedup counterparts (see
        /// <see cref="DedupProviderIds"/>) and surface any where a friend's server
        /// holds a meaningfully higher-resolution or higher-bitrate copy. Off by
        /// default - this only ever populates a review list on the config page
        /// (see <see cref="Services.FederationQualityAdvisorService"/>); nothing is
        /// ever deleted automatically, regardless of this setting.
        /// </summary>
        public bool PreferHigherQualityRemotes { get; set; } = false;

        /// <summary>
        /// Gets or sets the local item ids (see <see cref="MediaBrowser.Controller.Entities.BaseItem.Id"/>,
        /// stringified) that <see cref="Services.FederationQualityAdvisorService.FindUpgrades"/>
        /// must never surface, even when they would otherwise qualify - the
        /// admin's per-title "keep this exact copy" override to
        /// <see cref="PreferHigherQualityRemotes"/>. Excluding a title only stops
        /// it being suggested; it never touches anything already downloaded via
        /// an earlier Apply.
        /// </summary>
        public List<string> QualityUpgradeExcludedItemIds { get; set; } = new List<string>();

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
        /// Gets or sets a value indicating whether federated movies/episodes are
        /// exported as <c>.strm</c> files - plain text files containing just the
        /// item's existing proxy stream URL - so a *different* media server that
        /// can only scan a filesystem (Plex has no equivalent of Jellyfin's plugin
        /// system any more) can play the same federated content without this
        /// server ever downloading or duplicating it. Off by default: this writes
        /// files under <see cref="PlexStrmExportPath"/> on every refresh, which is
        /// meaningless (and pointless disk churn) for an install that isn't
        /// sharing its media folder with another server. Skips any source whose
        /// server has <see cref="RemoteServer.FriendUserAccessRules"/> configured,
        /// same as <see cref="Services.FederationLibraryManager.BuildStaticPath"/> -
        /// a per-remote-user restriction can't be enforced through a static file
        /// another, unrelated media server just reads off disk.
        /// </summary>
        public bool EnablePlexStrmExport { get; set; } = false;

        /// <summary>
        /// Gets or sets the directory <c>.strm</c> files are written under (this
        /// server's own filesystem view - e.g. inside its container). Must be a
        /// path another media server also has mounted (read-only is enough) for
        /// the exported files to actually be reachable there. Subfolders "Movies"
        /// and "Shows" are created underneath it automatically.
        /// </summary>
        public string PlexStrmExportPath { get; set; } = "/media/federated";

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
        /// Gets or sets pool invites this server has sent to already-known friends,
        /// still awaiting their decision. See <see cref="PoolInvite"/>.
        /// </summary>
        public List<PoolInvite> OutgoingPoolInvites { get; set; } = new List<PoolInvite>();

        /// <summary>
        /// Gets or sets pool invites received from already-known friends, awaiting
        /// this server's admin to accept or reject. See <see cref="PoolInvite"/>.
        /// </summary>
        public List<PoolInvite> IncomingPoolInvites { get; set; } = new List<PoolInvite>();


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

        /// <summary>
        /// One-time migration: rebuilds every streamable federated item so it picks
        /// up the remote's real container (see
        /// <see cref="Services.FederationLibraryManager.MaterializeItem"/>, which now
        /// always sets item.Container from the synced metadata). An older version
        /// of that method forced item.Container = "mp4" for the WAN-capped
        /// Direct-mode transcode URL regardless of the file's actual container - a
        /// leftover from when that internal-only capped URL was (wrongly) treated as
        /// client-facing. Every item persisted under that version keeps its wrong
        /// "mp4" container forever, because reconciliation only creates and deletes
        /// items, never updates them in place (same as V4-V10) - and a wrong
        /// container is not a cosmetic mismatch: Jellyfin's transcoder forces ffmpeg
        /// to demux the input as the item's stored container (e.g. "-f mp4" on what
        /// is actually a Matroska file), which fails almost instantly (FFmpeg exit
        /// code 183) and loops forever as the client keeps retrying playback -
        /// confirmed live via a real container's ffmpeg logs and a direct SQLite
        /// check of a stuck item's persisted Container. Item ids are unchanged;
        /// local watch progress on rebuilt items is reset, as with prior rebuilds.
        /// </summary>
        public bool MigratedContainerV11 { get; set; }

        /// <summary>
        /// Gets or sets the multi-server pools this server belongs to (owns or has
        /// joined). See <see cref="Services.FederationFriendService"/> for how a pool
        /// invite rides the same friend-request handshake as a direct friendship.
        /// </summary>
        public List<FederationPool> Pools { get; set; } = new List<FederationPool>();

        /// <summary>
        /// Gets or sets the federated items this server's own admin has chosen to hide
        /// from local browsing/search/home - e.g. a friend's low-quality rip of a movie
        /// already owned in better quality, or content that's simply unwanted clutter.
        /// This is a purely local, receiving-side suppression list: it is never sent to
        /// the friend server (they still think they're sharing it normally), and it is
        /// unrelated to per-friend sharing permissions (which control what a friend can
        /// see of *this* server's own content, the opposite direction).
        /// <para>
        /// Entries are <see cref="Services.FederatedCacheEntry.Key"/> values (the same stable
        /// cache key stamped as the <c>FederationKey</c> provider id on every
        /// materialized item - see <see cref="Services.FederationLibraryManager.MaterializeItem"/>),
        /// not raw Jellyfin item ids. Item ids are derived from the entry's CLR type and
        /// change across migrations (see the MigratedXxx flags above); the cache key does
        /// not, so it is the only identifier stable enough to survive a delete/recreate
        /// migration and still mean "the same friend item" on the other side.
        /// </para>
        /// </summary>
        public List<string> HiddenFederatedItemIds { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the receiving-side filter that decides what this server will
        /// even pull in from friends, before any per-friend sharing scope is evaluated.
        /// Empty means allow everything — the historical behaviour.
        /// </summary>
        public IncomingContentFilter IncomingFilter { get; set; } = new IncomingContentFilter();

        /// <summary>
        /// Gets or sets whether the one-time backfill for the new download/rating
        /// fields has run. Existing rules had AllowDownload=true implicitly before
        /// this existed, so no migration needed beyond a flag to record that fresh
        /// defaults are already correct.
        /// </summary>
        public bool MigratedIncomingFilterV12 { get; set; }

        /// <summary>
        /// Gets or sets this server's own local item ids that are never shared with
        /// ANY friend - present or future - regardless of that friend's
        /// <see cref="RemoteServer.ShareAllLibraries"/>/<see cref="RemoteServer.SharedLibraryFolderIds"/>/
        /// <see cref="RemoteServer.ExcludedItemIds"/>. The one-click "stop sharing
        /// this" toggle (item detail page button, and the settings page's catalog
        /// picker) writes here; per-friend/per-user narrowing stays on
        /// <see cref="RemoteServer.ExcludedItemIds"/>/<see cref="RemoteUserAccessRule.BlockedItemIds"/>
        /// instead, since those already mean "this friend/user specifically" and
        /// this list would be the wrong place for that. Enforced in
        /// <see cref="Services.FederationPeerAccessService.IsItemVisible(RemoteServer, string?, System.Guid, string?)"/>,
        /// ahead of every other check, on the exact same <c>Peer/*</c> code path
        /// every other sharing rule already goes through.
        /// </summary>
        public List<string> GloballyExcludedItemIds { get; set; } = new List<string>();
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
    /// What kind of media server a <see cref="RemoteServer"/> entry points at.
    /// Deliberately a field on the existing RemoteServer rather than a separate
    /// config list: every lookup, access-control check, library mapping and
    /// stream-proxy path already keys off RemoteServer.Id, so a parallel type
    /// would mean duplicating all of it. Only the two places that actually speak
    /// a server's native protocol - catalog sync and stream-URL construction -
    /// branch on this.
    /// </summary>
    public enum ServerKind
    {
        /// <summary>
        /// Another Jellyfin server running this same federation plugin, reached
        /// through its <c>/Plugins/Federation/...</c> endpoints. The default, and
        /// what every entry written before Plex support existed deserializes as.
        /// </summary>
        Jellyfin = 0,

        /// <summary>
        /// A Plex Media Server, reached through its own HTTP API with an
        /// <c>X-Plex-Token</c>. Always proxied (never Direct): the Plex token is
        /// a real credential for that whole server, so it must never reach a
        /// client, and Plex has no equivalent of this plugin's scoped
        /// per-item playback tokens.
        /// </summary>
        Plex = 1,

        /// <summary>
        /// A non-Jellyfin consumer of our own catalog - today, a Federation
        /// Companion instance a Plex-owning friend runs to import our federated
        /// content as <c>.strm</c> files. Never dialed out to (<see cref="RemoteServer.Url"/>/
        /// <see cref="RemoteServer.ApiKey"/> stay empty): this kind of entry only
        /// ever exists to be the target of <see cref="Services.FederationTokenAuth.ResolveCaller"/>
        /// on inbound <c>Peer/*</c> calls, the mirror image of every other kind
        /// which we call outward to pull content from. Sharing scope
        /// (<see cref="RemoteServer.ShareAllLibraries"/>/<see cref="RemoteServer.SharedLibraryFolderIds"/>)
        /// and the issued token work exactly the same as a real Jellyfin friend.
        /// </summary>
        Companion = 2
    }

    /// <summary>
    /// How this server (not a friend's) is reachable from the internet. See
    /// <see cref="PluginConfiguration.ConnectivityMode"/> for what it gates.
    /// </summary>
    public enum ServerConnectivityMode
    {
        /// <summary>Never chosen by this install. See <see cref="PluginConfiguration.ConnectivityMode"/>.</summary>
        Unset = 0,

        /// <summary>
        /// Port-forwarded to a stable address of its own - a real domain, not a
        /// Cloudflare-style tunnel (which routes through Cloudflare's own edge
        /// rather than this server having a directly dialable address).
        /// </summary>
        PublicFacing = 1,

        /// <summary>
        /// Reachable only through Tailscale (optionally with Funnel), not
        /// directly from the open internet.
        /// </summary>
        Tailscale = 2
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
        /// Gets or sets what kind of media server this entry points at. Defaults
        /// to <see cref="ServerKind.Jellyfin"/>, so every server configured
        /// before Plex support existed keeps behaving exactly as before.
        /// </summary>
        public ServerKind Kind { get; set; } = ServerKind.Jellyfin;

        /// <summary>
        /// Gets or sets the exact set of this <see cref="Kind"/> server's own
        /// library/section ids that are allowed to be synced from, or null for "no
        /// restriction on record" (a server configured before this existed, or
        /// added manually without one). Only meaningful for a non-Jellyfin peer
        /// (Plex today): unlike a Jellyfin friend, whose sharing choice is
        /// enforced remotely by their own server (see
        /// <see cref="Services.FederationPeerAccessService"/> - a library they
        /// don't share with us is simply invisible to every Peer/* call we make),
        /// a Plex access token has no per-library scope at all, so whichever
        /// libraries this admin was told are OK to sync have to be recorded and
        /// enforced on <em>this</em> side instead (see
        /// <see cref="Services.PlexCatalogProvider"/>). Null must mean "allow
        /// everything" rather than "allow nothing", or every server configured
        /// before this field existed would silently lose access to content it
        /// already had a working sync for.
        /// </summary>
        public List<string>? AllowedExternalLibraryIds { get; set; }

        /// <summary>
        /// Gets or sets the friendly name for this server.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the server URL (e.g., http://remote-jellyfin:8096).
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the federation token this friend minted for this server to
        /// use calling them - sent as the <c>X-Federation-Token</c> header on every
        /// server-to-server call to their <c>/Plugins/Federation/...</c> endpoints
        /// (see <see cref="Services.FederationTokenAuth"/>). Despite the field name
        /// (kept from before this became a custom token, to avoid an XML-shape
        /// migration), this is <em>not</em> a real Jellyfin API key and cannot
        /// authenticate against Jellyfin's own native REST API or satisfy
        /// <c>[Authorize]</c>/<c>RequiresElevation</c> - it only means anything to
        /// this plugin's own token-checking code. That is the whole point: a
        /// leaked federation token can browse/stream whatever is actually shared
        /// with this relationship and nothing else, unlike the real, full-admin-
        /// equivalent API key this field held before.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the federation token this server minted for the friend to
        /// use calling back in - the counterpart to <see cref="ApiKey"/> (theirs,
        /// for calling them). Captured at handshake time. Revocation needs no
        /// separate step: <see cref="Services.FederationTokenAuth"/> resolves an
        /// incoming caller by scanning <see cref="PluginConfiguration.RemoteServers"/>
        /// for a matching <see cref="IssuedApiKey"/>, so once this friend's
        /// <see cref="RemoteServer"/> entry is deleted (see
        /// <see cref="Services.FederationFriendService.NotifyAndRevokeOnUnfriendAsync"/>),
        /// the token they were holding stops matching anything and is rejected on
        /// their very next call - there is no external key store to keep in sync,
        /// unlike the real Jellyfin API key this field held before.
        /// </summary>
        public string IssuedApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets this server's own local item ids (this plugin's own
        /// federation-key item id space, not this friend's) that are never shared
        /// with this friend, regardless of <see cref="ShareAllLibraries"/> or any
        /// <see cref="RemoteUserAccessRule"/> - a blanket per-friend exclude list,
        /// finer-grained than <see cref="SharedLibraryFolderIds"/>'s whole-library
        /// granularity. Enforced directly by this plugin's own Peer/* endpoints
        /// (see <see cref="Services.FederationPeerAccessService"/>), so unlike
        /// <see cref="SharedLibraryFolderIds"/> this needs no dedicated local
        /// Jellyfin account to enforce.
        /// </summary>
        public List<string> ExcludedItemIds { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets a value indicating whether this server is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the user ID to authenticate as on the remote server.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets this friend's persistent federation id (their
        /// <see cref="PluginConfiguration.LocalFederationId"/>), captured when the
        /// friendship is formed. Used to match an inbound sharing-update call back
        /// to this specific friend - a URL alone is not a safe key across a rename
        /// or address change.
        /// </summary>
        public string FederationId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether every one of this server's own
        /// local libraries is shared with this friend. True by default, matching
        /// the plugin's original behavior before per-friend sharing existed. When
        /// false, only <see cref="SharedLibraryFolderIds"/> is visible to them.
        /// </summary>
        public bool ShareAllLibraries { get; set; } = true;

        /// <summary>
        /// Gets or sets the ids of this server's own local library folders shared
        /// with this friend when <see cref="ShareAllLibraries"/> is false. Ignored
        /// (and irrelevant) otherwise.
        /// </summary>
        public List<string> SharedLibraryFolderIds { get; set; } = new List<string>();

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

        /// <summary>
        /// Gets or sets per-remote-user overrides this server's admin has configured
        /// for individuals on this friend's server, narrowing what
        /// <see cref="ShareAllLibraries"/>/<see cref="SharedLibraryFolderIds"/>
        /// already exposes to them. Keyed by the friend's own local user id (fetched
        /// via <c>GetRemoteUsers</c>). This is the admin-editable source of truth on this
        /// side; whenever it changes it is pushed to the friend so their plugin can
        /// enforce it against their own users - see
        /// <see cref="Services.FederationFriendService.SetRemoteUserAccessRuleAsync"/>
        /// and <see cref="FriendUserAccessRules"/> for the mirror image of this on
        /// the receiving side.
        /// </summary>
        public List<RemoteUserAccessRule> RemoteUserAccessRules { get; set; } = new List<RemoteUserAccessRule>();

        /// <summary>
        /// Gets or sets this friend's own per-remote-user overrides about *this
        /// server's* local users, as last pushed to us by their admin - the mirror
        /// image of <see cref="RemoteUserAccessRules"/>, received rather than
        /// configured here. Enforced locally against whichever of this server's own
        /// users is actually browsing/streaming content that originated from this
        /// friend - see <see cref="Services.RemoteAccessControlService"/>. Not
        /// editable from this server's own admin UI - the friend owns it.
        /// </summary>
        public List<RemoteUserAccessRule> FriendUserAccessRules { get; set; } = new List<RemoteUserAccessRule>();

        /// <summary>
        /// Gets or sets whether this friend's federated items are allowed to be
        /// downloaded to local storage via <see cref="Services.FederationDownloadService"/>.
        /// True by default — downloads still need an authenticated admin request.
        /// </summary>
        public bool AllowDownloads { get; set; } = true;
    }

    /// <summary>
    /// How a specific remote user (an individual login on a friend's own server) is
    /// scoped, on top of whatever <see cref="RemoteServer.ShareAllLibraries"/>/
    /// <see cref="RemoteServer.SharedLibraryFolderIds"/> already exposes to that
    /// friend's server as a whole. Every mode here narrows that existing scope
    /// further - it can never grant a remote user more than the friend's
    /// server-level scope already allows.
    /// </summary>
    public enum RemoteUserAccessMode
    {
        /// <summary>
        /// No narrowing: this remote user sees whatever the friend's server-level
        /// scope already allows. Equivalent to having no rule at all - present so an
        /// existing rule (e.g. previously CertainItems) can be reset back to the
        /// default from the UI without deleting it outright.
        /// </summary>
        AllLibraries = 0,

        /// <summary>
        /// This remote user's view is intersected with a specific subset of the
        /// already-shared library folders, identified by
        /// <see cref="RemoteUserAccessRule.LibraryFolderIds"/> (this server's own
        /// folder ids, same id space as <see cref="RemoteServer.SharedLibraryFolderIds"/>).
        /// </summary>
        CertainLibraries = 1,

        /// <summary>
        /// This remote user's view is narrowed all the way down to specific items,
        /// identified by <see cref="RemoteUserAccessRule.ItemIds"/> (this server's
        /// own item ids).
        /// </summary>
        CertainItems = 2,

        /// <summary>
        /// This remote user is blocked entirely: no item or library from this server
        /// is visible to them, regardless of what the friend's server-level scope
        /// allows.
        /// </summary>
        Blocked = 3
    }

    /// <summary>
    /// One admin-configured override narrowing what a single individual login on a
    /// friend's server (identified by <see cref="RemoteUserId"/>) is allowed to
    /// see, layered on top of that friend's existing server-level sharing scope. See
    /// <see cref="RemoteServer.RemoteUserAccessRules"/>.
    /// </summary>
    public class RemoteUserAccessRule
    {
        /// <summary>
        /// Gets or sets the friend's own local user id this rule applies to, as
        /// returned by their <c>GetRemoteUsers</c> (Jellyfin's native user id on
        /// their server, not ours).
        /// </summary>
        public string RemoteUserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the friend's own display name for this user at the time the
        /// rule was created/edited. Cosmetic only (shown in this server's own admin
        /// UI) - never used for matching, since a remote username can change or be
        /// reused.
        /// </summary>
        public string RemoteUserName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets how this rule narrows the remote user's view. See
        /// <see cref="RemoteUserAccessMode"/>.
        /// </summary>
        public RemoteUserAccessMode Mode { get; set; } = RemoteUserAccessMode.AllLibraries;

        /// <summary>
        /// Gets or sets the ids of this server's own library folders this remote
        /// user may see when <see cref="Mode"/> is <see cref="RemoteUserAccessMode.CertainLibraries"/>.
        /// Ignored otherwise.
        /// </summary>
        public List<string> LibraryFolderIds { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the ids of this server's own items this remote user may see
        /// when <see cref="Mode"/> is <see cref="RemoteUserAccessMode.CertainItems"/>.
        /// Ignored otherwise.
        /// </summary>
        public List<string> ItemIds { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the maximum OfficialRating this specific remote user is
        /// allowed to see. Empty means no per-user rating ceiling — the global
        /// <see cref="IncomingContentFilter.MaxAllowedRating"/> or no ceiling at all
        /// applies instead. When both are set the stricter of the two wins.
        /// </summary>
        public string MaxAllowedRating { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether this remote user is allowed to use the Download
        /// action. False blocks only downloads — browsing/streaming may still be
        /// allowed depending on <see cref="Mode"/>/<see cref="LibraryFolderIds"/>/
        /// <see cref="ItemIds"/>.
        /// </summary>
        public bool AllowDownload { get; set; } = true;

        /// <summary>
        /// Gets or sets this server's own item ids that are hidden from this
        /// specific remote user regardless of <see cref="Mode"/> - a deny-list
        /// layered on top, for quickly hiding one item from one person without
        /// reconfiguring their whole access mode (their <see cref="Mode"/> might be
        /// <see cref="RemoteUserAccessMode.AllLibraries"/>, which has no other way
        /// to exclude a single item without narrowing everything else they can see
        /// too). Ignored when <see cref="Mode"/> is
        /// <see cref="RemoteUserAccessMode.Blocked"/> (already hidden from
        /// everything). Checked in
        /// <see cref="Services.FederationPeerAccessService.IsItemVisible(RemoteServer, string?, System.Guid, string?)"/>.
        /// </summary>
        public List<string> BlockedItemIds { get; set; } = new List<string>();
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

        /// <summary>
        /// Gets or sets the pool this request is introducing the recipient to, or
        /// null for an ordinary direct friend request. Set by
        /// <see cref="Services.FederationFriendService.SendPoolInviteAsync"/>; when
        /// present, accepting this request also joins the named pool and fans out
        /// friend requests to every other member in <see cref="PoolRoster"/> -
        /// see <see cref="Services.FederationFriendService.AcceptFriendRequestAsync"/>.
        /// </summary>
        public string? PoolId { get; set; }

        /// <summary>Gets or sets the pool's display name.</summary>
        public string? PoolName { get; set; }

        /// <summary>Gets or sets the persistent federation id of the pool's owner.</summary>
        public string? PoolOwnerFederationId { get; set; }

        /// <summary>Gets or sets the pool owner's display name.</summary>
        public string? PoolOwnerName { get; set; }

        /// <summary>
        /// Gets or sets the pool's membership as known by the sender at the time this
        /// request was sent - used to fan out connections to the rest of the pool on
        /// accept, not kept in sync afterward.
        /// </summary>
        public List<PoolMember>? PoolRoster { get; set; }
    }

    /// <summary>
    /// A multi-server pool: a named group of Federation servers who all federate
    /// with every other member. Membership is admin-curated - one admin creates the
    /// pool and invites specific servers into it - but every pairwise connection
    /// still goes through the ordinary friend-request handshake, so joining a pool
    /// never connects two servers without a human on each side clicking Accept.
    /// Each member keeps its own best-effort copy of the roster; there is no
    /// central server or consensus protocol reconciling them.
    /// </summary>
    public class FederationPool
    {
        /// <summary>Gets or sets the pool's id, shared by every member's copy.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Gets or sets the pool's display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this server created the pool.
        /// Informational only (e.g. for the UI to show "you own this pool") - every
        /// member can invite new servers in, ownership does not gate that.
        /// </summary>
        public bool IsOwner { get; set; }

        /// <summary>Gets or sets the owner's persistent federation id.</summary>
        public string OwnerFederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the owner's display name.</summary>
        public string OwnerName { get; set; } = string.Empty;

        /// <summary>Gets or sets this server's best-effort view of the pool's members.</summary>
        public List<PoolMember> Members { get; set; } = new List<PoolMember>();

        /// <summary>
        /// Gets or sets a small icon for the pool, as a base64-encoded image (no
        /// data: prefix). Purely cosmetic, set by any member and spread to the rest
        /// peer-to-peer through the same roster-sync notice used for membership
        /// changes - there is no central image host, this rides the existing
        /// gossip-only channel. Null/empty means no icon set.
        /// </summary>
        public string? IconBase64 { get; set; }
    }

    /// <summary>
    /// A server's entry in a <see cref="FederationPool"/>'s roster.
    /// </summary>
    public class PoolMember
    {
        /// <summary>Gets or sets the member's persistent federation id.</summary>
        public string FederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the member's display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the member's server address.</summary>
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// A pending invite to join a pool, extended by one already-connected friend to
    /// another. Reused for both directions the same way <see cref="FriendRequest"/>
    /// is: on <see cref="PluginConfiguration.OutgoingPoolInvites"/>,
    /// <see cref="RemoteServerUrl"/>/<see cref="RemoteServerName"/>/
    /// <see cref="RemoteServerId"/> identify the friend being invited; on
    /// <see cref="PluginConfiguration.IncomingPoolInvites"/> they identify the
    /// friend who sent the invite. Unlike a brand-new contact (who joins a pool by
    /// accepting an ordinary <see cref="FriendRequest"/> carrying pool fields),
    /// this exists specifically for the "we're already friends" fast path, which
    /// used to skip consent entirely - see
    /// <see cref="Services.FederationFriendService.AddExistingFriendToPoolAsync"/>.
    /// </summary>
    public class PoolInvite
    {
        /// <summary>Gets or sets the id shared by both sides of the invite.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Gets or sets the pool's id.</summary>
        public string PoolId { get; set; } = string.Empty;

        /// <summary>Gets or sets the pool's display name.</summary>
        public string PoolName { get; set; } = string.Empty;

        /// <summary>Gets or sets the persistent federation id of the pool's owner.</summary>
        public string OwnerFederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the pool owner's display name.</summary>
        public string OwnerName { get; set; } = string.Empty;

        /// <summary>Gets or sets the other friend's server address.</summary>
        public string RemoteServerUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets the other friend's display name.</summary>
        public string RemoteServerName { get; set; } = string.Empty;

        /// <summary>Gets or sets the other friend's persistent federation id.</summary>
        public string RemoteServerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the pool's membership as known by the sender at the time
        /// this invite was sent.
        /// </summary>
        public List<PoolMember> Roster { get; set; } = new List<PoolMember>();

        /// <summary>Gets or sets the pool's icon at the time this invite was sent, if any.</summary>
        public string? IconBase64 { get; set; }

        /// <summary>Gets or sets when the invite was created.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
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

    /// <summary>
    /// Receiving-side filter: what this server will accept from any friend,
    /// before per-friend sharing scope is evaluated. All fields are ANDed and
    /// empty means "allow everything" — so upgrading never hides content until
    /// an admin actually sets a filter.
    /// </summary>
    public class IncomingContentFilter
    {
        /// <summary>
        /// Gets or sets the set of item types this server will pull in. Empty
        /// means allow every type. Values match <see cref="FederatedCacheEntry.ItemType"/>
        /// ("Movie", "Series", "Episode", "MusicAlbum", "Audio", "MusicVideo", "Book",
        /// "Photo", "Video", "BoxSet").
        /// </summary>
        public List<string> AllowedItemTypes { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the maximum OfficialRating this server will pull in.
        /// Empty means no ceiling. Compared by rank — G &lt; PG &lt; PG-13 &lt; R &lt; NC-17
        /// and TV-Y &lt; TV-Y7 &lt; TV-G &lt; TV-PG &lt; TV-14 &lt; TV-MA. An item whose
        /// rating is not in the known ranking is allowed through (fail open rather
        /// than silently hiding foreign-rating content).
        /// </summary>
        public string MaxAllowedRating { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets tags that, if present on an item, cause it to be skipped.
        /// Case-insensitive exact match against <see cref="FederatedItemMetadata.Tags"/>.
        /// Empty means no tag filtering.
        /// </summary>
        public List<string> BlockedTags { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets genres that, if present on an item, cause it to be skipped.
        /// Case-insensitive exact match against <see cref="FederatedItemMetadata.Genres"/>.
        /// Empty means no genre filtering.
        /// </summary>
        public List<string> BlockedGenres { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets a value indicating whether downloads (server-side fetches via
        /// <see cref="Services.FederationDownloadService"/>) are allowed at all. When
        /// false the detail-page Download action and the dashboard's Downloads section
        /// still render but return 403. True by default — downloads never bypass
        /// per-friend/per-user sharing scope; this is one more admin gate on top.
        /// </summary>
        public bool AllowDownloads { get; set; } = true;
    }
}
