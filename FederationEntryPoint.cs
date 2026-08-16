using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation
{
    /// <summary>
    /// Hosted service that initializes federation services on server startup:
    /// loads the persisted cache, defaults the local server URL, auto-provisions
    /// virtual libraries when enabled, and kicks off a background sync so items
    /// appear without waiting for the first scheduled task run.
    /// </summary>
    public class FederationEntryPoint : IHostedService
    {
        private readonly ILogger<FederationEntryPoint> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly LibraryProvisioningService _provisioning;
        private readonly FederationSyncService _syncService;
        private readonly FederationItemPersistenceService _persistence;
        private readonly WebClientInjector _webClientInjector;
        private readonly FederationDirectoryStore _directoryStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationEntryPoint"/> class.
        /// </summary>
        public FederationEntryPoint(
            ILogger<FederationEntryPoint> logger,
            FederationLibraryManager federationManager,
            LibraryProvisioningService provisioning,
            FederationSyncService syncService,
            FederationItemPersistenceService persistence,
            WebClientInjector webClientInjector,
            FederationDirectoryStore directoryStore)
        {
            _logger = logger;
            _federationManager = federationManager;
            _provisioning = provisioning;
            _syncService = syncService;
            _persistence = persistence;
            _webClientInjector = webClientInjector;
            _directoryStore = directoryStore;
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

                var directoryStorePath = Path.Combine(Plugin.Instance!.DataFolderPath, "directory-store.json");
                _directoryStore.Initialize(directoryStorePath);

                if (config.AutoProvisionLibraries)
                {
                    await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);
                }

                // Runs synchronously, before the delayed background sync below, so any
                // items left over from an earlier plugin version under CLR types that
                // no longer exist are gone before anything else - a native library
                // scan, the web UI, another plugin - gets a chance to enumerate the
                // same folder and hit the same crash outside this plugin's control.
                // See the comment on PurgeUndeserializableItemsAtStartup.
                _persistence.PurgeUndeserializableItemsAtStartup(config.LibraryMappings ?? new List<LibraryMapping>());

                _webClientInjector.EnsureBadgeScriptInjected();

                _logger.LogInformation("Federation Plugin services initialized successfully");

                // Kick off a background sync so federation items appear without
                // waiting for the first scheduled task run. Fire-and-forget so
                // server startup is not blocked by remote server round-trips.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Brief delay to let Jellyfin finish its own startup sequence
                        // before we start hitting remote servers.
                        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                        _logger.LogInformation("[Federation] Starting background startup sync");
                        var result = await _syncService.SyncAllAsync(CancellationToken.None).ConfigureAwait(false);
                        if (result.Success)
                        {
                            _logger.LogInformation("[Federation] Startup sync complete: {Message}", result.Message);
                        }
                        else
                        {
                            _logger.LogWarning("[Federation] Startup sync completed with issues: {Message}", result.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Federation] Background startup sync failed");
                    }
                });
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
