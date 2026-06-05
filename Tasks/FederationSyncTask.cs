using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Tasks
{
    /// <summary>
  /// Scheduled task to automatically sync federation content.
    /// </summary>
    public class FederationSyncTask : IScheduledTask
    {
        private readonly ILogger<FederationSyncTask> _logger;
     private readonly ILibraryManager _libraryManager;
  private readonly ILoggerFactory _loggerFactory;

        /// <summary>
      /// Initializes a new instance of the <see cref="FederationSyncTask"/> class.
     /// </summary>
        public FederationSyncTask(
       ILogger<FederationSyncTask> logger,
            ILibraryManager libraryManager,
   ILoggerFactory loggerFactory)
   {
 _logger = logger;
   _libraryManager = libraryManager;
            _loggerFactory = loggerFactory;
  }

        /// <summary>
     /// Gets the task name.
        /// </summary>
        public string Name => "Sync Federation Content";

    /// <summary>
 /// Gets the task key.
        /// </summary>
        public string Key => "FederationSync";

    /// <summary>
        /// Gets the task description.
        /// </summary>
    public string Description => "Synchronizes content from remote federated servers and updates .strm files";

   /// <summary>
 /// Gets the task category.
        /// </summary>
        public string Category => "Federation";

        /// <summary>
        /// Executes the task.
        /// </summary>
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
   try
       {
_logger.LogInformation("[Federation] Starting scheduled sync task");
       progress?.Report(0);

   var syncService = new Services.FederationSyncService(
          _loggerFactory.CreateLogger<Services.FederationSyncService>(),
          _libraryManager,
   _loggerFactory);

          progress?.Report(10);
   _logger.LogInformation("[Federation] Syncing all enabled mappings");

                var result = await syncService.SyncAllAsync(cancellationToken);

                progress?.Report(90);

      if (result.Success)
{
          _logger.LogInformation("[Federation] Scheduled sync completed: {ItemCount} items synced", result.ItemCount);
           
   // Trigger library scan after sync
    _logger.LogInformation("[Federation] Triggering library scan");
         progress?.Report(95);
      
  // Note: Library will auto-scan when it detects file changes
             // We don't need to manually trigger it
}
      else
     {
       _logger.LogError("[Federation] Scheduled sync failed: {Message}", result.Message);
       }

           progress?.Report(100);
       }
      catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error during scheduled sync task");
     throw;
    }
        }

      /// <summary>
   /// Gets the default triggers for this task.
 /// </summary>
  public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
         // Run daily at 3 AM by default
            return new[]
    {
     new TaskTriggerInfo
    {
       Type = TaskTriggerInfoType.DailyTrigger,
   TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
    }
            };
      }
    }
}
