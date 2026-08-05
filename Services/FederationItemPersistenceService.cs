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

                // A Library (CollectionFolder) does not query its own children by
                // ParentId. Its Children/GetRecursiveChildren is overridden to union
                // the children of its *physical folders* - one real Folder row per
                // registered media path/location - found via PhysicalFolderIds. The
                // recursive browsing API filters the same way: by a TopParentId column
                // that must match one of those physical folder ids, not the library's
                // own id. Parenting an item directly to the library itself (as earlier
                // versions of this fix did) makes it invisible to both: it never shows
                // up in GetRecursiveChildren, and its own TopParentId (computed by
                // walking up to the nearest "top parent", which is the library itself)
                // never matches PhysicalFolderIds. Items must be parented to one of the
                // library's existing physical folders instead - any of them works,
                // since PhysicalFolderIds is a set and membership in any one counts.
                var itemParent = (libraryFolder as CollectionFolder)?.GetPhysicalFolders().FirstOrDefault() as Folder
                    ?? libraryFolder;

                var allChildren = libraryFolder.GetRecursiveChildren().ToList();

                _logger.LogInformation(
                    "[Federation] Debug {Name}: libraryFolder.Id={FolderId}, itemParent.Id={ParentId} (physical={IsPhysical}), allChildren={ChildCount}, withFederationKey={KeyCount}",
                    mapping.LocalLibraryName,
                    libraryFolder.Id,
                    itemParent.Id,
                    !ReferenceEquals(itemParent, libraryFolder),
                    allChildren.Count,
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

                // Content the user already owns locally (not federated in) - checked
                // by the same provider ids used to dedup across remote servers, so a
                // show that exists both on disk here and on a federated partner
                // doesn't get a second, episode-less shell created next to the real
                // one.
                var config = Plugin.Instance?.Configuration;
                var dedupKeys = (config?.EnableDedup ?? true)
                    ? (config?.DedupProviderIds ?? new List<string>())
                    : new List<string>();
                var localProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (dedupKeys.Count > 0)
                {
                    foreach (var child in allChildren)
                    {
                        if (FederationLibraryManager.GetFederationKey(child) != null || child.ProviderIds == null)
                        {
                            continue;
                        }

                        foreach (var key in dedupKeys)
                        {
                            if (child.ProviderIds.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                            {
                                localProviderIds.Add($"{key}:{val}");
                            }
                        }
                    }
                }

                // Seasons/Episodes nest under a Series entry via ParentKey instead of
                // itemParent directly (see IsEntryValid). An entry is only safe to
                // create if it, and everything above it in the ParentKey chain, is
                // either already persisted or still in the cache and not itself a
                // local-dedup match - otherwise it would get a ParentId pointing at
                // an item that will never exist (parent skipped/removed).
                var toCreate = new List<BaseItem>();
                foreach (var e in desired)
                {
                    if (existingKeys.Contains(e.Key) || HasLocalMatch(e, dedupKeys, localProviderIds))
                    {
                        continue;
                    }

                    FederatedCacheEntry? parentEntry = null;
                    if (e.ParentKey != null)
                    {
                        parentEntry = _federationManager.Cache.GetEntryByKey(e.ParentKey);
                        if (!IsEntryValid(parentEntry, dedupKeys, localProviderIds))
                        {
                            continue;
                        }
                    }

                    var item = _federationManager.MaterializeItem(e);
                    item.ParentId = parentEntry != null ? _federationManager.ComputeItemId(parentEntry) : itemParent.Id;
                    toCreate.Add(item);
                }

                // Retroactively remove federated items that duplicate content the
                // user already owns locally (added by earlier plugin versions before
                // this dedup check existed, or left behind by a config change), and
                // cascade that removal down to their Seasons/Episodes so nothing is
                // left pointing at a deleted parent.
                var toDelete = existing
                    .Where(x => !IsEntryValid(_federationManager.Cache.GetEntryByKey(x.Key!), dedupKeys, localProviderIds))
                    .Select(x => x.Item)
                    .ToList();

                foreach (var stale in toDelete)
                {
                    _libraryManager.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false });
                }

                if (toCreate.Count > 0)
                {
                    _libraryManager.CreateItems(toCreate, itemParent, cancellationToken);

                    itemParent.Children = null;
                    var freshCount = libraryFolder.GetRecursiveChildren().Count(i => FederationLibraryManager.GetFederationKey(i) != null);
                    _logger.LogInformation(
                        "[Federation] Debug {Name}: after create, federated items now visible via GetRecursiveChildren={FreshCount}",
                        mapping.LocalLibraryName,
                        freshCount);
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

        /// <summary>
        /// True if the entry, and everything above it in the ParentKey chain, is
        /// still present in the cache and none of them duplicates content the user
        /// already owns locally. An entry with a missing/invalid ancestor is not
        /// safe to create (its ParentId would point at an item that will never
        /// exist) and not safe to leave persisted (its parent is about to be, or
        /// already was, removed).
        /// </summary>
        private bool IsEntryValid(FederatedCacheEntry? entry, List<string> dedupKeys, HashSet<string> localProviderIds)
        {
            var depth = 0;
            while (entry != null)
            {
                if (depth++ > 16)
                {
                    return false;
                }

                if (HasLocalMatch(entry, dedupKeys, localProviderIds))
                {
                    return false;
                }

                if (entry.ParentKey == null)
                {
                    return true;
                }

                entry = _federationManager.Cache.GetEntryByKey(entry.ParentKey);
            }

            return false;
        }

        private static bool HasLocalMatch(FederatedCacheEntry? entry, List<string> dedupKeys, HashSet<string> localProviderIds)
        {
            if (entry == null || dedupKeys.Count == 0 || localProviderIds.Count == 0 || entry.Metadata.ProviderIds == null)
            {
                return false;
            }

            foreach (var key in dedupKeys)
            {
                if (entry.Metadata.ProviderIds.TryGetValue(key, out var val)
                    && !string.IsNullOrEmpty(val)
                    && localProviderIds.Contains($"{key}:{val}"))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
