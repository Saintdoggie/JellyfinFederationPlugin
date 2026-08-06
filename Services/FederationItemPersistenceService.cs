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
        /// <param name="mapping">The mapping to reconcile.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="forceRecreateNested">
        /// One-time migration flag (see <see cref="Configuration.PluginConfiguration.MigratedTieredCreationV1"/>):
        /// when true, every existing Season/Episode item is deleted and recreated
        /// fresh in proper parent-before-child order, since anything created before
        /// 0.0.13 may have been saved in a single flat batch with incomplete ancestry.
        /// </param>
        public Task ReconcileMappingAsync(LibraryMapping mapping, CancellationToken cancellationToken = default, bool forceRecreateNested = false)
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

                if (ReferenceEquals(itemParent, libraryFolder))
                {
                    // No physical folder found - anything created here would repeat
                    // the pre-0.0.9 invisible-items bug (see the comment above).
                    // Usually means the library was never fully provisioned with a
                    // media path yet.
                    _logger.LogWarning(
                        "[Federation] Library {Name} has no physical folder yet; items would not be visible if created. Check that it was provisioned correctly.",
                        mapping.LocalLibraryName);
                }

                var allChildren = libraryFolder.GetRecursiveChildren().ToList();

                _logger.LogInformation(
                    "[Federation] Debug {Name}: libraryFolder.Id={FolderId}, itemParent.Id={ParentId} (physical={IsPhysical}), allChildren={ChildCount}, withFederationKey={KeyCount}",
                    mapping.LocalLibraryName,
                    libraryFolder.Id,
                    itemParent.Id,
                    !ReferenceEquals(itemParent, libraryFolder),
                    allChildren.Count,
                    allChildren.Count(i => FederationLibraryManager.GetFederationKey(i) != null));

                _logger.LogInformation(
                    "[Federation] Debug {Name}: desired={DesiredCount} (Series={Series}, Season={Season}, Episode={Episode}, Movie={Movie}, Other={Other}), withParentKey={WithParentKey}",
                    mapping.LocalLibraryName,
                    desired.Count,
                    desired.Count(e => e.ItemType == "Series"),
                    desired.Count(e => e.ItemType == "Season"),
                    desired.Count(e => e.ItemType == "Episode"),
                    desired.Count(e => e.ItemType == "Movie"),
                    desired.Count(e => e.ItemType != "Series" && e.ItemType != "Season" && e.ItemType != "Episode" && e.ItemType != "Movie"),
                    desired.Count(e => e.ParentKey != null));

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

                // One-time migration (see MigratedTieredCreationV1): drop existing
                // Season/Episode items from existingKeys so the toCreate loop below
                // treats them as new and rebuilds them in proper tier order. Movies
                // and Series are untouched - their parent (the library's physical
                // folder) was always already persisted, so they were never affected.
                var forcedRecreateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (forceRecreateNested)
                {
                    foreach (var x in existing)
                    {
                        if (x.Item is MediaBrowser.Controller.Entities.TV.Episode or MediaBrowser.Controller.Entities.TV.Season)
                        {
                            forcedRecreateKeys.Add(x.Key!);
                        }
                    }

                    existingKeys.ExceptWith(forcedRecreateKeys);

                    if (forcedRecreateKeys.Count > 0)
                    {
                        _logger.LogInformation(
                            "[Federation] One-time migration: recreating {Count} existing Season/Episode item(s) in {Name} to fix ancestry",
                            forcedRecreateKeys.Count,
                            mapping.LocalLibraryName);
                    }
                }

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

                _logger.LogInformation(
                    "[Federation] Debug {Name}: localProviderIds collected={LocalProviderIdCount}",
                    mapping.LocalLibraryName,
                    localProviderIds.Count);

                // Seasons/Episodes nest under a Series entry via ParentKey instead of
                // itemParent directly (see IsEntryValid). An entry is only safe to
                // create if it, and everything above it in the ParentKey chain, is
                // either already persisted or still in the cache and not itself a
                // local-dedup match - otherwise it would get a ParentId pointing at
                // an item that will never exist (parent skipped/removed).
                var toCreate = new List<(BaseItem Item, int Depth)>();
                var skipExisting = 0;
                var skipLocalMatch = 0;
                var skipOrphan = 0;
                var skipOrphanNoParentEntry = 0;
                var localMatchSamples = new List<string>();
                var orphanSamples = new List<string>();
                foreach (var e in desired)
                {
                    if (existingKeys.Contains(e.Key))
                    {
                        skipExisting++;
                        continue;
                    }

                    if (HasLocalMatch(e, dedupKeys, localProviderIds))
                    {
                        skipLocalMatch++;
                        if (localMatchSamples.Count < 5)
                        {
                            localMatchSamples.Add($"{e.ItemType}:{e.Metadata.Name}");
                        }

                        continue;
                    }

                    FederatedCacheEntry? parentEntry = null;
                    var depth = 0;
                    if (e.ParentKey != null)
                    {
                        parentEntry = _federationManager.Cache.GetEntryByKey(e.ParentKey);
                        if (!IsEntryValid(parentEntry, dedupKeys, localProviderIds))
                        {
                            skipOrphan++;
                            if (parentEntry == null)
                            {
                                skipOrphanNoParentEntry++;
                            }

                            if (orphanSamples.Count < 5)
                            {
                                orphanSamples.Add($"{e.ItemType}:{e.Metadata.Name} (parentKey={e.ParentKey}, parentFound={parentEntry != null})");
                            }

                            continue;
                        }

                        var walk = parentEntry;
                        while (walk != null)
                        {
                            depth++;
                            walk = walk.ParentKey != null ? _federationManager.Cache.GetEntryByKey(walk.ParentKey) : null;
                        }
                    }

                    var item = _federationManager.MaterializeItem(e);
                    item.ParentId = parentEntry != null ? _federationManager.ComputeItemId(parentEntry) : itemParent.Id;
                    toCreate.Add((item, depth));
                }

                _logger.LogInformation(
                    "[Federation] Debug {Name}: skipExisting={SkipExisting}, skipLocalMatch={SkipLocalMatch} [{LocalMatchSamples}], skipOrphan={SkipOrphan} (noParentEntry={NoParentEntry}) [{OrphanSamples}], willCreate={WillCreate} (byDepth={ByDepth})",
                    mapping.LocalLibraryName,
                    skipExisting,
                    skipLocalMatch,
                    string.Join(" | ", localMatchSamples),
                    skipOrphan,
                    skipOrphanNoParentEntry,
                    string.Join(" | ", orphanSamples),
                    toCreate.Count,
                    string.Join(", ", toCreate.GroupBy(x => x.Depth).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));

                // Retroactively remove federated items that duplicate content the
                // user already owns locally (added by earlier plugin versions before
                // this dedup check existed, or left behind by a config change), and
                // cascade that removal down to their Seasons/Episodes so nothing is
                // left pointing at a deleted parent.
                var toDelete = existing
                    .Where(x => !IsEntryValid(_federationManager.Cache.GetEntryByKey(x.Key!), dedupKeys, localProviderIds)
                        || forcedRecreateKeys.Contains(x.Key!))
                    .Select(x => x.Item)
                    .ToList();

                _logger.LogInformation(
                    "[Federation] Debug {Name}: existing(federated)={ExistingCount}, toDelete={ToDeleteCount}",
                    mapping.LocalLibraryName,
                    existing.Count,
                    toDelete.Count);

                foreach (var stale in toDelete)
                {
                    _libraryManager.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = false });
                }

                if (toCreate.Count > 0)
                {
                    // Created tier-by-tier (Series/Movies, then Seasons, then Episodes)
                    // rather than in one flat batch. ParentId itself doesn't need this -
                    // ids are deterministic hashes, computable before the parent is
                    // persisted - but Jellyfin derives each item's AncestorIds (a
                    // separate, indexed column the show-navigation endpoints query
                    // against, distinct from the raw ParentId walk GetRecursiveChildren
                    // below uses) from its parent's own AncestorIds *at save time*. If a
                    // child is saved in the same batch before its parent has actually
                    // been persisted, Jellyfin has nothing to derive from and the child's
                    // ancestry ends up incomplete - invisible to ancestor-based queries
                    // even though it's still a normal row reachable by ParentId. This bit
                    // the plugin once before (see the 0.0.6 changelog); nothing here
                    // proves it's the same failure again, but it's the same shape.
                    foreach (var tier in toCreate.GroupBy(x => x.Depth).OrderBy(g => g.Key))
                    {
                        _libraryManager.CreateItems(tier.Select(x => x.Item).ToList(), itemParent, cancellationToken);
                    }

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
