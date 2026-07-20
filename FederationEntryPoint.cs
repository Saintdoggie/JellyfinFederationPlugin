using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation
{
    /// <summary>
    /// Hosted service that initializes federation services on server startup:
    /// loads the persisted cache, defaults the local server URL, and
    /// auto-provisions virtual libraries when enabled.
    /// </summary>
    public class FederationEntryPoint : IHostedService
    {
        private readonly ILogger<FederationEntryPoint> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly LibraryProvisioningService _provisioning;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationEntryPoint"/> class.
        /// </summary>
        public FederationEntryPoint(
            ILogger<FederationEntryPoint> logger,
            FederationLibraryManager federationManager,
            LibraryProvisioningService provisioning)
        {
            _logger = logger;
            _federationManager = federationManager;
            _provisioning = provisioning;
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Federation Plugin Entry Point started");

            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("[Federation] Plugin configuration not available");
                    return;
                }

                // The local server URL is intentionally left blank when unconfigured:
                // blank means "auto-detect from the incoming request" (see
                // FederationMediaSourceProvider). It is only needed for Proxy
                // streaming mode and can still be overridden on the config page.
                var cachePath = !string.IsNullOrEmpty(config.CachePath)
                    ? config.CachePath
                    : Plugin.Instance?.GetDefaultCachePath() ?? Path.Combine(Path.GetTempPath(), "federation-cache.json");

                _federationManager.Initialize(cachePath);

                if (config.AutoProvisionLibraries)
                {
                    await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Federation Plugin services initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing Federation Plugin services");
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
