using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>Serves dashboard resources registered by installed Federation add-ins.</summary>
    [ApiController]
    [Route("Plugins/Federation/AddInPages")]
    public sealed class FederationAddInPageController : ControllerBase
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<FederationAddInPageController> _logger;

        public FederationAddInPageController(IServiceProvider serviceProvider, ILogger<FederationAddInPageController> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        [HttpGet("{pageName}")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("text/html")]
        public IActionResult GetPage(string pageName)
        {
            var providers = _serviceProvider.GetServices<IFederationWebPageProvider>().ToList();
            if (providers.Count == 0)
            {
                providers.AddRange(FederationWebPageDiscovery.CreateProviders(_logger));
            }

            var match = providers
                .SelectMany(provider => provider.GetPages().Select(page => (Provider: provider, Page: page)))
                .FirstOrDefault(candidate => string.Equals(candidate.Page.Name, pageName, StringComparison.OrdinalIgnoreCase));
            if (match.Provider == null || string.IsNullOrEmpty(match.Page.EmbeddedResourcePath))
            {
                return NotFound();
            }

            var stream = match.Provider.GetType().Assembly.GetManifestResourceStream(match.Page.EmbeddedResourcePath);
            return stream == null ? NotFound() : File(stream, "text/html; charset=utf-8");
        }
    }
}
