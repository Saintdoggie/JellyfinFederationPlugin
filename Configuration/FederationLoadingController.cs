using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Feeds the injected web client's loading bar: a projected start time with the
    /// reason for the wait, and a fun fact. Any signed-in Jellyfin user may call it;
    /// it returns no secrets, only the friend's display name, speeds and a sentence.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation")]
    public class FederationLoadingController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly FederationLibraryManager _federationManager;
        private readonly WanBandwidthMonitor _bandwidthMonitor;
        private readonly FunFactService _funFacts;
        private readonly ExternalCatalogRegistry _externalCatalogs;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationLoadingController"/> class.
        /// </summary>
        public FederationLoadingController(
            ILibraryManager libraryManager,
            FederationLibraryManager federationManager,
            WanBandwidthMonitor bandwidthMonitor,
            FunFactService funFacts,
            ExternalCatalogRegistry externalCatalogs)
        {
            _libraryManager = libraryManager;
            _federationManager = federationManager;
            _bandwidthMonitor = bandwidthMonitor;
            _funFacts = funFacts;
            _externalCatalogs = externalCatalogs;
        }

        /// <summary>
        /// Projects how long a federated title will take to start.
        /// </summary>
        /// <param name="itemId">The Jellyfin item id the viewer pressed play on.</param>
        /// <returns>The projection, or <c>federated: false</c> for a local title.</returns>
        [HttpGet("StartEstimate/{itemId}")]
        [Authorize]
        [Produces("application/json")]
        public ActionResult<object> GetStartEstimate(Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            var key = FederationLibraryManager.GetFederationKey(item);
            var entry = key == null ? null : _federationManager.Cache.GetEntryByKey(key);
            var source = entry == null ? null : _federationManager.GetPlaybackSource(entry);
            if (entry == null || source == null)
            {
                return Ok(new { federated = false });
            }

            var server = _federationManager.GetServer(source.ServerId);
            var bitrate = source.Bitrate is > 0
                ? source.Bitrate
                : source.MediaStreams?.Where(s => s.BitRate.HasValue).Sum(s => (long)s.BitRate!.Value);
            var estimate = StartEstimator.Estimate(
                server?.Name ?? string.Empty,
                bitrate,
                _bandwidthMonitor.GetMeasuredLinkMbps(source.ServerId));

            return Ok(new
            {
                federated = true,
                seconds = estimate.Seconds,
                reason = estimate.Reason,
                serverName = estimate.ServerName,
                linkMbps = estimate.LinkMbps,
                bitrateMbps = estimate.BitrateMbps,
            });
        }

        /// <summary>
        /// Returns one fun fact for the loading bar.
        /// </summary>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>A single plain-text sentence.</returns>
        [HttpGet("FunFact")]
        [Authorize]
        [Produces("application/json")]
        public async Task<ActionResult<object>> GetFunFact(CancellationToken cancellationToken)
            => Ok(new { text = await _funFacts.GetAsync(cancellationToken).ConfigureAwait(false) });

        /// <summary>
        /// Admin diagnostic: asks the title's Plex server for a short capped transcode and
        /// reports whether it works and how fast data flows. Reads from the friend's server
        /// for a few seconds and uses a little of its CPU, so it is admin-only and bounded.
        /// </summary>
        /// <param name="itemId">A federated Plex title.</param>
        /// <param name="kbps">Video bitrate ceiling in kilobits per second (500-20000).</param>
        /// <param name="seconds">How long to read (2-15).</param>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>What Plex did.</returns>
        [HttpGet("Diagnostics/PlexTranscodeProbe/{itemId}")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("application/json")]
        public async Task<ActionResult<object>> ProbePlexTranscode(Guid itemId, [FromQuery] int kbps = 6000, [FromQuery] int seconds = 8, CancellationToken cancellationToken = default)
        {
            kbps = Math.Clamp(kbps, 500, 20000);
            seconds = Math.Clamp(seconds, 2, 15);
            var item = _libraryManager.GetItemById(itemId);
            var key = FederationLibraryManager.GetFederationKey(item);
            var entry = key == null ? null : _federationManager.Cache.GetEntryByKey(key);
            var source = entry == null ? null : _federationManager.GetPlaybackSource(entry);
            var server = source == null ? null : _federationManager.GetServer(source.ServerId);
            if (entry == null || source == null || server == null)
            {
                return NotFound(new { error = "Not a federated title." });
            }

            if (_externalCatalogs.For(server) is not PlexCatalogProvider plex || entry.GetNativeId(source) is not { Length: > 0 } nativeId)
            {
                return BadRequest(new { error = "This title does not come from a Plex server." });
            }

            var result = await plex.ProbeTranscodeAsync(server, nativeId, kbps, seconds, cancellationToken).ConfigureAwait(false);
            return result == null
                ? StatusCode(502, new { error = "Could not build a Plex client for this server." })
                : Ok(new
                {
                    server = server.Name,
                    requestedKbps = kbps,
                    httpStatus = result.HttpStatus,
                    firstByteMs = result.FirstByteMs,
                    bytes = result.Bytes,
                    sampleSeconds = Math.Round(result.SampleSeconds, 1),
                    observedMbps = result.Mbps.HasValue ? Math.Round(result.Mbps.Value, 2) : (double?)null,
                    contentType = result.ContentType,
                    detail = result.Detail,
                    plexDecision = result.PlexDecision,
                    plexMessage = result.PlexMessage,
                });
        }
    }
}
