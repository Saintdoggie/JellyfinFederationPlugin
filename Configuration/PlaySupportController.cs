using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Admin: the "what plays without converting" report. Starting a run reads the
    /// library and the friends' catalog; nothing is changed.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation/PlaySupport")]
    [Authorize(Policy = "RequiresElevation")]
    public sealed class PlaySupportController : ControllerBase
    {
        private readonly PlaySupportReportService _reports;

        /// <summary>Initializes the controller.</summary>
        public PlaySupportController(PlaySupportReportService reports) => _reports = reports;

        /// <summary>Starts a run (or reports that one is already going).</summary>
        [HttpPost("Start")]
        public IActionResult Start()
        {
            var started = _reports.Start();
            return Ok(new { success = true, started });
        }

        /// <summary>The state of the current run and the last finished report.</summary>
        [HttpGet]
        public IActionResult Get()
        {
            Response.Headers.CacheControl = "private, no-store";
            var (running, checkedCount, result, error) = _reports.State();
            var config = Plugin.Instance?.Configuration;
            return Ok(new
            {
                running,
                checkedCount,
                error,
                thisServerConverts = TranscodePolicy.MayConvertForOwnViewers(config),
                report = result == null ? null : new
                {
                    generatedUtc = result.GeneratedUtc,
                    filesChecked = result.FilesChecked,
                    undescribed = result.Undescribed,
                    truncated = result.Truncated,
                    players = result.Players.Select(p => new
                    {
                        id = p.Id,
                        name = p.Name,
                        note = p.Note,
                        playsAsStored = p.PlaysAsStored,
                        soundOrWrapperOnly = p.SoundOrWrapperOnly,
                        needsConverting = p.NeedsConverting,
                        topReasons = p.TopReasons.Select(r => new { reason = r.Reason, count = r.Count })
                    }),
                    formats = result.Formats.Select(f => new { label = f.Label, count = f.Count, players = f.Players, examples = f.Examples })
                }
            });
        }
    }
}
