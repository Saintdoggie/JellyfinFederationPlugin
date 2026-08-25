using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
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
        /// <summary>
        /// The item kinds local-dedup compares against - everything this plugin can
        /// federate and that carries provider ids. See the comment in
        /// <see cref="CollectServerWideLocalProviderIds"/> for why the query must name
        /// them rather than enumerate the library unrestricted.
        /// </summary>
        private static readonly Jellyfin.Data.Enums.BaseItemKind[] DedupCandidateKinds =
        {
            Jellyfin.Data.Enums.BaseItemKind.Movie,
            Jellyfin.Data.Enums.BaseItemKind.Series,
            Jellyfin.Data.Enums.BaseItemKind.Season,
            Jellyfin.Data.Enums.BaseItemKind.Episode,
            Jellyfin.Data.Enums.BaseItemKind.Video,
            Jellyfin.Data.Enums.BaseItemKind.MusicVideo,
            Jellyfin.Data.Enums.BaseItemKind.Audio,
            Jellyfin.Data.Enums.BaseItemKind.MusicAlbum,
            Jellyfin.Data.Enums.BaseItemKind.BoxSet,
            Jellyfin.Data.Enums.BaseItemKind.Book
        };

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
        /// One-time migration flag (see <see cref="Configuration.PluginConfiguration.MigratedTieredCreationV3"/>):
        /// when true, every existing Series/Season/Episode item is deleted and
        /// recreated fresh, since anything created before the fix that stamps
        /// PresentationUniqueKey/SeriesPresentationUniqueKey won't have it set,
        /// making it undiscoverable from the show/season browsing pages.
        /// </param>
        /// <param name="sweepSyntheticSeasons">
        /// One-time migration flag (see <see cref="Configuration.PluginConfiguration.MigratedSeasonIndexV5"/>):
        /// when true, Season items that sit under a federated Series but carry no
        /// FederationKey are deleted. Those are duplicates Jellyfin's
        /// SeriesMetadataService created while federated seasons had no IndexNumber
        /// for it to match against.
        /// </param>
        /// <param name="forceRecreateAll">
        /// One-time migration flag (see <see cref="Configuration.PluginConfiguration.MigratedRemoteLocationV6"/>):
        /// when true, every existing federated item is deleted and recreated, because its
        /// CLR type changed (Episode -&gt; FederatedEpisode, Movie -&gt; FederatedMovie, ...)
        /// and item ids are derived from the CLR type. Movies/Audio/... are included here that
        /// <paramref name="forceRecreateNested"/> deliberately leaves out (they have no
        /// Series-matching mechanism).
        /// </param>
        public async Task ReconcileMappingAsync(
            LibraryMapping mapping,
            CancellationToken cancellationToken = default,
            bool forceRecreateNested = false,
            bool sweepSyntheticSeasons = false,
            bool forceRecreateAll = false)
        {
            try
            {
                var root = _libraryManager.GetUserRootFolder();
                var libraryFolder = root.Children.OfType<Folder>()
                    .FirstOrDefault(f => string.Equals(f.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));

                if (libraryFolder == null)
                {
                    _logger.LogDebug("[Federation] Library {Name} is not provisioned; skipping item persistence", mapping.LocalLibraryName);
                    return;
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

                List<BaseItem> allChildren;
                try
                {
                    allChildren = libraryFolder.GetRecursiveChildren().ToList();
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("deserialize", StringComparison.OrdinalIgnoreCase))
                {
                    // Self-healing: 0.0.22-0.0.24 persisted items under plugin subclasses
                    // (FederatedMovie, FederatedSeries, ...) that no longer exist as of
                    // 0.0.27 (see MigratedStockTypesV8). Jellyfin's item repository can't
                    // resolve those rows' stored CLR type name back to a Type and throws -
                    // and that throw aborts the *entire* enumeration, not just the bad row,
                    // so every reconciliation of an affected library fails before it can
                    // even see what needs deleting. Purge them directly (bypassing
                    // deserialization) and retry once.
                    _logger.LogWarning(
                        ex,
                        "[Federation] {Name}: hit unrecoverable legacy item(s) while listing children; purging and retrying",
                        mapping.LocalLibraryName);

                    var purgedCount = PurgeUndeserializableDescendants(GetPhysicalFolders(libraryFolder));
                    _logger.LogWarning(
                        "[Federation] {Name}: purged {Count} unrecoverable legacy item(s) left over from an earlier plugin version",
                        mapping.LocalLibraryName,
                        purgedCount);

                    itemParent.Children = null;
                    allChildren = libraryFolder.GetRecursiveChildren().ToList();
                }

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

                // One-time migration: drop existing Series/Season/Episode items from
                // existingKeys so the toCreate loop below treats them as new and
                // rebuilds them fresh (in proper tier order). Series are included
                // here (not just Season/Episode) because a Series created before
                // 0.0.16 never had PresentationUniqueKey explicitly set - Jellyfin's
                // fallback computation for that (CreatePresentationUniqueKey) only
                // matches the value now stamped on its Season/Episode children when
                // the library's "EnableAutomaticSeriesGrouping" option is off, which
                // this plugin has no way to verify, so the series needs the same
                // explicit stamp. Movies are untouched - they don't participate in
                // this Series-matching mechanism at all.
                var forcedRecreateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (forceRecreateAll)
                {
                    // V6: every federated item's CLR type changed (Episode -> FederatedEpisode,
                    // Movie -> FederatedMovie, ...), and ids derive from the CLR type, so they
                    // all need rebuilding under the new types.
                    foreach (var x in existing)
                    {
                        forcedRecreateKeys.Add(x.Key!);
                    }

                    existingKeys.ExceptWith(forcedRecreateKeys);

                    if (forcedRecreateKeys.Count > 0)
                    {
                        _logger.LogInformation(
                            "[Federation] One-time migration: recreating all {Count} existing federated item(s) in {Name} under remapped (Remote LocationType) types",
                            forcedRecreateKeys.Count,
                            mapping.LocalLibraryName);
                    }
                }
                else if (forceRecreateNested)
                {
                    foreach (var x in existing)
                    {
                        if (x.Item is MediaBrowser.Controller.Entities.TV.Episode
                            or MediaBrowser.Controller.Entities.TV.Season
                            or MediaBrowser.Controller.Entities.TV.Series)
                        {
                            forcedRecreateKeys.Add(x.Key!);
                        }
                    }

                    existingKeys.ExceptWith(forcedRecreateKeys);

                    if (forcedRecreateKeys.Count > 0)
                    {
                        _logger.LogInformation(
                            "[Federation] One-time migration: recreating {Count} existing Series/Season/Episode item(s) in {Name} to set PresentationUniqueKey",
                            forcedRecreateKeys.Count,
                            mapping.LocalLibraryName);
                    }
                }

                // Content the user already owns locally (not federated in) - checked
                // by the same provider ids used to dedup across remote servers, so a
                // show that exists both on disk here and on a federated partner
                // doesn't get a second, episode-less shell created next to the real
                // one. Checked server-wide (not just this mapping's own library
                // folder) - a duplicate is just as real when the local copy lives in
                // a separate, non-federated library as when it shares this one.
                var config = Plugin.Instance?.Configuration;
                var dedupKeys = (config?.EnableDedup ?? true)
                    ? (config?.DedupProviderIds ?? new List<string>())
                    : new List<string>();
                var localProviderIds = CollectServerWideLocalProviderIds(dedupKeys);

                // Admin-chosen local suppression list (see PluginConfiguration.
                // HiddenFederatedItemIds) - keyed on the same stable cache key as
                // dedup, folded into IsEntryValid/HasLocalMatch below so a hidden
                // entry is treated exactly like a local-dedup match: never created,
                // and removed if it already exists. Purely local; never touches the
                // cache or is communicated to the friend server.
                var hiddenKeys = new HashSet<string>(config?.HiddenFederatedItemIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                _logger.LogInformation(
                    "[Federation] Debug {Name}: localProviderIds collected={LocalProviderIdCount}, hiddenKeys={HiddenKeyCount}",
                    mapping.LocalLibraryName,
                    localProviderIds.Count,
                    hiddenKeys.Count);

                // Seasons/Episodes nest under a Series entry via ParentKey instead of
                // itemParent directly (see IsEntryValid). An entry is only safe to
                // create if it, and everything above it in the ParentKey chain, is
                // either already persisted or still in the cache and not itself a
                // local-dedup match - otherwise it would get a ParentId pointing at
                // an item that will never exist (parent skipped/removed).
                var toCreate = new List<(BaseItem Item, FederatedCacheEntry Entry, int Depth)>();
                var skipExisting = 0;
                var skipLocalMatch = 0;
                var skipHidden = 0;
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

                    if (IsHidden(e, hiddenKeys))
                    {
                        skipHidden++;
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
                        if (!IsEntryValid(parentEntry, dedupKeys, localProviderIds, hiddenKeys))
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
                    toCreate.Add((item, e, depth));
                }

                _logger.LogInformation(
                    "[Federation] Debug {Name}: skipExisting={SkipExisting}, skipHidden={SkipHidden}, skipLocalMatch={SkipLocalMatch} [{LocalMatchSamples}], skipOrphan={SkipOrphan} (noParentEntry={NoParentEntry}) [{OrphanSamples}], willCreate={WillCreate} (byDepth={ByDepth})",
                    mapping.LocalLibraryName,
                    skipExisting,
                    skipHidden,
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
                    .Where(x => !IsEntryValid(_federationManager.Cache.GetEntryByKey(x.Key!), dedupKeys, localProviderIds, hiddenKeys)
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

                // Reconciliation creates and deletes but has never updated an item in
                // place, so the stream URL stamped on an item was effectively frozen at
                // creation time. That is what made switching a server to Proxy mode look
                // like it did nothing: item.Path still held the Direct URL built when the
                // item was created, and Jellyfin serves that stored path as a *static*
                // media source alongside the one this plugin builds fresh - with the
                // stale entry first, so clients play it and none of Proxy's resumable
                // relay is ever involved. The same freeze applies after a server changes
                // address or mints a new API key.
                //
                // Restamped in place rather than by delete-and-recreate (the tool every
                // earlier migration reached for) specifically because deleting an item
                // discards its watch progress, and there is no reason to lose a resume
                // point over a URL change.
                var deletedIds = new HashSet<Guid>(toDelete.Select(d => d.Id));
                var restamped = new List<BaseItem>();
                foreach (var x in existing)
                {
                    if (deletedIds.Contains(x.Item.Id))
                    {
                        continue;
                    }

                    var entry = _federationManager.Cache.GetEntryByKey(x.Key!);
                    if (entry == null || !FederationLibraryManager.IsStreamableType(entry.ItemType))
                    {
                        continue;
                    }

                    // Switching a server off has to actually stop its titles playing.
                    // The dynamic provider already refuses to serve a source for a
                    // disabled server, but Jellyfin independently exposes whatever is
                    // stamped on item.Path as a *static* source, so a stale URL kept
                    // the title playing straight from a server the admin had just
                    // turned off - silently contradicting what the switch claims to do.
                    //
                    // Resolved against every source in priority order rather than just
                    // GetPrimarySource(), because sources are ordered by Priority alone
                    // and never re-ordered by enabled state. On a deduped entry - the
                    // same title matched across servers, which is the whole point of
                    // dedup - the primary can be switched off while another server
                    // still serves it. Keying off the primary alone would blank the
                    // path and remove the Play button from an item that is perfectly
                    // playable elsewhere, breaking exactly the redundancy dedup exists
                    // to provide. The dynamic provider already picks the first enabled
                    // source this way; this keeps the stamped path consistent with it.
                    var playable = FirstEnabledSource(entry, config);
                    var changed = false;

                    // The duration/container shown on an item (grid badge, detail page,
                    // and - via item.RunTimeTicks as GetMediaSources' last-resort
                    // fallback when a live remote fetch fails - occasionally the actual
                    // playback bar) is stamped once at creation from whatever the cache
                    // held that sync and, unlike Path below, was never refreshed after
                    // that. The cache's own copy does get corrected on every sync (see
                    // UpdateFromRemote), so a bad value from one flaky sync - or from
                    // before some earlier fix - stayed wrong on the materialized item
                    // forever with no self-healing path. This is exactly what "the
                    // playbar shows the wrong length" turned out to be: not a live
                    // per-play miscalculation, but a stale value frozen in at creation
                    // time. Comparing and restamping it here, the same way Path already
                    // self-heals below, fixes it going forward without a full
                    // delete/recreate - so watch progress on the item is preserved.
                    if (entry.Metadata.RunTimeTicks.HasValue && x.Item.RunTimeTicks != entry.Metadata.RunTimeTicks)
                    {
                        x.Item.RunTimeTicks = entry.Metadata.RunTimeTicks;
                        changed = true;
                    }

                    // Self-heals items whose Name was overwritten by a local metadata
                    // provider's bad "identify" match before LockedFields below existed
                    // to stop it (see FederationLibraryManager.MaterializeItem) - the
                    // cache's own copy is always the source of truth, unaffected by
                    // anything Jellyfin's local scrapers do.
                    if (!string.IsNullOrEmpty(entry.Metadata.Name) && !string.Equals(x.Item.Name, entry.Metadata.Name, StringComparison.Ordinal))
                    {
                        x.Item.Name = entry.Metadata.Name;
                        changed = true;
                    }

                    // One-time backfill for items created before this locking existed -
                    // only when not already fully set, so this doesn't re-save on every
                    // sync once it's already been backfilled.
                    if (!FederationLibraryManager.LockedMetadataFields.All(x.Item.LockedFields.Contains))
                    {
                        x.Item.LockedFields = FederationLibraryManager.LockedMetadataFields;
                        changed = true;
                    }

                    // Backfill for items created before real stream data was tracked
                    // at all (see FederationLibraryManager.TryPersistMediaStreams), or
                    // before a Plex source's video BitRate was captured (Plex reports
                    // one combined bitrate per Media entry that ApplyMediaDetails used
                    // to drop entirely, leaving Jellyfin's own client-side quality
                    // selector with no idea what the item's real data rate was and
                    // falling back to a low, generic default regardless of the
                    // source's actual quality). Only when actually missing something
                    // the cache now has, so this doesn't re-save on every sync once
                    // it's already been backfilled.
                    var storedStreams = x.Item.GetMediaStreams();
                    var cachedStreams = entry.Metadata.MediaStreams;
                    var missingBitrate = cachedStreams != null
                        && storedStreams.Any(s => s.Type == MediaStreamType.Video && s.BitRate == null)
                        && cachedStreams.Any(s => s.Type == MediaStreamType.Video && s.BitRate.HasValue);

                    if (storedStreams.Count == 0 || missingBitrate)
                    {
                        _federationManager.TryPersistMediaStreams(x.Item, entry);
                    }

                    // Only when no source has an enabled home does the title genuinely
                    // have nowhere to play from. Clearing the path leaves Jellyfin with
                    // a placeholder source and no Play button, which is the intended
                    // meaning of "disabled"; a URL is stamped back automatically on the
                    // first sync after any of its servers is re-enabled.
                    if (playable == null)
                    {
                        if (!string.IsNullOrEmpty(x.Item.Path))
                        {
                            x.Item.Path = null;
                            changed = true;
                        }

                        if (changed)
                        {
                            restamped.Add(x.Item);
                        }

                        continue;
                    }

                    // Mirrors FederationLibraryManager.BuildStaticPath's guard, which
                    // only ever ran once at creation before this existed. The one
                    // case that must never keep a stamped Path is a server with
                    // FriendUserAccessRules: those rules are keyed by which of *our*
                    // local users is asking, but a stamped item.Path is one static
                    // value shared by every client, so keeping one would silently
                    // bypass the per-user restriction for the primary source - a
                    // real, reported bug this blanking self-heals on the next sync.
                    // Direct mode is deliberately NOT in that list any more: its
                    // stamped Path is the secret-free local proxy gateway (see
                    // BuildStaticPath), which previously meant "no Path at all" and
                    // therefore no Play button in jellyfin-web (LocationType Virtual).
                    var playableServer = _federationManager.GetServer(playable.ServerId);
                    var hasUserAccessRules = playableServer?.FriendUserAccessRules != null
                        && playableServer.FriendUserAccessRules.Count > 0;

                    if (hasUserAccessRules)
                    {
                        // Blank rather than merely "don't restamp" so an item whose
                        // server just gained its first FriendUserAccessRules entry
                        // self-heals on its next sync instead of keeping an ungated
                        // URL around indefinitely.
                        if (!string.IsNullOrEmpty(x.Item.Path))
                        {
                            x.Item.Path = null;
                            changed = true;
                        }
                    }
                    else
                    {
                        var expected = _federationManager.BuildStaticPath(entry.ItemType, playable);

                        // A null here does NOT mean "disabled" - that case is handled
                        // above. It means the URL cannot be built right now.
                        // Blanking a working path over a temporary inability to rebuild it
                        // would take the whole library offline, so leave it alone.
                        if (!string.IsNullOrEmpty(expected) && !string.Equals(x.Item.Path, expected, StringComparison.Ordinal))
                        {
                            x.Item.Path = expected;
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        restamped.Add(x.Item);
                    }
                }

                if (restamped.Count > 0)
                {
                    await _libraryManager.UpdateItemsAsync(
                        restamped,
                        itemParent,
                        ItemUpdateType.MetadataEdit,
                        cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "[Federation] Refreshed the stored stream URL and/or duration on {Count} item(s) in {Name}; watch progress preserved",
                        restamped.Count,
                        mapping.LocalLibraryName);
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
                        var tierList = tier.ToList();
                        _libraryManager.CreateItems(tierList.Select(x => x.Item).ToList(), itemParent, cancellationToken);

                        // Only safe now that CreateItems has actually persisted this
                        // tier's BaseItems rows - MediaStreamInfos has a foreign key on
                        // them, so saving any earlier (e.g. inside MaterializeItem, before
                        // any tier is created) always fails.
                        foreach (var (item, entry, _) in tierList)
                        {
                            _federationManager.TryPersistMediaStreams(item, entry);
                        }
                    }

                    itemParent.Children = null;
                    var freshCount = libraryFolder.GetRecursiveChildren().Count(i => FederationLibraryManager.GetFederationKey(i) != null);
                    _logger.LogInformation(
                        "[Federation] Debug {Name}: after create, federated items now visible via GetRecursiveChildren={FreshCount}",
                        mapping.LocalLibraryName,
                        freshCount);
                }

                // Runs after creation so the federated seasons above are back in
                // place and have re-adopted their episodes; anything still parented
                // to a duplicate at this point is unexpected, so those are left alone
                // and logged rather than deleted with their children.
                if (sweepSyntheticSeasons)
                {
                    itemParent.Children = null;
                    var current = libraryFolder.GetRecursiveChildren().ToList();
                    var federatedSeriesIds = new HashSet<Guid>(current
                        .Where(i => i is MediaBrowser.Controller.Entities.TV.Series
                            && FederationLibraryManager.GetFederationKey(i) != null)
                        .Select(i => i.Id));

                    var duplicates = current
                        .OfType<MediaBrowser.Controller.Entities.TV.Season>()
                        .Where(s => FederationLibraryManager.GetFederationKey(s) == null
                            && (federatedSeriesIds.Contains(s.ParentId) || federatedSeriesIds.Contains(s.SeriesId)))
                        .ToList();

                    var removed = 0;
                    var skipped = 0;
                    foreach (var dupe in duplicates)
                    {
                        if (dupe.GetRecursiveChildren().Count > 0)
                        {
                            skipped++;
                            continue;
                        }

                        _libraryManager.DeleteItem(dupe, new DeleteOptions { DeleteFileLocation = false });
                        removed++;
                    }

                    if (duplicates.Count > 0)
                    {
                        _logger.LogInformation(
                            "[Federation] One-time migration: removed {Removed} duplicate season(s) auto-created by Jellyfin under federated series in {Name} ({Skipped} skipped for still having children)",
                            removed,
                            mapping.LocalLibraryName,
                            skipped);
                    }
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
        }

        /// <summary>
        /// Returns the highest-priority source of <paramref name="entry"/> whose server
        /// is still enabled, or null when none is - meaning the title genuinely has
        /// nowhere left to play from and its stamped path should be cleared.
        /// <para>
        /// Deliberately not <c>GetPrimarySource()</c>. Sources are ordered by Priority
        /// and never re-ordered by enabled state, so on a deduped entry (the same title
        /// matched across several servers) the primary can be switched off while another
        /// server still serves it. Treating that as "nowhere to play" would strip the
        /// Play button from an item that is perfectly playable elsewhere - breaking the
        /// exact redundancy dedup exists to provide.
        /// </para>
        /// </summary>
        internal static FederatedSource? FirstEnabledSource(FederatedCacheEntry entry, PluginConfiguration? config)
        {
            foreach (var candidate in entry.GetSourcesSnapshot())
            {
                var candidateServer = config?.RemoteServers?.FirstOrDefault(s => s.Id == candidate.ServerId);
                if (candidateServer != null && candidateServer.Enabled)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Sweeps every provisioned mapping's library folder for unrecoverable legacy
        /// items and deletes them, up front - before <see cref="ReconcileMappingAsync"/>
        /// or anything else touches them. <see cref="ReconcileMappingAsync"/> only
        /// discovers these reactively, by catching the deserialization failure that
        /// happens when it lists a library that has one; that leaves a window (this
        /// plugin's background startup sync waits several seconds before its first run)
        /// during which any other Jellyfin code path that enumerates the same folder
        /// first - the web UI browsing it, a native library scan, another plugin -
        /// would hit the same crash outside this plugin's control. Called synchronously
        /// from <see cref="FederationEntryPoint.StartAsync"/>, before that delay, so the
        /// purge has already happened by the time anything else gets a chance to run.
        /// </summary>
        /// <param name="mappings">The configured library mappings to sweep.</param>
        public void PurgeUndeserializableItemsAtStartup(IEnumerable<LibraryMapping> mappings)
        {
            Folder root;
            try
            {
                root = _libraryManager.GetUserRootFolder();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Startup purge sweep: could not resolve the root folder");
                return;
            }

            foreach (var mapping in mappings)
            {
                try
                {
                    var libraryFolder = root.Children.OfType<Folder>()
                        .FirstOrDefault(f => string.Equals(f.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));
                    if (libraryFolder == null)
                    {
                        continue;
                    }

                    var purgedCount = PurgeUndeserializableDescendants(GetPhysicalFolders(libraryFolder));
                    if (purgedCount > 0)
                    {
                        _logger.LogWarning(
                            "[Federation] Startup sweep: purged {Count} unrecoverable legacy item(s) from {Name} left over from an earlier plugin version",
                            purgedCount,
                            mapping.LocalLibraryName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Federation] Startup purge sweep failed for {Name}", mapping.LocalLibraryName);
                }
            }
        }

        /// <summary>
        /// Resolves the real, physical Folder rows backing a library
        /// (<see cref="Configuration.LibraryMapping.LocalLibraryName"/>'s CollectionFolder
        /// is a virtual union of these - see the comment in
        /// <see cref="ReconcileMappingAsync"/>). Falls back to the library folder itself
        /// if it has none yet (not fully provisioned).
        /// </summary>
        private static List<Folder> GetPhysicalFolders(Folder libraryFolder)
        {
            var physicalFolders = (libraryFolder as CollectionFolder)?.GetPhysicalFolders().OfType<Folder>().ToList();
            return physicalFolders is { Count: > 0 } ? physicalFolders : new List<Folder> { libraryFolder };
        }

        /// <summary>
        /// Finds and deletes every descendant of <paramref name="physicalFolders"/> that
        /// Jellyfin's item repository can no longer deserialize (rows whose stored CLR
        /// type name no longer resolves to a type - see the migration comment at the
        /// call site). <see cref="IItemRepository.GetItemIdsList"/> only selects the Id
        /// column, so unlike <see cref="Folder.GetRecursiveChildren()"/> it cannot choke
        /// on a bad row; each id is then probed individually with
        /// <see cref="IItemRepository.RetrieveItem"/> so one bad row can't hide the rest,
        /// and the bad ones are deleted directly by id (also deserialization-free).
        /// </summary>
        private int PurgeUndeserializableDescendants(List<Folder> physicalFolders)
        {
            var allIds = BaseItem.ItemRepository.GetItemIdsList(new InternalItemsQuery
            {
                AncestorIds = physicalFolders.Select(f => f.Id).ToArray(),
                Recursive = true
            });

            var badIds = new List<Guid>();
            foreach (var id in allIds)
            {
                try
                {
                    BaseItem.ItemRepository.RetrieveItem(id);
                }
                catch (InvalidOperationException)
                {
                    badIds.Add(id);
                }
            }

            if (badIds.Count > 0)
            {
                BaseItem.ItemRepository.DeleteItem(badIds);
            }

            return badIds.Count;
        }

        /// <summary>
        /// True if the entry, and everything above it in the ParentKey chain, is
        /// still present in the cache, none of them duplicates content the user
        /// already owns locally, and none of them is locally hidden (see
        /// <see cref="Configuration.PluginConfiguration.HiddenFederatedItemIds"/>).
        /// An entry with a missing/invalid ancestor is not safe to create (its
        /// ParentId would point at an item that will never exist) and not safe to
        /// leave persisted (its parent is about to be, or already was, removed) -
        /// the same reasoning applies to hiding a Series/Season: its children have
        /// nothing sensible to nest under once it's gone from local browsing, so
        /// they are hidden along with it.
        /// </summary>
        private bool IsEntryValid(FederatedCacheEntry? entry, List<string> dedupKeys, HashSet<string> localProviderIds, HashSet<string> hiddenKeys)
        {
            var depth = 0;
            while (entry != null)
            {
                if (depth++ > 16)
                {
                    return false;
                }

                if (IsHidden(entry, hiddenKeys))
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

        /// <summary>
        /// Collects provider ids (e.g. <c>imdb:tt1517268</c>) from every non-federated
        /// item on the whole server, not just this mapping's own library folder - a
        /// user's local copy of a movie is just as real a duplicate if it lives in a
        /// separate, ordinary library as if it happens to share the federated one.
        /// </summary>
        private HashSet<string> CollectServerWideLocalProviderIds(List<string> dedupKeys)
        {
            var localProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (dedupKeys.Count == 0)
            {
                return localProviderIds;
            }

            IReadOnlyList<BaseItem> allItems;
            try
            {
                // Restricted to the kinds that can actually carry a provider id and be
                // duplicated. An unrestricted Recursive query walks every row in the
                // library, and Jellyfin keeps rows there that are not media at all -
                // including a built-in "PLACEHOLDER" row (id 0000...0001) that holds
                // UserData detached from deleted items and whose stored type resolves
                // to no CLR type. Deserializing the result set threw on that single
                // row, and because the throw aborts the whole enumeration, dedup was
                // silently skipped on every sync - which is exactly how a library ends
                // up showing every title twice, once local and once federated. Naming
                // the types both avoids that row and skips the thousands of
                // Person/Studio/Genre rows this never needed to load.
                allItems = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = DedupCandidateKinds
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not enumerate the server for local-dedup matching; dedup against locally-owned content will be skipped this cycle");
                return localProviderIds;
            }

            foreach (var item in allItems)
            {
                if (FederationLibraryManager.GetFederationKey(item) != null || item.ProviderIds == null)
                {
                    continue;
                }

                foreach (var key in dedupKeys)
                {
                    if (FederationLibraryManager.TryGetProviderId(item.ProviderIds, key, out var val))
                    {
                        localProviderIds.Add($"{key}:{val}");
                    }
                }
            }

            return localProviderIds;
        }

        /// <summary>
        /// True if the entry's stable cache key is on the admin's local hide list
        /// (see <see cref="Configuration.PluginConfiguration.HiddenFederatedItemIds"/>).
        /// Extracted as its own static helper (rather than folded silently into
        /// <see cref="HasLocalMatch"/>) so it is independently unit-testable and so a
        /// log line/skip-reason can distinguish "hidden by choice" from "duplicates
        /// something you already own" - the two look identical downstream (neither
        /// gets created) but mean very different things to an admin reading logs.
        /// </summary>
        internal static bool IsHidden(FederatedCacheEntry? entry, HashSet<string> hiddenKeys)
        {
            return entry != null && hiddenKeys.Count > 0 && hiddenKeys.Contains(entry.Key);
        }

        private static bool HasLocalMatch(FederatedCacheEntry? entry, List<string> dedupKeys, HashSet<string> localProviderIds)
        {
            if (entry == null || dedupKeys.Count == 0 || localProviderIds.Count == 0 || entry.Metadata.ProviderIds == null)
            {
                return false;
            }

            foreach (var key in dedupKeys)
            {
                if (FederationLibraryManager.TryGetProviderId(entry.Metadata.ProviderIds, key, out var val)
                    && localProviderIds.Contains($"{key}:{val}"))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
