using System;
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
            if (server == null)
            {
                return true;
            }

            if (localUserId == null || localUserId == Guid.Empty)
            {
                return true;
            }

            var rule = server.FriendUserAccessRules?.FirstOrDefault(r =>
                Guid.TryParse(r.RemoteUserId, out var ruleUserId) && ruleUserId == localUserId.Value);
            if (rule == null)
            {
                // No override for this specific user - fall back to the friend's
                // existing server-level scope, which already governed whatever was
                // fetched/materialized.
                return true;
            }

            // Mirrors FederationPeerAccessService's own BlockedItemIds check on the
            // friend's side - the authoritative enforcement already happened there
            // (they never sent us this item to begin with), this is only a second
            // layer in case we already cached/materialized it under an older copy
            // of their rule before it was pushed.
            if ((rule.BlockedItemIds ?? new System.Collections.Generic.List<string>())
                .Any(id => Guid.TryParse(id, out var blockedGuid) && blockedGuid == remoteItemId))
            {
                _logger.LogInformation(
                    "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} (per-item block)",
                    localUserId,
                    remoteItemId,
                    server.Name);
                return false;
            }

            switch (rule.Mode)
            {
                case RemoteUserAccessMode.Blocked:
                    _logger.LogInformation(
                        "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} (blocked by that friend's admin)",
                        localUserId,
                        remoteItemId,
                        server.Name);
                    return false;

                case RemoteUserAccessMode.AllLibraries:
                    // Even "allow everything" still respects the user's own rating
                    // ceiling and download gate below.
                    if (!string.IsNullOrWhiteSpace(rule.MaxAllowedRating))
                    {
                        var rating = TryResolveRating(server, remoteItemId);
                        if (!IncomingContentFilterService.IsAllowedByRatingCeilings(rating, null, rule.MaxAllowedRating))
                        {
                            _logger.LogInformation(
                                "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} (per-user rating ceiling {Ceiling})",
                                localUserId,
                                remoteItemId,
                                server.Name,
                                rule.MaxAllowedRating);
                            return false;
                        }
                    }

                    return true;

                case RemoteUserAccessMode.CertainItems:
                    var allowedItem = rule.ItemIds != null && rule.ItemIds.Any(id =>
                        Guid.TryParse(id, out var itemGuid) && itemGuid == remoteItemId);
                    if (!allowedItem)
                    {
                        _logger.LogInformation(
                            "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} (not in their allowed item list)",
                            localUserId,
                            remoteItemId,
                            server.Name);
                    }
                    else if (!string.IsNullOrWhiteSpace(rule.MaxAllowedRating))
                    {
                        var rating = TryResolveRating(server, remoteItemId);
                        if (!IncomingContentFilterService.IsAllowedByRatingCeilings(rating, null, rule.MaxAllowedRating))
                        {
                            _logger.LogInformation(
                                "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} (per-user rating ceiling {Ceiling})",
                                localUserId,
                                remoteItemId,
                                server.Name,
                                rule.MaxAllowedRating);
                            return false;
                        }
                    }

                    return allowedItem;

                case RemoteUserAccessMode.CertainLibraries:
                    var allowedLibrary = IsInAllowedLibrary(server, mappingName, rule);
                    if (!allowedLibrary)
                    {
                        _logger.LogInformation(
                            "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} (not in their allowed library list)",
                            localUserId,
                            remoteItemId,
                            server.Name);
                    }
                    else if (!string.IsNullOrWhiteSpace(rule.MaxAllowedRating))
                    {
                        var rating = TryResolveRating(server, remoteItemId);
                        if (!IncomingContentFilterService.IsAllowedByRatingCeilings(rating, null, rule.MaxAllowedRating))
                        {
                            _logger.LogInformation(
                                "[Federation] Blocking user {UserId} from item {ItemId} on {ServerName} (per-user rating ceiling {Ceiling})",
                                localUserId,
                                remoteItemId,
                                server.Name,
                                rule.MaxAllowedRating);
                            return false;
                        }
                    }

                    return allowedLibrary;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether a shared, user-agnostic item path is safe for this item. Jellyfin
        /// stores one Path for every local user, so it may only be stamped when every
        /// configured override permits this exact item; users without an override
        /// inherit the already-filtered server scope and are therefore allowed.
        /// </summary>
        public static bool IsAllowedForEveryConfiguredUser(
            RemoteServer? server,
            string? mappingName,
            Guid remoteItemId,
            string? officialRating)
        {
            if (server?.FriendUserAccessRules == null || server.FriendUserAccessRules.Count == 0)
            {
                return true;
            }

            foreach (var rule in server.FriendUserAccessRules)
            {
                if ((rule.BlockedItemIds ?? new System.Collections.Generic.List<string>())
                    .Any(id => Guid.TryParse(id, out var blocked) && blocked == remoteItemId))
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
                    RemoteUserAccessMode.CertainItems => (rule.ItemIds ?? new System.Collections.Generic.List<string>())
                        .Any(id => Guid.TryParse(id, out var item) && item == remoteItemId),
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
                var rule = server.FriendUserAccessRules?.FirstOrDefault(r =>
                    Guid.TryParse(r.RemoteUserId, out var ruleUserId) && ruleUserId == localUserId.Value);
                if (rule != null && !rule.AllowDownload)
                {
                    return false;
                }
            }

            return true;
        }

        private string? TryResolveRating(RemoteServer server, Guid remoteItemId)
        {
            try
            {
                // An episode/season's own remote id was never upserted as its own
                // top-level entry key (raw or provider-id) - only its parent Series
                // is - so this can come back empty for one; that's already covered
                // by the sync-time incoming filter and the peer-visibility path,
                // which check ratings up front rather than needing this
                // reconstruction at all. This fills in the one path that couldn't:
                // a per-user MaxAllowedRating ceiling checked lazily, after the item
                // is already cached, with only (server, remote item id) in hand.
                var key = _cache.TryGetLocalKeyForRemoteItem(server.Id, remoteItemId);
                return key == null ? null : _cache.GetEntryByKey(key)?.Metadata.OfficialRating;
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
