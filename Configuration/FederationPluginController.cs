using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// API controller for federation plugin: servers, mappings, refresh, streaming, diagnostics.
    /// All data and mutating endpoints require an elevated (admin) session. The only
    /// anonymous endpoints are the static config page markup and the bounded stream
    /// proxy (clients fetch media source URLs without Jellyfin auth headers).
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public class FederationController : ControllerBase
    {
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
            FederationPlaybackTokenService playbackTokens)
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
                        server.LocalShareUserId = oldServer.LocalShareUserId;
                        server.RemoteUserAccessRules = oldServer.RemoteUserAccessRules;
                        server.FriendUserAccessRules = oldServer.FriendUserAccessRules;
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

                    // Same class of field again: the hide list is managed exclusively
                    // through the HiddenItems/* endpoints (the item detail page's Hide
                    // chip, and Unhide in the config page's own Hidden Items section),
                    // never sent as part of the main Save form - without this, saving
                    // any unrelated setting would silently un-hide everything.
                    config.HiddenFederatedItemIds = existing.HiddenFederatedItemIds;
                }

                var errors = ConfigValidator.Validate(config);
                if (errors.Count > 0)
                {
                    return BadRequest(new { error = "Invalid configuration", details = errors });
                }

                _logger.LogInformation("[Federation] Updating configuration with {ServerCount} servers", config.RemoteServers?.Count ?? 0);
                Plugin.Instance?.UpdateConfiguration(config);
                _clientFactory.InvalidateAll();

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
                refreshIntervalHours = config.RefreshIntervalHours
            });
        }

        #endregion

        #region Server Management

        [HttpPost("TestServer")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> TestServer([FromBody] RemoteServer server, CancellationToken cancellationToken)
        {
            if (server == null || string.IsNullOrWhiteSpace(server.Url))
            {
                return BadRequest(new { success = false, message = "Server URL is required" });
            }

            // The config page never holds saved API keys; when testing an existing
            // server with a blank key, fall back to the stored one.
            if (string.IsNullOrEmpty(server.ApiKey) && !string.IsNullOrEmpty(server.Id))
            {
                var configured = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id);
                if (configured != null)
                {
                    server.ApiKey = configured.ApiKey;
                }
            }

            if (string.IsNullOrWhiteSpace(server.ApiKey))
            {
                return BadRequest(new { success = false, message = "API key is required" });
            }

            if (!ConfigValidator.IsValidServerUrl(server.Url))
            {
                return BadRequest(new { success = false, message = "Server URL must be an absolute http(s) URL" });
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
                    var message = systemInfoError != null
                        ? $"Connected, but failed to get system info: {systemInfoError}"
                        : "Connected but failed to get system info";
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

            if (string.IsNullOrWhiteSpace(server.ApiKey))
            {
                return BadRequest(new { success = false, message = "API key is required" });
            }

            if (!ConfigValidator.IsValidServerUrl(server.Url))
            {
                return BadRequest(new { success = false, message = "Server URL must be an absolute http(s) URL" });
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

            config!.RemoteServers!.Remove(server);

            // Drop this server's library sources and cached entries so no stale
            // items keep pointing at a deleted server.
            var seen = new HashSet<Guid>();
            var affectedMappings = config.LibraryMappings ?? new List<LibraryMapping>();
            foreach (var mapping in affectedMappings)
            {
                mapping.RemoteLibrarySources?.RemoveAll(s => s.ServerId == id);
                _cache.PruneServerSources(mapping.LocalLibraryName, id, seen);
            }

            Plugin.Instance?.SaveConfiguration();
            _clientFactory.Invalidate(id);

            // Drops the deleted server's own cached network classification/bandwidth
            // measurement (see WanBandwidthMonitor) - nothing references this id
            // anymore, so nothing should keep holding state for it.
            _bandwidthMonitor.RemoveServer(id);

            // Reconcile every affected mapping immediately rather than waiting for
            // the next scheduled/triggered sync: the cache entries for this server's
            // content are already gone above, so without this, its federated items
            // would keep sitting in the library - now pointing at a server that no
            // longer exists in config, unplayable - until whenever the next sync
            // happens to run. This only touches the already-pruned local cache, no
            // remote network calls, so it is cheap even though it runs inline.
            foreach (var mapping in affectedMappings.Where(m => m.Enabled))
            {
                try
                {
                    await _persistence.ReconcileMappingAsync(mapping, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Post-delete reconciliation failed for {Name}; it will be retried on the next sync", mapping.LocalLibraryName);
                }
            }

            return Ok(new { success = true });
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
        /// Server-to-server: a friend asking (using the API key we gave them) who our
        /// other friends are, for friends-of-friends discovery. Gated on
        /// AllowFriendsOfFriends rather than AllowAnonymous - only an existing friend
        /// (or this server's own admin) holds a key that passes RequiresElevation here.
        /// </summary>
        [HttpGet("Friends/List")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetFriendsList()
        {
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
        /// friend request and is handing us a key to use pulling from them.
        /// </summary>
        [HttpPost("Friends/Accept")]
        [AllowAnonymous]
        public IActionResult ReceiveFriendAccept([FromBody] FriendRequestPayload payload)
        {
            _friends.HandleAcceptCallback(payload);
            return Ok();
        }

        /// <summary>
        /// Server-to-server, anonymous: the other server declined our earlier friend request.
        /// </summary>
        [HttpPost("Friends/Reject")]
        [AllowAnonymous]
        public async Task<IActionResult> ReceiveFriendReject([FromBody] FriendRejectPayload payload)
        {
            await _friends.HandleRejectCallbackAsync(payload?.RequestId ?? string.Empty).ConfigureAwait(false);
            return Ok();
        }

        /// <summary>
        /// Admin-triggered: sets which of this server's own libraries a specific
        /// friend can see. Enforced via an existing local Jellyfin user the admin
        /// picks, not anything the plugin polices itself - see
        /// <see cref="FederationFriendService.UpdateFriendSharingAsync"/>.
        /// </summary>
        [HttpPost("Friends/{id}/Sharing")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> UpdateFriendSharing(string id, [FromBody] UpdateSharingBody body, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.UpdateFriendSharingAsync(
                id,
                body?.ShareAll ?? true,
                body?.FolderIds ?? new List<string>(),
                body?.LocalUserId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        /// <summary>
        /// Server-to-server: a friend we already share content with is telling us
        /// which local user id to use when querying them from now on. Not
        /// AllowAnonymous - only an existing friend's API key (or this server's own
        /// admin) passes RequiresElevation, same reasoning as Friends/List.
        /// </summary>
        [HttpPost("Friends/SharedUserUpdate")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult ReceiveSharedUserUpdate([FromBody] SharedUserUpdatePayload payload)
        {
            _friends.ReceiveSharedUserUpdate(payload);
            return Ok();
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
        /// Not AllowAnonymous - same reasoning as <see cref="ReceiveSharedUserUpdate"/>.
        /// </summary>
        [HttpPost("Friends/RemoteUserRules")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult ReceiveRemoteUserAccessRules([FromBody] RemoteUserAccessRulesPayload payload)
        {
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
        /// Admin-triggered: lists this server's own local Jellyfin users, for
        /// picking which restricted account enforces a friend's sharing scope -
        /// see <see cref="FederationFriendService.UpdateFriendSharingAsync"/>. The
        /// admin creates the account itself (Dashboard -> Users) so it goes
        /// through Jellyfin's own working user-creation path.
        /// </summary>
        [HttpGet("LocalUsers")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetLocalUsers()
        {
            var users = new List<object>();
            foreach (var u in EnumerateLocalUsers())
            {
                var t = u.GetType();
                var id = t.GetProperty("Id")?.GetValue(u);
                var name = t.GetProperty("Username")?.GetValue(u) as string;
                if (id is Guid guid && name != null)
                {
                    users.Add(new { id = guid.ToString("N"), name });
                }
            }

            return Ok(users);
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
        /// assembly that differs the same way, so even a successful reflective
        /// call is read back via reflection too (see GetLocalUsers above) rather
        /// than cast to a compile-time type, which could throw its own
        /// cross-version identity mismatch.
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
        /// Admin-triggered: adds a friend this server is already connected to into a
        /// pool, without re-typing their URL or repeating the handshake.
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
        /// Server-to-server: an already-known friend added us to a pool, or has an
        /// updated roster for one we're already in. Not AllowAnonymous - only an
        /// existing friend's API key (or this server's own admin) passes
        /// RequiresElevation, same reasoning as Friends/List.
        /// </summary>
        [HttpPost("Pools/Notice")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> ReceivePoolNotice([FromBody] PoolNoticePayload payload, CancellationToken cancellationToken)
        {
            await _friends.ReceivePoolNotice(payload, cancellationToken).ConfigureAwait(false);
            return Ok();
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

        #endregion

        #region Streaming

        /// <summary>
        /// Proxy stream endpoint (Proxy mode). Streams the body through this server so
        /// the remote api_key never reaches clients. Anonymous because media players
        /// fetch media source URLs without Jellyfin auth headers; bounded to
        /// configured servers and explicit item ids only.
        /// </summary>
        [HttpGet("Stream")]
        [AllowAnonymous]
        public async Task<IActionResult> Stream(
            [FromQuery] string serverId,
            [FromQuery] string itemId,
            CancellationToken cancellationToken,
            [FromQuery] bool audio = false,
            [FromQuery] string? requestingUserId = null)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                return NotFound($"Server not found: {serverId}");
            }

            if (!Guid.TryParse(itemId, out _))
            {
                return BadRequest("Invalid item id");
            }

            await _streamHandler.HandleProxyAsync(serverId, itemId, Request, Response, cancellationToken, audio, requestingUserId).ConfigureAwait(false);
            return new EmptyResult();
        }

        /// <summary>
        /// Server-to-server: a friend server asking us to mint a short-lived,
        /// single-item-scoped playback token, so its own users can Direct-mode-play
        /// an item of ours without ever seeing the real api_key we gave them. Not
        /// AllowAnonymous (a friend's real api_key is required to call this) and not
        /// RequiresElevation (this is a friend server calling on its own users'
        /// behalf, not this server's own admin) - plain [Authorize], matching the
        /// other genuine server-to-server endpoints in this file (e.g. Friends/List,
        /// Friends/SharedUserUpdate).
        /// </summary>
        [HttpPost("PlaybackToken")]
        [Authorize]
        public IActionResult IssuePlaybackToken([FromBody] IssuePlaybackTokenRequest? request)
        {
            if (!Guid.TryParse(request?.ItemId, out var itemGuid))
            {
                return BadRequest(new { error = "Invalid item id" });
            }

            var token = _playbackTokens.Issue(itemGuid.ToString("N"));
            return Ok(new { token, expiresUtc = DateTime.UtcNow.AddHours(24) });
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

            if (!_playbackTokens.TryValidate(token, itemGuid.ToString("N")))
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
            return Ok(new
            {
                totalServers = config?.RemoteServers?.Count ?? 0,
                activeServers = config?.RemoteServers?.Count(s => s.Enabled) ?? 0,
                federatedItems = _federationManager.Cache.Count,

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
            var (success, message, operationId) = _downloadService.StartDownload(body?.ItemId ?? string.Empty);
            if (!success)
            {
                return BadRequest(new { success, message });
            }

            return Ok(new { success, message, operationId });
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
                    var client = _clientFactory.GetClient(server);
                    var online = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var info = online ? await client.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false) : null;
                    results.Add(new
                    {
                        serverId = server.Id,
                        serverName = server.Name,
                        online,
                        systemInfo = info != null ? new { name = info.ServerName, version = info.Version } : null
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

        private static object Sanitize(PluginConfiguration config)
        {
            return new
            {
                config.ServerUrl,
                config.InternalServerUrl,
                config.CachePath,
                config.EnableDedup,
                config.DedupProviderIds,
                config.AutoProvisionLibraries,
                config.AllowFriendsOfFriends,
                config.RefreshIntervalHours,
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
                StreamingMode = (int)s.StreamingMode,
                s.Priority,
                s.RequireApiKeyForImages,
                WanCapMode = (int)s.WanCapMode,
                s.WanMaxBitrateMbps,
                s.WanMaxHeight,
                HasApiKey = !string.IsNullOrEmpty(s.ApiKey),
                s.ShareAllLibraries,
                s.SharedLibraryFolderIds,
                s.LocalShareUserId
            };
        }
    }

    public class RefreshServerRequest
    {
        public string? ServerId { get; set; }
    }

    public class SendFriendRequestBody
    {
        public string? Url { get; set; }
    }

    public class CreatePoolBody
    {
        public string? Name { get; set; }
    }

    public class AddFriendToPoolBody
    {
        public string? RemoteServerId { get; set; }
    }

    public class DownloadItemBody
    {
        public string? ItemId { get; set; }
    }

    public class IssuePlaybackTokenRequest
    {
        public string? ItemId { get; set; }
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

        public string? LocalUserId { get; set; }
    }
}
