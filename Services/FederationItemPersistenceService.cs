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
                var desiredKeys = new HashSet<string>(desired.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);

                var allChildren = libraryFolder.GetRecursiveChildren().ToList();

                _logger.LogInformation(
                    "[Federation] Debug {Name}: libraryFolder.Id={FolderId}, allChildren={ChildCount}, directChildren(ParentId match)={DirectCount}, withFederationKey={KeyCount}",
                    mapping.LocalLibraryName,
                    libraryFolder.Id,
                    allChildren.Count,
                    allChildren.Count(i => i.ParentId == libraryFolder.Id),
                    allChildren.Count(i => FederationLibraryManager.GetFederationKey(i) != null));

                // Self-healing migration: earlier plugin versions stamped a
                // "federation://" URI on item.Path. Jellyfin treated that as an
                // unrecognized/missing path and hid the items everywhere -
                // including from this exact dedup check - so every hourly sync
                // recreated a full duplicate set forever. Nothing this version
                // creates will ever match this pattern again, so sweep any
                // leftovers unconditionally; this becomes a no-op after the
                // first run on each upgraded server.
                var legacy = allChildren
                    .Where(i => i.Path != null && i.Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var stale in legacy)
                {
                    _libraryManager.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false });
                }

                if (legacy.Count > 0)
                {
                    _logger.LogInformation(
                        "[Federation] Removed {Count} legacy federation:// item(s) from {Name} (identity scheme migration)",
                        legacy.Count,
                        mapping.LocalLibraryName);
                }

                var existing = allChildren
                    .Except(legacy)
                    .Select(i => new { Item = i, Key = FederationLibraryManager.GetFederationKey(i) })
                    .Where(x => x.Key != null)
                    .ToList();
                var existingKeys = new HashSet<string>(existing.Select(x => x.Key!), StringComparer.OrdinalIgnoreCase);

                var toCreate = desired
                    .Where(e => !existingKeys.Contains(e.Key))
                    .Select(e =>
                    {
                        var item = _federationManager.MaterializeItem(e);

                        // ILibraryManager.CreateItems does not parent what it saves:
                        // its "parent" argument only feeds the ItemAdded event and
                        // invalidates the folder's cached children. Without an explicit
                        // ParentId the rows land in the database as orphans, matching
                        // neither the direct-children query (ParentId) nor the recursive
                        // one (AncestorIds, which the repository derives from the
                        // ParentId chain at save time). That made federated items
                        // invisible in the library *and* invisible to the existence
                        // check above - so every sync reported the full set as "created"
                        // and nothing ever showed up.
                        item.ParentId = libraryFolder.Id;
                        return item;
                    })
                    .ToList();
                var toDelete = existing
                    .Where(x => !desiredKeys.Contains(x.Key!))
                    .Select(x => x.Item)
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
