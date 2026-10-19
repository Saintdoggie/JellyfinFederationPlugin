using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Api
{
    /// <summary>
    /// Per-friend sharing extras: end dates, limits, the "what does this friend
    /// see" preview, usage statistics and popularity lists.
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
        public IActionResult SetFriendSharingExpiry(string id, [FromBody] SharingExpiryBody body)
        {
            var server = Plugin.Instance!.Configuration.RemoteServers.FirstOrDefault(s => s.Id == id);
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
            Plugin.Instance.SaveConfiguration();
            return Ok(new { success = true, message = "End dates saved." });
        }

        /// <summary>
        /// Admin-triggered: a friend's limits on this server - simultaneous
        /// streams and monthly data (0 = no limit). Enforced by <c>DirectStream</c>.
        /// </summary>
        [HttpPost("Friends/{id}/Limits")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetFriendLimits(string id, [FromBody] FriendLimitsBody body)
        {
            var server = Plugin.Instance!.Configuration.RemoteServers.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            if (body == null || body.MaxConcurrentStreams is < 0 or > 100 || body.MonthlyDataCapGb is < 0 or > 1_000_000)
            {
                return BadRequest(new { success = false, message = "Use 0 (no limit) to 100 streams and 0 (no limit) or more GB." });
            }

            server.MaxConcurrentStreams = body.MaxConcurrentStreams;
            server.MonthlyDataCapGb = body.MonthlyDataCapGb;
            Plugin.Instance.SaveConfiguration();
            return Ok(new { success = true, message = "Limits saved." });
        }

        /// <summary>
        /// Admin-triggered: whether this friend may see what is popular here,
        /// and whether this server shows their popular titles as a collection.
        /// </summary>
        [HttpPost("Friends/{id}/Popular")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult SetFriendPopular(string id, [FromBody] FriendPopularBody body)
        {
            var server = Plugin.Instance!.Configuration.RemoteServers.FirstOrDefault(s => s.Id == id);
            if (server == null)
            {
                return NotFound(new { success = false, message = "Friend not found." });
            }

            server.SharePopularity = body?.SharePopularity ?? false;
            server.ShowPopularCollection = body?.ShowPopularCollection ?? true;
            Plugin.Instance.SaveConfiguration();
            _ = Task.Run(() => _popularCollections.RefreshAsync(CancellationToken.None));
            return Ok(new { success = true, message = "Popular titles settings saved." });
        }

        /// <summary>Admin-triggered: rebuilds the friends' "Popular on ..." collections now.</summary>
        [HttpPost("PopularCollections/Refresh")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<IActionResult> RefreshPopularCollections(CancellationToken cancellationToken)
        {
            var ran = await _popularCollections.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                success = true,
                message = ran ? "Popular collections updated." : "An update was already running; it repeats with your change right after."
            });
        }

        /// <summary>
        /// Server-to-server: this server's most played titles (play counts only,
        /// never who watched), if this server's admin shares them with the
        /// calling friend. Only titles the friend may see are listed.
        /// </summary>
        [HttpGet("Peer/Popular")]
        [AllowAnonymous]
        public IActionResult GetPeerPopular()
        {
            var caller = FederationTokenAuth.ResolveCaller(Request);
            if (caller == null)
            {
                return Unauthorized();
            }

            if (!caller.SharePopularity || FederationPeerAccessService.IsSharingExpired(caller))
            {
                return Ok(new { shared = false, titles = Array.Empty<object>() });
            }

            var titles = _friendActivity.GetPopularTitles()
                .Where(t => _peerAccess.IsItemVisible(caller, null, t.TitleId))
                .Take(FederationPopularCollectionService.MaxTitles)
                .Select(t => new { id = t.TitleId.ToString("N"), plays = t.Plays })
                .ToList();
            return Ok(new { shared = true, titles });
        }

        /// <summary>
        /// Admin-only: usage of this server by friend servers - per friend this
        /// and last month, the most watched titles, and recent sessions.
        /// </summary>
        [HttpGet("Usage")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetFriendUsage()
        {
            var summary = _friendActivity.GetUsageSummary();
            var servers = Plugin.Instance?.Configuration?.RemoteServers ?? new List<RemoteServer>();
            return Ok(new
            {
                friends = summary.Friends.Select(f =>
                {
                    var server = servers.FirstOrDefault(s => s.Id == f.FriendId);
                    return new
                    {
                        friendId = f.FriendId,
                        friendName = server?.Name ?? f.FriendName,
                        stillFriend = server != null,
                        bytesThisMonth = f.BytesThisMonth,
                        bytesLastMonth = f.BytesLastMonth,
                        hoursThisMonth = f.HoursThisMonth,
                        sessionsThisMonth = f.SessionsThisMonth,
                        hoursLast30Days = f.HoursLast30Days,
                        monthlyDataCapGb = server?.MonthlyDataCapGb ?? 0,
                        topTitles = f.TopTitles.Select(t => new { name = t.Name, sessions = t.Sessions })
                    };
                }),
                topTitles = summary.TopTitles.Select(t => new { name = t.Name, sessions = t.Sessions, friends = t.Friends }),
                recent = _friendActivity.GetRecent(100).Select(e => new
                {
                    friendName = e.FriendName,
                    itemName = e.ItemName,
                    isDownload = e.IsDownload,
                    started = e.Started,
                    ended = e.Ended,
                    bytes = e.Bytes
                })
            });
        }

        /// <summary>
        /// Admin-only: everything one friend currently gets from this server -
        /// libraries with title counts, exclusions, users, friends, per-user
        /// rules, downloads, limits, popularity and end dates - computed with
        /// the same checks their requests go through.
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

            var users = LocalUserSummaries()
                .Where(u => FederationPeerAccessService.IsUserVisible(server, u.Id))
                .Select(u => u.Name)
                .ToList();
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
                allUsers = server.ShareAllUsers && !expired,
                users,
                friendsOfFriendsEnabled = config.AllowFriendsOfFriends,
                allFriends = server.ShareAllFriends && !expired,
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
                maxConcurrentStreams = server.MaxConcurrentStreams,
                monthlyDataCapGb = server.MonthlyDataCapGb,
                sharePopularity = server.SharePopularity && !expired
            });
        }

        /// <summary>This server's local users as (id, name, administrator).</summary>
        private IEnumerable<(Guid Id, string Name, bool IsAdministrator)> LocalUserSummaries()
        {
            foreach (var u in EnumerateLocalUsers())
            {
                var t = u.GetType();
                if (t.GetProperty("Id")?.GetValue(u) is Guid id && t.GetProperty("Username")?.GetValue(u) is string name)
                {
                    yield return (id, name, IsAdministratorUser(u));
                }
            }
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

    public class FriendLimitsBody
    {
        public int MaxConcurrentStreams { get; set; }

        public int MonthlyDataCapGb { get; set; }
    }

    public class FriendPopularBody
    {
        public bool SharePopularity { get; set; }

        public bool ShowPopularCollection { get; set; } = true;
    }
}
