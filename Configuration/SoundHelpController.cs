using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Sound help (<see cref="SoundHelpService"/>): the stream a player is given,
    /// and the linked server's side that converts the sound
    /// (<see cref="SoundHelpEncoder"/>).
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public sealed class SoundHelpController : ControllerBase
    {
        private readonly SoundHelpService _soundHelp;
        private readonly SoundHelpEncoder _encoder;

        /// <summary>Initializes a new instance of the <see cref="SoundHelpController"/> class.</summary>
        public SoundHelpController(SoundHelpService soundHelp, SoundHelpEncoder encoder)
        {
            _soundHelp = soundHelp;
            _encoder = encoder;
        }

        /// <summary>
        /// The play itself. Players load this address without sign-in headers (a
        /// video element can't send them), so the unguessable, short-lived play id
        /// in it is the permission; it holds no key or address.
        /// </summary>
        [HttpGet("SoundHelp/{ticket}/stream.{wrapper}")]
        [HttpHead("SoundHelp/{ticket}/stream.{wrapper}")]
        [AllowAnonymous]
        public async Task<IActionResult> Stream([FromRoute] string ticket, [FromRoute] string wrapper)
        {
            if (!SoundHelpEncoder.ValidPlayId(ticket))
            {
                return NotFound();
            }

            await _soundHelp.RunAsync(ticket, HttpContext).ConfigureAwait(false);
            return new EmptyResult();
        }

        /// <summary>Peer: can this server convert sound for its linked server now?</summary>
        [HttpGet("Peer/Linked/Sound")]
        [AllowAnonymous]
        public IActionResult Available()
        {
            if (LinkedCaller() == null)
            {
                return Unauthorized();
            }

            Response.Headers.CacheControl = "no-store";
            var why = _encoder.Unavailable(Plugin.Instance?.Configuration);
            return Ok(new SoundHelpAvailability { Available = why == null, Reason = why });
        }

        /// <summary>Peer: one piece of a play's sound in, the converted sound ready so far out.</summary>
        [HttpPost("Peer/Linked/Sound/{playId}")]
        [AllowAnonymous]
        [RequestSizeLimit(SoundHelpEncoder.MaxPieceBytes + 1024)]
        public async Task<IActionResult> Feed([FromRoute] string playId, [FromQuery] int seq, [FromQuery] int end, CancellationToken cancellationToken)
        {
            var caller = LinkedCaller();
            if (caller == null)
            {
                return Unauthorized();
            }

            Response.Headers.CacheControl = "no-store";
            var (status, body) = await _encoder.FeedAsync(caller.Id, playId, seq, end == 1, Request.Body, Plugin.Instance?.Configuration, cancellationToken).ConfigureAwait(false);
            return status == StatusCodes.Status200OK
                ? File(body, "application/octet-stream")
                : StatusCode(status);
        }

        /// <summary>Peer: the viewer stopped; end that play's conversion.</summary>
        [HttpPost("Peer/Linked/Sound/{playId}/Stop")]
        [AllowAnonymous]
        public IActionResult Stop([FromRoute] string playId)
        {
            var caller = LinkedCaller();
            if (caller == null)
            {
                return Unauthorized();
            }

            _encoder.Stop(caller.Id, playId);
            return Ok();
        }

        private RemoteServer? LinkedCaller()
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            var key = Request.Headers[RemoteServerClient.OwnerKeyHeader].ToString();
            return LinkedServerService.IsLinkedCaller(caller, key) ? caller : null;
        }
    }
}
