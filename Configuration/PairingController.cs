using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Paired friends (docs/PAIRING.md): the admin's pairing-code routes, and the two
    /// routes a paired friend reaches only over its own link.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public class PairingController : ControllerBase
    {
        private readonly PairingService _pairing;

        /// <summary>Initializes a new instance of the <see cref="PairingController"/> class.</summary>
        public PairingController(PairingService pairing)
        {
            _pairing = pairing;
        }

        /// <summary>Admin: pairing status (no secrets).</summary>
        /// <returns>The status.</returns>
        [HttpGet("Pairing/Status")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult Status() => Ok(_pairing.Status());

        /// <summary>Admin: the pairing settings.</summary>
        /// <returns>The settings.</returns>
        [HttpGet("Settings/Pairing")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetSettings()
        {
            var config = Plugin.Instance!.Configuration;
            return Ok(new PairingSettingsBody { EnablePairing = PairingService.IsEnabled(config), PortFirst = config.PairingPortFirst, PortLast = config.PairingPortLast });
        }

        /// <summary>Admin: saves the pairing settings.</summary>
        /// <param name="body">The settings.</param>
        /// <returns>Result.</returns>
        [HttpPost("Settings/Pairing")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetSettings([FromBody] PairingSettingsBody? body)
        {
            if (body == null || body.PortFirst < 1024 || body.PortLast > 65535 || body.PortLast < body.PortFirst || body.PortLast - body.PortFirst > 1000)
            {
                return BadRequest(new { success = false, message = "Use a port range between 1024 and 65535, at most 1000 ports wide." });
            }

            if (body.EnablePairing && PairingService.ReviewLocked)
            {
                return BadRequest(new { success = false, message = PairingService.ReviewLockedMessage });
            }

            var config = Plugin.Instance!.Configuration;
            config.PairingOptIn = body.EnablePairing;
            config.PairingPortFirst = body.PortFirst;
            config.PairingPortLast = body.PortLast;
            Plugin.Instance.SaveConfiguration();
            _pairing.Reconcile();
            return Ok(new { success = true, message = body.EnablePairing ? "Pairing codes are on." : "Pairing codes are off; no pairing port is open." });
        }

        /// <summary>Admin: makes a first code to send to a friend.</summary>
        /// <returns>The code and when it expires.</returns>
        [HttpPost("Pairing/Offer")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> CreateOffer(CancellationToken cancellationToken)
        {
            var (code, expires, error) = await _pairing.CreateOfferAsync(cancellationToken).ConfigureAwait(false);
            return code == null ? BadRequest(new { success = false, message = error }) : Ok(new { success = true, code, expires });
        }

        /// <summary>Admin: cancels an unused code.</summary>
        /// <param name="id">The code's id from the status.</param>
        /// <returns>Result.</returns>
        [HttpDelete("Pairing/Offer/{id}")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult CancelOffer(string id)
            => _pairing.CancelOffer(id) ? Ok(new { success = true }) : NotFound(new { success = false, message = "No such code." });

        /// <summary>Admin: enters a friend's first code; returns the answer to send back.</summary>
        /// <param name="body">The friend's code.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The answer code and the new friend's id.</returns>
        [HttpPost("Pairing/Answer")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> Answer([FromBody] PairingCodeBody? body, CancellationToken cancellationToken)
        {
            var (answer, friendId, error) = await _pairing.AnswerOfferAsync(body?.Code ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return answer == null ? BadRequest(new { success = false, message = error }) : Ok(new { success = true, code = answer, friendId });
        }

        /// <summary>Admin: enters the friend's answer to one of this server's codes.</summary>
        /// <param name="body">The answer code.</param>
        /// <returns>The new friend's id.</returns>
        [HttpPost("Pairing/Complete")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult Complete([FromBody] PairingCodeBody? body)
        {
            var (friendId, error) = _pairing.CompleteOffer(body?.Code ?? string.Empty);
            return friendId == null ? BadRequest(new { success = false, message = error }) : Ok(new { success = true, friendId });
        }

        /// <summary>Admin: the safety number matched the friend's.</summary>
        /// <param name="id">The paired friend's id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result.</returns>
        [HttpPost("Pairing/{id}/Confirm")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> Confirm(string id, CancellationToken cancellationToken)
        {
            var error = await _pairing.ConfirmAsync(id, cancellationToken).ConfigureAwait(false);
            return error == null ? Ok(new { success = true }) : NotFound(new { success = false, message = error });
        }

        /// <summary>Paired friend, over its link only: its admin confirmed the safety number.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>204, or 404 when the request didn't come over a paired link.</returns>
        [HttpPost("Pairing/Peer/Confirmed")]
        [AllowAnonymous]
        public async Task<IActionResult> PeerConfirmed(CancellationToken cancellationToken)
        {
            var friendId = LinkFriend();
            if (friendId == null)
            {
                return NotFound();
            }

            await _pairing.OnPeerConfirmedAsync(friendId, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }

        /// <summary>Paired friend, over its link only: swaps federation ids and tokens once both admins confirmed.</summary>
        /// <param name="hello">The friend's introduction.</param>
        /// <returns>This server's introduction.</returns>
        [HttpPost("Pairing/Peer/Hello")]
        [AllowAnonymous]
        public IActionResult PeerHello([FromBody] PairingHello? hello)
        {
            var friendId = LinkFriend();
            if (friendId == null)
            {
                return NotFound();
            }

            var (answer, error) = _pairing.OnHello(friendId, hello);
            return answer == null ? Conflict(new { message = error }) : Ok(answer);
        }

        /// <summary>
        /// The paired friend a request came over, or null. Anything not from the
        /// forwarder (which alone knows the per-process secret) gets the same 404 as a
        /// route that doesn't exist.
        /// </summary>
        private string? LinkFriend()
        {
            var id = PairedLinkContext.FriendId(Request);
            return id != null && Plugin.Instance?.Configuration?.RemoteServers?.Any(s => s.Id == id && s.Paired && s.Enabled) == true ? id : null;
        }
    }

    /// <summary>Body carrying a pairing code.</summary>
    public class PairingCodeBody
    {
        /// <summary>Gets or sets the code.</summary>
        public string Code { get; set; } = string.Empty;
    }

    /// <summary>Body of <c>Settings/Pairing</c>.</summary>
    public class PairingSettingsBody
    {
        /// <summary>Gets or sets a value indicating whether pairing is on.</summary>
        public bool EnablePairing { get; set; }

        /// <summary>Gets or sets the first port.</summary>
        public int PortFirst { get; set; } = 47950;

        /// <summary>Gets or sets the last port.</summary>
        public int PortLast { get; set; } = 47999;
    }
}
