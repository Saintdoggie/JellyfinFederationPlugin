using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Linked servers (<see cref="LinkedServerService"/>): admin routes to link,
    /// enter codes and unlink, and the peer routes the two servers use.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public sealed class LinkedServerController : ControllerBase
    {
        private readonly LinkedServerService _links;

        /// <summary>Initializes the controller.</summary>
        public LinkedServerController(LinkedServerService links) => _links = links;

        /// <summary>Admin: friends that are linked or being linked, with the code to show.</summary>
        [HttpGet("Link/Status")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult Status()
        {
            Response.Headers.CacheControl = "private, no-store";
            return Ok(_links.Status().Select(s => new
            {
                friendId = s.FriendId,
                name = s.Name,
                linked = s.Linked,
                linkedAtUtc = s.LinkedAtUtc,
                code = s.Code,
                codeSecondsLeft = s.CodeSecondsLeft,
                secondsLeft = s.SecondsLeft,
                peerAsked = s.PeerAsked,
                theyAcceptedMyEntry = s.TheyAcceptedMyEntry,
                iAcceptedTheirEntry = s.IAcceptedTheirEntry,
                attemptsLeft = s.AttemptsLeft,
                friendsThrough = s.FriendsThrough,
                cannotLink = s.CannotLink
            }));
        }

        /// <summary>Admin: start linking with a friend.</summary>
        [HttpPost("Link/{friendId}/Start")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> Start([FromRoute] string friendId, CancellationToken cancellationToken)
        {
            var (ok, message) = await _links.StartAsync(friendId, cancellationToken).ConfigureAwait(false);
            return Ok(new { success = ok, message });
        }

        /// <summary>Admin: the code shown on the other server.</summary>
        [HttpPost("Link/{friendId}/Code")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> Code([FromRoute] string friendId, [FromBody] LinkCodeBody? body, CancellationToken cancellationToken)
        {
            var (ok, message) = await _links.EnterCodeAsync(friendId, body?.Code, cancellationToken).ConfigureAwait(false);
            return Ok(new { success = ok, message });
        }

        /// <summary>Admin: stop a link that is being made.</summary>
        [HttpDelete("Link/{friendId}/Pending")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult CancelPending([FromRoute] string friendId)
        {
            _links.Cancel(friendId);
            return Ok(new { success = true });
        }

        /// <summary>Admin: unlink (the friendship itself stays).</summary>
        [HttpDelete("Link/{friendId}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> Unlink([FromRoute] string friendId, CancellationToken cancellationToken)
        {
            var (ok, message) = await _links.UnlinkAsync(friendId, cancellationToken).ConfigureAwait(false);
            return Ok(new { success = ok, message });
        }

        /// <summary>Peer: the friend's admin clicked Link.</summary>
        [HttpPost("Peer/Linked/Request")]
        [AllowAnonymous]
        public IActionResult PeerRequest()
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            if (LinkedServerService.CannotLink(caller) != null)
            {
                return Ok(new LinkAnswer { Ok = false, Error = "invalid" });
            }

            _links.PeerRequested(caller);
            return Ok(new LinkAnswer { Ok = true });
        }

        /// <summary>Peer: the friend's admin typed this server's code.</summary>
        [HttpPost("Peer/Linked/Verify")]
        [AllowAnonymous]
        public IActionResult PeerVerify([FromBody] LinkVerifyBody? body)
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            var answer = _links.PeerVerify(caller, body);
            return answer.Error == "not-started" ? Conflict(answer) : Ok(answer);
        }

        /// <summary>Peer: this server's friends, for a linked server.</summary>
        [HttpGet("Peer/Linked/Friends")]
        [AllowAnonymous]
        public IActionResult PeerFriends()
        {
            var caller = LinkedCaller();
            if (caller == null)
            {
                return Unauthorized();
            }

            Response.Headers.CacheControl = "no-store";
            return Ok(LinkedServerService.Export(caller));
        }

        /// <summary>Peer: the linked server's friends changed; fetch them again.</summary>
        [HttpPost("Peer/Linked/Changed")]
        [AllowAnonymous]
        public IActionResult PeerChanged()
        {
            var caller = LinkedCaller();
            if (caller == null)
            {
                return Unauthorized();
            }

            _ = Task.Run(() => _links.ImportFromAsync(caller, CancellationToken.None));
            return Ok(new LinkAnswer { Ok = true });
        }

        /// <summary>Peer: the linked server unlinked.</summary>
        [HttpPost("Peer/Linked/Remove")]
        [AllowAnonymous]
        public async Task<IActionResult> PeerRemove(CancellationToken cancellationToken)
        {
            var caller = LinkedCaller();
            if (caller == null)
            {
                return Unauthorized();
            }

            await _links.PeerRemovedAsync(caller, cancellationToken).ConfigureAwait(false);
            return Ok(new LinkAnswer { Ok = true });
        }

        private RemoteServer? LinkedCaller()
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            var key = Request.Headers[RemoteServerClient.OwnerKeyHeader].ToString();
            return LinkedServerService.IsLinkedCaller(caller, key) ? caller : null;
        }
    }

    /// <summary>Body of <c>Link/{friendId}/Code</c>.</summary>
    public sealed class LinkCodeBody
    {
        /// <summary>Gets or sets the code shown on the other server.</summary>
        public string? Code { get; set; }
    }
}
