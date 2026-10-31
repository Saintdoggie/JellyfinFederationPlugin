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
    /// <summary>Plex and Companion HTTP endpoints supplied by the optional add-in.</summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public sealed class PlexController : ControllerBase
    {
        private readonly ILogger<PlexController> _logger;
        private readonly FederationSyncService _syncService;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly IPlexCompanionIntegration _plex;
        private readonly FriendDataLimits? _limits;

        public PlexController(
            ILogger<PlexController> logger,
            FederationSyncService syncService,
            IRemoteServerClientFactory clientFactory,
            IPlexCompanionIntegration plex,
            FriendDataLimits? limits = null)
        {
            _logger = logger;
            _syncService = syncService;
            _clientFactory = clientFactory;
            _plex = plex;
            _limits = limits;
        }

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

            var normalized = body.Url.TrimEnd('/');
            if (config.RemoteServers.Any(server => string.Equals(server.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
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
                StreamingMode = StreamingMode.Proxy,
                Enabled = true
            };
            config.RemoteServers.Add(server);
            Plugin.Instance!.SaveConfiguration();
            _clientFactory.InvalidateAll();
            return Ok(new { success = true, server = FederationServerPresentation.Sanitize(server, _limits) });
        }

        [HttpPost("PlexOffers")]
        [AllowAnonymous]
        public IActionResult ReceivePlexOffer([FromBody] ConnectPlexCompanionBody? body)
        {
            var (success, message) = _plex.ReceiveOffer(body?.Code);
            return success ? Ok(new { success, message }) : BadRequest(new { success, error = message, message });
        }

        [HttpGet("PlexOffers")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult ListPlexOffers()
        {
            var offers = _plex.ListOffers()
                .Select(offer => new { offer.Id, offer.ServerName, offer.CompanionUrl, offer.CreatedUtc });
            return Ok(new { incoming = offers });
        }

        [HttpPost("PlexOffers/{id}/Accept")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> AcceptPlexOffer(string id, CancellationToken cancellationToken)
        {
            var (success, message, server) = await _plex.AcceptOfferAsync(id, cancellationToken).ConfigureAwait(false);
            if (!success || server == null)
            {
                return BadRequest(new { success = false, error = message, message });
            }

            await TryInitialSync(server, "Accepted Plex offer", cancellationToken).ConfigureAwait(false);
            return Ok(new { success = true, message, server = FederationServerPresentation.Sanitize(server, _limits) });
        }

        [HttpDelete("PlexOffers/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult RejectPlexOffer(string id)
        {
            var (success, message) = _plex.RejectOffer(id);
            return success ? Ok(new { success, message }) : BadRequest(new { success, error = message, message });
        }

        [HttpPost("ExternalServers/ConnectCode")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> ConnectPlexCompanion([FromBody] ConnectPlexCompanionBody? body, CancellationToken cancellationToken)
        {
            var (success, message, server) = await _plex.ConnectAsync(body?.Code, cancellationToken).ConfigureAwait(false);
            if (!success || server == null)
            {
                return BadRequest(new { success = false, error = message, message });
            }

            await TryInitialSync(server, "Connected to Plex source", cancellationToken).ConfigureAwait(false);
            return Ok(new { success = true, message, server = FederationServerPresentation.Sanitize(server, _limits) });
        }

        [HttpPost("Servers/Companion")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult AddCompanionFriend([FromBody] AddCompanionFriendBody? body)
        {
            var (success, message, connectCode) = _plex.CreateReceiver(
                body?.Name ?? string.Empty,
                body?.ShareAllLibraries ?? true,
                body?.SharedLibraryFolderIds);

            if (!success)
            {
                return BadRequest(new { success, message });
            }

            _clientFactory.InvalidateAll();
            var created = Plugin.Instance?.Configuration?.RemoteServers?.LastOrDefault(server => server.Kind == ServerKind.Companion);
            return Ok(new { success, message, connectCode, server = created == null ? null : FederationServerPresentation.Sanitize(created, _limits) });
        }

        [HttpGet("Servers/{id}/CompanionConnectCode")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetCompanionConnectCode(string id)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(candidate => candidate.Id == id);
            if (server == null || server.Kind != ServerKind.Companion)
            {
                return NotFound(new { error = "Companion friend not found" });
            }

            var (success, message, connectCode) = _plex.GetConnectCode(server);
            if (!success)
            {
                return BadRequest(new { success, message });
            }

            return Ok(new { success, connectCode });
        }

        private async Task TryInitialSync(RemoteServer server, string action, CancellationToken cancellationToken)
        {
            try
            {
                await _syncService.SyncServerAsync(server.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] {Action} {Name} but the first sync failed", action, server.Name);
            }
        }
    }

    public sealed class AddExternalServerBody
    {
        public string? Name { get; set; }

        public string? Url { get; set; }

        public string? Token { get; set; }
    }

    public sealed class ConnectPlexCompanionBody
    {
        public string? Code { get; set; }
    }

    public sealed class AddCompanionFriendBody
    {
        public string? Name { get; set; }

        public bool ShareAllLibraries { get; set; } = true;

        public List<string>? SharedLibraryFolderIds { get; set; }
    }
}
