using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// API controller for federation plugin: servers, mappings, refresh, streaming, diagnostics.
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
        private readonly IServerConfigurationManager _serverConfigManager;
        private readonly IRemoteServerClientFactory _clientFactory;

        public FederationController(
            ILogger<FederationController> logger,
            FederationSyncService syncService,
            FederationLibraryManager federationManager,
            LibraryProvisioningService provisioning,
            FederationStreamHandler streamHandler,
            IServerConfigurationManager serverConfigManager,
            IRemoteServerClientFactory clientFactory)
        {
            _logger = logger;
            _syncService = syncService;
            _federationManager = federationManager;
            _provisioning = provisioning;
            _streamHandler = streamHandler;
            _serverConfigManager = serverConfigManager;
            _clientFactory = clientFactory;
        }

        #region Configuration

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

        [HttpGet("Configuration")]
        [AllowAnonymous]
        public ActionResult<PluginConfiguration> GetConfiguration()
        {
            return Ok(Plugin.Instance?.Configuration ?? new PluginConfiguration());
        }

        [HttpPost("Configuration")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult UpdateConfiguration([FromBody] PluginConfiguration config)
        {
            if (config == null)
            {
                return BadRequest(new { error = "Configuration is required" });
            }

            try
            {
                _logger.LogInformation("[Federation] Updating configuration with {ServerCount} servers", config.RemoteServers?.Count ?? 0);
                Plugin.Instance?.UpdateConfiguration(config);
                _clientFactory.InvalidateAll();
                return Ok(new { success = true, message = "Configuration updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error updating configuration");
                return StatusCode(500, new { error = "Failed to update configuration", message = ex.Message });
            }
        }

        #endregion

        #region System Info

        [HttpGet("SystemInfo")]
        [AllowAnonymous]
        public IActionResult GetSystemInfo()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var detectedUrl = string.IsNullOrEmpty(config.ServerUrl)
                ? DetectLocalServerUrl()
                : config.ServerUrl;

            return Ok(new
            {
                detectedUrl,
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

        private string DetectLocalServerUrl()
        {
            // No reliable address field is exposed via ServerConfiguration in this ABI.
            return "http://localhost:8096";
        }

        #endregion

        #region Server Management

        [HttpPost("TestServer")]
        [AllowAnonymous]
        public async Task<IActionResult> TestServer([FromBody] RemoteServer server, CancellationToken cancellationToken)
        {
            if (server == null || string.IsNullOrWhiteSpace(server.Url) || string.IsNullOrWhiteSpace(server.ApiKey))
            {
                return BadRequest(new { success = false, message = "Server URL and API key are required" });
            }

            try
            {
                using var client = new RemoteServerClient(server, _logger);
                if (!await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return Ok(new { success = false, message = "Failed to connect to server" });
                }

                var systemInfo = await client.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
                if (systemInfo == null)
                {
                    return Ok(new { success = false, message = "Connected but failed to get system info" });
                }

                string? userId = server.UserId;
                if (string.IsNullOrEmpty(userId))
                {
                    var users = await client.GetUsersAsync(cancellationToken).ConfigureAwait(false);
                    userId = users?.FirstOrDefault()?.Id;
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

        [HttpGet("Servers")]
        [AllowAnonymous]
        public ActionResult<List<RemoteServer>> GetServers()
            => Ok(Plugin.Instance?.Configuration.RemoteServers ?? new List<RemoteServer>());

        [HttpPost("Servers")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult AddServer([FromBody] RemoteServer server)
        {
            if (server == null)
            {
                return BadRequest(new { error = "Server configuration is required" });
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
            return Ok(new { success = true, server });
        }

        [HttpPut("Servers/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult UpdateServer(string id, [FromBody] RemoteServer server)
        {
            var config = Plugin.Instance?.Configuration;
            var existing = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (existing == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            existing.Name = server.Name;
            existing.Url = server.Url;
            existing.ApiKey = server.ApiKey;
            existing.UserId = server.UserId;
            existing.Enabled = server.Enabled;
            existing.StreamingMode = server.StreamingMode;
            existing.Priority = server.Priority;
            existing.RequireApiKeyForImages = server.RequireApiKeyForImages;

            Plugin.Instance?.SaveConfiguration();
            _clientFactory.Invalidate(existing.Id);
            return Ok(new { success = true });
        }

        [HttpDelete("Servers/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult DeleteServer(string id)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { error = "Server not found" });
            }

            config!.RemoteServers!.Remove(server);
            Plugin.Instance?.SaveConfiguration();
            _clientFactory.Invalidate(id);
            return Ok(new { success = true });
        }

        #endregion

        #region Remote Library Browsing

        [HttpGet("GetRemoteLibraries")]
        [AllowAnonymous]
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
        /// 302 redirect to the remote server (Direct mode). Default.
        /// </summary>
        [HttpGet("Redirect")]
        [AllowAnonymous]
        public Task<IActionResult> RedirectStream([FromQuery] string serverId, [FromQuery] string itemId, CancellationToken cancellationToken)
        {
            try
            {
                var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
                if (server == null)
                {
                    return Task.FromResult<IActionResult>(NotFound($"Server not found: {serverId}"));
                }

                if (server.StreamingMode == StreamingMode.Proxy)
                {
                    _streamHandler.HandleProxyAsync(serverId, itemId, Request, Response, cancellationToken);
                    return Task.FromResult<IActionResult>(new EmptyResult());
                }

                _streamHandler.HandleRedirectAsync(serverId, itemId, Response, cancellationToken);
                return Task.FromResult<IActionResult>(new EmptyResult());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error during redirect");
                return Task.FromResult<IActionResult>(StatusCode(500, "Error redirecting stream"));
            }
        }

        /// <summary>
        /// Proxy stream endpoint (Proxy mode). Streams the body through this server.
        /// </summary>
        [HttpGet("Stream")]
        [AllowAnonymous]
        public async Task<IActionResult> Stream([FromQuery] string serverId, [FromQuery] string itemId, CancellationToken cancellationToken)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                return NotFound($"Server not found: {serverId}");
            }

            await _streamHandler.HandleProxyAsync(serverId, itemId, Request, Response, cancellationToken).ConfigureAwait(false);
            return new EmptyResult();
        }

        #endregion

        #region Refresh / Library Provisioning

        [HttpPost("Refresh")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> TriggerRefresh(CancellationToken cancellationToken)
        {
            var result = await _syncService.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { result.Success, result.Message, result.ItemCount, result.OperationId });
        }

        [HttpPost("RefreshServer")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RefreshServer([FromBody] RefreshServerRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request?.serverId))
            {
                return BadRequest(new { success = false, message = "serverId is required" });
            }

            var result = await _syncService.SyncServerAsync(request.serverId, cancellationToken).ConfigureAwait(false);
            return Ok(new { result.Success, result.Message, result.ItemCount });
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
        [AllowAnonymous]
        public ActionResult<List<LibraryMapping>> GetMappings()
            => Ok(Plugin.Instance?.Configuration.LibraryMappings ?? new List<LibraryMapping>());

        #endregion

        #region Status / Progress

        [HttpGet("Status")]
        [AllowAnonymous]
        public IActionResult GetStatus()
        {
            var config = Plugin.Instance?.Configuration;
            return Ok(new
            {
                totalServers = config?.RemoteServers?.Count ?? 0,
                activeServers = config?.RemoteServers?.Count(s => s.Enabled) ?? 0,
                federatedItems = _federationManager.Cache.Count,
                lastRefresh = _federationManager.Cache.LastRefresh,
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
        [AllowAnonymous]
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
                totalItems = progress.TotalItems,
                processedItems = progress.ProcessedItems,
                percentage = progress.Percentage,
                status = progress.Status,
                isComplete = progress.IsComplete,
                success = progress.Success,
                elapsedSeconds = progress.ElapsedTime?.TotalSeconds
            });
        }

        [HttpPost("TestAllServers")]
        [AllowAnonymous]
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
    }

    public class RefreshServerRequest
    {
        public string? serverId { get; set; }
    }
}
