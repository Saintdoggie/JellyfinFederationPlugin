using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Federation.Services
{
    internal static class FederationRating
    {
        internal static string? Resolve(FederationItemCache? cache, string? serverId, Guid remoteItemId, string? ownRating)
        {
            if (!string.IsNullOrWhiteSpace(ownRating) || cache == null || string.IsNullOrEmpty(serverId))
            {
                return ownRating;
            }

            var key = cache.TryGetLocalKeyForRemoteItem(serverId, remoteItemId);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var depth = 0; depth < 64 && !string.IsNullOrEmpty(key) && visited.Add(key); depth++)
            {
                var entry = cache.GetEntryByKey(key);
                if (entry == null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(entry.Metadata.OfficialRating))
                {
                    return entry.Metadata.OfficialRating;
                }

                key = entry.ParentKey;
            }

            return ownRating;
        }

        internal static string? Resolve(ILibraryManager libraryManager, Guid itemId)
        {
            var visited = new HashSet<Guid>();
            for (var depth = 0; depth < 64 && itemId != Guid.Empty && visited.Add(itemId); depth++)
            {
                var item = libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(item.OfficialRating))
                {
                    return item.OfficialRating;
                }

                itemId = item.ParentId;
            }

            return null;
        }
    }
}
