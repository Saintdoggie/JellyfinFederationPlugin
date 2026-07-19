using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Tasks
{
    /// <summary>
    /// Background task that walks each mapping and pulls items from remote servers,
    /// merging duplicates by provider ID. Replaces the old <c>FederationSyncTask</c>.
    /// </summary>
    public class FederationRefreshTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger<FederationRefreshTask> _logger;
        private readonly FederationSyncService _syncService;
        private readonly LibraryProvisioningService _provisioning;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationRefreshTask"/> class.
        /// </summary>
        public FederationRefreshTask(
            ILogger<FederationRefreshTask> logger,
            FederationSyncService syncService,
            LibraryProvisioningService provisioning)
        {
            _logger = logger;
            _syncService = syncService;
            _provisioning = provisioning;
        }

        /// <inheritdoc />
        public string Name => "Refresh Federation Cache";

        /// <inheritdoc />
        public string Key => "FederationRefresh";

        /// <inheritdoc />
        public string Description => "Pulls items from configured remote Jellyfin servers and refreshes the federation cache.";

        /// <inheritdoc />
        public string Category => "Federation";

        /// <inheritdoc />
        public bool IsHidden => false;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Federation] Starting scheduled refresh task");
            progress?.Report(0);

            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config != null && config.AutoProvisionLibraries)
                {
                    progress?.Report(5);
                    await _provisioning.EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);
                }

                progress?.Report(15);
                var result = await _syncService.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(100);

                if (result.Success)
                {
                    _logger.LogInformation("[Federation] Refresh task complete: {Message}", result.Message);
                }
                else
                {
                    _logger.LogError("[Federation] Refresh task failed: {Message}", result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Federation] Refresh task cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error during scheduled refresh task");
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var config = Plugin.Instance?.Configuration;
            var intervalHours = config?.RefreshIntervalHours > 0 ? config.RefreshIntervalHours : 1;

            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks,
                    MaxRuntimeTicks = TimeSpan.FromMinutes(30).Ticks
                }
            };
        }
    }
}
