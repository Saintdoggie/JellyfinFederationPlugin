using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Enforces per-remote-user access overrides on top of a friend's existing
    /// server-level sharing scope (<see cref="RemoteServer.ShareAllLibraries"/>/
    /// <see cref="RemoteServer.SharedLibraryFolderIds"/>). This runs on the
    /// consuming side (the server actually playing/browsing a friend's content),
    /// evaluated against <see cref="RemoteServer.FriendUserAccessRules"/> - the
    /// friend's own rules about *this server's* local users, pushed down by
    /// <see cref="FederationFriendService.SetRemoteUserAccessRuleAsync"/> and
    /// stored locally so no extra network round trip is needed at play time.
    /// See <see cref="RemoteUserAccessRule"/> for why this can only be evaluated
    /// here rather than by the friend directly: content this plugin pulls from a
    /// friend is fetched once, server-wide, under one shared hidden account (see
    /// <see cref="RemoteServer.LocalShareUserId"/>), so the friend has no visibility
    /// into which of *our* local users is actually the one browsing or streaming -
    /// only we do.
    /// </summary>
    public class RemoteAccessControlService
    {
        private readonly ILogger<RemoteAccessControlService> _logger;
        private readonly FederationItemCache _cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteAccessControlService"/> class.
        /// </summary>
        public RemoteAccessControlService(ILogger<RemoteAccessControlService> logger, FederationItemCache cache)
        {
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// Test-only constructor: allows tests to construct without a cache.
        /// </summary>
        public RemoteAccessControlService(ILogger<RemoteAccessControlService> logger)
            : this(logger, new FederationItemCache(Microsoft.Extensions.Logging.Abstractions.NullLogger<FederationItemCache>.Instance))
        {
        }

        /// <summary>
        /// Whether <paramref name="localUserId"/> (one of this server's own users) is
        /// allowed to see/play <paramref name="remoteItemId"/> sourced from
        /// <paramref name="server"/>, given whatever per-remote-user override that
        /// friend has configured for this specific local user.
        /// </summary>
        /// <param name="server">The friend server the content originated from.</param>
        /// <param name="localUserId">
        /// The local user whose own action (browsing/playback) this check gates, or
        /// null when it cannot be determined (e.g. a background sync with no
        /// authenticated request context) - in which case this returns true
        /// unconditionally, since there is nothing to evaluate a per-user rule
        /// against and the friend's existing server-level scope already governs
        /// what was fetched in the first place. This keeps the feature additive:
        /// an install with no rules configured, or a call with no known user,
        /// behaves exactly as before.
        /// </param>
        /// <param name="mappingName">
        /// The <see cref="LibraryMapping.LocalLibraryName"/> the item was
        /// materialized under, used to resolve which of the friend's own library
        /// folders it came from for <see cref="RemoteUserAccessMode.CertainLibraries"/>.
        /// May be null/empty (treated as "unknown library", which fails a
        /// CertainLibraries rule closed rather than open).
        /// </param>
        /// <param name="remoteItemId">The friend's own item id for the content.</param>
        public bool IsAllowed(RemoteServer? server, Guid? localUserId, string? mappingName, Guid remoteItemId)
        {
            if (server == null || localUserId == null || localUserId == Guid.Empty)
            {
                return true;
            }

            if (Evaluate(server, localUserId.Value, mappingName, remoteItemId, _cache, out var reason))
            {
                return true;
            }

            _logger.LogInformation(
                "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} ({Reason})",
                localUserId,
                remoteItemId,
                server.Name,
                reason);
            return false;
        }

        /// <summary>
        /// Finds the rule <paramref name="server"/>'s admin set for one of this
        /// server's users. Ids are compared as Guids: the owner lists users in
        /// "N" form, but a rule typed or stored in "D" form is the same user.
        /// </summary>
        public static RemoteUserAccessRule? FindRule(RemoteServer? server, Guid localUserId)
            => server?.FriendUserAccessRules?.FirstOrDefault(r =>
                Guid.TryParse(r.RemoteUserId, out var ruleUserId) && ruleUserId == localUserId);

        /// <summary>
        /// True when a rule can hide anything at all. A rule that only turns off
        /// downloads (or an "all libraries" rule with nothing extra) never changes
        /// what the user sees.
        /// </summary>
        public static bool RestrictsVisibility(RemoteUserAccessRule? rule)
            => rule != null
                && (rule.Mode != RemoteUserAccessMode.AllLibraries
                    || !string.IsNullOrWhiteSpace(rule.MaxAllowedRating)
                    || (rule.BlockedItemIds?.Count ?? 0) > 0);

        /// <summary>
        /// Pure, log-free evaluation of whether the friend's rule for
        /// <paramref name="localUserId"/> lets them see/play
        /// <paramref name="remoteItemId"/>. Shared by the playback checks
        /// (<see cref="IsAllowed"/>) and by library hiding
        /// (<see cref="FederationVisibilityService"/>), so what a user can see and
        /// what they can play can never disagree. No rule means allowed: the
        /// friend's server-level scope already governed what was synced.
        /// </summary>
        /// <param name="reason">Why it was refused; null when allowed.</param>
        public static bool Evaluate(
            RemoteServer server,
            Guid localUserId,
            string? mappingName,
            Guid remoteItemId,
            FederationItemCache? cache,
            out string? reason)
        {
            reason = null;
            var rule = FindRule(server, localUserId);
            if (rule == null)
            {
                return true;
            }

            // Mirrors FederationPeerAccessService.IsItemOrAncestorListed: a series
            // block/allow also covers cached seasons and episodes.
            if (IsRemoteItemOrAncestorListed(cache, server.Id, remoteItemId, rule.BlockedItemIds))
            {
                reason = "per-item block";
                return false;
            }

            bool allowed;
            switch (rule.Mode)
            {
                case RemoteUserAccessMode.Blocked:
                    reason = "blocked by that friend's admin";
                    return false;
                case RemoteUserAccessMode.CertainItems:
                    allowed = IsRemoteItemOrAncestorListed(cache, server.Id, remoteItemId, rule.ItemIds);
                    reason = allowed ? null : "not in their allowed item list";
                    break;
                case RemoteUserAccessMode.CertainLibraries:
                    allowed = IsInAllowedLibrary(server, mappingName, rule);
                    reason = allowed ? null : "not in their allowed library list";
                    break;
                default:
                    // AllLibraries still respects the user's own rating ceiling.
                    allowed = true;
                    break;
            }

            if (allowed && !string.IsNullOrWhiteSpace(rule.MaxAllowedRating))
            {
                var rating = TryResolveRating(cache, server.Id, remoteItemId);
                if (!IncomingContentFilterService.IsAllowedByRatingCeilings(rating, null, rule.MaxAllowedRating))
                {
                    reason = $"per-user rating ceiling {rule.MaxAllowedRating}";
                    return false;
                }
            }

            return allowed;
        }

        /// <summary>
        /// Whether a shared, user-agnostic item path is safe for this item. Jellyfin
        /// stores one Path for every local user, so it may only be stamped when every
        /// configured override permits this item, including a series-level block that
        /// covers nested seasons/episodes via the federation cache parent chain.
        /// Users without an override inherit the already-filtered server scope.
        /// </summary>
        public static bool IsAllowedForEveryConfiguredUser(
            RemoteServer? server,
            string? mappingName,
            Guid remoteItemId,
            string? officialRating,
            FederationItemCache? cache = null)
        {
            if (server?.FriendUserAccessRules == null || server.FriendUserAccessRules.Count == 0)
            {
                return true;
            }

            officialRating = FederationRating.Resolve(cache, server.Id, remoteItemId, officialRating);
            foreach (var rule in server.FriendUserAccessRules)
            {
                if (IsRemoteItemOrAncestorListed(cache, server.Id, remoteItemId, rule.BlockedItemIds))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(rule.MaxAllowedRating)
                    && !IncomingContentFilterService.IsAllowedByRatingCeilings(officialRating, null, rule.MaxAllowedRating))
                {
                    return false;
                }

                var allowed = rule.Mode switch
                {
                    RemoteUserAccessMode.Blocked => false,
                    RemoteUserAccessMode.AllLibraries => true,
                    RemoteUserAccessMode.CertainItems => IsRemoteItemOrAncestorListed(cache, server.Id, remoteItemId, rule.ItemIds),
                    RemoteUserAccessMode.CertainLibraries => IsInAllowedLibrary(server, mappingName, rule),
                    _ => true
                };

                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsInAllowedLibrary(RemoteServer server, string? mappingName, RemoteUserAccessRule rule)
        {
            if (string.IsNullOrEmpty(mappingName) || rule.LibraryFolderIds == null || rule.LibraryFolderIds.Count == 0)
            {
                return false;
            }

            var config = Plugin.Instance?.Configuration;
            var mapping = config?.LibraryMappings?.FirstOrDefault(m =>
                string.Equals(m.LocalLibraryName, mappingName, StringComparison.OrdinalIgnoreCase));
            var remoteLibraryId = mapping?.RemoteLibrarySources?
                .FirstOrDefault(s => string.Equals(s.ServerId, server.Id, StringComparison.OrdinalIgnoreCase))?
                .RemoteLibraryId;

            return !string.IsNullOrEmpty(remoteLibraryId)
                && rule.LibraryFolderIds.Any(id => string.Equals(id, remoteLibraryId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// True when <paramref name="remoteItemId"/> or a cached series/season
        /// ancestor is in <paramref name="configuredIds"/>. The walk follows
        /// <see cref="FederatedCacheEntry.ParentKey"/> and is bounded/cycle-safe.
        /// Exact id still matches when the cache has no parent links.
        /// </summary>
        private static bool IsRemoteItemOrAncestorListed(
            FederationItemCache? cache,
            string? serverId,
            Guid remoteItemId,
            IEnumerable<string>? configuredIds)
        {
            var listed = new HashSet<Guid>();
            foreach (var id in configuredIds ?? Array.Empty<string>())
            {
                if (Guid.TryParse(id, out var parsed))
                {
                    listed.Add(parsed);
                }
            }

            if (listed.Count == 0)
            {
                return false;
            }

            if (listed.Contains(remoteItemId))
            {
                return true;
            }

            if (cache == null || string.IsNullOrEmpty(serverId))
            {
                return false;
            }

            var key = cache.TryGetLocalKeyForRemoteItem(serverId, remoteItemId);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var depth = 0; depth < 64 && !string.IsNullOrEmpty(key) && visited.Add(key); depth++)
            {
                var entry = cache.GetEntryByKey(key);
                if (entry == null)
                {
                    break;
                }

                foreach (var source in entry.GetSourcesSnapshot())
                {
                    if (string.Equals(source.ServerId, serverId, StringComparison.OrdinalIgnoreCase)
                        && listed.Contains(source.RemoteItemId))
                    {
                        return true;
                    }
                }

                key = entry.ParentKey;
            }

            return false;
        }

        /// <summary>
        /// Whether a specific local user is allowed to use the Download action for
        /// content from <paramref name="server"/>. Checks both the global incoming
        /// filter and the per-friend / per-user download gates.
        /// </summary>
        public bool IsDownloadAllowed(RemoteServer? server, Guid? localUserId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.IncomingFilter != null && !config.IncomingFilter.AllowDownloads)
            {
                return false;
            }

            if (server != null && !server.AllowDownloads)
            {
                return false;
            }

            if (server != null && localUserId != null && localUserId != Guid.Empty)
            {
                var rule = FindRule(server, localUserId.Value);
                if (rule != null && !rule.AllowDownload)
                {
                    return false;
                }
            }

            return true;
        }

        private static string? TryResolveRating(FederationItemCache? cache, string? serverId, Guid remoteItemId)
        {
            try
            {
                return FederationRating.Resolve(cache, serverId, remoteItemId, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Direct rating check when the caller already has the item's official
        /// rating in hand (e.g. sync path). Stricter of global and per-user wins.
        /// </summary>
        public static bool IsRatingAllowedForUser(string? itemOfficialRating, RemoteUserAccessRule? rule, IncomingContentFilter? globalFilter)
        {
            var globalCeiling = globalFilter?.MaxAllowedRating;
            var perUserCeiling = rule?.MaxAllowedRating;
            return IncomingContentFilterService.IsAllowedByRatingCeilings(itemOfficialRating, globalCeiling, perUserCeiling);
        }
    }
}
