using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Direct link routes: the friend-facing offer (token-authenticated, the only
    /// place connection details are exchanged), and the admin status/settings.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public class DirectLinkController : ControllerBase
    {
        private readonly DirectLinkManager _manager;

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectLinkController"/> class.
        /// </summary>
        /// <param name="manager">The direct link manager.</param>
        public DirectLinkController(DirectLinkManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// Server-to-server: a friend offers its half of a direct link and gets
        /// this server's half back. Authenticated with the friend's federation
        /// token like every Peer route.
        /// </summary>
        /// <param name="offer">The friend's connection details.</param>
        /// <param name="cancellationToken">Request aborted.</param>
        /// <returns>This server's connection details, or a refusal.</returns>
        [HttpPost("Peer/DirectLink/Offer")]
        [AllowAnonymous]
        public async Task<IActionResult> Offer([FromBody] DirectLinkOffer? offer, CancellationToken cancellationToken)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            var (status, answer, message) = await _manager.HandleOfferAsync(caller, offer, cancellationToken).ConfigureAwait(false);
            return status == StatusCodes.Status200OK
                ? Ok(answer)
                : StatusCode(status, new { success = false, message });
        }

        /// <summary>Admin: every friend's direct link state.</summary>
        /// <returns>The status.</returns>
        [HttpGet("DirectLink/Status")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult Status() => Ok(_manager.GetStatus());

        /// <summary>Admin: the direct link settings.</summary>
        /// <returns>The settings.</returns>
        [HttpGet("Settings/DirectLink")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetSettings()
        {
            var config = Plugin.Instance!.Configuration;
            return Ok(new { config.EnableDirectLink, config.DirectLinkStunServers });
        }

        /// <summary>Admin: saves the direct link settings.</summary>
        /// <param name="body">The settings.</param>
        /// <returns>Result.</returns>
        [HttpPost("Settings/DirectLink")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetSettings([FromBody] DirectLinkSettingsBody? body)
        {
            if (body == null)
            {
                return BadRequest(new { success = false, message = "Missing settings." });
            }

            var config = Plugin.Instance!.Configuration;
            if (body.DirectLinkStunServers != null)
            {
                var servers = body.DirectLinkStunServers.Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0).Distinct().ToArray();
                if (servers.Length > 6 || servers.Any(s => s.Length > 200 || StunServerInvalid(s)))
                {
                    return BadRequest(new { success = false, message = "Use up to 6 STUN servers as host:port." });
                }

                config.DirectLinkStunServers = servers.Length > 0 ? servers : new PluginConfiguration().DirectLinkStunServers;
            }

            config.EnableDirectLink = body.EnableDirectLink;
            Plugin.Instance.SaveConfiguration();
            foreach (var server in config.RemoteServers)
            {
                _manager.Reconsider(server.Id);
            }

            return Ok(new { success = true, message = "Direct link settings saved." });
        }

        /// <summary>Admin: turns the direct link to one friend on (Auto) or Off.</summary>
        /// <param name="id">The friend's configured server id.</param>
        /// <param name="body">The mode.</param>
        /// <returns>Result.</returns>
        [HttpPost("Friends/{id}/DirectLink")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetFriendMode(string id, [FromBody] DirectLinkModeBody? body)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            var mode = body?.Mode;
            if (!string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { success = false, message = "Mode must be Auto or Off." });
            }

            server.DirectLinkMode = string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase) ? "Off" : "Auto";
            Plugin.Instance!.SaveConfiguration();
            _manager.Reconsider(id);
            return Ok(new { success = true, message = "Saved." });
        }

        /// <summary>Admin: try the direct link to a friend again now.</summary>
        /// <param name="id">The friend's configured server id.</param>
        /// <returns>Result.</returns>
        [HttpPost("DirectLink/Retry/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult Retry(string id)
        {
            if (Plugin.Instance?.Configuration?.RemoteServers?.Any(s => s.Id == id) != true)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            _manager.Reconsider(id);
            return Ok(new { success = true, message = "Retrying." });
        }

        private static bool StunServerInvalid(string value)
        {
            var colon = value.LastIndexOf(':');
            var host = colon > 0 ? value.Substring(0, colon) : value;
            return host.Length == 0 || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-'));
        }
    }

    /// <summary>Body of <c>Settings/DirectLink</c>.</summary>
    public class DirectLinkSettingsBody
    {
        /// <summary>Gets or sets a value indicating whether direct links are on.</summary>
        public bool EnableDirectLink { get; set; } = true;

        /// <summary>Gets or sets the STUN servers (null keeps the current ones).</summary>
        public string[]? DirectLinkStunServers { get; set; }
    }

    /// <summary>Body of <c>Friends/{id}/DirectLink</c>.</summary>
    public class DirectLinkModeBody
    {
        /// <summary>Gets or sets <c>Auto</c> or <c>Off</c>.</summary>
        public string? Mode { get; set; }
    }
}
