using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Admin setup for using the owner's own Plex server as the artwork and metadata source
    /// of their Jellyfin libraries. The plex.tv account token never leaves the server: the UI
    /// only ever sees server names and a sign-in id.
    /// </summary>
    [ApiController]
    [Route("Plugins/Federation/PlexSetup")]
    [Authorize(Policy = "RequiresElevation")]
    public class PlexMetadataSetupController : ControllerBase
    {
        private static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(15);
        private static readonly ConcurrentDictionary<long, (string Token, DateTime Expires)> SignIns = new();

        private readonly PlexMetadataSourceService _plex;
        private readonly PlexLibraryImportService _import;
        private readonly ILogger<PlexMetadataSetupController> _logger;

        /// <summary>Initializes a new instance of the <see cref="PlexMetadataSetupController"/> class.</summary>
        public PlexMetadataSetupController(PlexMetadataSourceService plex, PlexLibraryImportService import, ILogger<PlexMetadataSetupController> logger)
        {
            _plex = plex;
            _import = import;
            _logger = logger;
        }

        /// <summary>Current settings (never the token), the eligible Jellyfin libraries, and the Plex sections when connected.</summary>
        [HttpGet("Status")]
        public async Task<IActionResult> Status(CancellationToken cancellationToken)
        {
            var settings = Plugin.Instance!.Configuration.PlexMetadataSource;
            object[] sections = Array.Empty<object>();
            string? error = null;
            if (!string.IsNullOrEmpty(settings.ServerUrl) && !string.IsNullOrEmpty(settings.Token))
            {
                try
                {
                    sections = (await _plex.ListSectionsAsync(settings.ServerUrl, settings.Token, cancellationToken).ConfigureAwait(false))
                        .Select(s => (object)new { s.Key, s.Title, s.Type }).ToArray();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = "Could not reach the Plex server: " + ex.Message;
                }
            }

            return Ok(new
            {
                settings.Enabled,
                settings.ServerName,
                settings.ServerUrl,
                Connected = !string.IsNullOrEmpty(settings.Token),
                settings.SectionKeys,
                settings.JellyfinLibraryIds,
                settings.PullArtwork,
                settings.PullText,
                PathMappings = settings.PathMappings.Select(m => new { m.From, m.To }),
                Sections = sections,
                Libraries = _plex.ListEligibleLibraries().Select(l => new { Id = l.Id, Name = l.Name }),
                Error = error,
            });
        }

        /// <summary>Starts plex.tv sign-in; the admin opens <c>authUrl</c> in a browser and approves.</summary>
        [HttpPost("SignIn")]
        public async Task<IActionResult> StartSignIn(CancellationToken cancellationToken)
        {
            var started = await _plex.StartSignInAsync(cancellationToken).ConfigureAwait(false);
            if (started == null)
            {
                return StatusCode(502, new { error = "plex.tv did not give a sign-in code. Check this server can reach plex.tv, or use the manual option." });
            }

            return Ok(new { pinId = started.Value.PinId, authUrl = started.Value.AuthUrl });
        }

        /// <summary>Polled until the admin approves; then returns the servers on the account.</summary>
        [HttpGet("SignIn/{pinId:long}")]
        public async Task<IActionResult> PollSignIn(long pinId, CancellationToken cancellationToken)
        {
            Prune();
            if (!SignIns.TryGetValue(pinId, out var known))
            {
                var token = await _plex.PollSignInAsync(pinId, cancellationToken).ConfigureAwait(false);
                if (token == null)
                {
                    return Ok(new { signedIn = false });
                }

                known = (token, DateTime.UtcNow + SignInLifetime);
                SignIns[pinId] = known;
            }

            try
            {
                var servers = await _plex.ListServersAsync(known.Token, cancellationToken).ConfigureAwait(false);
                return Ok(new
                {
                    signedIn = true,
                    servers = servers.Select(s => new { s.Name, s.MachineId, s.Owned, addresses = s.Connections.Count }),
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Federation] Could not list Plex servers");
                return StatusCode(502, new { error = "Signed in, but plex.tv would not list your servers." });
            }
        }

        /// <summary>Connects to the chosen server, trying each address plex.tv knows until one answers.</summary>
        [HttpPost("Connect")]
        public async Task<IActionResult> Connect([FromBody] ConnectBody body, CancellationToken cancellationToken)
        {
            if (!SignIns.TryGetValue(body.PinId, out var known))
            {
                return BadRequest(new { error = "Sign-in expired. Sign in to Plex again." });
            }

            var servers = await _plex.ListServersAsync(known.Token, cancellationToken).ConfigureAwait(false);
            var server = servers.FirstOrDefault(s => string.Equals(s.MachineId, body.MachineId, StringComparison.Ordinal));
            if (server == null)
            {
                return BadRequest(new { error = "That server is no longer on the account." });
            }

            var reachable = await _plex.FindReachableAddressAsync(server, cancellationToken).ConfigureAwait(false);
            if (reachable == null)
            {
                return StatusCode(502, new { error = "This Jellyfin server could not reach '" + server.Name + "' at any address plex.tv lists. If Plex runs on the same machine, use the manual option with http://localhost:32400." });
            }

            Save(s =>
            {
                s.ServerName = server.Name;
                s.ServerUrl = reachable.Value.Url;
                s.Token = server.Token;
                s.SectionKeys = new List<string>();
            });
            SignIns.TryRemove(body.PinId, out _);
            return await Status(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Manual setup: an address and a Plex token, for servers not on a plex.tv account.</summary>
        [HttpPost("Manual")]
        public async Task<IActionResult> Manual([FromBody] ManualBody body, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(body.Url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(body.Token))
            {
                return BadRequest(new { error = "Enter the Plex address (http:// or https://) and a Plex token." });
            }

            string? name;
            try
            {
                name = await _plex.TestAsync(uri.ToString().TrimEnd('/'), body.Token.Trim(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                name = null;
                _logger.LogDebug(ex, "[Federation] Manual Plex test failed");
            }

            if (name == null)
            {
                return StatusCode(502, new { error = "Could not connect to Plex with that address and token." });
            }

            Save(s =>
            {
                s.ServerName = name;
                s.ServerUrl = uri.ToString().TrimEnd('/');
                s.Token = body.Token.Trim();
                s.SectionKeys = new List<string>();
            });
            return await Status(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Saves what to pull and where, and attaches or detaches the Plex fetcher on the libraries.</summary>
        [HttpPost("Options")]
        public async Task<IActionResult> Options([FromBody] OptionsBody body, CancellationToken cancellationToken)
        {
            var current = Plugin.Instance!.Configuration.PlexMetadataSource;
            if (body.Enabled && (string.IsNullOrEmpty(current.ServerUrl) || string.IsNullOrEmpty(current.Token)))
            {
                return BadRequest(new { error = "Connect a Plex server first." });
            }

            Save(s =>
            {
                s.Enabled = body.Enabled;
                s.SectionKeys = body.SectionKeys ?? new List<string>();
                s.JellyfinLibraryIds = body.JellyfinLibraryIds ?? new List<string>();
                s.PullArtwork = body.PullArtwork;
                s.PullText = body.PullText;
            });
            _plex.Invalidate();
            await _plex.ApplyFetchersAsync(body.JellyfinLibraryIds ?? new List<string>(), body.Enabled).ConfigureAwait(false);
            return await Status(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Forgets the Plex server and detaches Plex from every library.</summary>
        [HttpPost("Disconnect")]
        public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
        {
            await _plex.ApplyFetchersAsync(Array.Empty<string>(), false).ConfigureAwait(false);
            Save(s =>
            {
                s.Enabled = false;
                s.ServerName = string.Empty;
                s.ServerUrl = string.Empty;
                s.Token = string.Empty;
                s.SectionKeys = new List<string>();
                s.JellyfinLibraryIds = new List<string>();
            });
            _plex.Invalidate();
            return await Status(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Re-reads Plex and queues a full artwork/metadata refresh of the chosen libraries.</summary>
        [HttpPost("PullNow")]
        public IActionResult PullNow()
        {
            if (!Plugin.Instance!.Configuration.PlexMetadataSource.Enabled)
            {
                return BadRequest(new { error = "Turn the Plex metadata source on first." });
            }

            return Ok(new { queued = _plex.QueueFullRefresh() });
        }

        /// <summary>
        /// Guided import, step 1: each Plex library, where this server sees its folders, and whether a Jellyfin
        /// library will be created or reused. <c>PathMappings</c> previews unsaved rewrites; omit it to use the saved ones.
        /// </summary>
        [HttpPost("ImportPlan")]
        public async Task<IActionResult> ImportPlan([FromBody] PlanBody? body, CancellationToken cancellationToken)
        {
            var settings = Plugin.Instance!.Configuration.PlexMetadataSource;
            if (string.IsNullOrEmpty(settings.ServerUrl) || string.IsNullOrEmpty(settings.Token))
            {
                return BadRequest(new { error = "Connect a Plex server first." });
            }

            try
            {
                var plan = await _import.PlanAsync(body?.PathMappings, cancellationToken).ConfigureAwait(false);
                return Ok(new
                {
                    PathMappings = (body?.PathMappings ?? settings.PathMappings).Select(m => new { m.From, m.To }),
                    Sections = plan.Select(p => new
                    {
                        p.Key,
                        p.Title,
                        p.Type,
                        State = p.State.ToString(),
                        p.LibraryName,
                        Folders = p.Folders.Select(f => new { f.PlexPath, f.LocalPath, f.Found }),
                    }),
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Federation] Could not read Plex libraries for the guided import");
                return StatusCode(502, new { error = "Could not read your Plex libraries: " + ex.Message });
            }
        }

        /// <summary>Guided import, step 2: create or reuse a Jellyfin library per chosen Plex library and turn Plex artwork on.</summary>
        [HttpPost("Import")]
        public async Task<IActionResult> Import([FromBody] ImportBody body, CancellationToken cancellationToken)
        {
            var settings = Plugin.Instance!.Configuration.PlexMetadataSource;
            if (string.IsNullOrEmpty(settings.ServerUrl) || string.IsNullOrEmpty(settings.Token))
            {
                return BadRequest(new { error = "Connect a Plex server first." });
            }

            if (body.SectionKeys == null || body.SectionKeys.Count == 0)
            {
                return BadRequest(new { error = "Pick at least one Plex library." });
            }

            try
            {
                var results = await _import.ImportAsync(body.SectionKeys, body.PathMappings ?? new List<PlexPathMapping>(), cancellationToken).ConfigureAwait(false);
                return Ok(new { Results = results.Select(r => new { r.Key, r.Title, r.Ok, r.LibraryName, r.Message }) });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Federation] Guided Plex import failed");
                return StatusCode(502, new { error = "Import stopped: " + ex.Message });
            }
        }

        /// <summary>Guided import, step 3: how many movies and shows in each library found their Plex twin.</summary>
        [HttpGet("Report")]
        public async Task<IActionResult> Report(CancellationToken cancellationToken)
        {
            if (!Plugin.Instance!.Configuration.PlexMetadataSource.Enabled)
            {
                return Ok(new { Libraries = Array.Empty<object>() });
            }

            var reports = await _import.ReportAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { Libraries = reports.Select(r => new { r.Id, r.Name, r.Total, r.Matched, r.Unmatched }) });
        }

        private static void Save(Action<PlexMetadataSourceSettings> change)
        {
            var plugin = Plugin.Instance!;
            var config = plugin.Configuration;
            config.PlexMetadataSource ??= new PlexMetadataSourceSettings();
            change(config.PlexMetadataSource);
            plugin.UpdateConfiguration(config);
        }

        private static void Prune()
        {
            foreach (var pair in SignIns.Where(p => p.Value.Expires < DateTime.UtcNow).ToList())
            {
                SignIns.TryRemove(pair.Key, out _);
            }
        }

        /// <summary>Body of <c>PlexSetup/Connect</c>.</summary>
        public sealed class ConnectBody
        {
            /// <summary>Gets or sets the sign-in id returned by <c>SignIn</c>.</summary>
            public long PinId { get; set; }

            /// <summary>Gets or sets the Plex server's machine identifier.</summary>
            public string MachineId { get; set; } = string.Empty;
        }

        /// <summary>Body of <c>PlexSetup/Manual</c>.</summary>
        public sealed class ManualBody
        {
            /// <summary>Gets or sets the Plex server address.</summary>
            public string? Url { get; set; }

            /// <summary>Gets or sets the Plex token.</summary>
            public string? Token { get; set; }
        }

        /// <summary>Body of <c>PlexSetup/ImportPlan</c>.</summary>
        public sealed class PlanBody
        {
            /// <summary>Gets or sets folder rewrites to preview; null uses the saved ones.</summary>
            public List<PlexPathMapping>? PathMappings { get; set; }
        }

        /// <summary>Body of <c>PlexSetup/Import</c>.</summary>
        public sealed class ImportBody
        {
            /// <summary>Gets or sets the Plex library section keys to bring over.</summary>
            public List<string>? SectionKeys { get; set; }

            /// <summary>Gets or sets the folder rewrites to save and use.</summary>
            public List<PlexPathMapping>? PathMappings { get; set; }
        }

        /// <summary>Body of <c>PlexSetup/Options</c>.</summary>
        public sealed class OptionsBody
        {
            /// <summary>Gets or sets a value indicating whether Plex is used.</summary>
            public bool Enabled { get; set; }

            /// <summary>Gets or sets the Plex section keys to match against.</summary>
            public List<string>? SectionKeys { get; set; }

            /// <summary>Gets or sets the Jellyfin library ids to apply to.</summary>
            public List<string>? JellyfinLibraryIds { get; set; }

            /// <summary>Gets or sets a value indicating whether artwork is pulled.</summary>
            public bool PullArtwork { get; set; } = true;

            /// <summary>Gets or sets a value indicating whether text metadata is pulled.</summary>
            public bool PullText { get; set; } = true;
        }
    }
}
