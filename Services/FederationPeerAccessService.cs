using System;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Enforces what a federated friend (identified by <see cref="FederationTokenAuth.ResolveCaller"/>,
    /// optionally narrowed to one of their own local users via
    /// <see cref="RemoteServerClient.RemoteUserIdHeader"/>) may see of this
    /// server's own content - <see cref="RemoteServer.ShareAllLibraries"/>/
    /// <see cref="RemoteServer.SharedLibraryFolderIds"/>, per-friend
    /// <see cref="RemoteServer.ExcludedItemIds"/>, and per-remote-user
    /// <see cref="RemoteUserAccessRule"/>s - all applied server-side, in this
    /// plugin's own code, before any Peer/* endpoint (or <c>PlaybackToken</c>)
    /// returns anything. This is what replaces the old model's dependency on a
    /// dummy local Jellyfin account + native <c>EnabledFolders</c> policy to
    /// enforce sharing scope: a friend's federation token never authenticates as
    /// any Jellyfin user at all, so there is nothing for Jellyfin's own
    /// permission system to enforce here even if there were an account to use.
    /// </summary>
    public class FederationPeerAccessService
    {
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationPeerAccessService"/> class.
        /// </summary>
        public FederationPeerAccessService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Whether <paramref name="libraryFolderId"/> (this server's own top-level
        /// library folder id, same id space as <see cref="RemoteServer.SharedLibraryFolderIds"/>)
        /// is visible to <paramref name="caller"/>, optionally narrowed further by
        /// a <see cref="RemoteUserAccessRule"/> for <paramref name="remoteUserId"/>.
        /// </summary>
        public bool IsLibraryVisible(RemoteServer caller, string? remoteUserId, string libraryFolderId)
        {
            if (!caller.ShareAllLibraries
                && !(caller.SharedLibraryFolderIds ?? new System.Collections.Generic.List<string>())
                    .Any(id => FolderIdsEqual(id, libraryFolderId)))
            {
                return false;
            }

            var rule = FindRule(caller, remoteUserId);
            if (rule == null)
            {
                return true;
            }

            return rule.Mode switch
            {
                RemoteUserAccessMode.Blocked => false,
                RemoteUserAccessMode.AllLibraries => true,
                RemoteUserAccessMode.CertainLibraries => rule.LibraryFolderIds.Any(id => FolderIdsEqual(id, libraryFolderId)),
                // A CertainItems rule narrows to specific items, not whole
                // libraries - the library itself stays "visible" so a client can
                // still browse into it; IsItemVisible is what actually filters
                // which items inside it come back.
                RemoteUserAccessMode.CertainItems => true,
                _ => true
            };
        }

        /// <summary>
        /// Whether <paramref name="itemId"/> (one of this server's own local
        /// items) is visible to <paramref name="caller"/>. Resolves the item's
        /// own top-level library folder to evaluate
        /// <see cref="RemoteServer.SharedLibraryFolderIds"/>/
        /// <see cref="RemoteUserAccessMode.CertainLibraries"/> against - prefer
        /// the overload taking <paramref name="libraryFolderId"/> directly when
        /// the caller already knows it (e.g. iterating one library's contents),
        /// since this overload pays a library lookup per item.
        /// </summary>
        public bool IsItemVisible(RemoteServer caller, string? remoteUserId, Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return false;
            }

            return IsItemVisible(caller, remoteUserId, itemId, ResolveTopLibraryFolderId(item));
        }

        /// <summary>
        /// Whether <paramref name="itemId"/> is visible to <paramref name="caller"/>,
        /// given its already-known top-level library folder id (or null if it
        /// could not be resolved - treated as "not in any shared library" for
        /// the whole-library scope check, same as a genuinely unmatched folder).
        /// </summary>
        public bool IsItemVisible(RemoteServer caller, string? remoteUserId, Guid itemId, string? libraryFolderId)
        {
            var itemIdString = itemId.ToString("N");

            if ((caller.ExcludedItemIds ?? new System.Collections.Generic.List<string>())
                .Any(id => string.Equals(id, itemIdString, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!caller.ShareAllLibraries
                && (string.IsNullOrEmpty(libraryFolderId)
                    || !(caller.SharedLibraryFolderIds ?? new System.Collections.Generic.List<string>())
                        .Any(id => FolderIdsEqual(id, libraryFolderId))))
            {
                return false;
            }

            var rule = FindRule(caller, remoteUserId);
            if (rule == null)
            {
                return true;
            }

            // Per-user rating ceiling applies even when Mode is AllLibraries (no
            // library/item restriction): a parent can still allow a kid to browse
            // "all shared libraries" but block adult-rated titles.
            if (!string.IsNullOrWhiteSpace(rule.MaxAllowedRating))
            {
                var item = _libraryManager.GetItemById(itemId);
                var rating = item?.OfficialRating;
                if (!string.IsNullOrWhiteSpace(rating)
                    && !IncomingContentFilterService.IsWithinRatingCeiling(rating!, rule.MaxAllowedRating))
                {
                    return false;
                }
            }

            return rule.Mode switch
            {
                RemoteUserAccessMode.Blocked => false,
                RemoteUserAccessMode.AllLibraries => true,
                RemoteUserAccessMode.CertainLibraries => !string.IsNullOrEmpty(libraryFolderId)
                    && rule.LibraryFolderIds.Any(id => FolderIdsEqual(id, libraryFolderId)),
                RemoteUserAccessMode.CertainItems => rule.ItemIds.Any(id => string.Equals(id, itemIdString, StringComparison.OrdinalIgnoreCase)),
                _ => true
            };
        }

        /// <summary>
        /// Whether a specific remote user may download (server-side fetch) items
        /// from this server. Separate from <see cref="IsItemVisible"/> which
        /// gates browsing/streaming — a user may be allowed to stream but not
        /// download. Anonymous / no-rule users inherit the friend-level and
        /// global gates only.
        /// </summary>
        public bool IsDownloadAllowedForRemoteUser(RemoteServer caller, string? remoteUserId)
        {
            if (string.IsNullOrWhiteSpace(remoteUserId))
            {
                // Friend-level gate still applies; per-user gate is skipped when
                // there is no user identity (e.g. background sync).
                return caller.AllowDownloads;
            }

            if (!caller.AllowDownloads)
            {
                return false;
            }

            var rule = FindRule(caller, remoteUserId);
            if (rule != null && !rule.AllowDownload)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resolves the id of the top-level library folder an item belongs to, or
        /// null if it could not be determined (e.g. an item not under any real
        /// library folder). Compared against <see cref="RemoteServer.SharedLibraryFolderIds"/>/
        /// <see cref="RemoteUserAccessRule.LibraryFolderIds"/> via
        /// <see cref="FolderIdsEqual"/> rather than raw string equality, since
        /// <see cref="ILibraryManager.GetVirtualFolders"/> - the source of what
        /// gets stored in those lists via the <c>LocalLibraries</c> picker - is
        /// not guaranteed to report a folder's id in the same string format
        /// <see cref="Guid.ToString(string)"/> with the "N" specifier produces.
        /// </summary>
        public string? ResolveTopLibraryFolderId(BaseItem item)
        {
            var top = item.GetTopParent();
            return top?.Id.ToString("N");
        }

        /// <summary>
        /// Compares two library folder id strings for equality tolerant of
        /// formatting differences (with/without dashes, casing) - both are
        /// parsed as a <see cref="Guid"/> and compared by value when possible,
        /// falling back to an ordinal, case-insensitive string compare only if
        /// either side fails to parse as a Guid at all.
        /// </summary>
        private static bool FolderIdsEqual(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }

            if (Guid.TryParse(a, out var guidA) && Guid.TryParse(b, out var guidB))
            {
                return guidA == guidB;
            }

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static RemoteUserAccessRule? FindRule(RemoteServer caller, string? remoteUserId)
        {
            if (string.IsNullOrEmpty(remoteUserId))
            {
                return null;
            }

            return caller.RemoteUserAccessRules?.FirstOrDefault(r => string.Equals(r.RemoteUserId, remoteUserId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
