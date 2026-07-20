using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Reconciles the Jellyfin library database with the federation cache after a
    /// sync: creates virtual items for new cache entries and removes items whose
    /// cache entries are gone. Runs per mapping under the mapping's provisioned
    /// library folder.
    /// </summary>
    public class FederationItemPersistenceService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<FederationItemPersistenceService> _logger;
        private readonly FederationLibraryManager _federationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationItemPersistenceService"/> class.
        /// </summary>
        public FederationItemPersistenceService(
            ILibraryManager libraryManager,
            ILogger<FederationItemPersistenceService> logger,
            FederationLibraryManager federationManager)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _federationManager = federationManager;
        }

        /// <summary>
        /// Creates/removes persisted virtual items so the mapping's library folder
        /// mirrors the cache. Never throws; failures are logged.
        /// </summary>
        public Task ReconcileMappingAsync(LibraryMapping mapping, CancellationToken cancellationToken = default)
        {
            try
            {
                var root = _libraryManager.GetUserRootFolder();
                var libraryFolder = root.Children.OfType<Folder>()
                    .FirstOrDefault(f => string.Equals(f.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));

                if (libraryFolder == null)
                {
                    _logger.LogDebug("[Federation] Library {Name} is not provisioned; skipping item persistence", mapping.LocalLibraryName);
                    return Task.CompletedTask;
                }

                var desired = _federationManager.GetEntriesForMapping(mapping.LocalLibraryName).ToList();
                var desiredPaths = new HashSet<string>(desired.Select(e => e.FederationPath), StringComparer.OrdinalIgnoreCase);

                var existing = libraryFolder.GetRecursiveChildren()
                    .Where(i => i.Path != null && i.Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var existingPaths = new HashSet<string>(existing.Select(i => i.Path!), StringComparer.OrdinalIgnoreCase);

                var toCreate = desired
                    .Where(e => !existingPaths.Contains(e.FederationPath))
                    .Select(e => _federationManager.MaterializeItem(e))
                    .ToList();
                var toDelete = existing
                    .Where(i => !desiredPaths.Contains(i.Path!))
                    .ToList();

                foreach (var stale in toDelete)
                {
                    _libraryManager.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false });
                }

                if (toCreate.Count > 0)
                {
                    _libraryManager.CreateItems(toCreate, libraryFolder, cancellationToken);
                }

                _logger.LogInformation(
                    "[Federation] Reconciled library {Name}: {Created} item(s) created, {Deleted} removed, {Total} total",
                    mapping.LocalLibraryName,
                    toCreate.Count,
                    toDelete.Count,
                    desired.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to reconcile library items for {Name}", mapping.LocalLibraryName);
            }

            return Task.CompletedTask;
        }
    }
}
