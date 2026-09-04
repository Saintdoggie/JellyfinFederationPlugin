using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Jellyfin.Plugin.Federation.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// API controller for federation plugin: servers, mappings, refresh, streaming, diagnostics.
    /// All data and mutating endpoints require an elevated (admin) session. The only
    /// anonymous endpoints are the static config page markup and capability-bounded stream
    /// proxy (clients fetch media source URLs without Jellyfin auth headers).
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public class FederationController : ControllerBase
    {
        /// <summary>
        /// The first plugin version whose Peer/* endpoints accept a scoped
        /// federation token instead of a real Jellyfin API key (see
        /// <see cref="Services.FederationTokenAuth"/>). Used only to turn a
        /// remote's reported plugin version into a human-readable reason in
        /// <see cref="TestServer"/> - not part of the actual compatibility gate
        /// itself, which is the <c>SupportsFederationToken</c> flag exchanged
        /// live during the friend-request handshake
        /// (see <see cref="Services.FederationFriendService"/>).
        /// </summary>
        private static readonly Version MinimumFederationTokenVersion = new(0, 0, 70);

        /// <summary>
        /// Upper bound for one internal page fetch while filling a single
        /// <see cref="GetPeerItems"/> response. The response is filled through as
        /// many of these as filtering requires, so a caller's page never comes
        /// back short just because some raw items were filtered out of it.
        /// </summary>
        private const int InternalPeerItemPageSize = 500;

        private readonly ILogger<FederationController> _logger;
        private readonly FederationSyncService _syncService;
        private readonly FederationLibraryManager _federationManager;
        private readonly LibraryProvisioningService _provisioning;
        private readonly FederationStreamHandler _streamHandler;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly FederationItemCache _cache;
        private readonly FederationItemPersistenceService _persistence;
        private readonly WanBandwidthMonitor _bandwidthMonitor;
        private readonly FederationFriendService _friends;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly FederationDownloadService _downloadService;
        private readonly FederationPlaybackTokenService _playbackTokens;
        private readonly FederationUserSessionTokenService _userSessionTokens;
        private readonly FederationPeerAccessService _peerAccess;
        private readonly IServerApplicationHost _applicationHost;
        private readonly FederationNowWatchingService _nowWatching;
        private readonly ExternalCatalogRegistry _externalCatalogs;
        private readonly TailscaleService _tailscale;
        private readonly ITaskManager _taskManager;
        private readonly FederationQualityAdvisorService _qualityAdvisor;

        public FederationController(
            ILogger<FederationController> logger,
            FederationSyncService syncService,
            FederationLibraryManager federationManager,
            LibraryProvisioningService provisioning,
            FederationStreamHandler streamHandler,
            IRemoteServerClientFactory clientFactory,
            FederationItemCache cache,
            FederationItemPersistenceService persistence,
            WanBandwidthMonitor bandwidthMonitor,
            FederationFriendService friends,
            ILibraryManager libraryManager,
            IUserManager userManager,
            FederationDownloadService downloadService,
            FederationPlaybackTokenService playbackTokens,
            FederationUserSessionTokenService userSessionTokens,
            FederationPeerAccessService peerAccess,
            IServerApplicationHost applicationHost,
            FederationNowWatchingService nowWatching,
            ExternalCatalogRegistry externalCatalogs,
            TailscaleService tailscale,
            ITaskManager taskManager,
            FederationQualityAdvisorService qualityAdvisor)
        {
            _logger = logger;
            _syncService = syncService;
            _federationManager = federationManager;
            _provisioning = provisioning;
            _streamHandler = streamHandler;
            _clientFactory = clientFactory;
            _cache = cache;
            _persistence = persistence;
            _bandwidthMonitor = bandwidthMonitor;
            _userManager = userManager;
            _friends = friends;
            _libraryManager = libraryManager;
            _downloadService = downloadService;
            _playbackTokens = playbackTokens;
            _userSessionTokens = userSessionTokens;
            _peerAccess = peerAccess;
            _applicationHost = applicationHost;
            _nowWatching = nowWatching;
            _externalCatalogs = externalCatalogs;
            _tailscale = tailscale;
            _taskManager = taskManager;
            _qualityAdvisor = qualityAdvisor;
        }

        /// <summary>
        /// Pushes this plugin's own scheduled task's trigger back in sync with
        /// whatever RefreshIntervalHours was just saved. IScheduledTaskWorker.
        /// Triggers is only ever seeded from GetDefaultTriggers() the very first
        /// time a task is registered - every save afterward is otherwise
        /// invisible to Jellyfin's task scheduler, since it treats a task's live
        /// trigger set as admin-owned state from then on, not something to keep
        /// re-deriving. Without this, changing "Refresh interval" in this
        /// plugin's own Advanced settings silently did nothing after first
        /// install; the admin had to go find "Refresh Federation Cache" under
        /// Jellyfin's own Dashboard - Scheduled Tasks and edit its trigger by
        /// hand to actually change the cadence. Best-effort: a failure here just
        /// means the interval takes until a server restart to apply, same as
        /// before this existed, so it never blocks the save itself.
        /// </summary>
        private void SyncRefreshTaskTrigger()
        {
            try
            {
                var worker = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FederationRefreshTask);
                if (worker?.ScheduledTask is not FederationRefreshTask task)
                {
                    return;
                }

                worker.Triggers = task.GetDefaultTriggers().ToList();
                worker.ReloadTriggerEvents();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not refresh the live schedule for the refresh task after a config change");
            }
        }

        /// <summary>
        /// Reads <see cref="RemoteServerClient.RemoteUserIdHeader"/> from the
        /// current request - which of the calling friend's own local users
        /// triggered this call, if they sent it. Used by every Peer/* endpoint
        /// (and <see cref="IssuePlaybackToken"/>) to evaluate a
        /// <see cref="RemoteUserAccessRule"/> via <see cref="_peerAccess"/>; null
        /// (evaluated as "no per-user rule applies, fall back to the caller's
        /// whole-relationship scope") when the caller doesn't send it.
        /// </summary>
        private string? RequestingRemoteUserId()
        {
            return Request.Headers.TryGetValue(RemoteServerClient.RemoteUserIdHeader, out var values)
                ? values.ToString()
                : null;
        }

        #region Configuration

        /// <summary>
        /// Serves the static configuration page markup (contains no secrets).
        /// </summary>
        [HttpGet("Config")]
        [AllowAnonymous]
        [Produces("text/html")]
        public IActionResult GetConfigPage()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "Jellyfin.Plugin.Federation.Configuration.configPage.html";
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    return NotFound("Configuration page resource not found");
                }

                using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
                var html = reader.ReadToEnd();

                // No caching headers were set here before, which left it up to each
                // browser's own heuristics whether a re-visit re-fetched this page or
                // served a stale copy from its HTTP cache - a real, previously-hit
                // source of "I shipped a fix but it doesn't look like it's there"
                // confusion (see WebClientInjector's own permission-bug history).
                // Explicit no-store removes that ambiguity entirely: this page is
                // small, admin-only, and always current when reloaded.
                Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                Response.Headers.Pragma = "no-cache";
                return Content(html, "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error serving config page");
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the plugin configuration with API keys stripped (HasApiKey flags instead).
        /// </summary>
        [HttpGet("Configuration")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult<object> GetConfiguration()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return Ok(Sanitize(config));
        }

        [HttpPost("Configuration")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> UpdateConfiguration([FromBody] PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (config == null)
            {
                return BadRequest(new { error = "Configuration is required" });
            }

            try
            {
                // Preserve existing API keys: the GET endpoint never serializes them,
                // so an empty key in the POST body means "unchanged".
                var existing = Plugin.Instance?.Configuration;
                foreach (var server in config.RemoteServers ?? new List<RemoteServer>())
                {
                    if (string.IsNullOrEmpty(server.ApiKey))
                    {
                        var old = existing?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id);
                        if (old != null)
                        {
                            server.ApiKey = old.ApiKey;
                        }
                    }

                    // The config page's main Save form (see saveConfiguration() in
                    // configPage.html) only ever POSTs the explicit field list it
                    // knows how to edit - every other per-server field managed
                    // exclusively through its own dedicated endpoint (sharing via
                    // Friends/{id}/Sharing, per-remote-user overrides via
                    // Friends/{id}/RemoteUserRule, friendship identity via the
                    // Friends/* handshake) would otherwise silently reset to its C#
                    // default on every unrelated main Save, same class of bug the
                    // top-level-field preservation block below this loop already
                    // guards against.
                    var oldServer = existing?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id);
                    if (oldServer != null)
                    {
                        server.FederationId = oldServer.FederationId;
                        server.ShareAllLibraries = oldServer.ShareAllLibraries;
                        server.SharedLibraryFolderIds = oldServer.SharedLibraryFolderIds;
                        server.RemoteUserAccessRules = oldServer.RemoteUserAccessRules;
                        server.FriendUserAccessRules = oldServer.FriendUserAccessRules;
                        server.ExcludedItemIds = oldServer.ExcludedItemIds;
                        server.AllowDownloads = oldServer.AllowDownloads;

                        // IssuedApiKey (the federation token this server minted for the
                        // friend, added alongside the token-rewrite) was missing from
                        // this list entirely - same "silently reset on every unrelated
                        // save" class of bug this whole block exists to guard against,
                        // except for a field where losing it is actively dangerous
                        // rather than just annoying: once wiped, FederationTokenAuth.
                        // ResolveCaller can never match this friend's incoming requests
                        // again (it skips any entry with an empty IssuedApiKey), so every
                        // Peer/* call and PlaybackToken/RegisterUserSession request they
                        // ever make comes back 401 - indistinguishable from a genuinely
                        // revoked/incompatible-version friend - until the whole
                        // friendship is torn down and re-established from scratch.
                        // Confirmed live: a single config save broke an otherwise-working
                        // fresh handshake this same session.
                        server.IssuedApiKey = oldServer.IssuedApiKey;

                        // Kind is set once at creation (Servers POST) and has no field
                        // in the main Save form - same class of bug as the rest of this
                        // block. Without this, every unrelated config save silently
                        // reset every Plex (or other non-Jellyfin) server back to
                        // ServerKind.Jellyfin, since that's the property's C# default
                        // when the incoming JSON has no Kind at all. Confirmed live:
                        // this happened to a freshly-added Plex server within the same
                        // session it was created in.
                        server.Kind = oldServer.Kind;

                        // Same class of bug as Kind immediately above, for the same
                        // reason: no field in the main Save form, so without this an
                        // already-recorded allow-list would silently reset to null
                        // (meaning "allow everything") on every unrelated save -
                        // exactly the sharing-consent regression this field exists
                        // to prevent, just triggered by Save instead of never being
                        // enforced at all.
                        server.AllowedExternalLibraryIds = oldServer.AllowedExternalLibraryIds;
                    }
                }

                // Preserve server-internal state the config page's UI has no field
                // for and never sends: the config page builds its POST body from an
                // explicit field list (see saveConfiguration() in configPage.html),
                // so anything added to PluginConfiguration since then silently reset
                // to its C# default on every save - including mid-migration, which
                // made the tiered-creation migration re-trigger (deleting and
                // recreating every federated item again) on the very next sync after
                // any unrelated config save.
                if (existing != null)
                {
                    config.MigratedTieredCreationV4 = existing.MigratedTieredCreationV4;
                    config.MigratedSeasonIndexV5 = existing.MigratedSeasonIndexV5;
                    config.MigratedRemoteLocationV6 = existing.MigratedRemoteLocationV6;
                    config.MigratedRemotePathV7 = existing.MigratedRemotePathV7;
                    config.MigratedStockTypesV8 = existing.MigratedStockTypesV8;

                    // These two migration flags were added after the block above and
                    // never wired into it (found while restoring the friend system
                    // below, same "silently reset" class of bug the comment above
                    // describes) - without this, every config save from the UI would
                    // re-trigger a full item rebuild of every WAN-capped/shortcut-
                    // affected federated item on the very next sync.
                    config.MigratedRemoveShortcutV9 = existing.MigratedRemoveShortcutV9;
                    config.MigratedPlaceholderPathV10 = existing.MigratedPlaceholderPathV10;
                    config.MigratedContainerV11 = existing.MigratedContainerV11;

                    // Friend state and this server's identity are likewise server-
                    // internal - the config page never sends them, so without this
                    // every save would wipe pending friend requests and mint a new
                    // federation identity out from under any friend who already has
                    // the old one recorded.
                    config.LocalFederationId = existing.LocalFederationId;
                    config.IncomingFriendRequests = existing.IncomingFriendRequests;
                    config.OutgoingFriendRequests = existing.OutgoingFriendRequests;

                    // Pools are managed through their own Pools/* endpoints (create,
                    // invite, leave), never sent by the config page's main Save form -
                    // same class of field as the friend-request lists above.
                    config.Pools = existing.Pools;

                    // Same class of field again, found while adding the two above:
                    // pending pool invites were never preserved, so any unrelated
                    // config save silently dropped every invite a friend was still
                    // deciding on (and every invite this server itself had sent and
                    // was still waiting on) with no way to get it back.
                    config.OutgoingPoolInvites = existing.OutgoingPoolInvites;
                    config.IncomingPoolInvites = existing.IncomingPoolInvites;

                    // Set through its own Setup/Connectivity endpoint, not the main
                    // Save form - same class of field as Pools above.
                    config.ConnectivityMode = existing.ConnectivityMode;

                    // This server's own real Jellyfin API key, minted once (see
                    // FederationFriendService.GetOrCreateInternalRelayApiKeyAsync) and
                    // used only locally, over loopback, to relay Direct-mode playback
                    // requests. Never sent by the config page - without this, every
                    // unrelated save wipes it back to empty, so the next relay request
                    // treats it as never-created and mints a brand new real API key,
                    // abandoning the old one for good. Confirmed live: three of these
                    // orphaned in a single day of ordinary config saves.
                    config.InternalRelayApiKey = existing.InternalRelayApiKey;

                    // Same class of field again: the hide list is managed exclusively
                    // through the HiddenItems/* endpoints (the item detail page's Hide
                    // chip, and Unhide in the config page's own Hidden Items section),
                    // never sent as part of the main Save form - without this, saving
                    // any unrelated setting would silently un-hide everything.
                    config.HiddenFederatedItemIds = existing.HiddenFederatedItemIds;
                    config.IncomingFilter = existing.IncomingFilter ?? new IncomingContentFilter();
                    config.MigratedIncomingFilterV12 = existing.MigratedIncomingFilterV12;

                    // Same class of field again, the sending-side counterpart to
                    // HiddenFederatedItemIds above: managed exclusively through
                    // the Sharing/Disable, Sharing/Enable and Sharing/Excluded
                    // endpoints (see the "Outgoing sharing" region below), never
                    // sent by the main Save form. Without this, an item an admin
                    // explicitly stopped sharing with every friend would silently
                    // become visible to all of them again on the next unrelated
                    // config save - enforced in FederationPeerAccessService.IsItemVisible.
                    config.GloballyExcludedItemIds = existing.GloballyExcludedItemIds;

                    // Managed by the dedicated QualityUpgrades/Exclude endpoints,
                    // not serialized back by the main settings form.
                    config.QualityUpgradeExcludedItemIds = existing.QualityUpgradeExcludedItemIds;
                }

                // DedupProviderIds is a free-form comma-separated text field on the
                // Advanced tab. Without normalisation, repeated saves with the default
                // "imdb,tmdb,tvdb" value grew to 84 entries (28× the default) on at
                // least one live install, because the list was never de-duplicated and
                // the raw user input was saved verbatim each time. Normalise here so
                // any existing bloat is cleaned on the very next save, and future
                // saves cannot re-introduce it.
                if (config.DedupProviderIds != null)
                {
                    config.DedupProviderIds = config.DedupProviderIds
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (config.DedupProviderIds.Count == 0)
                    {
                        config.DedupProviderIds = new List<string> { "imdb", "tmdb", "tvdb" };
                    }
                }

                var errors = ConfigValidator.Validate(config);
                if (errors.Count > 0)
                {
                    return BadRequest(new { error = "Invalid configuration", details = errors });
                }

                _logger.LogInformation("[Federation] Updating configuration with {ServerCount} servers", config.RemoteServers?.Count ?? 0);
                Plugin.Instance?.UpdateConfiguration(config);
                _clientFactory.InvalidateAll();
                SyncRefreshTaskTrigger();

                // A mapping deleted from config used to orphan everything it ever
                // created, forever: its library folder, every virtual item in it,
                // and all its cache entries - nothing ever looked at names that
                // were no longer in the config (ClearMapping had no production
                // caller at all). Clean them up here instead: clear the cache
                // entries, reconcile (which deletes every persisted item whose
                // cache entry is gone - see FederationItemPersistenceService),
                // then remove/detach the provisioned library itself.
                var removedMappingNames = (existing?.LibraryMappings ?? new List<LibraryMapping>())
                    .Select(m => m.LocalLibraryName)
                    .Except((config.LibraryMappings ?? new List<LibraryMapping>()).Select(m => m.LocalLibraryName), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var removedName in removedMappingNames)
                {
                    try
                    {
                        _cache.ClearMapping(removedName);
                        await _persistence.ReconcileMappingAsync(new LibraryMapping { LocalLibraryName = removedName }, cancellationToken).ConfigureAwait(false);
                        await _provisioning.RemoveLibraryAsync(removedName).ConfigureAwait(false);
                        _logger.LogInformation("[Federation] Removed library mapping {Name}: deleted its federated items and library", removedName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Cleanup after removing mapping {Name} failed; leftovers will be retried on the next sync", removedName);
                    }
                }

                if (removedMappingNames.Count > 0)
                {
                    await _cache.SaveAsync(cancellationToken).ConfigureAwait(false);
                }

                if (config.AutoProvisionLibraries)
                {
                    await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);
                }

                return Ok(new { success = true, message = "Configuration updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error updating configuration");
                return StatusCode(500, new { error = "Failed to update configuration", message = ex.Message });
            }
        }

        #endregion

        #region Web client badge

        /// <summary>
        /// Serves the client-side script that draws a small icon next to federated
        /// items' titles in jellyfin-web, injected into index.html by
        /// <see cref="Services.WebClientInjector"/>. Static asset containing no
        /// secrets, so anonymous like the config page above.
        /// </summary>
        [HttpGet("ClientScript")]
        [AllowAnonymous]
        [Produces("application/javascript")]
        public IActionResult GetClientScript()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "Jellyfin.Plugin.Federation.Web.federation-badge.js";
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    return NotFound("Client script resource not found");
                }

                using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
                var js = reader.ReadToEnd();

                // Same reasoning as GetConfigPage's no-store: this is loaded into
                // every page of jellyfin-web via a plain <script src> with no
                // cache-busting query param, so a browser is otherwise free to keep
                // serving an old cached copy indefinitely after an upgrade.
                Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                Response.Headers.Pragma = "no-cache";
                return Content(js, "application/javascript; charset=utf-8");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error serving client script");
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the local item ids (format "N") of every currently federated
        /// item, for <see cref="GetClientScript"/> to badge in the UI. Ids only, no
        /// other item data, so anonymous is fine here too.
        /// </summary>
        [HttpGet("FederatedIds")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult<object> GetFederatedIds()
        {
            var config = Plugin.Instance?.Configuration;

            // Carries the source server's display name alongside each id so the
            // in-page badge can say *which* server a title comes from, rather than
            // only that it is "from somewhere else" - that is the useful half of the
            // information and what makes showing a badge worth the pixels.
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _federationManager.GetAllEntries())
            {
                var id = _federationManager.ComputeItemId(entry).ToString("N");
                if (items.ContainsKey(id))
                {
                    continue;
                }

                var primary = entry.GetPrimarySource();
                var server = primary == null
                    ? null
                    : config?.RemoteServers?.FirstOrDefault(s => s.Id == primary.ServerId);
                items[id] = server?.Name ?? string.Empty;
            }

            return Ok(items);
        }

        /// <summary>
        /// Non-sensitive presentation preferences consumed by the injected
        /// jellyfin-web client. Anonymous read is safe because this returns one
        /// cosmetic boolean and no server/item/user data; changing it still
        /// requires the elevated configuration endpoint.
        /// </summary>
        [HttpGet("ClientSettings")]
        [AllowAnonymous]
        public IActionResult GetClientSettings()
        {
            var config = Plugin.Instance?.Configuration;
            return Ok(new
            {
                showFederatedCloudBadges = config?.ShowFederatedCloudBadges == true
            });
        }

        #endregion

        #region System Info

        [HttpGet("SystemInfo")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetSystemInfo()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            return Ok(new
            {
                detectedUrl = config.ServerUrl,
                requestUrl = $"{Request.Scheme}://{Request.Host}",
                cachePath = !string.IsNullOrEmpty(config.CachePath)
                    ? config.CachePath
                    : Plugin.Instance?.GetDefaultCachePath(),
                // Never refreshed comes back as DateTime.MinValue, not null - see the
                // matching comment in GetStatus() above.
                lastRefresh = _federationManager.Cache.LastRefresh == DateTime.MinValue
                    ? (DateTime?)null
                    : _federationManager.Cache.LastRefresh,
                cacheEntries = _federationManager.Cache.Count,
                autoProvisionLibraries = config.AutoProvisionLibraries,
                enableDedup = config.EnableDedup,
                dedupProviderIds = config.DedupProviderIds,
                refreshIntervalHours = config.RefreshIntervalHours,
                // Assembly version (the csproj Version, same one shipped in
                // meta.json) for the config page's footer - the runtime source
                // of truth for "what am I actually running", rather than the
                // page guessing from a hardcoded string that would drift. The
                // plugin auto-updates through Jellyfin's own plugin catalog
                // whenever the manifest ships a newer version, so the footer
                // doubles as the "did my update land yet?" check.
                pluginVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            });
        }

        /// <summary>
        /// Returns the plugin's own current assembly version, for the config
        /// page's footer ("plugin vX.Y.Z" line + releases link). Separate tiny
        /// endpoint rather than overloading <see cref="GetSystemInfo"/> with
        /// it: that shape is mirrored in tests and consumed piecemeal, and a
        /// version-only route can also be added to the config page's initial
        /// load without pulling the whole diagnostics payload.
        /// </summary>
        [HttpGet("PluginVersion")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetPluginVersion()
        {
            return Ok(new
            {
                version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"
            });
        }

        #endregion

        #region Server Management

        /// <summary>
        /// Tests reachability and credential validity for a configured server.
        /// A Jellyfin friend goes through the full Peer/SystemInfo path below;
        /// a non-Jellyfin server (Plex) is routed through its
        /// <see cref="IExternalCatalogProvider"/> instead, since it has no
        /// Peer/* endpoints to ask - previously a Plex server here could only
        /// ever report "Failed to connect".
        /// </summary>
        [HttpPost("TestServer")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> TestServer([FromBody] RemoteServer server, CancellationToken cancellationToken)
        {
            if (server == null || string.IsNullOrWhiteSpace(server.Url))
            {
                return BadRequest(new { success = false, message = "Server URL is required" });
            }

            // The config page never holds saved federation tokens; when testing
            // an existing server with a blank one, fall back to the stored one.
            var configured = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id);
            if (string.IsNullOrEmpty(server.ApiKey) && configured != null)
            {
                server.ApiKey = configured.ApiKey;
            }

            if (string.IsNullOrWhiteSpace(server.ApiKey))
            {
                // Never something an admin types in - a federation token is only
                // ever minted automatically during the friend-request handshake.
                // Reaching this means the handshake for this entry never actually
                // completed (or its token was since revoked) - send/accept a
                // friend request rather than editing this field by hand. For a
                // non-Jellyfin server (Plex) the same field holds that server's
                // own token instead, which IS pasted by hand from the Plex card,
                // so it gets its own wording rather than friend-handshake advice.
                return BadRequest(new
                {
                    success = false,
                    message = configured?.Kind != ServerKind.Jellyfin
                        ? "No token on file for this server yet - paste one into the Plex servers card, or re-add the server there."
                        : "No federation token on file for this friend yet - send or accept a friend request first, it's minted automatically."
                });
            }

            if (!ConfigValidator.IsValidServerUrl(server.Url))
            {
                return BadRequest(new { success = false, message = "Server URL must be an absolute http(s) URL" });
            }

            // A non-Jellyfin server (Plex today) doesn't speak this plugin's own
            // Peer/* protocol at all, so the RemoteServerClient path below can
            // only ever fail against it - route through the same external-
            // catalog abstraction sync/streaming already use instead, so the
            // Test button (both the People row's and the Plex card's) gives a
            // real reachable/token-valid answer for a Plex server too.
            if (configured != null && configured.Kind != ServerKind.Jellyfin)
            {
                var externalProvider = _externalCatalogs.For(configured);
                if (externalProvider == null)
                {
                    return Ok(new { success = false, message = $"This server kind ({configured.Kind}) isn't supported." });
                }

                var friendlyName = await externalProvider.TestConnectionAsync(server, cancellationToken).ConfigureAwait(false);
                if (friendlyName == null)
                {
                    return Ok(new { success = false, message = $"Could not reach {server.Name}, or its token was rejected. Check the address is reachable from this server and the token is still valid." });
                }

                return Ok(new
                {
                    success = true,
                    message = "Connection successful",
                    serverInfo = new
                    {
                        name = friendlyName,
                        version = "-",
                        operatingSystem = configured.Kind.ToString(),
                        serverId = configured.Id,
                        suggestedUserId = (string?)null,
                        federationPluginVersion = (string?)null
                    }
                });
            }

            try
            {
                using var client = new RemoteServerClient(server, _logger);
                if (!await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return Ok(new { success = false, message = "Failed to connect to server" });
                }

                var (systemInfo, systemInfoError) = await client.GetSystemInfoDetailedAsync(cancellationToken).ConfigureAwait(false);
                if (systemInfo == null)
                {
                    // A friend still on a pre-federation-token plugin version has
                    // no Peer/SystemInfo route at all - but Jellyfin's own routing
                    // returns 401 for that (not 404, which GetSystemInfoDetailedAsync's
                    // own reason-mapping assumed), so this looked identical to a
                    // genuinely invalid/revoked token, confirmed live against a
                    // real old-version friend. GetRemoteFederationPluginVersionAsync
                    // uses a route that has existed since well before the
                    // federation-token rewrite and works regardless of version or
                    // token state, so it can tell the two apart here - the one
                    // place a person setting up or diagnosing a connection
                    // actually needs that distinction, not just success/failure.
                    var remoteVersion = await client.GetRemoteFederationPluginVersionAsync(cancellationToken).ConfigureAwait(false);
                    string message;
                    if (!string.IsNullOrEmpty(remoteVersion)
                        && Version.TryParse(remoteVersion, out var parsedVersion)
                        && parsedVersion < MinimumFederationTokenVersion)
                    {
                        message = $"{server.Name} is running Federation v{remoteVersion}, which predates the federation-token security update in v{MinimumFederationTokenVersion} - both sides need to be on a compatible version to connect. Ask them to update the plugin.";
                    }
                    else
                    {
                        message = systemInfoError != null
                            ? $"Connected, but failed to get system info: {systemInfoError}"
                            : "Connected but failed to get system info";
                    }

                    return Ok(new { success = false, message });
                }

                string? userId = server.UserId;
                if (string.IsNullOrEmpty(userId))
                {
                    // Prefer an administrator: a non-admin account can be restricted to
                    // specific libraries or have playback disabled entirely, which lets
                    // items sync/browse fine but breaks playback later. Suggesting an
                    // admin by default avoids steering users into that trap.
                    var users = await client.GetUsersAsync(cancellationToken).ConfigureAwait(false);
                    userId = (users?.FirstOrDefault(u => u.IsAdministrator) ?? users?.FirstOrDefault())?.Id;
                }

                // Surfaced so a version mismatch between friends (e.g. one side
                // stuck on an old release with a since-fixed sync bug) is visible
                // right on the test-connection result, instead of only showing up
                // later as a confusing "sync isn't working" report.
                var federationPluginVersion = await client.GetRemoteFederationPluginVersionAsync(cancellationToken).ConfigureAwait(false);

                return Ok(new
                {
                    success = true,
                    message = "Connection successful",
                    serverInfo = new
                    {
                        name = systemInfo.ServerName,
                        version = systemInfo.Version,
                        operatingSystem = systemInfo.OperatingSystem,
                        serverId = systemInfo.Id,
                        suggestedUserId = userId,
                        federationPluginVersion
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Fetches the live list of user accounts from a remote server, so the config
        /// page can offer a picker instead of the admin having to paste a raw user
        /// GUID from the remote's dashboard. Mirrors <see cref="TestServer"/>'s
        /// handling of an unsaved-but-filled-in server form (blank ApiKey + known Id
        /// falls back to the stored key) so it works both while adding a server and
        /// while editing one already saved.
        /// </summary>
        [HttpPost("GetRemoteUsers")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> GetRemoteUsers([FromBody] RemoteServer server, CancellationToken cancellationToken)
        {
            if (server == null || string.IsNullOrWhiteSpace(server.Url))
            {
                return BadRequest(new { success = false, message = "Server URL is required" });
            }

            if (string.IsNullOrEmpty(server.ApiKey) && !string.IsNullOrEmpty(server.Id))
            {
                var configured = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id);
                if (configured != null)
                {
                    server.ApiKey = configured.ApiKey;
                }
            }

            // Per-remote-user overrides are pushed to the friend's own server
            // through this plugin's Peer/* endpoints (see SetRemoteUserAccessRule)
            // - a non-Jellyfin server (Plex) has none, so asking it for a user
            // list can only ever produce a confusing "failed to connect" a
            // moment later. Say what's actually wrong instead.
            var kindOnFile = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id);
            if (kindOnFile != null && kindOnFile.Kind != ServerKind.Jellyfin)
            {
                return Ok(new { success = false, message = "Per-user permissions only apply to Jellyfin friends - a Plex server's own logins aren't visible through this plugin." });
            }

            if (string.IsNullOrWhiteSpace(server.ApiKey))
            {
                // Same reasoning as TestServer's Plex-aware wording above: for a
                // non-Jellyfin server the "token" is pasted by hand from that
                // server's own card, not minted by the friend handshake.
                var configuredKind = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id)?.Kind;
                return BadRequest(new
                {
                    success = false,
                    message = configuredKind != ServerKind.Jellyfin
                        ? "No token on file for this server yet - paste one into the Plex servers card, or re-add the server there."
                        : "No federation token on file for this friend yet - send or accept a friend request first, it's minted automatically."
                });
            }

            try
            {
                using var client = new RemoteServerClient(server, _logger);
                var users = await client.GetUsersAsync(cancellationToken).ConfigureAwait(false);
                if (users == null)
                {
                    return Ok(new { success = false, message = "Failed to fetch users from server" });
                }

                return Ok(new
                {
                    success = true,
                    users = users.Select(u => new { id = u.Id, name = u.Name, isAdministrator = u.IsAdministrator }).ToList()
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Returns configured servers with API keys stripped (HasApiKey flags instead).
        /// </summary>
        [HttpGet("Servers")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult<IEnumerable<object>> GetServers()
        {
            var servers = Plugin.Instance?.Configuration?.RemoteServers ?? new List<RemoteServer>();
            return Ok(servers.Select(SanitizeServer));
        }

        [HttpPost("Servers")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult AddServer([FromBody] RemoteServer server)
        {
            if (server == null)
            {
                return BadRequest(new { error = "Server configuration is required" });
            }

            if (!ConfigValidator.IsValidServerUrl(server.Url))
            {
                return BadRequest(new { error = "Server URL must be an absolute http(s) URL" });
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return BadRequest(new { error = "Plugin not initialized" });
            }

            server.Id = Guid.NewGuid().ToString();
            config.RemoteServers ??= new List<RemoteServer>();
            config.RemoteServers.Add(server);
            Plugin.Instance?.SaveConfiguration();
            _clientFactory.InvalidateAll();
            return Ok(new { success = true, server = SanitizeServer(server) });
        }

        [HttpPut("Servers/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> UpdateServer(string id, [FromBody] RemoteServer server, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            var existing = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (existing == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            if (!ConfigValidator.IsValidServerUrl(server.Url))
            {
                return BadRequest(new { error = "Server URL must be an absolute http(s) URL" });
            }

            existing.Name = server.Name;
            existing.Url = server.Url;
            if (!string.IsNullOrEmpty(server.ApiKey))
            {
                existing.ApiKey = server.ApiKey;
            }

            existing.UserId = server.UserId;
            var enabledChanged = existing.Enabled != server.Enabled;
            existing.Enabled = server.Enabled;
            existing.StreamingMode = server.StreamingMode;
            existing.Priority = server.Priority;
            existing.RequireApiKeyForImages = server.RequireApiKeyForImages;
            existing.WanCapMode = server.WanCapMode;
            existing.WanMaxBitrateMbps = server.WanMaxBitrateMbps;
            existing.WanMaxHeight = server.WanMaxHeight;

            Plugin.Instance?.SaveConfiguration();
            _clientFactory.Invalidate(existing.Id);

            // Toggling Enabled has to take effect now, not whenever the next
            // scheduled sync happens to run (up to RefreshIntervalHours later).
            // Switching a server off is meant to stop its titles playing, which
            // depends on reconciliation clearing the stream URL stamped on each of
            // its items; switching it back on has to restore those URLs. Leaving
            // that to the next cycle makes the switch look broken for an hour.
            // Same inline reconcile DeleteServer already does, for the same reason.
            if (enabledChanged)
            {
                foreach (var mapping in (config!.LibraryMappings ?? new List<LibraryMapping>()).Where(m => m.Enabled))
                {
                    try
                    {
                        await _persistence.ReconcileMappingAsync(mapping, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Post-toggle reconciliation failed for {Name}; it will be retried on the next sync", mapping.LocalLibraryName);
                    }
                }
            }

            return Ok(new { success = true });
        }

        [HttpDelete("Servers/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> DeleteServer(string id, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            // Tell them first, while server.Url/ApiKey/IssuedApiKey are still
            // whatever was configured - best-effort and never blocks the local
            // removal below (see NotifyAndRevokeOnUnfriendAsync's own doc comment
            // for why this exists: previously unfriending was entirely one-sided,
            // and the other side kept pulling this server's content until an
            // admin noticed and separately removed it themselves).
            await _friends.NotifyAndRevokeOnUnfriendAsync(server, cancellationToken).ConfigureAwait(false);

            await RemoveServerLocallyAsync(server, config!, cancellationToken).ConfigureAwait(false);

            return Ok(new { success = true });
        }

        /// <summary>
        /// Server-to-server: a friend telling us they removed this friendship on
        /// their side (see <see cref="FederationFriendService.NotifyAndRevokeOnUnfriendAsync"/>).
        /// Removes the matching local server entry the same way an admin-triggered
        /// <see cref="DeleteServer"/> does, so a one-sided unfriend on either side
        /// now actually disconnects both.
        /// </summary>
        [HttpPost("Friends/Unfriend")]
        [AllowAnonymous]
        public async Task<IActionResult> ReceiveUnfriend([FromBody] UnfriendPayload? payload, CancellationToken cancellationToken)
        {
            // Authenticated (and identified) purely by the federation token
            // itself, not by trusting payload.FromFederationId - a token only
            // ever resolves to the RemoteServer entry it was actually issued to,
            // so there is no way to unfriend anyone but the caller's own
            // relationship with this server.
            var server = FederationTokenAuth.ResolveCaller(Request);
            if (server == null)
            {
                return Unauthorized();
            }

            var config = Plugin.Instance!.Configuration;
            await RemoveServerLocallyAsync(server, config, cancellationToken).ConfigureAwait(false);

            return Ok(new { success = true });
        }

        /// <summary>
        /// Shared local-cleanup body for both an admin-triggered
        /// <see cref="DeleteServer"/> and a friend-triggered
        /// <see cref="ReceiveUnfriend"/>: drops this server from config, prunes its
        /// library sources/cached entries and bandwidth-monitor state, and
        /// reconciles every affected mapping immediately so its federated items
        /// don't sit around pointing at a server no longer in config until
        /// whatever sync happens to run next.
        /// </summary>
        private async Task RemoveServerLocallyAsync(RemoteServer server, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var id = server.Id;
            config.RemoteServers!.Remove(server);

            var seen = new HashSet<Guid>();
            var affectedMappings = config.LibraryMappings ?? new List<LibraryMapping>();
            foreach (var mapping in affectedMappings)
            {
                mapping.RemoteLibrarySources?.RemoveAll(s => s.ServerId == id);
                _cache.PruneServerSources(mapping.LocalLibraryName, id, seen);
            }

            // Pool rosters kept removed friends as dead members until now - drop
            // them so rosters (and the pool fan-out notices built from them) stop
            // referencing a server that is no longer configured.
            foreach (var pool in config.Pools ?? new List<FederationPool>())
            {
                var before = pool.Members.Count;
                pool.Members.RemoveAll(m =>
                    (!string.IsNullOrEmpty(server.FederationId) && string.Equals(m.FederationId, server.FederationId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(server.Url) && string.Equals(m.Url, server.Url, StringComparison.OrdinalIgnoreCase)));
                if (pool.Members.Count != before)
                {
                    _logger.LogInformation("[Federation] Removed {ServerName} from pool {PoolName}", server.Name, pool.Name);
                }
            }

            // Static per-server caches in RemoteServerClient (playback info, peer
            // status, session tokens, capability probes) are keyed by server id -
            // drop them so this removal (and any later re-friend with a fresh
            // identity) never reuses stale entries.
            RemoteServerClient.InvalidateServerCaches(id);

            Plugin.Instance?.SaveConfiguration();
            _clientFactory.Invalidate(id);
            _bandwidthMonitor.RemoveServer(id);

            foreach (var mapping in affectedMappings.Where(m => m.Enabled))
            {
                try
                {
                    await _persistence.ReconcileMappingAsync(mapping, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Post-removal reconciliation failed for {Name}; it will be retried on the next sync", mapping.LocalLibraryName);
                }
            }
        }

        #endregion

        #region Friends

        /// <summary>
        /// Admin-triggered: send a friend request to a server running Federation.
        /// </summary>
        [HttpPost("Friends/Send")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> SendFriendRequest([FromBody] SendFriendRequestBody body, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.SendFriendRequestAsync(body?.Url ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: list this server's pending incoming and outgoing friend requests.
        /// </summary>
        [HttpGet("Friends")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetFriendRequests()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return Ok(new
            {
                incoming = config.IncomingFriendRequests.Select(r => new { r.Id, r.RemoteServerUrl, r.RemoteServerName, r.CreatedUtc, r.Verified }),
                outgoing = config.OutgoingFriendRequests.Select(r => new { r.Id, r.RemoteServerUrl, r.RemoteServerName, r.CreatedUtc })
            });
        }

        /// <summary>
        /// Admin-triggered: accept an incoming friend request.
        /// </summary>
        [HttpPost("Friends/{id}/Accept")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> AcceptFriendRequest(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.AcceptFriendRequestAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: reject an incoming friend request.
        /// </summary>
        [HttpPost("Friends/{id}/Reject")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RejectFriendRequest(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.RejectFriendRequestAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: cancel a friend request this server sent before the other
        /// side responded.
        /// </summary>
        [HttpDelete("Friends/Outgoing/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> CancelFriendRequest(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.CancelOutgoingFriendRequestAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Server-to-server, anonymous: receives a friend request from another
        /// Federation install. Anonymous is required - the sender has no API key for
        /// us yet, since issuing one is the point of accepting.
        /// </summary>
        [HttpPost("Friends/Request")]
        [AllowAnonymous]
        public async Task<IActionResult> ReceiveFriendRequest([FromBody] FriendRequestPayload payload, CancellationToken cancellationToken)
        {
            var result = await _friends.ReceiveFriendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
            return result.Success ? Ok(result) : BadRequest(result);
        }

        /// <summary>
        /// Server-to-server: a friend asking (using the federation token we gave
        /// them) who our other friends are, for friends-of-friends discovery.
        /// Gated on AllowFriendsOfFriends and a valid federation token - see
        /// <see cref="FederationTokenAuth"/>.
        /// </summary>
        [HttpGet("Friends/List")]
        [AllowAnonymous]
        public IActionResult GetFriendsList()
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.AllowFriendsOfFriends)
            {
                return Ok(new { allowsIntroductions = false, friends = Array.Empty<object>() });
            }

            var friends = (config.RemoteServers ?? new List<RemoteServer>())
                .Where(s => s.Enabled)
                .Select(s => new { name = s.Name, url = s.Url });
            return Ok(new { allowsIntroductions = true, friends });
        }

        /// <summary>
        /// Server-to-server, anonymous: lets a server we sent a request to confirm
        /// that request genuinely originated from us, by checking it exists in our
        /// own outgoing list. Reveals only existence, keyed by an unguessable
        /// request id known solely to the two servers involved.
        /// </summary>
        [HttpGet("Friends/Outgoing/{id}")]
        [AllowAnonymous]
        public IActionResult VerifyOutgoingRequest(string id)
        {
            return _friends.HasOutgoingRequest(id) ? Ok() : NotFound();
        }

        /// <summary>
        /// Server-to-server, anonymous: the other server has accepted our earlier
        /// friend request and is handing us a token to use pulling from them.
        /// Returns 400 when <see cref="FederationFriendService.HandleAcceptCallback"/>
        /// rejects it (unknown request, or the other side didn't confirm scoped
        /// federation-token support) - the accepting side's own
        /// <see cref="FederationFriendService.AcceptFriendRequestAsync"/> already
        /// surfaces a clear error to its admin on anything but a 2xx response.
        /// </summary>
        [HttpPost("Friends/Accept")]
        [AllowAnonymous]
        public IActionResult ReceiveFriendAccept([FromBody] FriendRequestPayload payload)
        {
            return _friends.HandleAcceptCallback(payload) ? Ok() : BadRequest();
        }

        /// <summary>
        /// Server-to-server, anonymous: the other server declined our earlier friend request.
        /// </summary>
        [HttpPost("Friends/Reject")]
        [AllowAnonymous]
        public IActionResult ReceiveFriendReject([FromBody] FriendRejectPayload payload)
        {
            _friends.HandleRejectCallbackAsync(payload?.RequestId ?? string.Empty);
            return Ok();
        }

        /// <summary>
        /// Admin-triggered: sets which of this server's own libraries a specific
        /// friend can see. Purely local state - <see cref="FederationPeerAccessService"/>
        /// enforces it server-side on every <c>Peer/*</c> request, so there is
        /// nothing to notify the friend of - see
        /// <see cref="FederationFriendService.UpdateFriendSharingAsync"/>.
        /// </summary>
        [HttpPost("Friends/{id}/Sharing")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> UpdateFriendSharing(string id, [FromBody] UpdateSharingBody body)
        {
            var (success, message) = await _friends.UpdateFriendSharingAsync(
                id,
                body?.ShareAll ?? true,
                body?.FolderIds ?? new List<string>(),
                body?.ExcludedItemIds ?? new List<string>()).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: sets (mode AllLibraries clears) a per-remote-user
        /// override for one of this friend's own local users - block them entirely,
        /// narrow them to specific already-shared libraries, or narrow them all the
        /// way to specific items. Pushed to the friend so their plugin can enforce
        /// it against their own users - see
        /// <see cref="FederationFriendService.SetRemoteUserAccessRuleAsync"/>.
        /// </summary>
        [HttpPost("Friends/{id}/RemoteUserRule")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> SetRemoteUserAccessRule(string id, [FromBody] RemoteUserAccessRule rule, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.SetRemoteUserAccessRuleAsync(id, rule, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Server-to-server: a friend telling us the complete, current list of
        /// per-remote-user overrides they've configured for our own local users.
        /// Requires a valid federation token - see <see cref="FederationTokenAuth"/>.
        /// </summary>
        [HttpPost("Friends/RemoteUserRules")]
        [AllowAnonymous]
        public IActionResult ReceiveRemoteUserAccessRules([FromBody] RemoteUserAccessRulesPayload payload)
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            _friends.ReceiveRemoteUserAccessRules(payload);
            return Ok();
        }

        /// <summary>
        /// Admin-triggered: lists this server's own top-level libraries, for the
        /// per-friend sharing picker. Deliberately not filtered down to "real,
        /// non-federated" libraries - a library the plugin auto-provisions from
        /// federated content shares the same name (and merges into) an admin's own
        /// real library just as often as it stands alone, and there is no reliable
        /// signal in config to tell those two cases apart. It doesn't need to be:
        /// FederationSyncService already refuses to pull in any item carrying a
        /// FederationKey provider id regardless of which library grants access to
        /// it, so a friend seeing a library that happens to contain re-shared
        /// content can still never relay that content onward through their own
        /// server - the non-transitive guarantee holds at the content layer, not
        /// this picker.
        /// </summary>
        [HttpGet("LocalLibraries")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetLocalLibraries()
        {
            var folders = _libraryManager.GetVirtualFolders()
                .Select(f => new { id = f.ItemId, name = f.Name })
                .ToList();

            return Ok(folders);
        }

        /// <summary>
        /// Admin-triggered: searches this server's own local items by name, for the
        /// per-remote-user "certain items" override picker (see
        /// <see cref="SetRemoteUserAccessRule"/>) - there was no existing item
        /// search/browse endpoint on this page to reuse, so this is a minimal one
        /// purpose-built for that picker. Deliberately name-only and capped: this is
        /// a quick "find the thing you're about to restrict", not a general browse
        /// API.
        /// </summary>
        [HttpGet("SearchLocalItems")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SearchLocalItems([FromQuery] string? query, [FromQuery] int limit = 25)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Ok(Array.Empty<object>());
            }

            var boundedLimit = Math.Clamp(limit, 1, 100);
            var results = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                SearchTerm = query,
                Limit = boundedLimit,
                Recursive = true,
                IsVirtualItem = false
            });

            var items = results
                .Select(i => new { id = i.Id.ToString("N"), name = i.Name, type = i.GetType().Name, year = i.ProductionYear })
                .ToList();

            return Ok(items);
        }

        /// <summary>
        /// Admin-triggered: pages through this server's own local, non-federated
        /// catalog with cover art, for the settings page's "Catalog" picker - the
        /// one place an admin can browse everything they own by cover art and pick
        /// what to stop sharing, rather than knowing an exact name to type into
        /// <see cref="SearchLocalItems"/>. Federated items (anything carrying a
        /// <c>FederationKey</c> provider id - already someone else's content
        /// passing through this server) are excluded: this picker is about what
        /// *this server* shares out, and federated content can never be re-shared
        /// onward anyway (see <see cref="FederationPeerAccessService"/>'s remarks
        /// on the non-transitive guarantee).
        /// </summary>
        [HttpGet("BrowseLocalItems")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult BrowseLocalItems([FromQuery] string? query, [FromQuery] string? type, [FromQuery] int startIndex = 0, [FromQuery] int limit = 60)
        {
            try
            {
                var boundedLimit = Math.Clamp(limit, 1, 200);
                var boundedStart = Math.Max(0, startIndex);
                var requestedKind = string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase)
                    ? Jellyfin.Data.Enums.BaseItemKind.Series
                    : Jellyfin.Data.Enums.BaseItemKind.Movie;

                var itemQuery = new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false,
                    IncludeItemTypes = new[] { requestedKind }
                };

                if (!string.IsNullOrWhiteSpace(query))
                {
                    itemQuery.SearchTerm = query;
                }

                // Federated items are excluded below by their FederationKey provider id -
                // ILibraryManager's own query has no "does not have provider id"
                // predicate to push that down to, so this fetches a bounded batch
                // up front and filters/pages it in memory instead. 5000 comfortably
                // covers a real single library's catalog for the search/type-filtered
                // case this is normally used with; an admin browsing their entire
                // unfiltered catalog on a much larger install sees a floor, not
                // necessarily the exact total, past that point.
                itemQuery.StartIndex = 0;
                itemQuery.Limit = 5000;

                var result = _libraryManager.GetItemsResult(itemQuery);
                var localOnly = PrepareLocalCatalog(result.Items);

                var config = Plugin.Instance?.Configuration;
                var globallyExcluded = config?.GloballyExcludedItemIds ?? new List<string>();
                var page = localOnly.Skip(boundedStart).Take(boundedLimit).Select(i =>
                {
                    var idString = i.Id.ToString("N");
                    var excludedFriends = (config?.RemoteServers ?? new List<RemoteServer>())
                        .Where(s => (s.ExcludedItemIds ?? new List<string>()).Any(x => string.Equals(x, idString, StringComparison.OrdinalIgnoreCase)))
                        .Select(s => s.Name)
                        .ToList();

                    return new
                    {
                        id = idString,
                        name = i.Name,
                        type = i.GetType().Name,
                        year = i.ProductionYear,
                        dateCreated = i.DateCreated,
                        hiddenFromEveryone = globallyExcluded.Any(x => string.Equals(x, idString, StringComparison.OrdinalIgnoreCase)),
                        excludedFriendNames = excludedFriends
                    };
                }).ToList();

                return Ok(new { totalRecordCount = localOnly.Count, items = page });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error browsing local catalog (query={Query}, type={Type})", query, type);
                return StatusCode(500, new { error = $"Error: {ex.Message}" });
            }
        }

        /// <summary>
        /// Lists Jellyfin's own local users, entirely through reflection rather
        /// than a direct <c>_userManager.Users</c>/<c>GetUsers()</c> call. The
        /// member exposing this changed shape between the <c>Jellyfin.Controller</c>
        /// NuGet version this plugin compiles against (10.11.6, a <c>Users</c>
        /// property) and at least one real server build it runs on (10.11.11, a
        /// <c>GetUsers()</c> method, no property) - binding to either one directly
        /// throws <see cref="MissingMethodException"/> on whichever server doesn't
        /// have it. The element type (User) is itself defined in a versioned
        /// assembly that differs the same way, so every caller reads results back
        /// through reflection too rather than casting to a compile-time type,
        /// which could throw its own cross-version identity mismatch.
        /// </summary>
        private IEnumerable<object> EnumerateLocalUsers()
        {
            var type = _userManager.GetType();
            var iface = typeof(IUserManager);

            var prop = iface.GetProperty("Users") ?? type.GetProperty("Users");
            if (prop?.GetValue(_userManager) is System.Collections.IEnumerable propResult)
            {
                foreach (var u in propResult)
                {
                    yield return u;
                }

                yield break;
            }

            var method = iface.GetMethod("GetUsers", Type.EmptyTypes) ?? type.GetMethod("GetUsers", Type.EmptyTypes);
            if (method?.Invoke(_userManager, null) is System.Collections.IEnumerable methodResult)
            {
                foreach (var u in methodResult)
                {
                    yield return u;
                }

                yield break;
            }

            _logger.LogWarning("[Federation] Could not find a Users property or GetUsers() method on IUserManager for this server build");
        }

        #endregion

        #region Pools

        /// <summary>
        /// Gets this server's own connectivity mode - see
        /// <see cref="PluginConfiguration.ConnectivityMode"/>.
        /// </summary>
        [HttpGet("Setup/Connectivity")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetConnectivityMode()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return Ok(new { mode = config.ConnectivityMode.ToString() });
        }

        /// <summary>
        /// Admin-triggered: records how this server is reachable from the
        /// internet. Self-saves immediately, same as the other Setup/Pools
        /// actions - not part of the main Save form.
        /// </summary>
        [HttpPost("Setup/Connectivity")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetConnectivityMode([FromBody] SetConnectivityModeBody body)
        {
            // Enum.TryParse alone isn't enough validation here: C# enums accept
            // any underlying integer ("99", "-5") and even comma-combined names
            // ("PublicFacing, Tailscale") as "successfully parsed" even though
            // none of those are valid members - Enum.IsDefined only rules out the
            // numeric case, so the two real choices are checked explicitly
            // instead. Unset is deliberately not settable here either: it is
            // this field's un-migrated default, not something an admin should be
            // able to choose back into once they've made a real choice.
            if (!Enum.TryParse<ServerConnectivityMode>(body?.Mode, ignoreCase: true, out var mode)
                || (mode != ServerConnectivityMode.PublicFacing && mode != ServerConnectivityMode.Tailscale))
            {
                return BadRequest(new { error = "Mode must be one of: PublicFacing, Tailscale" });
            }

            var config = Plugin.Instance!.Configuration;
            config.ConnectivityMode = mode;
            Plugin.Instance.SaveConfiguration();
            return Ok(new { success = true, mode = config.ConnectivityMode.ToString() });
        }

        /// <summary>
        /// Checks whether this process can plausibly install/drive Tailscale
        /// itself - see <see cref="TailscaleService.CheckEnvironmentAsync"/>.
        /// </summary>
        [HttpGet("Setup/Tailscale/Environment")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> GetTailscaleEnvironment(CancellationToken cancellationToken)
        {
            var check = await _tailscale.CheckEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { canAutoInstall = check.CanAutoInstall, reason = check.Reason, command = check.Command });
        }

        /// <summary>Reads Tailscale's current login/connection state.</summary>
        [HttpGet("Setup/Tailscale/Status")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> GetTailscaleStatus(CancellationToken cancellationToken)
        {
            var status = await _tailscale.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { state = status.State.ToString(), dnsName = status.DnsName, message = status.Message });
        }

        /// <summary>
        /// Admin-triggered, explicit consent point: runs Tailscale's official
        /// install script on this host.
        /// </summary>
        [HttpPost("Setup/Tailscale/Install")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> InstallTailscale(CancellationToken cancellationToken)
        {
            // InstallAsync's own doc comment says it must only be called once
            // CheckEnvironmentAsync reports true - it runs no environment checks
            // of its own. The only caller was this action, and it never actually
            // did that check: the config page's Install button is disabled based
            // on it client-side, but starts enabled by default until that async
            // fetch resolves, and a direct API call skips the UI entirely - so
            // nothing server-side ever stopped curl|sh from running on a host
            // that can't actually use the result (non-root, no /dev/net/tun, a
            // non-Linux OS).
            var check = await _tailscale.CheckEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            if (!check.CanAutoInstall)
            {
                return Ok(new { success = false, message = check.Reason ?? "Auto-install is not available on this host." });
            }

            var (success, message) = await _tailscale.InstallAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: starts <c>tailscale up</c> and returns the login link
        /// for the admin to open, if one was needed.
        /// </summary>
        [HttpPost("Setup/Tailscale/Login")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> StartTailscaleLogin(CancellationToken cancellationToken)
        {
            var result = await _tailscale.StartLoginAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { success = result.Success, loginUrl = result.LoginUrl, message = result.Message });
        }

        /// <summary>
        /// Admin-triggered: turns on Funnel for this server's own local Jellyfin
        /// port, so it is reachable at an https://*.ts.net address without any
        /// port-forwarding.
        /// </summary>
        [HttpPost("Setup/Tailscale/Funnel")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> SetUpTailscaleFunnel(CancellationToken cancellationToken)
        {
            var baseUrl = _federationManager.GetInternalPlaybackBaseUrl();
            var port = Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ? parsed.Port : 8096;

            var result = await _tailscale.SetUpFunnelAsync(port, cancellationToken).ConfigureAwait(false);
            return Ok(new { success = result.Success, funnelUrl = result.FunnelUrl, message = result.Message });
        }

        /// <summary>
        /// Admin-triggered: lists the multi-server pools this server owns or belongs to.
        /// </summary>
        [HttpGet("Pools")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetPools()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var pools = config.Pools.Select(p => new
            {
                p.Id,
                p.Name,
                p.IsOwner,
                p.OwnerFederationId,
                p.OwnerName,
                Members = p.Members.Select(m => new { m.FederationId, m.Name, m.Url })
            });
            return Ok(pools);
        }

        /// <summary>
        /// Admin-triggered: creates a new pool owned by this server.
        /// </summary>
        [HttpPost("Pools/Create")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult CreatePool([FromBody] CreatePoolBody body)
        {
            if (string.IsNullOrWhiteSpace(body?.Name))
            {
                return BadRequest(new { error = "Pool name is required" });
            }

            // A pool only works if the other members can actually reach this
            // server to connect - see PluginConfiguration.ConnectivityMode. Unset
            // (every install predating this setting) is refused rather than
            // allowed, the same conservative default the field's own doc comment
            // describes.
            var config = Plugin.Instance?.Configuration;
            if (config?.ConnectivityMode != ServerConnectivityMode.PublicFacing)
            {
                return BadRequest(new { error = "Only a public-facing server can create a pool - set this under Connectivity on the Pools tab first." });
            }

            var pool = _friends.CreatePool(body.Name);
            return Ok(new { success = true, poolId = pool.Id });
        }

        /// <summary>
        /// Admin-triggered: invites a server into a pool this server belongs to.
        /// Rides the ordinary friend-request handshake - the recipient's admin still
        /// has to accept, same as a direct friend request.
        /// </summary>
        [HttpPost("Pools/{poolId}/Invite")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> InviteToPool(string poolId, [FromBody] SendFriendRequestBody body, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.SendPoolInviteAsync(poolId, body?.Url ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: invites a friend this server is already connected to
        /// into a pool, without re-typing their URL or repeating the friend
        /// handshake. Still requires their admin to accept - see
        /// <see cref="GetPoolInvites"/>/<see cref="AcceptPoolInvite"/>.
        /// </summary>
        [HttpPost("Pools/{poolId}/AddFriend")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> AddFriendToPool(string poolId, [FromBody] AddFriendToPoolBody body, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.AddFriendToPoolAsync(poolId, body?.RemoteServerId ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: removes this server's own membership record for a pool.
        /// Does not unfriend servers already connected through it.
        /// </summary>
        [HttpDelete("Pools/{poolId}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult LeavePool(string poolId)
        {
            var removed = _friends.LeavePool(poolId);
            return removed ? Ok(new { success = true }) : NotFound(new { error = "Pool not found" });
        }

        /// <summary>
        /// Admin-triggered: sets or clears a pool's icon, and best-effort spreads it
        /// to every other current member.
        /// </summary>
        [HttpPost("Pools/{poolId}/Icon")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> SetPoolIcon(string poolId, [FromBody] SetPoolIconBody body, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.SetPoolIconAsync(poolId, body?.IconBase64, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: lists this server's pending incoming and outgoing pool invites.
        /// </summary>
        [HttpGet("Pools/Invites")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetPoolInvites()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return Ok(new
            {
                incoming = config.IncomingPoolInvites.Select(i => new { i.Id, i.PoolId, i.PoolName, i.RemoteServerName, i.CreatedUtc }),
                outgoing = config.OutgoingPoolInvites.Select(i => new { i.Id, i.PoolId, i.PoolName, i.RemoteServerName, i.CreatedUtc })
            });
        }

        /// <summary>
        /// Admin-triggered: accepts a pending pool invite from an already-known friend.
        /// </summary>
        [HttpPost("Pools/Invites/{id}/Accept")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> AcceptPoolInvite(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.AcceptPoolInviteAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: rejects a pending pool invite from an already-known friend.
        /// </summary>
        [HttpPost("Pools/Invites/{id}/Reject")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RejectPoolInvite(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.RejectPoolInviteAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Admin-triggered: cancels a pool invite this server sent before the other
        /// side responded.
        /// </summary>
        [HttpDelete("Pools/Invites/Outgoing/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> CancelPoolInvite(string id)
        {
            var (success, message) = await _friends.CancelOutgoingPoolInviteAsync(id).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Server-to-server, anonymous: an already-known friend is inviting us into
        /// a pool. Anonymous at the ASP.NET layer like every genuine peer endpoint -
        /// authenticated instead via the federation token, which never satisfies
        /// RequiresElevation (it isn't registered with Jellyfin's own auth manager).
        /// </summary>
        [HttpPost("Pools/InviteNotice")]
        [AllowAnonymous]
        public async Task<IActionResult> ReceivePoolInviteNotice([FromBody] PoolInviteNoticePayload payload, CancellationToken cancellationToken)
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            await _friends.ReceivePoolInviteNotice(payload, cancellationToken).ConfigureAwait(false);
            return Ok();
        }

        /// <summary>
        /// Server-to-server, anonymous: the friend we invited into a pool accepted.
        /// </summary>
        [HttpPost("Pools/AcceptNotice")]
        [AllowAnonymous]
        public IActionResult ReceivePoolAcceptNotice([FromBody] PoolInviteResponsePayload payload)
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            return _friends.HandlePoolAcceptNotice(payload) ? Ok() : BadRequest();
        }

        /// <summary>
        /// Server-to-server, anonymous: the friend we invited into a pool declined.
        /// </summary>
        [HttpPost("Pools/RejectNotice")]
        [AllowAnonymous]
        public IActionResult ReceivePoolRejectNotice([FromBody] PoolInviteResponsePayload payload)
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            _friends.HandlePoolRejectNotice(payload?.InviteId ?? string.Empty);
            return Ok();
        }

        /// <summary>
        /// Server-to-server, anonymous: an already-known member of a pool we're
        /// already in is syncing its current roster/icon. Not a new introduction -
        /// see <see cref="ReceivePoolInviteNotice"/> for that. Anonymous at the
        /// ASP.NET layer like every genuine peer endpoint - authenticated instead
        /// via the federation token, which never satisfies RequiresElevation (it
        /// isn't registered with Jellyfin's own auth manager).
        /// </summary>
        [HttpPost("Pools/Notice")]
        [AllowAnonymous]
        public async Task<IActionResult> ReceivePoolNotice([FromBody] PoolNoticePayload payload, CancellationToken cancellationToken)
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            await _friends.ReceivePoolNotice(payload, cancellationToken).ConfigureAwait(false);
            return Ok();
        }

        #endregion

        #region Discovery

        /// <summary>
        /// Admin-triggered: asks every current friend who their other friends are
        /// and caches the results for the settings page's Discovery dashboard.
        /// Never adds anyone as a friend by itself - see <see cref="SearchDiscovery"/>
        /// for the results and <see cref="SendFriendRequest"/> for actually adding
        /// one. Requires the asked friend to have friends-of-friends sharing
        /// enabled on their own side.
        /// </summary>
        [HttpPost("Discovery/Refresh")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RefreshDiscovery(CancellationToken cancellationToken)
        {
            var count = await _friends.RefreshDiscoveredServersAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { success = true, count });
        }

        /// <summary>
        /// Admin-triggered: searches the in-memory discovery cache built by
        /// <see cref="RefreshDiscovery"/>, flagging servers already friended or
        /// with a pending request so the dashboard can grey those out.
        /// </summary>
        [HttpGet("Discovery/Search")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SearchDiscovery([FromQuery] string? query)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var results = _friends.SearchDiscoveredServers(query).Select(s =>
            {
                var normalized = s.Url.TrimEnd('/');
                var alreadyFriend = config.RemoteServers.Any(r => string.Equals(r.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase));
                var pending = config.OutgoingFriendRequests.Any(r => string.Equals(r.RemoteServerUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
                    || config.IncomingFriendRequests.Any(r => string.Equals(r.RemoteServerUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    name = s.Name,
                    url = s.Url,
                    discoveredVia = s.DiscoveredViaFriendName,
                    lastSeenUtc = s.LastSeenUtc,
                    alreadyFriend,
                    pending
                };
            });
            return Ok(results);
        }

        #endregion

        #region Remote Library Browsing

        [HttpGet("GetRemoteLibraries")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> GetRemoteLibraries(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.RemoteServers == null || config.RemoteServers.Count == 0)
            {
                return Ok(new { success = false, message = "No remote servers configured" });
            }

            var results = new List<object>();
            foreach (var server in config.RemoteServers.Where(s => s.Enabled))
            {
                try
                {
                    // A non-Jellyfin peer (Plex today) has no Peer/Libraries endpoint
                    // to ask - RemoteServerClient.GetLibrariesAsync would just fail
                    // to connect against it, which is why this always silently
                    // showed "Failed to connect" for a Plex server instead of an
                    // actual library list. Route through the same external-catalog
                    // abstraction streaming/images/sync already use instead.
                    var externalProvider = _externalCatalogs.For(server);
                    if (externalProvider != null)
                    {
                        var externalLibraries = await externalProvider.GetLibrariesAsync(server, cancellationToken).ConfigureAwait(false);
                        results.Add(new
                        {
                            serverId = server.Id,
                            serverName = server.Name,
                            libraries = externalLibraries.Select(lib => new
                            {
                                id = lib.Id,
                                name = lib.Name,
                                collectionType = lib.MediaType,
                                itemCount = 0
                            }).ToList()
                        });
                        continue;
                    }

                    var client = _clientFactory.GetClient(server);
                    var libraries = await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
                    results.Add(new
                    {
                        serverId = server.Id,
                        serverName = server.Name,
                        libraries = (libraries ?? new List<MediaBrowser.Model.Dto.BaseItemDto>()).Select(lib => new
                        {
                            id = lib.Id,
                            name = lib.Name,
                            collectionType = lib.CollectionType?.ToString() ?? "unknown",
                            itemCount = lib.ChildCount ?? 0
                        }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new
                    {
                        serverId = server.Id,
                        serverName = server.Name,
                        error = $"Failed to connect: {ex.Message}",
                        libraries = new List<object>()
                    });
                }
            }

            return Ok(new { success = true, servers = results });
        }

        /// <summary>
        /// Admin-triggered: sets the exact list of a non-Jellyfin (Plex) server's
        /// own library/section ids allowed to be synced from - see
        /// <see cref="RemoteServer.AllowedExternalLibraryIds"/> for why this has to
        /// be recorded and enforced here rather than trusted to the remote, and
        /// <see cref="Services.PlexCatalogProvider"/> for the enforcement itself.
        /// An empty (not null) list means "allow nothing", matching how leaving
        /// every checkbox unchecked in the picker UI should behave; omit the field
        /// entirely (or pass a Jellyfin server's id) to make no change.
        /// </summary>
        /// <summary>
        /// Adds a non-Jellyfin (Plex) media server. Kept deliberately separate
        /// from the friend handshake: a Plex server has no admin on the other
        /// end clicking Accept, so the ordinary <c>Friends/Send</c> flow can
        /// never complete against one - the token is the whole credential and
        /// is pasted by hand, exactly once, here.
        /// </summary>
        [HttpPost("ExternalServers")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult AddExternalServer([FromBody] AddExternalServerBody body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.Url) || string.IsNullOrWhiteSpace(body.Token))
            {
                return BadRequest(new { error = "Server URL and token are required" });
            }

            if (!ConfigValidator.IsValidServerUrl(body.Url))
            {
                return BadRequest(new { error = "Server URL must be an absolute http(s) URL" });
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return BadRequest(new { error = "Plugin not initialized" });
            }

            // Same duplicate-guard the friend flow gets for free from the
            // handshake: re-adding a server that's already configured (a
            // double-clicked Add, a forgotten paste from last week) used to
            // silently create a second entry with a fresh id, and the two then
            // fought over the same federated library.
            var normalized = body.Url.TrimEnd('/');
            if (config.RemoteServers.Any(s => string.Equals(s.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(new { error = "A server with this address is already configured - edit it in the list instead of adding it again." });
            }

            var server = new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Kind = ServerKind.Plex,
                Name = string.IsNullOrWhiteSpace(body.Name) ? body.Url : body.Name,
                Url = body.Url,
                ApiKey = body.Token,
                // Always proxied - see ServerKind.Plex's doc comment: the Plex
                // token is a real credential for that whole server and must
                // never reach a client, and Plex has no equivalent of this
                // plugin's scoped per-item playback tokens to use instead.
                StreamingMode = StreamingMode.Proxy,
                Enabled = true
            };
            config.RemoteServers.Add(server);
            Plugin.Instance?.SaveConfiguration();
            _clientFactory.InvalidateAll();
            return Ok(new { success = true, server = SanitizeServer(server) });
        }

        /// <summary>
        /// Replaces a non-Jellyfin (Plex) server's pasted token in place. Kind,
        /// name and every other field survive untouched - this is the "the token
        // expired, here's the new one" path, not a re-add.
        /// </summary>
        [HttpPost("ExternalServers/{id}/Token")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetExternalServerToken(string id, [FromBody] SetExternalServerTokenBody body)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            if (server.Kind == ServerKind.Jellyfin)
            {
                return BadRequest(new { error = "A Jellyfin friend's token is minted by the friend handshake - use Send friend request instead." });
            }

            if (string.IsNullOrWhiteSpace(body?.Token))
            {
                return BadRequest(new { error = "Token is required" });
            }

            server.ApiKey = body.Token;
            Plugin.Instance?.SaveConfiguration();
            _clientFactory.Invalidate(server.Id);
            return Ok(new { success = true });
        }

        /// <summary>
        /// Admin-triggered: registers a non-Jellyfin consumer of our own
        /// catalog (<see cref="ServerKind.Companion"/>) - a Federation
        /// Companion instance a Plex-owning friend runs to import our content
        /// as <c>.strm</c> files. Returns a one-time connect code the admin
        /// copies to their friend; see <see cref="FederationFriendService.CreateCompanionFriend"/>.
        /// </summary>
        [HttpPost("Servers/Companion")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult AddCompanionFriend([FromBody] AddCompanionFriendBody? body)
        {
            var (success, message, connectCode) = _friends.CreateCompanionFriend(
                body?.Name ?? string.Empty,
                body?.ShareAllLibraries ?? true,
                body?.SharedLibraryFolderIds);

            if (!success)
            {
                return BadRequest(new { success, message });
            }

            _clientFactory.InvalidateAll();
            var created = Plugin.Instance?.Configuration?.RemoteServers?.LastOrDefault(s => s.Kind == ServerKind.Companion);
            return Ok(new { success, message, connectCode, server = created == null ? null : SanitizeServer(created) });
        }

        /// <summary>
        /// Re-derives the same connect code <see cref="AddCompanionFriend"/>
        /// returned, from the token already on file - so an admin who lost the
        /// copied text can pull it up again without rotating the token.
        /// </summary>
        [HttpGet("Servers/{id}/CompanionConnectCode")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetCompanionConnectCode(string id)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null || server.Kind != ServerKind.Companion)
            {
                return NotFound(new { error = "Companion friend not found" });
            }

            var (success, message, connectCode) = _friends.GetCompanionConnectCode(server);
            if (!success)
            {
                return BadRequest(new { success, message });
            }

            return Ok(new { success, connectCode });
        }

        /// <summary>
        /// Admin-triggered: fetches the live library/section list from a
        /// non-Jellyfin (Plex) server, merged with this side's recorded
        /// sharing consent (see <see cref="RemoteServer.AllowedExternalLibraryIds"/>),
        /// for the Plex management card's library-visibility picker. Sections
        /// the admin already declined are still returned (flagged), so consent
        /// can be revised rather than only ever granted.
        /// </summary>
        [HttpGet("ExternalServers/{id}/Libraries")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> GetExternalServerLibraries(string id, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            if (server.Kind == ServerKind.Jellyfin)
            {
                return BadRequest(new { error = "A Jellyfin friend's sharing is controlled by their own server - see Friends/{id}/Sharing instead." });
            }

            var provider = _externalCatalogs.For(server);
            if (provider == null)
            {
                return BadRequest(new { error = $"This server kind ({server.Kind}) isn't supported." });
            }

            var libraries = await provider.GetLibrariesAsync(server, cancellationToken).ConfigureAwait(false);
            var allowed = server.AllowedExternalLibraryIds;
            var result = libraries.Select(lib => new
            {
                id = lib.Id,
                name = lib.Name,
                mediaType = lib.MediaType,
                // Null allow-list means "no restriction on record" (a server
                // configured before this existed) - see the field's doc comment.
                allowed = allowed == null || allowed.Contains(lib.Id)
            }).ToList();

            return Ok(result);
        }

        [HttpPost("Servers/{id}/AllowedLibraries")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetAllowedLibraries(string id, [FromBody] SetAllowedLibrariesBody body)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            if (server.Kind == ServerKind.Jellyfin)
            {
                return BadRequest(new { error = "A Jellyfin friend's sharing is controlled by their own server - see Friends/{id}/Sharing instead." });
            }

            server.AllowedExternalLibraryIds = body?.LibraryIds ?? new List<string>();
            Plugin.Instance?.SaveConfiguration();
            return Ok(new { success = true, allowedLibraryIds = server.AllowedExternalLibraryIds });
        }

        /// <summary>
        /// Admin-triggered: lists EVERY library/section a non-Jellyfin (Plex)
        /// server currently exposes - deliberately NOT filtered through this
        /// server's allow list, unlike <see cref="GetRemoteLibraries"/> (which
        /// routes through IExternalCatalogProvider.GetLibrariesAsync and only
        /// ever returns already-allowed libraries). Backs the Plex management
        /// card's library-visibility picker, which by definition needs to offer
        /// the currently-disallowed sections too - otherwise a library declined
        /// once could never be re-allowed from the UI.
        /// </summary>
        [HttpGet("Servers/{id}/ExternalLibraries")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> GetExternalLibraries(string id, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            if (server.Kind == ServerKind.Jellyfin)
            {
                return BadRequest(new { error = "A Jellyfin friend's sharing is controlled by their own server - see Friends/{id}/Sharing instead." });
            }

            var provider = _externalCatalogs.For(server);
            if (provider == null)
            {
                return BadRequest(new { error = $"Unsupported server kind ({server.Kind})." });
            }

            var libraries = await provider.GetAllLibrariesAsync(server, cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                libraries = libraries.Select(l => new { id = l.Id, name = l.Name, mediaType = l.MediaType }),
                allowed = server.AllowedExternalLibraryIds ?? new List<string>()
            });
        }

        #endregion

        #region Streaming

        /// <summary>
        /// Proxy stream endpoint (Proxy mode). Streams the body through this server so
        /// the remote api_key never reaches clients. Anonymous because media players
        /// fetch media source URLs without Jellyfin auth headers; bounded by a
        /// cryptographic signature to one configured server, item, media kind and
        /// (for per-request paths) local user.
        /// </summary>
        [HttpGet("Stream")]
        [AllowAnonymous]
        public async Task<IActionResult> Stream(
            [FromQuery] string serverId,
            [FromQuery] string itemId,
            CancellationToken cancellationToken,
            [FromQuery] bool audio = false,
            [FromQuery] string? requestingUserId = null,
            [FromQuery] bool download = false,
            [FromQuery] string? fileName = null,
            [FromQuery] string? sig = null)
        {
            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                return BadRequest("Invalid item id");
            }

            // This endpoint has to remain anonymous because Jellyfin's ffmpeg
            // fetch does not forward the viewer's auth header. The URL itself is
            // therefore an item/user-scoped capability: changing the server, item,
            // media kind or claimed user invalidates it. This replaces the old
            // enumerable serverId+itemId URL and makes a forged requestingUserId
            // useless.
            if (!_federationManager.ValidateProxySignature(serverId, itemGuid, audio, requestingUserId, sig))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            // Only disclose configured-server/cache membership after the caller
            // has demonstrated possession of a valid item capability.
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null || !server.Enabled)
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            var cacheKey = _federationManager.Cache.TryGetLocalKeyForRemoteItem(serverId, itemGuid);
            var entry = cacheKey == null ? null : _federationManager.Cache.GetEntryByKey(cacheKey);
            if (entry == null || !entry.GetSourcesSnapshot().Any(source =>
                string.Equals(source.ServerId, serverId, StringComparison.OrdinalIgnoreCase)
                && source.RemoteItemId == itemGuid))
            {
                return NotFound();
            }

            // A static item.Path cannot identify which local user Jellyfin's
            // transcoder is fetching for. It is only valid when every configured
            // user rule allows this exact item. Per-request provider URLs carry a
            // signed user id and are checked again by the stream handler below.
            if (string.IsNullOrEmpty(requestingUserId)
                && !RemoteAccessControlService.IsAllowedForEveryConfiguredUser(
                    server,
                    entry.MappingName,
                    itemGuid,
                    entry.Metadata.OfficialRating))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            // download=true is the only difference from a normal play request: a
            // Content-Disposition header so the browser saves the file to the
            // viewer's own device instead of playing it inline - see
            // GetDownloadUrl below, which is what actually hands this URL to
            // clients. Header value is untrusted input (this endpoint is
            // capability URL and callable directly), so strip anything that could
            // inject an extra header/response-split rather than trusting it was
            // already sanitized upstream.
            if (download)
            {
                var safeName = string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
                safeName = safeName.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace("\"", string.Empty);
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeName}\"";
            }

            await _streamHandler.HandleProxyAsync(serverId, itemId, Request, Response, cancellationToken, audio, requestingUserId).ConfigureAwait(false);
            return new EmptyResult();
        }

        /// <summary>
        /// Resolves a browser-downloadable URL for a federated item, given its
        /// local Jellyfin item id: the same proxy stream URL playback already
        /// uses (see <see cref="Stream"/> above), with <c>download=true</c> and a
        /// filesystem-safe filename appended so the browser saves it to the
        /// viewer's own device instead of playing it inline. Distinct from
        /// <see cref="StartDownload"/>, which downloads a permanent copy onto
        /// *this server's* disk instead - this never touches server storage at
        /// all, it just streams straight to whoever asked.
        /// <para>
        /// Deliberately <see cref="AuthorizeAttribute"/> alone rather than the
        /// <c>RequiresElevation</c> every admin endpoint on this controller uses:
        /// it returns a signed item-scoped capability for <see cref="Stream"/>,
        /// after the download service has applied the source's download policy.
        /// Requiring a normal Jellyfin login here prevents anonymous callers from
        /// minting capabilities while still allowing ordinary viewers to use
        /// "save this to my phone" without granting dashboard elevation. This
        /// endpoint does not yet check the
        /// caller's own <c>EnableContentDownloading</c> user policy the way
        /// Jellyfin's native per-item downloads do - a known gap, not a
        /// deliberate design choice.
        /// </para>
        /// </summary>
        [HttpGet("DownloadUrl/{localItemId}")]
        [Authorize]
        public IActionResult GetDownloadUrl(string localItemId)
        {
            var (success, message, url, fileName) = _downloadService.GetDownloadUrl(localItemId);
            if (!success)
            {
                return BadRequest(new { success, message });
            }

            return Ok(new { success, message, url, fileName });
        }

        /// <summary>
        /// Server-to-server: a friend server asking us to mint a short-lived,
        /// single-item-scoped playback token, so its own users can Direct-mode-play
        /// an item of ours without ever seeing this server's own real api_key.
        /// Requires a valid federation token (see <see cref="FederationTokenAuth"/>)
        /// - a friend server calling on its own users' behalf, not this server's
        /// own admin. Also enforced here, not just in the Peer/* listing
        /// endpoints: sharing scope/excludes/per-remote-user rules could change
        /// between when a friend last synced this item and when it actually
        /// presses play, and a token, once minted, is usable on its own without
        /// going through those endpoints again.
        /// </summary>
        [HttpPost("PlaybackToken")]
        [AllowAnonymous]
        public IActionResult IssuePlaybackToken([FromBody] IssuePlaybackTokenRequest? request)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            // An entry whose FederationId was never captured (Companion friends
            // from 0.0.116, older friendships) otherwise minted every playback
            // token bound to an empty id - unresolvable at stream time, so the
            // friend browsed fine but every stream 403'd. Heal the entry here so
            // the token minted just below binds to something resolvable.
            _friends.EnsureFederationId(caller);

            if (!Guid.TryParse(request?.ItemId, out var itemGuid))
            {
                return BadRequest(new { error = "Invalid item id" });
            }

            if (!_peerAccess.IsItemVisible(caller, RequestingRemoteUserId(), itemGuid))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            var token = _playbackTokens.Issue(itemGuid.ToString("N"), caller.FederationId);
            return Ok(new { token, expiresUtc = DateTime.UtcNow.AddHours(24) });
        }

        /// <summary>
        /// Server-to-server: a friend server registering one of its own local
        /// users the moment that user actually starts playing something, in
        /// exchange for a per-user streaming session token (see
        /// <see cref="FederationUserSessionTokenService"/>). This is the second
        /// credential tier alongside the federation token
        /// (<see cref="FederationTokenAuth"/>): the federation token proves "this
        /// is friend X" and is used for browsing/admin-ish calls, but is never
        /// itself accepted by <see cref="DirectStream"/>/<see cref="DirectImage"/> -
        /// only a session token minted here is. Requires a valid federation token
        /// to call (a friend registering on its own users' behalf), same as
        /// <see cref="IssuePlaybackToken"/>. A user this friend's admin has fully
        /// blocked via a <see cref="RemoteUserAccessRule"/> is rejected at
        /// registration time, before any session token is ever handed out for
        /// them - later, per-item visibility is still re-checked at every actual
        /// stream request, since a rule can change during a session's lifetime.
        /// </summary>
        [HttpPost("RegisterUserSession")]
        [AllowAnonymous]
        public IActionResult RegisterUserSession([FromBody] RegisterUserSessionRequest? request)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request?.RemoteUserId))
            {
                return BadRequest(new { error = "Remote user id is required" });
            }

            // Same missing-FederationId heal as IssuePlaybackToken: a session
            // token bound to an empty id could never be re-resolved at stream
            // time, leaving every user of such a friend unable to play anything.
            _friends.EnsureFederationId(caller);

            // A Blocked rule denies everything regardless of item, so checking it
            // against a synthetic empty item id here (rather than duplicating the
            // rule lookup) rejects a fully-blocked user up front - anything less
            // restrictive than Blocked still needs a real item to evaluate, so it
            // is left to the per-stream IsItemVisible check instead of guessed at
            // here.
            if (!_peerAccess.IsItemVisible(caller, request.RemoteUserId, Guid.Empty)
                && (caller.RemoteUserAccessRules?.Any(r =>
                    string.Equals(r.RemoteUserId, request.RemoteUserId, StringComparison.OrdinalIgnoreCase)
                    && r.Mode == RemoteUserAccessMode.Blocked) ?? false))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "This user is blocked by the server admin" });
            }

            var token = _userSessionTokens.Issue(caller.FederationId, request.RemoteUserId);
            return Ok(new { token, expiresUtc = DateTime.UtcNow.AddHours(6) });
        }

        /// <summary>
        /// Whether <paramref name="token"/> authorizes streaming <paramref name="itemGuid"/>
        /// right now - either an item-scoped <see cref="FederationPlaybackTokenService"/>
        /// token (the original, still-supported mechanism: mint-then-play, one
        /// token per item), or a <see cref="FederationUserSessionTokenService"/>
        /// per-user session token, re-checked against
        /// <see cref="FederationPeerAccessService.IsItemVisible(Configuration.RemoteServer, string?, Guid)"/>
        /// for this specific item at this specific moment - a session token only
        /// proves "this user wasn't blocked when they started watching", not that
        /// every item they might request with it stays visible for the session's
        /// whole 6-hour lifetime.
        /// </summary>
        private bool IsStreamTokenAuthorized(string? token, Guid itemGuid)
        {
            if (_playbackTokens.TryValidate(token, itemGuid.ToString("N"), out var ownerFederationId))
            {
                // The friendship that minted this token must still exist. An
                // item-scoped token used to keep working for up to 24 hours after
                // an unfriend/server removal because nothing here ever re-resolved
                // which friend the mint came from - binding the federation id at
                // mint time (see IssuePlaybackToken) makes removal revoke it
                // instantly instead.
                return _friends.FindByFederationId(ownerFederationId) != null;
            }

            if (_userSessionTokens.TryValidate(token, out var federationId, out var remoteUserId))
            {
                var server = _friends.FindByFederationId(federationId);
                if (server != null && _peerAccess.IsItemVisible(server, remoteUserId, itemGuid))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Direct-mode's token-gated relay gateway: a friend's client fetches media
        /// straight from here rather than being handed this server's real api_key
        /// (see <see cref="FederationPlaybackTokenService"/> and
        /// <see cref="Services.FederationMediaSourceProvider"/> for the full
        /// rationale). Anonymous for the same reason as <see cref="Stream"/> - media
        /// players fetch media URLs without Jellyfin auth headers - but bounded to a
        /// short-lived token minted for exactly this item, not a standing credential.
        /// </summary>
        [HttpGet("DirectStream/{itemId}")]
        [AllowAnonymous]
        public async Task<IActionResult> DirectStream(
            string itemId,
            [FromQuery] string token,
            CancellationToken cancellationToken,
            [FromQuery] bool audio = false)
        {
            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                return BadRequest("Invalid item id");
            }

            if (!IsStreamTokenAuthorized(token, itemGuid))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            var internalRelayKey = await _friends.GetOrCreateInternalRelayApiKeyAsync().ConfigureAwait(false);
            var localUrl = _federationManager.GetInternalPlaybackBaseUrl();
            var endpoint = audio ? "Audio" : "Videos";
            var loopbackUrl = $"{localUrl}/{endpoint}/{itemGuid:N}/stream?api_key={Uri.EscapeDataString(internalRelayKey)}&Static=true";

            await _streamHandler.HandleDirectGatewayAsync(loopbackUrl, Request, Response, cancellationToken).ConfigureAwait(false);
            return new EmptyResult();
        }

        /// <summary>
        /// Image counterpart to <see cref="DirectStream"/>: a federated item's
        /// images used to be hotlinked straight to a friend's native
        /// <c>/Items/{id}/Images/{type}</c> endpoint (see
        /// <see cref="Providers.FederationImageProvider"/>), with
        /// <c>RemoteServer.RequireApiKeyForImages</c> optionally appending
        /// <c>server.ApiKey</c> as a raw query-string api_key. Under the
        /// federation-token model that key is no longer a real Jellyfin
        /// credential at all, so that URL would just 401 for anyone with the
        /// option on, and was an unnecessary leak for anyone without it (an
        /// unauthenticated hotlink to a friend's own native API). Reuses the
        /// same short-lived, single-item-scoped token
        /// <see cref="FederationPlaybackTokenService"/> already mints for
        /// Direct-mode video/audio - a browser &lt;img&gt; tag can't send a
        /// custom header, so this has to be a query-string token, not
        /// <see cref="FederationTokenAuth"/>'s header.
        /// </summary>
        [HttpGet("Peer/Images/{itemId}/{imageType}/{index?}")]
        [AllowAnonymous]
        public async Task<IActionResult> DirectImage(
            string itemId,
            string imageType,
            int? index,
            [FromQuery] string token,
            [FromQuery] string? tag,
            CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                return BadRequest("Invalid item id");
            }

            if (!IsStreamTokenAuthorized(token, itemGuid))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            var internalRelayKey = await _friends.GetOrCreateInternalRelayApiKeyAsync().ConfigureAwait(false);
            var localUrl = _federationManager.GetInternalPlaybackBaseUrl();
            var indexSegment = index.HasValue ? $"/{index.Value}" : string.Empty;
            var tagParam = string.IsNullOrEmpty(tag) ? string.Empty : $"&tag={Uri.EscapeDataString(tag)}";
            var loopbackUrl = $"{localUrl}/Items/{itemGuid:N}/Images/{Uri.EscapeDataString(imageType)}{indexSegment}?api_key={Uri.EscapeDataString(internalRelayKey)}{tagParam}";

            await _streamHandler.HandleDirectGatewayAsync(loopbackUrl, Request, Response, cancellationToken).ConfigureAwait(false);
            return new EmptyResult();
        }

        #endregion

        #region Peer data (replaces a friend calling Jellyfin's own native REST API)

        // Used only for internal loopback JSON fetches below - separate from
        // FederationStreamHandler's own byte-streaming client, which is tuned for
        // relaying large media bodies, not small JSON responses.
        private static readonly System.Net.Http.HttpClient InternalJsonHttpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Resolves which local user id this server's own internal loopback
        /// fetches (below) should query as - preferring an administrator, so a
        /// restricted account's own <c>EnabledFolders</c> never silently narrows
        /// what this plugin considers "everything that exists" before its own
        /// sharing-scope filtering ever runs. Reflection-based for the same
        /// version-skew reason as <see cref="EnumerateLocalUsers"/>.
        /// </summary>
        private Guid? ResolveInternalQueryUserId()
        {
            object? admin = null;
            object? first = null;
            foreach (var u in EnumerateLocalUsers())
            {
                first ??= u;
                if (IsAdministratorUser(u))
                {
                    admin = u;
                    break;
                }
            }

            var chosen = admin ?? first;
            return chosen?.GetType().GetProperty("Id")?.GetValue(chosen) as Guid?;
        }

        private static bool IsAdministratorUser(object user)
        {
            var type = user.GetType();
            if (type.GetProperty("IsAdministrator")?.GetValue(user) is bool direct)
            {
                return direct;
            }

            var policy = type.GetProperty("Policy")?.GetValue(user);
            return policy?.GetType().GetProperty("IsAdministrator")?.GetValue(policy) is bool policyIsAdmin && policyIsAdmin;
        }

        /// <summary>
        /// Fetches JSON from this server's own native REST API over loopback,
        /// authenticated with <see cref="FederationFriendService.GetOrCreateInternalRelayApiKeyAsync"/> -
        /// never exposed beyond this call. This plugin's own Peer/* endpoints use
        /// this to get at the real, unfiltered data (exactly like
        /// <c>DirectStream</c> already does for media bytes), then apply
        /// <see cref="FederationPeerAccessService"/>'s filtering themselves before
        /// anything reaches a friend - Jellyfin's native per-user permission
        /// system plays no role in what a friend can see under this model.
        /// </summary>
        private async Task<System.Text.Json.Nodes.JsonObject?> FetchInternalJsonAsync(string path, CancellationToken cancellationToken)
        {
            var internalRelayKey = await _friends.GetOrCreateInternalRelayApiKeyAsync().ConfigureAwait(false);
            var localUrl = _federationManager.GetInternalPlaybackBaseUrl();
            var separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var url = $"{localUrl}{path}{separator}api_key={Uri.EscapeDataString(internalRelayKey)}";

            using var response = await InternalJsonHttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
        }

        /// <summary>
        /// Token-authenticated counterpart to Jellyfin's native
        /// <c>/Playback/BitrateTest</c>: a friend measures its real download
        /// throughput from this server so its <see cref="WanBandwidthMonitor"/> can
        /// size a WAN bitrate cap from a measurement instead of a guess. The native
        /// endpoint requires real Jellyfin auth (which a scoped federation token
        /// cannot satisfy), so before this route existed the measurement always
        /// failed post-token-rewrite and WAN caps silently stopped applying -
        /// clients then attempted to direct-play bitrates the link couldn't carry
        /// (constant buffering). Serves <paramref name="size"/> bytes of zeros from
        /// a small reused buffer; the content is irrelevant, only the timing
        /// matters.
        /// </summary>
        [HttpGet("Peer/BitrateTest")]
        [AllowAnonymous]
        public async Task<IActionResult> PeerBitrateTest([FromQuery] int size, CancellationToken cancellationToken)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            var clamped = Math.Clamp(size, 1, 50_000_000);
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = clamped;

            var chunk = new byte[64 * 1024];
            var remaining = clamped;
            while (remaining > 0)
            {
                var take = Math.Min(chunk.Length, remaining);
                await Response.Body.WriteAsync(chunk.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                remaining -= take;
            }

            return new EmptyResult();
        }

        /// <summary>
        /// Replaces a friend's old native <c>/Users/{id}/Views</c> call
        /// (<see cref="Services.RemoteServerClient.GetLibrariesAsync"/>): this
        /// server's own top-level library folders, filtered to what
        /// <paramref name="caller"/> (and, if sent, one of their own users) is
        /// actually allowed to see. Same response shape as native Jellyfin's
        /// Views endpoint so the client-side parser needs no changes.
        /// </summary>
        [HttpGet("Peer/Libraries")]
        [AllowAnonymous]
        public IActionResult GetPeerLibraries()
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            var remoteUserId = RequestingRemoteUserId();
            var items = _libraryManager.GetVirtualFolders()
                .Where(f => _peerAccess.IsLibraryVisible(caller, remoteUserId, f.ItemId))
                .Select(f => new
                {
                    Id = f.ItemId,
                    f.Name,
                    CollectionType = f.CollectionType?.ToString()
                })
                .ToList();

            return Ok(new { Items = items, TotalRecordCount = items.Count });
        }

        /// <summary>
        /// Replaces a friend's old native <c>/Users/{id}/Items</c> call
        /// (<see cref="Services.RemoteServerClient.GetItemsAsync"/>). Fetches the
        /// real, unfiltered response from this server's own loopback, then drops
        /// every item <paramref name="caller"/> is not allowed to see - excluded
        /// items, anything outside their (or their specific remote user's) shared
        /// scope - before returning it. <c>parentId</c> is one of this server's
        /// own library folder ids (from <see cref="GetPeerLibraries"/>), so the
        /// whole-library scope check uses it directly rather than resolving each
        /// item's own top parent.
        /// <para>
        /// Filtering happens AFTER the internal page is fetched, which previously
        /// broke every client's paging contract: they page until a response comes
        /// back with fewer than <c>limit</c> items, but a filtered page comes
        /// back short even when more content remained - so a friend whose caller
        /// had anything excluded (hidden items, per-user rules, library scope)
        /// silently stopped importing at the first partially-filtered page,
        /// dropping every later item from their sync entirely. Pages here are
        /// therefore filled by over-fetching internally until <c>limit</c>
        /// visible items have accumulated or the source is exhausted, restoring
        /// "a short page means there is genuinely nothing left".
        /// </para>
        /// </summary>
        [HttpGet("Peer/Items")]
        [AllowAnonymous]
        public async Task<IActionResult> GetPeerItems(
            [FromQuery] string? mediaType,
            [FromQuery] string? parentId,
            [FromQuery] int? startIndex,
            [FromQuery] int? limit,
            [FromQuery] string? sortBy,
            [FromQuery] string? sortOrder,
            CancellationToken cancellationToken)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            var userId = ResolveInternalQueryUserId();
            if (userId == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No local user available to serve this request" });
            }

            var queryParams = new List<string>
            {
                "Recursive=true",
                // MediaSources deliberately excluded: nothing on the sync path ever
                // reads it (playback fetches media sources separately, per-item, from
                // PlaybackInfo - see FederationMediaSourceProvider.FetchRemoteSourceAsync).
                // It was pure dead weight on every page of every sync: on top of forcing
                // Jellyfin to load one more related collection per item, requesting it
                // alongside MediaStreams/People/Genres/Tags/Studios triggers EF Core's
                // "multiple collection Include" query-splitting warning (no
                // QuerySplittingBehavior configured), which is a real slow-query hit
                // repeated on every 200-item page across every mapping, every sync.
                "Fields=BasicSyncInfo,Path,MediaStreams,Overview,Genres,Tags,Studios,People,ProviderIds,OriginalTitle,ProductionYear,DateCreated",
                "EnableImageTypes=Primary,Backdrop,Banner,Thumb"
            };
            if (string.Equals(sortBy, "DateCreated", StringComparison.OrdinalIgnoreCase))
            {
                queryParams.Add("SortBy=DateCreated");
                queryParams.Add(string.Equals(sortOrder, "Ascending", StringComparison.OrdinalIgnoreCase)
                    ? "SortOrder=Ascending"
                    : "SortOrder=Descending");
            }
            else if (string.Equals(sortBy, "EpisodeOrder", StringComparison.OrdinalIgnoreCase))
            {
                // Season/episode order for BrowseSeriesEpisodes - browsing one
                // show's own episode list needs watch order, not date added,
                // which is what every other caller of this endpoint sorts by.
                // A fixed literal, not sortBy forwarded as-is, so this endpoint
                // never passes caller-supplied text straight into the internal
                // Items API's SortBy query param.
                queryParams.Add("SortBy=ParentIndexNumber,IndexNumber,SortName");
                queryParams.Add("SortOrder=Ascending");
            }
            if (!string.IsNullOrEmpty(mediaType))
            {
                queryParams.Add($"IncludeItemTypes={Uri.EscapeDataString(mediaType)}");
            }

            if (!string.IsNullOrEmpty(parentId))
            {
                queryParams.Add($"ParentId={Uri.EscapeDataString(parentId)}");
            }

            // No limit in the request means "everything", exactly as before: an
            // effectively-unbounded target keeps the loop running until the
            // internal source is exhausted.
            var target = limit.HasValue && limit.Value > 0 ? limit.Value : int.MaxValue;

            var remoteUserId = RequestingRemoteUserId();
            var pageItems = new System.Text.Json.Nodes.JsonArray();
            var cursor = Math.Max(0, startIndex ?? 0);
            var safetyPage = 0;

            while (pageItems.Count < target)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++safetyPage > 10_000)
                {
                    _logger.LogWarning("[Federation] Peer/Items hit the internal paging safety cap for {Caller} (library {Library}); returning what was collected", caller.Name, parentId ?? "(none)");
                    break;
                }

                // Internal pages are bounded independently of the caller's limit:
                // fewer visible items than asked for just means another internal
                // round, not an unbounded single query against our own API.
                var requestCount = Math.Min(target - pageItems.Count, InternalPeerItemPageSize);

                var pageParams = new List<string>(queryParams)
                {
                    $"StartIndex={cursor}",
                    $"Limit={requestCount}"
                };

                var json = await FetchInternalJsonAsync($"/Users/{userId:N}/Items?{string.Join("&", pageParams)}", cancellationToken).ConfigureAwait(false);
                if (json?["Items"] is not System.Text.Json.Nodes.JsonArray items || items.Count == 0)
                {
                    break;
                }

                var rawCount = items.Count;

                // Removing each kept node detaches it from the fetched page's
                // array, so it can be re-parented into the response array below
                // (a JsonNode may only ever have one parent).
                foreach (var item in items.ToList())
                {
                    if (item != null
                        && Guid.TryParse(item["Id"]?.GetValue<string>(), out var itemGuid)
                        && _peerAccess.IsItemVisible(caller, remoteUserId, itemGuid, parentId))
                    {
                        items.Remove(item);
                        pageItems.Add(item);
                    }
                }

                cursor += rawCount;
                if (rawCount < requestCount)
                {
                    // The internal source returned fewer raw items than asked -
                    // there is nothing further to fill the page from.
                    break;
                }
            }

            while (pageItems.Count > target)
            {
                pageItems.RemoveAt(pageItems.Count - 1);
            }

            return new ContentResult
            {
                Content = new System.Text.Json.Nodes.JsonObject
                {
                    ["Items"] = pageItems,
                    ["TotalRecordCount"] = pageItems.Count
                }.ToJsonString(),
                ContentType = "application/json",
                StatusCode = StatusCodes.Status200OK
            };
        }

        /// <summary>
        /// Replaces a friend's old native <c>/Users/{id}/Items/{itemId}</c> call
        /// (<see cref="Services.RemoteServerClient.GetItemAsync"/>). Same
        /// filtering as <see cref="GetPeerItems"/>, for exactly one item -
        /// resolves the item's own top library folder itself since, unlike a
        /// listing call, there is no already-known <c>parentId</c> to reuse.
        /// </summary>
        [HttpGet("Peer/Items/{itemId}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetPeerItem(string itemId, CancellationToken cancellationToken)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                return BadRequest();
            }

            if (!_peerAccess.IsItemVisible(caller, RequestingRemoteUserId(), itemGuid))
            {
                return NotFound();
            }

            var userId = ResolveInternalQueryUserId();
            if (userId == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No local user available to serve this request" });
            }

            var json = await FetchInternalJsonAsync($"/Users/{userId:N}/Items/{itemGuid:N}", cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return NotFound();
            }

            return new ContentResult
            {
                Content = json.ToJsonString(),
                ContentType = "application/json",
                StatusCode = StatusCodes.Status200OK
            };
        }

        /// <summary>
        /// Replaces a friend's old native <c>/Items/{id}/PlaybackInfo</c> call
        /// (<see cref="Services.RemoteServerClient.GetPlaybackInfoAsync"/>).
        /// Access is re-checked here even though a friend would normally have
        /// already been filtered out of <see cref="GetPeerItems"/>/<see cref="GetPeerItem"/> -
        /// sharing scope/excludes/rules can change between when a friend last
        /// synced and when it actually asks how to play something.
        /// </summary>
        [HttpGet("Peer/PlaybackInfo/{itemId}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetPeerPlaybackInfo(string itemId, CancellationToken cancellationToken)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                return BadRequest();
            }

            if (!_peerAccess.IsItemVisible(caller, RequestingRemoteUserId(), itemGuid))
            {
                return NotFound();
            }

            var userId = ResolveInternalQueryUserId();
            if (userId == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "No local user available to serve this request" });
            }

            var json = await FetchInternalJsonAsync($"/Items/{itemGuid:N}/PlaybackInfo?UserId={userId:N}", cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return NotFound();
            }

            return new ContentResult
            {
                Content = json.ToJsonString(),
                ContentType = "application/json",
                StatusCode = StatusCodes.Status200OK
            };
        }

        /// <summary>
        /// Replaces a friend's old native <c>/Users</c> call
        /// (<see cref="Services.RemoteServerClient.GetUsersAsync"/>): this
        /// server's own local user accounts, so a friend's admin can pick one for
        /// a <see cref="RemoteUserAccessRule"/> (see <c>GetRemoteUsers</c>) - the
        /// only remaining reason anything needs this list under the new model,
        /// since sync/playback no longer impersonates any of this server's users.
        /// Same shape as native <c>/Users</c> (Id, Name, Policy.IsAdministrator)
        /// so <see cref="Services.UserDto"/>'s existing deserialization needs no
        /// changes.
        /// </summary>
        [HttpGet("Peer/Users")]
        [AllowAnonymous]
        public IActionResult GetPeerUsers()
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            var users = new List<object>();
            foreach (var u in EnumerateLocalUsers())
            {
                var t = u.GetType();
                var id = t.GetProperty("Id")?.GetValue(u);
                var name = t.GetProperty("Username")?.GetValue(u) as string;
                if (id is Guid guid && name != null)
                {
                    users.Add(new { Id = guid.ToString("N"), Name = name, Policy = new { IsAdministrator = IsAdministratorUser(u) } });
                }
            }

            return Ok(users);
        }

        /// <summary>
        /// Replaces a friend's old native <c>/System/Info</c> call
        /// (<see cref="Services.RemoteServerClient.GetSystemInfoDetailedAsync"/>),
        /// used by the config page's "Test" button. <c>/System/Info/Public</c>
        /// (already anonymous and harmless) still covers basic reachability
        /// checks; this one needs a valid federation token instead of a real
        /// admin-equivalent key, matching everything else under the new model.
        /// </summary>
        [HttpGet("Peer/SystemInfo")]
        [AllowAnonymous]
        public IActionResult GetPeerSystemInfo()
        {
            if (FederationTokenAuth.ResolveCaller(Request) == null)
            {
                return Unauthorized();
            }

            var hostType = _applicationHost.GetType();
            var version = hostType.GetProperty("ApplicationVersionString")?.GetValue(_applicationHost) as string
                ?? hostType.GetProperty("ApplicationVersion")?.GetValue(_applicationHost)?.ToString()
                ?? string.Empty;
            var systemId = hostType.GetProperty("SystemId")?.GetValue(_applicationHost) as string ?? string.Empty;

            return Ok(new
            {
                ServerName = _applicationHost.FriendlyName,
                Version = version,
                OperatingSystem = string.Empty,
                Id = systemId,
                FederationPluginVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            });
        }

        #endregion

        #region Incoming content filters (Catalog)

        [HttpGet("IncomingFilter")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetIncomingFilter()
        {
            var f = Plugin.Instance?.Configuration?.IncomingFilter ?? new IncomingContentFilter();
            return Ok(f);
        }

        [HttpPost("IncomingFilter")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetIncomingFilter([FromBody] IncomingContentFilter body)
        {
            var config = Plugin.Instance!.Configuration;
            config.IncomingFilter = body ?? new IncomingContentFilter();
            // Normalise lists to trimmed, non-empty entries
            config.IncomingFilter.AllowedItemTypes = (config.IncomingFilter.AllowedItemTypes ?? new List<string>())
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            config.IncomingFilter.BlockedTags = (config.IncomingFilter.BlockedTags ?? new List<string>())
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            config.IncomingFilter.BlockedGenres = (config.IncomingFilter.BlockedGenres ?? new List<string>())
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            config.IncomingFilter.MaxAllowedRating = (config.IncomingFilter.MaxAllowedRating ?? string.Empty).Trim();
            Plugin.Instance.SaveConfiguration();
            return Ok(new { success = true, message = "Incoming filters saved." });
        }

        [HttpPost("Friends/{id}/DownloadAccess")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetFriendDownloadAccess(string id, [FromBody] DownloadAccessBody body)
        {
            var config = Plugin.Instance!.Configuration;
            var server = config.RemoteServers.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            server.AllowDownloads = body?.AllowDownloads ?? true;
            Plugin.Instance.SaveConfiguration();
            return Ok(new { success = true, message = "Download access updated." });
        }

        #endregion

        #region Hidden Items (local suppression)
        //
        // A purely local, receiving-side "don't show me this" list - the opposite
        // direction from per-friend sharing permissions (which gate what a friend can
        // see of *this* server's own content). Hiding a federated item here never
        // touches the cache and is never communicated to the friend server; they keep
        // thinking they're sharing it normally. See
        // Configuration.PluginConfiguration.HiddenFederatedItemIds and
        // Services.FederationItemPersistenceService.ReconcileMappingAsync (the
        // enforcement point) for the rest of the mechanism.

        /// <summary>
        /// Hides a federated item from this server's own local browsing/search/home.
        /// Resolves the local item id (as shown on its detail page) to the stable
        /// <see cref="Services.FederatedCacheEntry.Key"/> stamped on it as the
        /// <c>FederationKey</c> provider id, records that key so the next
        /// reconciliation pass never recreates it, and also deletes the item right
        /// now rather than waiting for that pass - hiding something should feel
        /// immediate, not "eventually, next sync". Only the local virtual item is
        /// deleted; the underlying cache entry (and the friend's own copy) is
        /// untouched, so unhiding it later just needs a fresh reconciliation pass to
        /// bring it back.
        /// </summary>
        [HttpPost("HiddenItems/Hide")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult HideItem([FromBody] HideItemBody body)
        {
            if (!Guid.TryParse(body?.ItemId, out var itemGuid))
            {
                return BadRequest(new { success = false, message = "Invalid item id" });
            }

            var item = _libraryManager.GetItemById(itemGuid);
            var key = FederationLibraryManager.GetFederationKey(item);
            if (item == null || key == null)
            {
                return NotFound(new { success = false, message = "Not a federated item" });
            }

            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                config.HiddenFederatedItemIds ??= new List<string>();
                if (!config.HiddenFederatedItemIds.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    config.HiddenFederatedItemIds.Add(key);
                    Plugin.Instance?.SaveConfiguration();
                }
            }

            try
            {
                _libraryManager.DeleteItem(item, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = false });
            }
            catch (Exception ex)
            {
                // The hide list entry is already saved either way, so the next
                // reconciliation pass will remove it even if this immediate delete
                // failed for some reason - log and report success on that basis.
                _logger.LogWarning(ex, "[Federation] Hid {Key} but could not delete the local item immediately; it will be removed on the next sync", key);
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// Lists items currently on the local hide list, resolving each key back to a
        /// display name/type from the cache (the underlying materialized item is
        /// usually already deleted at this point) so the config page can show an
        /// admin what they hid, not just an opaque key.
        /// </summary>
        [HttpGet("HiddenItems")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult<object> GetHiddenItems()
        {
            var config = Plugin.Instance?.Configuration;
            var keys = config?.HiddenFederatedItemIds ?? new List<string>();

            var items = keys.Select(key =>
            {
                var entry = _cache.GetEntryByKey(key);
                return new
                {
                    key,
                    name = entry?.Metadata.Name ?? "(no longer in cache)",
                    itemType = entry?.ItemType,
                    mappingName = entry?.MappingName
                };
            }).ToList();

            return Ok(items);
        }

        /// <summary>
        /// Removes an item from the local hide list. Does not recreate it directly -
        /// that happens on the next reconciliation pass (a scheduled sync, or the
        /// "Provision libraries"/"Refresh" actions already on this page), the same
        /// way any other newly-visible entry would be picked up.
        /// </summary>
        [HttpPost("HiddenItems/Unhide")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult UnhideItem([FromBody] UnhideItemBody body)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.HiddenFederatedItemIds == null || string.IsNullOrEmpty(body?.Key))
            {
                return Ok(new { success = true });
            }

            var removed = config.HiddenFederatedItemIds.RemoveAll(k => string.Equals(k, body.Key, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Plugin.Instance?.SaveConfiguration();
            }

            return Ok(new { success = true });
        }

        #endregion

        #region Outgoing sharing (stop sharing this item with anyone)
        //
        // The opposite direction from Hidden Items above: this is a sending-side
        // "never share this" toggle on one of *this server's* own items, not a
        // receiving-side suppression of a friend's. See
        // Configuration.PluginConfiguration.GloballyExcludedItemIds and
        // Services.FederationPeerAccessService.IsItemVisible (the enforcement
        // point) for the rest of the mechanism. Per-friend/per-user exclusion
        // already existed via Friends/{id}/Sharing and Friends/{id}/RemoteUserRule;
        // these three endpoints are the "everyone" case, plus the ids list the
        // item detail page's own button/badge (Web/federation-badge.js) and the
        // settings page's Catalog picker both read to know current state.

        /// <summary>
        /// Stops sharing one of this server's own items with every friend,
        /// present and future. The detail-page "Stop sharing" button and the
        /// Catalog picker's "Hide from everyone" action both call this.
        /// </summary>
        [HttpPost("Sharing/Disable")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult DisableSharing([FromBody] HideItemBody body)
        {
            if (!Guid.TryParse(body?.ItemId, out var itemGuid))
            {
                return BadRequest(new { success = false, message = "Invalid item id" });
            }

            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                config.GloballyExcludedItemIds ??= new List<string>();
                var idString = itemGuid.ToString("N");
                if (!config.GloballyExcludedItemIds.Contains(idString, StringComparer.OrdinalIgnoreCase))
                {
                    config.GloballyExcludedItemIds.Add(idString);
                    Plugin.Instance?.SaveConfiguration();
                }
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// Resumes sharing a previously globally-disabled item, subject to
        /// whatever per-friend/per-user scope already applied before it was
        /// disabled (this only removes the "everyone" override, it does not touch
        /// <see cref="RemoteServer.ExcludedItemIds"/> or any
        /// <see cref="RemoteUserAccessRule.BlockedItemIds"/>).
        /// </summary>
        [HttpPost("Sharing/Enable")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult EnableSharing([FromBody] HideItemBody body)
        {
            if (!Guid.TryParse(body?.ItemId, out var itemGuid))
            {
                return BadRequest(new { success = false, message = "Invalid item id" });
            }

            var config = Plugin.Instance?.Configuration;
            if (config?.GloballyExcludedItemIds != null)
            {
                var removed = config.GloballyExcludedItemIds.RemoveAll(id => string.Equals(id, itemGuid.ToString("N"), StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    Plugin.Instance?.SaveConfiguration();
                }
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// Returns the local item ids (format "N") currently excluded from sharing
        /// with everyone, for <see cref="GetClientScript"/> to badge in the UI.
        /// Ids only, no other item data, so anonymous is fine here too - same
        /// reasoning as <see cref="GetFederatedIds"/>.
        /// </summary>
        [HttpGet("Sharing/DisabledIds")]
        [AllowAnonymous]
        [Produces("application/json")]
        public ActionResult<object> GetGloballyDisabledIds()
        {
            var ids = Plugin.Instance?.Configuration?.GloballyExcludedItemIds ?? new List<string>();
            return Ok(ids);
        }

        #endregion

        #region Refresh / Library Provisioning

        [HttpPost("Refresh")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> TriggerRefresh(CancellationToken cancellationToken)
        {
            var result = await _syncService.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { result.Success, result.Message, result.ItemCount, result.FailedSources, result.OperationId });
        }

        [HttpPost("RefreshServer")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RefreshServer([FromBody] RefreshServerRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request?.ServerId))
            {
                return BadRequest(new { success = false, message = "serverId is required" });
            }

            var result = await _syncService.SyncServerAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
            return Ok(new { result.Success, result.Message, result.ItemCount, result.FailedSources });
        }

        [HttpPost("ProvisionLibraries")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> ProvisionLibraries(CancellationToken cancellationToken)
        {
            await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { success = true, message = "Libraries provisioned" });
        }

        #endregion

        #region Mappings

        [HttpGet("Mappings")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult<List<LibraryMapping>> GetMappings()
            => Ok(Plugin.Instance?.Configuration?.LibraryMappings ?? new List<LibraryMapping>());

        #endregion

        #region Status / Progress

        [HttpGet("Status")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetStatus()
        {
            var config = Plugin.Instance?.Configuration;

            // Assembly version, not meta.json - meta is the repository's release
            // manifest (never shipped inside the plugin), while the assembly
            // version is what's actually running, set from the same number in the
            // .csproj on every build. GetPeerSystemInfo already uses this exact
            // pattern for the remote-side version it reports on Test.
            var pluginVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();

            return Ok(new
            {
                totalServers = config?.RemoteServers?.Count ?? 0,
                activeServers = config?.RemoteServers?.Count(s => s.Enabled) ?? 0,
                federatedItems = _federationManager.Cache.Count,
                pluginVersion,

                // Never refreshed comes back as DateTime.MinValue, not null - null it
                // here so the config page's "d.lastRefresh ? ... : 'Never'" check
                // actually works instead of formatting 0001-01-01 into something like
                // "12/31/1" (year 1, off by a day from the UTC->local conversion).
                lastRefresh = _federationManager.Cache.LastRefresh == DateTime.MinValue
                    ? (DateTime?)null
                    : _federationManager.Cache.LastRefresh,

                // Whether the last sync actually worked. Without this the page can
                // only show counts, which look identical whether federation is
                // healthy or has been failing every cycle for hours.
                lastSync = _syncService.LastSync == null ? null : new
                {
                    finishedUtc = _syncService.LastSync.FinishedUtc,
                    success = _syncService.LastSync.Success,
                    message = _syncService.LastSync.Message,
                    itemCount = _syncService.LastSync.ItemCount,
                    failedSources = _syncService.LastSync.FailedSources
                },
                servers = (config?.RemoteServers ?? new List<RemoteServer>()).Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    enabled = s.Enabled,
                    streamingMode = s.StreamingMode.ToString()
                }).ToList()
            });
        }

        [HttpGet("Progress/{operationId}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetProgress(string operationId)
        {
            var progress = SyncProgressTracker.Get(operationId);
            if (progress == null)
            {
                return NotFound(new { error = "Operation not found" });
            }

            return Ok(new
            {
                operationId = progress.OperationId,
                processedItems = progress.ProcessedItems,
                status = progress.Status,
                isComplete = progress.IsComplete,
                success = progress.Success,
                elapsedSeconds = progress.ElapsedTime.TotalSeconds
            });
        }

        /// <summary>
        /// Admin-triggered: downloads a federated item's media file to this server so
        /// it becomes a normal local item, no longer dependent on the friend's server.
        /// Starts a background job and returns immediately; poll
        /// <see cref="GetDownloadProgress"/> for status.
        /// </summary>
        [HttpPost("Download")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult StartDownload([FromBody] DownloadItemBody body)
        {
            // Temporarily disabled - see the matching guard on BrowseDownload and
            // ApplyQualityUpgrades below. Restore the _downloadService.StartDownload
            // call (see git history) to re-enable.
            return BadRequest(new { success = false, message = "Downloading federated content to this server is temporarily disabled." });
        }

        /// <summary>
        /// Polls the progress of a "download to server" job started via
        /// <see cref="StartDownload"/>.
        /// </summary>
        [HttpGet("Download/Progress/{operationId}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetDownloadProgress(string operationId)
        {
            var progress = DownloadProgressTracker.Get(operationId);
            if (progress == null)
            {
                return NotFound(new { error = "Operation not found" });
            }

            return Ok(new
            {
                operationId = progress.OperationId,
                itemName = progress.ItemName,
                percentComplete = progress.PercentComplete,
                status = progress.Status,
                isComplete = progress.IsComplete,
                success = progress.Success,
                bytesDownloaded = progress.BytesDownloaded,
                totalBytes = progress.TotalBytes,
                bytesPerSecond = progress.BytesPerSecond,
                destinationPath = progress.DestinationPath
            });
        }

        /// <summary>
        /// Admin-triggered: lists every tracked download (active or recently
        /// finished) - backs the dashboard's Downloads section and the
        /// cover-art progress ring shown on cards site-wide via the client
        /// badge script.
        /// </summary>
        [HttpGet("Downloads")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetDownloads()
        {
            var downloads = DownloadProgressTracker.GetAll().Select(d => new
            {
                operationId = d.OperationId,
                localItemId = d.LocalItemId,
                itemName = d.ItemName,
                percentComplete = d.PercentComplete,
                status = d.Status,
                isComplete = d.IsComplete,
                success = d.Success,
                bytesDownloaded = d.BytesDownloaded,
                totalBytes = d.TotalBytes,
                bytesPerSecond = d.BytesPerSecond,
                destinationPath = d.DestinationPath,
                startTime = d.StartTime
            });

            return Ok(downloads);
        }

        #region Browse & Selective Download

        /// <summary>
        /// Lists the libraries a connected server exposes, for the Browse tab's
        /// server picker. Works for a Jellyfin peer (native <c>Peer/Libraries</c>)
        /// and an external kind (Plex today, via its <see cref="IExternalCatalogProvider"/>)
        /// alike - the same duality <see cref="TestServer"/> already routes on.
        /// </summary>
        [HttpGet("Browse/{serverId}/Libraries")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> BrowseLibraries(string serverId, CancellationToken cancellationToken)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Server not found." });
            }

            if (server.Kind != ServerKind.Jellyfin)
            {
                var provider = _externalCatalogs.For(server);
                var libs = provider == null
                    ? new List<ExternalLibrary>()
                    : (await provider.GetAllLibrariesAsync(server, cancellationToken).ConfigureAwait(false)).ToList();
                return Ok(libs.Select(l => new { id = l.Id, name = l.Name }).ToList());
            }

            var client = _clientFactory.GetClient(server);
            var views = await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
            return Ok((views ?? new List<MediaBrowser.Model.Dto.BaseItemDto>()).Select(v => new { id = v.Id.ToString(), name = v.Name }).ToList());
        }

        /// <summary>
        /// Lists the items under one library on a connected server, paginated -
        /// backs the Browse tab's item grid. Native ids only: the returned
        /// <c>id</c> is directly what <see cref="BrowseDownload"/> expects back,
        /// whether that is a Jellyfin peer's own item Guid or a Plex
        /// <see cref="ExternalItem.NativeId"/> rating key.
        /// </summary>
        [HttpGet("Browse/{serverId}/Items")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> BrowseItems(
            string serverId,
            [FromQuery] string libraryId,
            [FromQuery] string? type,
            [FromQuery] string? sort,
            [FromQuery] int startIndex,
            [FromQuery] int? limit,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(libraryId))
            {
                return BadRequest(new { success = false, message = "libraryId is required." });
            }

            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Server not found." });
            }

            // Capped well below what a client could ask for - a friend's library
            // can run into the tens of thousands of items, and this is a synchronous
            // per-request fetch (unlike the paged background sync), not something
            // to let balloon into a multi-minute remote call.
            var pageSize = Math.Clamp(limit ?? 60, 1, 200);

            // TV browsing lists Series here, not Episode - a flat page of the
            // most-recently-added episodes across every show reads as random to
            // an admin looking for "the episodes of this show". Episode is still
            // accepted for callers that genuinely want a flat episode list (none
            // currently do), and one show's own episodes are fetched in order via
            // BrowseSeriesEpisodes once its card is opened.
            var requestedKind = type switch
            {
                _ when string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase) => Jellyfin.Data.Enums.BaseItemKind.Episode,
                _ when string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase) => Jellyfin.Data.Enums.BaseItemKind.Series,
                _ => Jellyfin.Data.Enums.BaseItemKind.Movie
            };

            IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> localOwned;
            try
            {
                localOwned = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false,
                    IncludeItemTypes = new[] { requestedKind }
                })
                .Where(i => FederationLibraryManager.GetFederationKey(i) == null)
                .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not build local duplicate index for Downloads");
                localOwned = Array.Empty<MediaBrowser.Controller.Entities.BaseItem>();
            }

            bool HasLocalCopy(MediaBrowser.Model.Dto.BaseItemDto remote)
                => HasEquivalentLocalCopy(
                    remote,
                    localOwned,
                    Plugin.Instance?.Configuration?.DedupProviderIds ?? new List<string> { "imdb", "tmdb", "tvdb" });

            if (server.Kind != ServerKind.Jellyfin)
            {
                // GetAllItemsAsync, not GetItemsAsync - Browse is ad-hoc exploration
                // independent of AllowedExternalLibraryIds sync consent (see that
                // interface method's own doc comment). Using the sync-gated variant
                // here made Browse come back empty for any library not yet allowed
                // for auto-sync, even though nothing about looking at it or
                // downloading one item requires that consent.
                var provider = _externalCatalogs.For(server);
                var items = provider == null
                    ? null
                    : await provider.GetAllItemsAsync(server, libraryId, cancellationToken).ConfigureAwait(false);
                var page = (items ?? new List<ExternalItem>())
                    .Where(i => i.Dto.Type == requestedKind)
                    .OrderByDescending(i => i.Dto.DateCreated)
                    .ThenBy(i => i.Dto.Name, StringComparer.OrdinalIgnoreCase)
                    .Skip(Math.Max(0, startIndex))
                    .Take(pageSize);
                return Ok(page.Select(i => new
                {
                    id = i.NativeId,
                    name = i.Dto.Name,
                    type = i.Dto.Type.ToString(),
                    year = i.Dto.ProductionYear,
                    dateCreated = i.Dto.DateCreated,
                    hasLocalCopy = HasLocalCopy(i.Dto),
                    seriesName = i.Dto.SeriesName,
                    parentIndexNumber = i.Dto.ParentIndexNumber,
                    indexNumber = i.Dto.IndexNumber,
                    overview = i.Dto.Overview,
                    genres = i.Dto.Genres,
                    officialRating = i.Dto.OfficialRating,
                    communityRating = i.Dto.CommunityRating
                }).ToList());
            }

            var client = _clientFactory.GetClient(server);
            var jfItems = await client.GetItemsAsync(
                mediaType: requestedKind.ToString(),
                parentId: libraryId,
                startIndex: Math.Max(0, startIndex),
                limit: pageSize,
                sortBy: "DateCreated",
                sortOrder: "Descending",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Ok((jfItems ?? new List<MediaBrowser.Model.Dto.BaseItemDto>()).Select(i => new
            {
                id = i.Id.ToString(),
                name = i.Name,
                type = i.Type.ToString(),
                year = i.ProductionYear,
                dateCreated = i.DateCreated,
                hasLocalCopy = HasLocalCopy(i),
                seriesName = i.SeriesName,
                parentIndexNumber = i.ParentIndexNumber,
                indexNumber = i.IndexNumber,
                overview = i.Overview,
                genres = i.Genres,
                officialRating = i.OfficialRating,
                communityRating = i.CommunityRating
            }).ToList());
        }

        /// <summary>
        /// Lists one TV show's episodes, across every season, in watch order -
        /// backs the drill-down from a show card in <see cref="BrowseItems"/>'s
        /// Series listing. Deliberately a separate endpoint rather than folding
        /// "parentId=seriesId" into <see cref="BrowseItems"/>: that endpoint's
        /// paging/sort story (newest-added-first, page size capped well below a
        /// full show) is for browsing a whole library, not for showing one
        /// show's episode list, which needs season/episode order instead and is
        /// small enough that it is not worth paginating.
        /// </summary>
        [HttpGet("Browse/{serverId}/Series/{seriesId}/Episodes")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> BrowseSeriesEpisodes(
            string serverId,
            string seriesId,
            [FromQuery] string? libraryId,
            CancellationToken cancellationToken)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Server not found." });
            }

            IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> localOwned;
            try
            {
                localOwned = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false,
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode }
                })
                .Where(i => FederationLibraryManager.GetFederationKey(i) == null)
                .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not build local duplicate index for series episode browse");
                localOwned = Array.Empty<MediaBrowser.Controller.Entities.BaseItem>();
            }

            bool HasLocalCopy(MediaBrowser.Model.Dto.BaseItemDto remote)
                => HasEquivalentLocalCopy(
                    remote,
                    localOwned,
                    Plugin.Instance?.Configuration?.DedupProviderIds ?? new List<string> { "imdb", "tmdb", "tvdb" });

            List<(string Id, MediaBrowser.Model.Dto.BaseItemDto Dto)> episodes;

            if (server.Kind != ServerKind.Jellyfin)
            {
                if (string.IsNullOrWhiteSpace(libraryId))
                {
                    return BadRequest(new { success = false, message = "libraryId is required." });
                }

                var provider = _externalCatalogs.For(server);
                var items = provider == null
                    ? null
                    : await provider.GetAllItemsAsync(server, libraryId, cancellationToken).ConfigureAwait(false);
                if (items == null)
                {
                    return Ok(new List<object>());
                }

                var series = items.FirstOrDefault(i => i.NativeId == seriesId && i.Dto.Type == Jellyfin.Data.Enums.BaseItemKind.Series);
                if (series == null)
                {
                    return Ok(new List<object>());
                }

                episodes = items
                    .Where(i => i.Dto.Type == Jellyfin.Data.Enums.BaseItemKind.Episode && i.Dto.SeriesId == series.Dto.Id)
                    .OrderBy(i => i.Dto.ParentIndexNumber ?? 0)
                    .ThenBy(i => i.Dto.IndexNumber ?? 0)
                    .Select(i => (i.NativeId, i.Dto))
                    .ToList();
            }
            else
            {
                var client = _clientFactory.GetClient(server);

                // Recursive on the receiving side (see Peer/Items) means ParentId
                // matches every descendant, not just direct children, so this
                // reaches episodes through their season without needing to
                // enumerate seasons here at all.
                var jfItems = await client.GetItemsAsync(
                    mediaType: "Episode",
                    parentId: seriesId,
                    sortBy: "EpisodeOrder",
                    sortOrder: "Ascending",
                    limit: 2000,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                episodes = (jfItems ?? new List<MediaBrowser.Model.Dto.BaseItemDto>())
                    .Select(i => (i.Id.ToString(), i))
                    .ToList();
            }

            return Ok(episodes.Select(e => new
            {
                id = e.Id,
                name = e.Dto.Name,
                type = e.Dto.Type.ToString(),
                seriesName = e.Dto.SeriesName,
                parentIndexNumber = e.Dto.ParentIndexNumber,
                indexNumber = e.Dto.IndexNumber,
                dateCreated = e.Dto.DateCreated,
                hasLocalCopy = HasLocalCopy(e.Dto),
                overview = e.Dto.Overview,
                communityRating = e.Dto.CommunityRating
            }).ToList());
        }

        // Dedicated, short-timeout client for the browse-time image proxy below -
        // a cover image is small and should fail fast rather than hang a page
        // load; the long-timeout DownloadHttpClient equivalents elsewhere exist
        // for whole media files, which this is not.
        private static readonly HttpClient BrowseImageHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// Cover art for one item in the Browse tab's catalog grid, proxied
        /// through this server so neither kind of remote credential ever reaches
        /// the admin's browser directly: a Jellyfin peer's image is fetched using
        /// a short-lived, single-item-scoped token (the same one
        /// <see cref="FederationStreamHandler"/> mints for playback - see
        /// <c>Peer/Images</c> on the receiving side), and a Plex source's image
        /// URL carries a real, whole-server Plex token that must never leave the
        /// server (see <see cref="IExternalCatalogProvider.GetImagesAsync"/>'s own
        /// doc comment). Both are fetched here and the bytes streamed straight
        /// back - the browser never sees either credential.
        /// </summary>
        [HttpGet("Browse/{serverId}/Image")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> BrowseImage(string serverId, [FromQuery] string itemId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return NotFound();
            }

            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                return NotFound();
            }

            string? imageUrl;
            if (server.Kind != ServerKind.Jellyfin)
            {
                var provider = _externalCatalogs.For(server);
                var images = provider == null
                    ? null
                    : await provider.GetImagesAsync(server, itemId, cancellationToken).ConfigureAwait(false);
                imageUrl = images?.PrimaryUrl;
            }
            else
            {
                var client = _clientFactory.GetClient(server);
                var (token, _) = await client.GetPlaybackTokenAsync(itemId, cancellationToken).ConfigureAwait(false);
                imageUrl = token == null
                    ? null
                    : $"{server.Url.TrimEnd('/')}/Plugins/Federation/Peer/Images/{itemId}/Primary?token={Uri.EscapeDataString(token)}";
            }

            if (imageUrl == null)
            {
                return NotFound();
            }

            try
            {
                using var response = await BrowseImageHttpClient.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return NotFound();
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                return File(bytes, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Browse image fetch failed for {Server}/{Item}", server.Name, itemId);
                return NotFound();
            }
        }

        /// <summary>
        /// Downloads one item straight off a server's browsed catalog to this
        /// server's disk - does not require the item to already be federated into
        /// a mapped library first. Runs in the background; poll
        /// <see cref="GetDownloadProgress"/> (same tracker <see cref="GetDownloads"/>
        /// already lists) with the returned operation id.
        /// </summary>
        [HttpPost("Browse/Download")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult BrowseDownload([FromBody] BrowseDownloadBody body)
        {
            // Temporarily disabled - see the matching guard on StartDownload above
            // and ApplyQualityUpgrades below. Restore the
            // _downloadService.StartBrowseDownload call (see git history) to re-enable.
            return BadRequest(new { success = false, message = "Downloading federated content to this server is temporarily disabled." });
        }

        #endregion

        #region Prefer Higher Quality

        /// <summary>
        /// Lists locally-owned items where a friend's server holds a meaningfully
        /// better copy (see <see cref="FederationQualityAdvisorService"/>) - backs
        /// the review list shown on the config page. Always empty when
        /// <see cref="PluginConfiguration.PreferHigherQualityRemotes"/> is off,
        /// regardless of what a caller asks for - this is never computed, let
        /// alone acted on, without the admin having opted in first.
        /// </summary>
        [HttpGet("QualityUpgrades")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetQualityUpgrades()
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.PreferHigherQualityRemotes != true)
            {
                return Ok(new List<object>());
            }

            var candidates = _qualityAdvisor.FindUpgrades();
            return Ok(candidates.Select(c => new
            {
                localItemId = c.LocalItemId,
                name = c.Name,
                year = c.Year,
                localHeight = c.LocalHeight,
                localBitrate = c.LocalBitrate,
                remoteHeight = c.RemoteHeight,
                remoteBitrate = c.RemoteBitrate,
                remoteServerName = c.RemoteServerName,
                itemType = c.ItemType,
                seriesId = c.SeriesId,
                seriesName = c.SeriesName,
                parentIndexNumber = c.ParentIndexNumber,
                indexNumber = c.IndexNumber
            }).ToList());
        }

        /// <summary>
        /// Admin-triggered from the review list: for each selected item,
        /// downloads the friend's higher-quality copy and only then removes the
        /// old local one (see <see cref="FederationDownloadService.StartQualityReplace"/>
        /// for why in that order). Never runs on its own - this is the only path
        /// that ever removes anything for a quality upgrade, and it only fires on
        /// an explicit admin click naming exact item ids; "select all" on the
        /// config page is just this same call with every candidate's id.
        /// </summary>
        [HttpPost("QualityUpgrades/Apply")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult ApplyQualityUpgrades([FromBody] QualityUpgradeApplyBody body)
        {
            // Temporarily disabled - see the matching guard on StartDownload and
            // BrowseDownload above. Restore the body below (see git history) to
            // re-enable.
            return BadRequest(new { success = false, message = "Downloading federated content to this server is temporarily disabled." });
        }

        /// <summary>
        /// Admin's per-title override to <see cref="PluginConfiguration.PreferHigherQualityRemotes"/>:
        /// stop suggesting this exact local item, even if a friend's copy still
        /// qualifies as an upgrade. Purely a "stop asking" list - never touches
        /// anything already on disk.
        /// </summary>
        [HttpPost("QualityUpgrades/Exclude")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult ExcludeQualityUpgrade([FromBody] QualityUpgradeExcludeBody body)
        {
            if (string.IsNullOrWhiteSpace(body?.ItemId))
            {
                return BadRequest(new { success = false, message = "ItemId is required." });
            }

            var config = Plugin.Instance!.Configuration;
            config.QualityUpgradeExcludedItemIds ??= new List<string>();
            if (!config.QualityUpgradeExcludedItemIds.Contains(body.ItemId, StringComparer.OrdinalIgnoreCase))
            {
                config.QualityUpgradeExcludedItemIds.Add(body.ItemId);
                Plugin.Instance.SaveConfiguration();
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// Reverses <see cref="ExcludeQualityUpgrade"/> - this title is eligible
        /// to be suggested again next time it qualifies.
        /// </summary>
        [HttpDelete("QualityUpgrades/Exclude/{itemId}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult UnexcludeQualityUpgrade(string itemId)
        {
            var config = Plugin.Instance!.Configuration;
            config.QualityUpgradeExcludedItemIds?.RemoveAll(id => string.Equals(id, itemId, StringComparison.OrdinalIgnoreCase));
            Plugin.Instance.SaveConfiguration();
            return Ok(new { success = true });
        }

        /// <summary>
        /// Lists the current per-title exclusions, with names resolved for
        /// display where the local item still exists - backs the "manage
        /// exceptions" list on the config page.
        /// </summary>
        [HttpGet("QualityUpgrades/Excluded")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetExcludedQualityUpgrades()
        {
            var ids = Plugin.Instance?.Configuration?.QualityUpgradeExcludedItemIds ?? new List<string>();
            var results = new List<object>();
            foreach (var id in ids)
            {
                string name = id;
                if (Guid.TryParse(id, out var guid))
                {
                    var item = _libraryManager.GetItemById(guid);
                    if (item != null)
                    {
                        name = item.Name;
                    }
                }

                results.Add(new { itemId = id, name });
            }

            return Ok(results);
        }

        #endregion

        /// <summary>
        /// Admin-triggered: lists every active session currently playing a federated
        /// item - backs the dashboard's "Now watching" indicator.
        /// </summary>
        [HttpGet("NowWatching")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetNowWatching()
        {
            var sessions = _nowWatching.GetNowWatching().Select(s => new
            {
                sessionId = s.SessionId,
                userName = s.UserName,
                itemName = s.ItemName,
                serverName = s.ServerName,
                deviceName = s.DeviceName,
                client = s.Client,
                isPaused = s.IsPaused,
                playMethod = s.PlayMethod,
                positionTicks = s.PositionTicks,
                runtimeTicks = s.RuntimeTicks
            });

            return Ok(sessions);
        }

        /// <summary>
        /// Admin-triggered: cancels an in-progress download started via
        /// <see cref="StartDownload"/>. Deletes whatever was written so far.
        /// </summary>
        [HttpPost("Download/Cancel/{operationId}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult CancelDownload(string operationId)
        {
            var (success, message) = _downloadService.CancelDownload(operationId);
            if (!success)
            {
                return NotFound(new { success, message });
            }

            return Ok(new { success, message });
        }

        [HttpPost("TestAllServers")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> TestAllServers(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.RemoteServers == null || config.RemoteServers.Count == 0)
            {
                return Ok(new { success = false, message = "No servers configured" });
            }

            var results = new List<object>();
            foreach (var server in config.RemoteServers)
            {
                try
                {
                    // A non-Jellyfin server (Plex) has no /System/Info/Public the
                    // way Jellyfin defines it - RemoteServerClient would report it
                    // offline even when it's perfectly reachable. Route external
                    // kinds through their provider (which also validates the
                    // token), same as TestServer above.
                    bool online;
                    if (server.Kind != ServerKind.Jellyfin)
                    {
                        online = await _externalCatalogs.For(server)!
                            .TestConnectionAsync(server, cancellationToken).ConfigureAwait(false) != null;
                    }
                    else
                    {
                        var client = _clientFactory.GetClient(server);
                        online = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                    }

                    results.Add(new
                    {
                        serverId = server.Id,
                        serverName = server.Name,
                        online
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new { serverId = server.Id, serverName = server.Name, online = false, error = ex.Message });
                }
            }

            return Ok(new { success = true, results });
        }

        #endregion

        internal static List<MediaBrowser.Controller.Entities.BaseItem> PrepareLocalCatalog(
            IEnumerable<MediaBrowser.Controller.Entities.BaseItem> items)
            => items
                .Where(i => FederationLibraryManager.GetFederationKey(i) == null)
                .OrderByDescending(i => i.DateCreated)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        internal static bool HasEquivalentLocalCopy(
            MediaBrowser.Model.Dto.BaseItemDto remote,
            IEnumerable<MediaBrowser.Controller.Entities.BaseItem> localItems,
            IEnumerable<string> dedupKeys)
        {
            foreach (var local in localItems)
            {
                foreach (var key in dedupKeys)
                {
                    if (FederationLibraryManager.TryGetProviderId(local.ProviderIds, key, out var localValue)
                        && FederationLibraryManager.TryGetProviderId(remote.ProviderIds, key, out var remoteValue)
                        && string.Equals(localValue, remoteValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // A warning may use the conservative title/year fallback when
                // either catalog lacks provider ids. It does not authorize a
                // replacement; that path requires a provider-id match in the
                // quality advisor and a fresh per-title confirmation.
                if (string.Equals(local.Name, remote.Name, StringComparison.OrdinalIgnoreCase)
                    && local.ProductionYear == remote.ProductionYear)
                {
                    return true;
                }
            }

            return false;
        }

        private static object Sanitize(PluginConfiguration config)
        {
            return new
            {
                config.ServerUrl,
                config.InternalServerUrl,
                config.CachePath,
                config.EnableDedup,
                config.DedupProviderIds,
                config.ShowFederatedCloudBadges,
                config.PreferHigherQualityRemotes,
                config.EnableQualityReplacementActions,
                config.AutoProvisionLibraries,
                config.AllowFriendsOfFriends,
                config.RefreshIntervalHours,
                config.EnablePlexStrmExport,
                config.PlexStrmExportPath,
                config.IncomingFilter,
                config.MigratedIncomingFilterV12,
                RemoteServers = (config.RemoteServers ?? new List<RemoteServer>()).Select(SanitizeServer).ToList(),
                config.LibraryMappings
            };
        }

        private static object SanitizeServer(RemoteServer s)
        {
            return new
            {
                s.Id,
                s.Name,
                s.Url,
                s.Enabled,
                s.UserId,
                Kind = (int)s.Kind,
                s.AllowedExternalLibraryIds,
                StreamingMode = (int)s.StreamingMode,
                s.Priority,
                s.RequireApiKeyForImages,
                WanCapMode = (int)s.WanCapMode,
                s.WanMaxBitrateMbps,
                s.WanMaxHeight,
                HasApiKey = !string.IsNullOrEmpty(s.ApiKey),
                HasIssuedApiKey = !string.IsNullOrEmpty(s.IssuedApiKey),
                s.ShareAllLibraries,
                s.SharedLibraryFolderIds,
                s.ExcludedItemIds,
                s.AllowDownloads,
                RemoteUserAccessRules = (s.RemoteUserAccessRules ?? new List<RemoteUserAccessRule>()).Select(r => new
                {
                    r.RemoteUserId,
                    r.RemoteUserName,
                    Mode = (int)r.Mode,
                    r.LibraryFolderIds,
                    r.ItemIds,
                    r.MaxAllowedRating,
                    r.AllowDownload
                }).ToList(),
                FriendUserAccessRules = (s.FriendUserAccessRules ?? new List<RemoteUserAccessRule>()).Select(r => new
                {
                    r.RemoteUserId,
                    r.RemoteUserName,
                    Mode = (int)r.Mode,
                    r.LibraryFolderIds,
                    r.ItemIds,
                    r.MaxAllowedRating,
                    r.AllowDownload
                }).ToList()
            };
        }
    }

    public class RefreshServerRequest
    {
        public string? ServerId { get; set; }
    }

    public class SetAllowedLibrariesBody
    {
        public List<string>? LibraryIds { get; set; }
    }

    public class AddExternalServerBody
    {
        public string? Name { get; set; }

        public string? Url { get; set; }

        public string? Token { get; set; }
    }

    public class SetExternalServerTokenBody
    {
        public string? Token { get; set; }
    }

    public class AddCompanionFriendBody
    {
        public string? Name { get; set; }

        public bool ShareAllLibraries { get; set; } = true;

        public List<string>? SharedLibraryFolderIds { get; set; }
    }

    public class SendFriendRequestBody
    {
        public string? Url { get; set; }
    }

    public class CreatePoolBody
    {
        public string? Name { get; set; }
    }

    public class SetConnectivityModeBody
    {
        public string? Mode { get; set; }
    }

    public class AddFriendToPoolBody
    {
        public string? RemoteServerId { get; set; }
    }

    public class SetPoolIconBody
    {
        public string? IconBase64 { get; set; }
    }

    public class DownloadItemBody
    {
        public string? ItemId { get; set; }
    }

    public class BrowseDownloadBody
    {
        public string? ServerId { get; set; }

        public string? ItemId { get; set; }

        public string? Name { get; set; }
    }

    public class QualityUpgradeApplyBody
    {
        public List<string>? ItemIds { get; set; }
    }

    public class QualityUpgradeExcludeBody
    {
        public string? ItemId { get; set; }
    }

    public class IssuePlaybackTokenRequest
    {
        public string? ItemId { get; set; }
    }

    /// <summary>
    /// Request body for <see cref="FederationController.RegisterUserSession"/>.
    /// </summary>
    public class RegisterUserSessionRequest
    {
        /// <summary>Gets or sets the calling friend's own local user id starting to play.</summary>
        public string? RemoteUserId { get; set; }

        /// <summary>Gets or sets the user's display name, for logging only.</summary>
        public string? RemoteUserName { get; set; }
    }

    public class HideItemBody
    {
        public string? ItemId { get; set; }
    }

    public class UnhideItemBody
    {
        public string? Key { get; set; }
    }

    public class UpdateSharingBody
    {
        public bool ShareAll { get; set; } = true;

        public List<string>? FolderIds { get; set; }

        public List<string>? ExcludedItemIds { get; set; }
    }

    public class DownloadAccessBody
    {
        public bool AllowDownloads { get; set; } = true;
    }
}
