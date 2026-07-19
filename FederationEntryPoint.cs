using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation
{
    /// <summary>
    /// Entry point for initializing federation services and auto-detecting the
    /// local server URL. Runs once on plugin startup.
    /// </summary>
    public class FederationEntryPoint
    {
        private readonly ILogger<FederationEntryPoint> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly IServerConfigurationManager _serverConfigManager;
        private readonly LibraryProvisioningService _provisioning;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationEntryPoint"/> class.
        /// </summary>
        public FederationEntryPoint(
            ILogger<FederationEntryPoint> logger,
            FederationLibraryManager federationManager,
            IServerConfigurationManager serverConfigManager,
            LibraryProvisioningService provisioning)
        {
            _logger = logger;
            _federationManager = federationManager;
            _serverConfigManager = serverConfigManager;
            _provisioning = provisioning;
        }

        /// <summary>
        /// Runs initialization tasks.
        /// </summary>
        public async Task RunAsync()
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

                // Auto-detect local server URL if not overridden.
                if (string.IsNullOrEmpty(config.ServerUrl))
                {
                    var detected = DetectLocalServerUrl();
                    if (!string.IsNullOrEmpty(detected))
                    {
                        config.ServerUrl = detected;
                        Plugin.Instance?.SaveConfiguration();
                        _logger.LogInformation("[Federation] Auto-detected local server URL: {Url}", detected);
                    }
                    else
                    {
                        _logger.LogWarning("[Federation] Could not auto-detect local server URL. Please set it in the config page.");
                    }
                }

                var cachePath = !string.IsNullOrEmpty(config.CachePath)
                    ? config.CachePath
                    : Plugin.Instance?.GetDefaultCachePath() ?? Path.Combine(Path.GetTempPath(), "federation-cache.json");

                _federationManager.Initialize(cachePath);

                if (config.AutoProvisionLibraries)
                {
                    await _provisioning.EnsureLibrariesAsync().ConfigureAwait(false);
                }

                _logger.LogInformation("Federation Plugin services initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing Federation Plugin services");
            }
        }

        private string DetectLocalServerUrl()
        {
            try
            {
                // No reliable address field is exposed via ServerConfiguration in this ABI.
                // Fall back to localhost on default port; user can correct via config page.
                return "http://localhost:8096";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to auto-detect local server URL");
                return string.Empty;
            }
        }
    }
}