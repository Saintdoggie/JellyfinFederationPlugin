using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Finds locally-owned items where a federated friend's server holds a
    /// meaningfully higher-resolution or higher-bitrate copy of the same title
    /// (matched the same way dedup already matches "the same movie" across
    /// servers - see <see cref="PluginConfiguration.DedupProviderIds"/>).
    /// <para>
    /// Purely advisory: nothing here ever deletes or downloads anything by
    /// itself. It only runs when <see cref="PluginConfiguration.PreferHigherQualityRemotes"/>
    /// is on, and its output is a plain list the config page shows the admin to
    /// review and act on by hand, item by item - see
    /// <see cref="FederationDownloadService.StartQualityReplace"/> for the
    /// actual download-then-remove step once the admin picks which ones.
    /// </para>
    /// </summary>
    public class FederationQualityAdvisorService
    {
        // Deliberately narrower than dedup's own DedupCandidateKinds
        // (FederationItemPersistenceService) - Series/Season are containers with
        // no media stream of their own to compare, so only the kinds that carry
        // an actual video file are worth a quality check.
        private static readonly BaseItemKind[] CandidateKinds =
        {
            BaseItemKind.Movie,
            BaseItemKind.Episode
        };

        // A remote at the same resolution needs to beat the local bitrate by
        // more than this to count as an upgrade - otherwise ordinary re-encode
        // noise between two copies of the same resolution would flag constantly
        // for a difference nobody would notice watching it.
        private const double BitrateUpgradeThreshold = 1.15;

        private readonly ILibraryManager _libraryManager;
        private readonly FederationItemCache _cache;
        private readonly ILogger<FederationQualityAdvisorService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationQualityAdvisorService"/> class.
        /// </summary>
        public FederationQualityAdvisorService(ILibraryManager libraryManager, FederationLibraryManager federationManager, ILogger<FederationQualityAdvisorService> logger)
        {
            _libraryManager = libraryManager;
            _cache = federationManager.Cache;
            _logger = logger;
        }

        /// <summary>
        /// Scans every locally-owned Movie/Episode for a federated, dedup-matched
        /// counterpart with meaningfully better resolution or bitrate. Returns an
        /// empty list (never throws) when dedup is off, no provider ids are
        /// configured, or enumeration fails - matching the local-dedup scan's own
        /// fail-safe behavior in <c>FederationItemPersistenceService.CollectServerWideLocalProviderIds</c>.
        /// </summary>
        public List<QualityUpgradeCandidate> FindUpgrades()
        {
            var results = new List<QualityUpgradeCandidate>();
            var config = Plugin.Instance?.Configuration;
            var dedupKeys = (config?.EnableDedup ?? true) ? (config?.DedupProviderIds ?? new List<string>()) : new List<string>();
            if (dedupKeys.Count == 0)
            {
                return results;
            }

            var servers = (config?.RemoteServers ?? new List<RemoteServer>()).ToDictionary(s => s.Id, s => s);
            var excludedItemIds = new HashSet<string>(config?.QualityUpgradeExcludedItemIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<BaseItem> localItems;
            try
            {
                localItems = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = CandidateKinds
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not enumerate the server for quality-upgrade matching");
                return results;
            }

            // Every cache entry with a dedup provider id, indexed for O(1) lookup
            // instead of re-scanning the whole cache per local item.
            var byProviderKey = new Dictionary<string, FederatedCacheEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _cache.GetAllEntries())
            {
                if (entry.Metadata.ProviderIds == null)
                {
                    continue;
                }

                foreach (var key in dedupKeys)
                {
                    if (FederationLibraryManager.TryGetProviderId(entry.Metadata.ProviderIds, key, out var val))
                    {
                        byProviderKey[$"{key}:{val}"] = entry;
                    }
                }
            }

            foreach (var item in localItems)
            {
                // Already a federated virtual item (streamed from elsewhere) -
                // nothing local to compare against or replace.
                if (FederationLibraryManager.GetFederationKey(item) != null || item.ProviderIds == null)
                {
                    continue;
                }

                // Admin's per-title override: never suggest replacing this exact
                // local copy, even though it would otherwise qualify.
                if (excludedItemIds.Contains(item.Id.ToString()))
                {
                    continue;
                }

                FederatedCacheEntry? match = null;
                foreach (var key in dedupKeys)
                {
                    if (FederationLibraryManager.TryGetProviderId(item.ProviderIds, key, out var val)
                        && byProviderKey.TryGetValue($"{key}:{val}", out var found))
                    {
                        match = found;
                        break;
                    }
                }

                if (match == null)
                {
                    continue;
                }

                var source = match.GetPrimarySource();
                if (source == null || !servers.TryGetValue(source.ServerId, out var server) || !server.Enabled)
                {
                    continue;
                }

                var (localHeight, localBitrate) = BestVideoStream(item.GetMediaStreams());
                var (remoteHeight, remoteBitrate) = BestVideoStream(match.Metadata.MediaStreams);

                if (!IsUpgrade(localHeight, localBitrate, remoteHeight, remoteBitrate))
                {
                    continue;
                }

                // The Jellyfin-peer remote id is the cached source's own RemoteItemId
                // directly; a non-Jellyfin (Plex) source's real id has to come from
                // RemoteNativeId instead - see FederationStreamHandler.ResolveExternalNativeId's
                // doc comment for why the two can't be interchanged.
                var nativeItemId = server.Kind == ServerKind.Jellyfin
                    ? source.RemoteItemId.ToString()
                    : match.Metadata.RemoteNativeId;
                if (string.IsNullOrEmpty(nativeItemId))
                {
                    continue;
                }

                results.Add(new QualityUpgradeCandidate
                {
                    LocalItemId = item.Id.ToString(),
                    Name = item.Name,
                    Year = item.ProductionYear,
                    LocalHeight = localHeight,
                    LocalBitrate = localBitrate,
                    RemoteHeight = remoteHeight,
                    RemoteBitrate = remoteBitrate,
                    RemoteServerId = server.Id,
                    RemoteServerName = server.Name,
                    RemoteNativeItemId = nativeItemId
                });
            }

            return results;
        }

        /// <summary>
        /// Finds the same candidate this scan would have found for one specific
        /// local item, re-run fresh rather than cached from a prior
        /// <see cref="FindUpgrades"/> call - the config page's Apply step only
        /// carries item ids back, and re-deriving here means the local/remote
        /// state actually gets re-checked at apply time instead of trusting
        /// whatever was true when the review list was first shown.
        /// </summary>
        public QualityUpgradeCandidate? FindUpgradeFor(string localItemId)
        {
            return FindUpgrades().FirstOrDefault(c => string.Equals(c.LocalItemId, localItemId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// True when the remote copy counts as a meaningful upgrade over the
        /// local one: strictly higher resolution always counts; at the same
        /// resolution, only a bitrate at least <see cref="BitrateUpgradeThreshold"/>
        /// times the local one does, so ordinary re-encode noise between two
        /// copies of the same resolution doesn't flag constantly for a
        /// difference nobody would notice watching it. Never an upgrade when
        /// either side's bitrate is unknown (0) - there is nothing to compare.
        /// Extracted as its own internal method (rather than inlined into
        /// <see cref="FindUpgrades"/>) so the decision itself is directly unit
        /// testable without needing a real <c>BaseItem</c>/library mock.
        /// </summary>
        internal static bool IsUpgrade(int localHeight, int localBitrate, int remoteHeight, int remoteBitrate)
        {
            if (remoteHeight > localHeight)
            {
                return true;
            }

            return remoteHeight == localHeight
                && localHeight > 0
                && remoteBitrate > 0
                && localBitrate > 0
                && remoteBitrate > localBitrate * BitrateUpgradeThreshold;
        }

        internal static (int Height, int Bitrate) BestVideoStream(IEnumerable<MediaStream>? streams)
        {
            if (streams == null)
            {
                return (0, 0);
            }

            var best = streams
                .Where(s => s.Type == MediaStreamType.Video)
                .OrderByDescending(s => s.Height ?? 0)
                .ThenByDescending(s => s.BitRate ?? 0)
                .FirstOrDefault();

            return best == null ? (0, 0) : (best.Height ?? 0, best.BitRate ?? 0);
        }
    }

    /// <summary>
    /// One local item where a federated friend holds a better copy, as offered
    /// to the admin on the config page's review list.
    /// </summary>
    public class QualityUpgradeCandidate
    {
        public string LocalItemId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int? Year { get; set; }

        public int LocalHeight { get; set; }

        public int LocalBitrate { get; set; }

        public int RemoteHeight { get; set; }

        public int RemoteBitrate { get; set; }

        public string RemoteServerId { get; set; } = string.Empty;

        public string RemoteServerName { get; set; } = string.Empty;

        public string RemoteNativeItemId { get; set; } = string.Empty;
    }
}
