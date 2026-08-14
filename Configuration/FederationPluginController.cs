using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Authorization;
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
            FederationFriendService friends)
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
            _friends = friends;
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
                lastRefresh = _federationManager.Cache.LastRefresh,
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
                        suggestedUserId = userId
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
            [FromQuery] bool audio = false)
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

            await _streamHandler.HandleProxyAsync(serverId, itemId, Request, Response, cancellationToken, audio).ConfigureAwait(false);
            return new EmptyResult();
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
                lastRefresh = _federationManager.Cache.LastRefresh,

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
                HasApiKey = !string.IsNullOrEmpty(s.ApiKey)
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
}
