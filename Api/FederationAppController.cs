using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// Serves the standalone local admin UI that replaced the old in-Jellyfin
    /// config page: a dedicated, simple page at its own path, rather than a
    /// second network port. A plugin cannot open a new port through
    /// Docker/Podman's own port publishing on its own - that's decided by the
    /// container's own config before the plugin even starts - so riding
    /// Jellyfin's own already-published port is what actually delivers "install
    /// the plugin and it just works," in every deployment (bare metal,
    /// Docker/Podman, behind a reverse proxy) with nothing for an admin to
    /// configure.
    /// <para>
    /// The static assets (markup/styles/script) are anonymous - no secrets in
    /// them. Every <c>api/*</c> action requires the same elevated Jellyfin
    /// session as the rest of the plugin's data endpoints
    /// (<see cref="FederationController"/>), since this now rides Jellyfin's own
    /// port - which is often internet-reachable - rather than a loopback-only
    /// listener. The page's own script picks up the browser's existing Jellyfin
    /// login token (same pattern the old config page used) rather than asking
    /// the admin to log in separately.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation/App")]
    public class FederationAppController : ControllerBase
    {
        private readonly ILogger<FederationAppController> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly FederationSyncService _syncService;
        private readonly LibraryProvisioningService _provisioning;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly FederationItemCache _cache;
        private readonly WanBandwidthMonitor _bandwidthMonitor;
        private readonly FederationDirectoryService _directory;
        private readonly FederationFriendService _friends;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly UploadBudgetService _uploadBudget;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationAppController"/> class.
        /// </summary>
        public FederationAppController(
            ILogger<FederationAppController> logger,
            FederationLibraryManager federationManager,
            FederationSyncService syncService,
            LibraryProvisioningService provisioning,
            IRemoteServerClientFactory clientFactory,
            FederationItemCache cache,
            WanBandwidthMonitor bandwidthMonitor,
            FederationDirectoryService directory,
            FederationFriendService friends,
            ILibraryManager libraryManager,
            IUserManager userManager,
            UploadBudgetService uploadBudget)
        {
            _logger = logger;
            _federationManager = federationManager;
            _syncService = syncService;
            _provisioning = provisioning;
            _clientFactory = clientFactory;
            _cache = cache;
            _bandwidthMonitor = bandwidthMonitor;
            _directory = directory;
            _friends = friends;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _uploadBudget = uploadBudget;
        }

        private static PluginConfiguration Config => Plugin.Instance!.Configuration;

        #region Static assets

        [HttpGet("")]
        [AllowAnonymous]
        public IActionResult Index() => ServeEmbedded("index.html", "text/html; charset=utf-8");

        [HttpGet("styles.css")]
        [AllowAnonymous]
        public IActionResult Styles() => ServeEmbedded("styles.css", "text/css; charset=utf-8");

        [HttpGet("app.js")]
        [AllowAnonymous]
        [Produces("application/javascript")]
        public IActionResult Script() => ServeEmbedded("app.js", "application/javascript; charset=utf-8");

        private IActionResult ServeEmbedded(string fileName, string contentType)
        {
            // MSBuild's default embedded-resource naming replaces characters
            // that aren't valid in a C# identifier - including '-' - with '_'
            // when deriving the manifest resource name from the folder path, so
            // the "Web/local-ui/" folder on disk becomes "Web.local_ui." here.
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"Jellyfin.Plugin.Federation.Web.local_ui.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return NotFound();
            }

            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), contentType);
        }

        #endregion

        #region Profile

        [HttpGet("api/status")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetStatus()
        {
            var config = Config;
            return Ok(new
            {
                username = config.LocalUsername,
                hasAvatar = config.HasAvatar,
                directoryServerUrl = config.DirectoryServerUrl,
                hostDirectory = config.HostDirectory,
                serverUrl = config.ServerUrl,
                federationId = _friends.GetOrCreateLocalFederationId()
            });
        }

        #endregion

        #region Dashboard / Settings

        [HttpGet("api/dashboard")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetDashboard()
        {
            var config = Config;
            var servers = config.RemoteServers ?? new List<RemoteServer>();
            var activeSessions = _uploadBudget.GetActiveSessionCount();
            return Ok(new
            {
                lastSync = _syncService.LastSync,
                serverCount = servers.Count,
                disabledServerCount = servers.Count(s => !s.Enabled),
                mappingCount = (config.LibraryMappings ?? new List<LibraryMapping>()).Count,
                activeSessions,
                uploadBudget = new
                {
                    config.LocalUploadCapacityMbps,
                    config.AutoManageUploadBudget,
                    projectedPerStreamMbps = UploadBudgetService.ComputePerStreamMbps(config.LocalUploadCapacityMbps, activeSessions)
                }
            });
        }

        [HttpGet("api/settings")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetSettings()
        {
            var config = Config;
            return Ok(new
            {
                config.ServerUrl,
                config.CachePath,
                config.EnableDedup,
                config.DedupProviderIds,
                config.AutoProvisionLibraries,
                config.RefreshIntervalHours,
                config.AllowFriendsOfFriends,
                config.HostDirectory,
                config.LocalUploadCapacityMbps,
                config.AutoManageUploadBudget
            });
        }

        [HttpPost("api/settings")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SaveSettings([FromBody] SettingsRequest body)
        {
            var config = Config;
            config.ServerUrl = body.ServerUrl ?? string.Empty;
            config.CachePath = body.CachePath ?? string.Empty;
            config.EnableDedup = body.EnableDedup;
            config.DedupProviderIds = body.DedupProviderIds ?? config.DedupProviderIds;
            config.AutoProvisionLibraries = body.AutoProvisionLibraries;
            config.RefreshIntervalHours = body.RefreshIntervalHours;
            config.AllowFriendsOfFriends = body.AllowFriendsOfFriends;
            config.HostDirectory = body.HostDirectory;
            config.LocalUploadCapacityMbps = body.LocalUploadCapacityMbps;
            config.AutoManageUploadBudget = body.AutoManageUploadBudget;

            var errors = ConfigValidator.Validate(config);
            if (errors.Count > 0)
            {
                return BadRequest(new { error = string.Join(" ", errors) });
            }

            Plugin.Instance!.SaveConfiguration();
            return Ok(new { success = true });
        }

        #endregion

        #region Profile

        [HttpPost("api/profile")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SaveProfile([FromBody] ProfileRequest body)
        {
            if (!ConfigValidator.IsValidUsername(body?.Username))
            {
                return BadRequest(new { error = "Username must be 3-32 characters: letters, digits, underscore, or hyphen only." });
            }

            Config.LocalUsername = body!.Username;
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { success = true });
        }

        [HttpPost("api/profile/avatar")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> UploadAvatar()
        {
            var contentType = Request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Upload an image (image/png, image/jpeg, ...)." });
            }

            const long maxBytes = 2 * 1024 * 1024;
            if (Request.ContentLength is > maxBytes)
            {
                return BadRequest(new { error = "Image must be 2 MB or smaller." });
            }

            var path = Path.Combine(Plugin.Instance!.DataFolderPath, "avatar.img");
            await using (var file = System.IO.File.Create(path))
            {
                await Request.Body.CopyToAsync(file).ConfigureAwait(false);
            }

            Config.HasAvatar = true;
            Config.AvatarContentType = contentType;
            Plugin.Instance.SaveConfiguration();
            return Ok(new { success = true });
        }

        #endregion

        #region Servers

        [HttpGet("api/servers")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetServers()
            => Ok((Config.RemoteServers ?? new List<RemoteServer>()).Select(SanitizeServer));

        [HttpPost("api/servers")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult AddServer([FromBody] RemoteServer server)
        {
            if (!ConfigValidator.IsValidServerUrl(server?.Url))
            {
                return BadRequest(new { error = "A valid http(s) server URL is required." });
            }

            server!.Id = Guid.NewGuid().ToString();
            Config.RemoteServers ??= new List<RemoteServer>();
            Config.RemoteServers.Add(server);
            Plugin.Instance!.SaveConfiguration();
            _clientFactory.InvalidateAll();
            return Ok(new { success = true, server = SanitizeServer(server) });
        }

        [HttpDelete("api/servers/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult DeleteServer(string id)
        {
            var server = Config.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound();
            }

            Config.RemoteServers!.Remove(server);
            var seen = new HashSet<Guid>();
            foreach (var mapping in Config.LibraryMappings ?? new List<LibraryMapping>())
            {
                mapping.RemoteLibrarySources?.RemoveAll(s => s.ServerId == id);
                _cache.PruneServerSources(mapping.LocalLibraryName, id, seen);
            }

            Plugin.Instance!.SaveConfiguration();
            _clientFactory.Invalidate(id);
            _bandwidthMonitor.RemoveServer(id);
            return Ok(new { success = true });
        }

        [HttpPut("api/servers/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> UpdateServer(string id, [FromBody] UpdateServerRequest body, CancellationToken cancellationToken)
        {
            var server = Config.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrEmpty(body.Name))
            {
                server.Name = body.Name;
            }

            server.Enabled = body.Enabled;
            server.StreamingMode = body.StreamingMode;
            server.Priority = body.Priority;
            server.RequireApiKeyForImages = body.RequireApiKeyForImages;
            server.WanCapMode = body.WanCapMode;
            server.WanMaxBitrateMbps = body.WanMaxBitrateMbps;
            server.WanMaxHeight = body.WanMaxHeight;
            Plugin.Instance!.SaveConfiguration();
            _clientFactory.InvalidateAll();

            // The share-picker fields go through FederationFriendService, which also
            // enforces the restriction via a scoped local Jellyfin user and notifies
            // the friend - see the comment on UpdateFriendSharingAsync.
            var (success, message) = await _friends.UpdateFriendSharingAsync(
                id,
                body.ShareAllLibraries,
                body.SharedLibraryFolderIds ?? new List<string>(),
                body.LocalShareUserId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);

            return Ok(new { success, message, server = SanitizeServer(server) });
        }

        [HttpGet("api/local-users")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetLocalUsers()
            => Ok(_userManager.Users.Select(u => new { id = u.Id, name = u.Username }));

        [HttpGet("api/remote-libraries/{serverId}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> GetRemoteLibraries(string serverId, CancellationToken cancellationToken)
        {
            var server = Config.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                return NotFound();
            }

            var client = _clientFactory.GetClient(server);
            var libraries = await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
            return Ok((libraries ?? new List<MediaBrowser.Model.Dto.BaseItemDto>()).Select(l => new
            {
                id = l.Id,
                name = l.Name,
                collectionType = l.CollectionType?.ToString() ?? "unknown"
            }));
        }

        private static object SanitizeServer(RemoteServer s) => new
        {
            s.Id,
            s.Name,
            s.Url,
            s.Enabled,
            s.StreamingMode,
            s.Priority,
            s.FederationId,
            s.RequireApiKeyForImages,
            s.WanCapMode,
            s.WanMaxBitrateMbps,
            s.WanMaxHeight,
            s.ShareAllLibraries,
            s.SharedLibraryFolderIds,
            s.LocalShareUserId,
            HasApiKey = !string.IsNullOrEmpty(s.ApiKey)
        };

        #endregion

        #region Library mappings

        [HttpGet("api/mappings")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetMappings() => Ok(Config.LibraryMappings ?? new List<LibraryMapping>());

        [HttpPost("api/mappings")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> AddMapping([FromBody] CreateMappingRequest body, CancellationToken cancellationToken)
        {
            var server = Config.RemoteServers?.FirstOrDefault(s => s.Id == body.ServerId);
            if (server == null || !ConfigValidator.IsValidMappingName(body.LocalLibraryName))
            {
                return BadRequest(new { error = "A server and a valid local library name are required." });
            }

            Config.LibraryMappings ??= new List<LibraryMapping>();
            var mapping = new LibraryMapping
            {
                LocalLibraryName = body.LocalLibraryName,
                MediaType = body.MediaType,
                Enabled = true,
                AutoProvision = true,
                RemoteServerIds = new List<string> { server.Id },
                RemoteLibrarySources = new List<RemoteLibrarySource>
                {
                    new RemoteLibrarySource
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        RemoteLibraryId = body.RemoteLibraryId,
                        RemoteLibraryName = body.RemoteLibraryName
                    }
                }
            };
            Config.LibraryMappings.Add(mapping);
            Plugin.Instance!.SaveConfiguration();

            if (Config.AutoProvisionLibraries)
            {
                await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);
            }

            return Ok(new { success = true });
        }

        [HttpPut("api/mappings/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> UpdateMapping(string id, [FromBody] UpdateMappingRequest body, CancellationToken cancellationToken)
        {
            var mapping = Config.LibraryMappings?.FirstOrDefault(m => m.Id == id);
            if (mapping == null)
            {
                return NotFound();
            }

            mapping.Enabled = body.Enabled;
            mapping.AutoProvision = body.AutoProvision;
            Plugin.Instance!.SaveConfiguration();

            if (Config.AutoProvisionLibraries)
            {
                await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);
            }

            return Ok(new { success = true, mapping });
        }

        [HttpDelete("api/mappings/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> DeleteMapping(string id, CancellationToken cancellationToken)
        {
            var mapping = Config.LibraryMappings?.FirstOrDefault(m => m.Id == id);
            if (mapping == null)
            {
                return NotFound();
            }

            // Remove the provisioned Jellyfin library before dropping the mapping
            // from config - EnsureLibrariesAsync's disabled-mapping cleanup pass
            // needs the mapping's LocalLibraryName still present in config to know
            // which virtual folder to tear down.
            mapping.Enabled = false;
            await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);

            Config.LibraryMappings!.Remove(mapping);
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { success = true });
        }

        #endregion

        #region Library

        [HttpGet("api/library")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetLibrary()
        {
            var mappings = Config.LibraryMappings ?? new List<LibraryMapping>();
            var virtualFolders = _libraryManager.GetVirtualFolders();

            return Ok(mappings.Select(m =>
            {
                var folder = virtualFolders?.FirstOrDefault(vf => string.Equals(vf.Name, m.LocalLibraryName, StringComparison.OrdinalIgnoreCase));
                var itemCount = folder != null && Guid.TryParse(folder.ItemId, out var folderId)
                    ? _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery { ParentId = folderId, Recursive = true }).Count
                    : 0;

                return new
                {
                    m.Id,
                    m.LocalLibraryName,
                    m.MediaType,
                    m.Enabled,
                    m.AutoProvision,
                    itemCount,
                    folderId = folder?.ItemId,
                    sources = m.RemoteLibrarySources?.Select(s => new { s.ServerId, s.ServerName, s.RemoteLibraryName })
                };
            }));
        }

        [HttpGet("api/library/{id}/items")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetLibraryItems(string id, [FromQuery] int skip = 0, [FromQuery] int take = 30)
        {
            var mapping = Config.LibraryMappings?.FirstOrDefault(m => m.Id == id);
            if (mapping == null)
            {
                return NotFound();
            }

            var folder = _libraryManager.GetVirtualFolders()?
                .FirstOrDefault(vf => string.Equals(vf.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));
            if (folder == null || !Guid.TryParse(folder.ItemId, out var folderId))
            {
                return Ok(new { items = Array.Empty<object>(), total = 0 });
            }

            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                ParentId = folderId,
                Recursive = true,
                Limit = Math.Clamp(take, 1, 100),
                StartIndex = Math.Max(0, skip)
            };
            var items = _libraryManager.GetItemList(query);
            return Ok(new
            {
                items = items.Select(i => new { id = i.Id, name = i.Name, hasImage = i.HasImage(MediaBrowser.Model.Entities.ImageType.Primary) }),
                folderId = folder.ItemId
            });
        }

        #endregion

        #region Friends

        [HttpGet("api/friends")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetFriends()
        {
            var config = Config;
            return Ok(new
            {
                incoming = config.IncomingFriendRequests.Select(r => new { r.Id, r.RemoteServerUrl, r.RemoteServerName, r.CreatedUtc, r.Verified }),
                outgoing = config.OutgoingFriendRequests.Select(r => new { r.Id, r.RemoteServerUrl, r.RemoteServerName, r.CreatedUtc })
            });
        }

        [HttpPost("api/friends/send")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> SendFriendRequest([FromBody] SendFriendRequestBody body, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.SendFriendRequestAsync(body?.Url ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        [HttpPost("api/friends/{id}/accept")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> AcceptFriendRequest(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.AcceptFriendRequestAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        [HttpPost("api/friends/{id}/reject")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RejectFriendRequest(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.RejectFriendRequestAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        [HttpDelete("api/friends/outgoing/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> CancelFriendRequest(string id, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.CancelOutgoingFriendRequestAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        #endregion

        #region Pools

        [HttpGet("api/pools")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetPools() => Ok(Config.Pools ?? new List<FederationPool>());

        [HttpPost("api/pools")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult CreatePool([FromBody] CreatePoolRequest body)
        {
            var pool = _friends.CreatePool(body.Name);
            return Ok(new { success = true, pool });
        }

        [HttpPost("api/pools/{id}/invite")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> InvitePool(string id, [FromBody] InvitePoolRequest body, CancellationToken cancellationToken)
        {
            var (success, message) = await _friends.SendPoolInviteAsync(id, body?.Url ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        [HttpDelete("api/pools/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult LeavePool(string id) => Ok(new { success = _friends.LeavePool(id) });

        #endregion

        #region Sync

        [HttpPost("api/sync")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> SyncNow(CancellationToken cancellationToken)
        {
            var result = await _syncService.SyncAllAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { result.Success, result.Message });
        }

        #endregion

        #region Directory

        [HttpPost("api/directory/url")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetDirectoryUrl([FromBody] DirectoryUrlRequest body)
        {
            if (!string.IsNullOrEmpty(body?.Url) && !ConfigValidator.IsValidServerUrl(body.Url))
            {
                return BadRequest(new { error = "Directory address must be an absolute http(s) URL." });
            }

            Config.DirectoryServerUrl = body?.Url ?? string.Empty;
            Plugin.Instance!.SaveConfiguration();
            return Ok(new { success = true });
        }

        [HttpGet("api/directory/search")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> SearchDirectory([FromQuery] string username, CancellationToken cancellationToken)
        {
            var (success, message, results) = await _directory.SearchAsync(username, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message, results });
        }

        [HttpPost("api/directory/register")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RegisterDirectory(CancellationToken cancellationToken)
        {
            var (success, message) = await _directory.RegisterAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        [HttpPost("api/directory/invite/create")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> CreateInvite(CancellationToken cancellationToken)
        {
            var (success, message, code) = await _directory.CreateInviteAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message, code });
        }

        [HttpPost("api/directory/invite/redeem")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RedeemInvite([FromBody] RedeemInviteRequest body, CancellationToken cancellationToken)
        {
            var (success, message) = await _directory.RedeemInviteAsync(body?.Code ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return Ok(new { success, message });
        }

        #endregion

        /// <summary>Request body for <see cref="SaveSettings"/>.</summary>
        public class SettingsRequest
        {
            /// <summary>Gets or sets <see cref="PluginConfiguration.ServerUrl"/>.</summary>
            public string? ServerUrl { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.CachePath"/>.</summary>
            public string? CachePath { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.EnableDedup"/>.</summary>
            public bool EnableDedup { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.DedupProviderIds"/>.</summary>
            public List<string>? DedupProviderIds { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.AutoProvisionLibraries"/>.</summary>
            public bool AutoProvisionLibraries { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.RefreshIntervalHours"/>.</summary>
            public int RefreshIntervalHours { get; set; } = 1;

            /// <summary>Gets or sets <see cref="PluginConfiguration.AllowFriendsOfFriends"/>.</summary>
            public bool AllowFriendsOfFriends { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.HostDirectory"/>.</summary>
            public bool HostDirectory { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.LocalUploadCapacityMbps"/>.</summary>
            public int LocalUploadCapacityMbps { get; set; }

            /// <summary>Gets or sets <see cref="PluginConfiguration.AutoManageUploadBudget"/>.</summary>
            public bool AutoManageUploadBudget { get; set; }
        }

        /// <summary>Request body for <see cref="SaveProfile"/>.</summary>
        public class ProfileRequest
        {
            /// <summary>Gets or sets the username to save.</summary>
            public string Username { get; set; } = string.Empty;
        }

        /// <summary>Request body for <see cref="UpdateServer"/>.</summary>
        public class UpdateServerRequest
        {
            /// <summary>Gets or sets the friendly name.</summary>
            public string? Name { get; set; }

            /// <summary>Gets or sets whether the server is enabled.</summary>
            public bool Enabled { get; set; } = true;

            /// <summary>Gets or sets the streaming mode.</summary>
            public StreamingMode StreamingMode { get; set; }

            /// <summary>Gets or sets the priority used when picking a primary source.</summary>
            public int Priority { get; set; }

            /// <summary>Gets or sets whether the remote requires an api_key for image fetches.</summary>
            public bool RequireApiKeyForImages { get; set; }

            /// <summary>Gets or sets the WAN bitrate cap mode.</summary>
            public WanCapMode WanCapMode { get; set; }

            /// <summary>Gets or sets the fixed bitrate cap in Mbps (Manual mode only).</summary>
            public int WanMaxBitrateMbps { get; set; }

            /// <summary>Gets or sets the max output height applied alongside a cap.</summary>
            public int WanMaxHeight { get; set; } = 1080;

            /// <summary>Gets or sets whether every local library is shared with this friend.</summary>
            public bool ShareAllLibraries { get; set; } = true;

            /// <summary>Gets or sets the specific local folder ids shared when not sharing all.</summary>
            public List<string>? SharedLibraryFolderIds { get; set; }

            /// <summary>Gets or sets the local Jellyfin user id enforcing a restricted share.</summary>
            public string? LocalShareUserId { get; set; }
        }

        /// <summary>Request body for <see cref="AddMapping"/>.</summary>
        public class CreateMappingRequest
        {
            /// <summary>Gets or sets the server id supplying the remote library.</summary>
            public string ServerId { get; set; } = string.Empty;

            /// <summary>Gets or sets the local virtual library name to create.</summary>
            public string LocalLibraryName { get; set; } = string.Empty;

            /// <summary>Gets or sets the media type (Movie, Series, ...).</summary>
            public string MediaType { get; set; } = "Movie";

            /// <summary>Gets or sets the remote library's id on that server.</summary>
            public string RemoteLibraryId { get; set; } = string.Empty;

            /// <summary>Gets or sets the remote library's display name.</summary>
            public string RemoteLibraryName { get; set; } = string.Empty;
        }

        /// <summary>Request body for <see cref="UpdateMapping"/>.</summary>
        public class UpdateMappingRequest
        {
            /// <summary>Gets or sets whether this mapping is enabled.</summary>
            public bool Enabled { get; set; } = true;

            /// <summary>Gets or sets whether a virtual library is auto-provisioned for it.</summary>
            public bool AutoProvision { get; set; } = true;
        }

        /// <summary>Request body for <see cref="SendFriendRequest"/>.</summary>
        public class SendFriendRequestBody
        {
            /// <summary>Gets or sets the target server's address.</summary>
            public string? Url { get; set; }
        }

        /// <summary>Request body for <see cref="CreatePool"/>.</summary>
        public class CreatePoolRequest
        {
            /// <summary>Gets or sets the new pool's name.</summary>
            public string Name { get; set; } = string.Empty;
        }

        /// <summary>Request body for <see cref="InvitePool"/>.</summary>
        public class InvitePoolRequest
        {
            /// <summary>Gets or sets the address to invite into the pool.</summary>
            public string? Url { get; set; }
        }

        /// <summary>Request body for <see cref="RedeemInvite"/>.</summary>
        public class RedeemInviteRequest
        {
            /// <summary>Gets or sets the invite code to redeem.</summary>
            public string? Code { get; set; }
        }

        /// <summary>Request body for <see cref="SetDirectoryUrl"/>.</summary>
        public class DirectoryUrlRequest
        {
            /// <summary>Gets or sets the directory server's address.</summary>
            public string? Url { get; set; }
        }
    }
}
