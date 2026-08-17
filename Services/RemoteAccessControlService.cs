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

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteAccessControlService"/> class.
        /// </summary>
        public RemoteAccessControlService(ILogger<RemoteAccessControlService> logger)
        {
            _logger = logger;
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

                    return allowedLibrary;

                default:
                    return true;
            }
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
    }
}
