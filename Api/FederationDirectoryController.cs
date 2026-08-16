using System;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// Serves this server's side of the friend-directory feature when
    /// <see cref="PluginConfiguration.HostDirectory"/> is on - a small, optional,
    /// self-hostable "who is this username, and where do I find them" lookup that
    /// other servers can point their own <see cref="PluginConfiguration.DirectoryServerUrl"/>
    /// at. Every route here is anonymous by necessity, same reasoning as the
    /// server-to-server friend-request callbacks in <see cref="FederationController"/>:
    /// the whole point is for a server with no prior relationship to this one to be
    /// able to look someone up. Only ever stores/returns username + server address
    /// + federation id - never profile images (see
    /// <see cref="FederationController.GetAvatar"/>) and never anything that grants
    /// access to anything; redeeming a result still goes through the ordinary
    /// friend-request handshake, which still needs a human to accept it.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation/Directory")]
    public class FederationDirectoryController : ControllerBase
    {
        private readonly ILogger<FederationDirectoryController> _logger;
        private readonly FederationDirectoryStore _store;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDirectoryController"/> class.
        /// </summary>
        public FederationDirectoryController(ILogger<FederationDirectoryController> logger, FederationDirectoryStore store)
        {
            _logger = logger;
            _store = store;
        }

        private bool HostingEnabled => Plugin.Instance?.Configuration?.HostDirectory == true;

        [HttpPost("Register")]
        [AllowAnonymous]
        public IActionResult Register([FromBody] RegisterRequest body)
        {
            if (!HostingEnabled)
            {
                return NotFound();
            }

            if (body == null
                || !ConfigValidator.IsValidUsername(body.Username)
                || string.IsNullOrEmpty(body.FederationId)
                || !ConfigValidator.IsValidServerUrl(body.ServerUrl))
            {
                return BadRequest(new { error = "username, federationId, and a valid serverUrl are required" });
            }

            _store.Register(body.Username, body.FederationId, body.ServerUrl.TrimEnd('/'));
            _logger.LogInformation("[Federation] Directory: registered {Username} ({FederationId})", body.Username, body.FederationId);
            return Ok(new { success = true });
        }

        [HttpGet("Search")]
        [AllowAnonymous]
        public IActionResult Search([FromQuery] string username)
        {
            if (!HostingEnabled)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                return Ok(Array.Empty<object>());
            }

            var results = _store.Search(username).Select(e => new
            {
                username = e.Username,
                federationId = e.FederationId,
                serverUrl = e.ServerUrl
            });
            return Ok(results);
        }

        [HttpPost("Invite/Create")]
        [AllowAnonymous]
        public IActionResult CreateInvite([FromBody] InviteCreateRequest body)
        {
            if (!HostingEnabled)
            {
                return NotFound();
            }

            if (body == null || string.IsNullOrEmpty(body.FederationId) || !ConfigValidator.IsValidServerUrl(body.ServerUrl))
            {
                return BadRequest(new { error = "federationId and a valid serverUrl are required" });
            }

            var code = _store.CreateInvite(body.ServerUrl.TrimEnd('/'), body.FederationId);
            return Ok(new { code });
        }

        [HttpGet("Invite/{code}")]
        [AllowAnonymous]
        public IActionResult ResolveInvite(string code)
        {
            if (!HostingEnabled)
            {
                return NotFound();
            }

            var invite = _store.ResolveInvite(code);
            if (invite == null)
            {
                return NotFound();
            }

            return Ok(new { federationId = invite.FederationId, serverUrl = invite.ServerUrl });
        }

        /// <summary>
        /// Request body for <see cref="Register"/>.
        /// </summary>
        public class RegisterRequest
        {
            /// <summary>Gets or sets the username being registered.</summary>
            public string Username { get; set; } = string.Empty;

            /// <summary>Gets or sets the registering server's federation id.</summary>
            public string FederationId { get; set; } = string.Empty;

            /// <summary>Gets or sets the registering server's address.</summary>
            public string ServerUrl { get; set; } = string.Empty;
        }

        /// <summary>
        /// Request body for <see cref="CreateInvite"/>.
        /// </summary>
        public class InviteCreateRequest
        {
            /// <summary>Gets or sets the requesting server's federation id.</summary>
            public string FederationId { get; set; } = string.Empty;

            /// <summary>Gets or sets the requesting server's address.</summary>
            public string ServerUrl { get; set; } = string.Empty;
        }
    }
}
