using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>Exposes compatible entries from the reviewed, discovery-only integration catalog.</summary>
    [ApiController]
    [Route("Plugins/Federation/Integrations")]
    [Authorize(Policy = "RequiresElevation")]
    public sealed class IntegrationCatalogController : ControllerBase
    {
        private readonly IntegrationCatalogService _catalog;

        /// <summary>Initializes the controller.</summary>
        public IntegrationCatalogController(IntegrationCatalogService catalog) => _catalog = catalog;

        /// <summary>Lists integrations compatible with this Federation/Jellyfin/platform combination.</summary>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool refresh, CancellationToken cancellationToken)
        {
            var result = await _catalog.GetAsync(refresh, cancellationToken).ConfigureAwait(false);
            Response.Headers.CacheControl = "private, no-store";
            return Ok(new
            {
                source = result.Source,
                warning = result.Warning,
                integrations = result.Integrations.Select(i => new
                {
                    id = i.Id,
                    type = i.Type,
                    name = i.Name,
                    summary = i.Summary,
                    publisher = i.Publisher,
                    builtinProviderId = i.BuiltinProviderId,
                    installGuideUrl = i.InstallGuideUrl,
                    capabilities = i.Capabilities,
                    installed = i.Installed
                })
            });
        }
    }
}
