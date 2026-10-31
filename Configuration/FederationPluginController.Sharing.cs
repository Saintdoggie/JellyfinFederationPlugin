using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// Per-friend sharing end dates and the "what does this friend see" preview.
    /// </summary>
    public partial class FederationController
    {
        private static readonly BaseItemKind[] PreviewTitleKinds =
        {
            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum, BaseItemKind.MusicVideo
        };

        /// <summary>
        /// Admin-triggered: when sharing with a friend ends, as a whole and per
        /// library. Null or missing dates share without an end. Enforced by
        /// <see cref="FederationPeerAccessService"/> at request and stream time.
        /// </summary>
        [HttpPost("Friends/{id}/Expiry")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetFriendSharingExpiry(string id, [FromBody] SharingExpiryBody? body)
        {
            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            server.SharingExpiresUtc = body?.ExpiresUtc?.ToUniversalTime();
            server.LibraryShareExpiries = (body?.LibraryExpiries ?? new List<LibraryShareExpiry>())
                .Where(e => !string.IsNullOrWhiteSpace(e.FolderId))
                .GroupBy(e => e.FolderId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new LibraryShareExpiry { FolderId = g.Key, ExpiresUtc = g.Last().ExpiresUtc.ToUniversalTime() })
                .ToList();
            Plugin.Instance!.SaveConfiguration();
            NotifyFriendOfSharingChange(id);
            return Ok(new { success = true, message = "End dates saved." });
        }

        /// <summary>
        /// Admin-only: what one friend currently gets from this server -
        /// libraries with title counts, exclusions, the user and friend lists
        /// they can read, per-user rules, downloads, conversion and limits -
        /// computed with the same checks their requests go through.
        /// </summary>
        [HttpGet("Friends/{id}/Preview")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult PreviewFriendAccess(string id)
        {
            var config = Plugin.Instance!.Configuration;
            var server = config.RemoteServers.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            var expired = FederationPeerAccessService.IsSharingExpired(server);
            var libraries = new List<object>();
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                var folderId = folder.ItemId;
                var visible = _peerAccess.IsLibraryVisible(server, null, folderId);
                var expiry = (server.LibraryShareExpiries ?? new List<LibraryShareExpiry>())
                    .FirstOrDefault(e => FederationPeerAccessService.FolderIdsEqual(e.FolderId, folderId));
                libraries.Add(new
                {
                    id = folderId,
                    name = folder.Name,
                    visible,
                    expiresUtc = expiry?.ExpiresUtc,
                    expired = FederationPeerAccessService.IsLibraryShareExpired(server, folderId),
                    titles = visible ? CountVisibleTitles(server, folderId) : 0
                });
            }

            // What Peer/Users and Friends/List give this friend.
            var users = new List<string>();
            foreach (var u in EnumerateLocalUsers())
            {
                var t = u.GetType();
                if (t.GetProperty("Id")?.GetValue(u) is Guid userId
                    && t.GetProperty("Username")?.GetValue(u) is string name
                    && FederationPeerAccessService.IsUserVisible(server, userId))
                {
                    users.Add(name);
                }
            }

            var friends = config.AllowFriendsOfFriends
                ? config.RemoteServers.Where(s => FederationPeerAccessService.IsFriendVisible(server, s)).Select(s => s.Name).ToList()
                : new List<string>();

            return Ok(new
            {
                success = true,
                name = server.Name,
                enabled = server.Enabled,
                expired,
                expiresUtc = server.SharingExpiresUtc,
                libraries,
                excludedTitles = (server.ExcludedItemIds ?? new List<string>()).Select(DescribeLocalItem).ToList(),
                globallyExcludedCount = config.GloballyExcludedItemIds?.Count ?? 0,
                users,
                friendsOfFriendsEnabled = config.AllowFriendsOfFriends,
                friends,
                userRules = (server.RemoteUserAccessRules ?? new List<RemoteUserAccessRule>()).Select(r => new
                {
                    name = string.IsNullOrWhiteSpace(r.RemoteUserName) ? "One of their users" : r.RemoteUserName,
                    mode = r.Mode.ToString(),
                    maxRating = r.MaxAllowedRating,
                    allowDownload = r.AllowDownload
                }),
                allowDownloads = server.AllowDownloads && !expired,
                allowBulkDownloads = server.AllowBulkDownloads && server.AllowDownloads && !expired,
                allowRemoteTranscode = config.EnableRemoteTranscodeForFriends && server.AllowRemoteTranscode && !expired,
                maxConcurrentStreams = server.MaxConcurrentStreams,
                monthlyDataCapGb = server.MonthlyDataCapGb,
                sharePopular = FederationFriendPopularService.SharesPopularWith(server) && !expired,
                shareViewerNames = FederationViewerReporter.SharesNamesWith(server)
            });
        }

        private string DescribeLocalItem(string itemId)
        {
            var item = Guid.TryParse(itemId, out var guid) ? _libraryManager.GetItemById(guid) : null;
            if (item == null)
            {
                return "A title that no longer exists";
            }

            return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
        }

        /// <summary>
        /// Movies, shows and albums of one library the friend can see, or null
        /// when they cannot be counted.
        /// </summary>
        private int? CountVisibleTitles(RemoteServer server, string folderId)
        {
            try
            {
                if (!Guid.TryParse(folderId, out var folderGuid) || _libraryManager.GetItemById(folderGuid) is not Folder folder)
                {
                    return null;
                }

                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Parent = folder,
                    Recursive = true,
                    IncludeItemTypes = PreviewTitleKinds,
                    IsVirtualItem = false
                });

                // Without excluded titles, an item of a visible library is
                // visible unless it is not this server's own media - the same
                // first checks IsItemVisible makes, without its per-item
                // lookups and ancestor walks.
                var hasExclusions = (server.ExcludedItemIds?.Count ?? 0) > 0
                    || (Plugin.Instance?.Configuration?.GloballyExcludedItemIds?.Count ?? 0) > 0;
                return hasExclusions
                    ? items.Count(i => _peerAccess.IsItemVisible(server, null, i.Id, folderId))
                    : items.Count(i => !FederationLibraryManager.IsIneligibleForOutgoingShare(i) && !FederationDownloadService.IsDownloadedFilePath(i.Path));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not count titles of library {FolderId} for the sharing preview", folderId);
                return null;
            }
        }
    }

    public class SharingExpiryBody
    {
        public DateTime? ExpiresUtc { get; set; }

        public List<LibraryShareExpiry>? LibraryExpiries { get; set; }
    }
}
