using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Receiver side: the friend Playback token behind each media source this
    /// server just offered a viewer, so the remote-transcode filter can hand that
    /// viewer's transcode/subtitle requests to the friend without minting again.
    /// Keyed by viewer, item and media source id (what the player's requests carry).
    /// A token is bound by the friend to that viewer, so it is never shared across
    /// viewers.
    /// </summary>
    public static class PeerPlaybackGrants
    {
        /// <summary>Kept a little under the friend's 24 h token lifetime.</summary>
        internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(20);

        private static readonly ConcurrentDictionary<(Guid User, Guid Item, string Source), Grant> Grants = new();

        /// <summary>Clock, replaceable in tests.</summary>
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Remembers the grant behind one offered source.</summary>
        public static void Record(Guid userId, Guid itemId, string sourceId, string serverId, Guid remoteItemId, string token, string? remoteMediaSourceId = null)
        {
            Prune();
            Grants[(userId, itemId, Normalize(sourceId))] = new Grant(serverId, remoteItemId, token, UtcNow() + Lifetime, remoteMediaSourceId);
        }

        /// <summary>The grant for this viewer's source, or null when unknown or expired.</summary>
        public static Grant? Find(Guid userId, Guid itemId, string? sourceId)
        {
            var key = (userId, itemId, Normalize(string.IsNullOrEmpty(sourceId) ? itemId.ToString("N") : sourceId));
            return Grants.TryGetValue(key, out var grant) && grant.ExpiresUtc > UtcNow() ? grant : null;
        }

        /// <summary>Drops a grant the friend refused, so the next request mints a fresh one.</summary>
        public static void Forget(Guid userId, Guid itemId, string? sourceId)
            => Grants.TryRemove((userId, itemId, Normalize(string.IsNullOrEmpty(sourceId) ? itemId.ToString("N") : sourceId)), out _);

        /// <summary>
        /// The token on a Direct-mode source path
        /// (<c>.../Plugins/Federation/DirectStream/{id}?token=...</c>), or null.
        /// </summary>
        public static string? TokenFromDirectPath(string? path)
        {
            if (string.IsNullOrEmpty(path)
                || !Uri.TryCreate(path, UriKind.Absolute, out var uri)
                || !uri.AbsolutePath.Contains("/Plugins/Federation/DirectStream/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var token = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("token", out var value)
                ? value.ToString()
                : null;
            return string.IsNullOrEmpty(token) ? null : token;
        }

        /// <summary>Test hook.</summary>
        internal static void Reset() => Grants.Clear();

        private static string Normalize(string sourceId)
            => Guid.TryParse(sourceId, out var g) ? g.ToString("N") : sourceId.ToLowerInvariant();

        private static void Prune()
        {
            var now = UtcNow();
            foreach (var kv in Grants)
            {
                if (kv.Value.ExpiresUtc <= now)
                {
                    Grants.TryRemove(kv.Key, out _);
                }
            }
        }

        /// <summary>One remembered grant.</summary>
        /// <param name="ServerId">The friend.</param>
        /// <param name="RemoteItemId">The friend's own item id.</param>
        /// <param name="Token">The item-scoped Playback token.</param>
        /// <param name="ExpiresUtc">When to stop using it.</param>
        /// <param name="RemoteMediaSourceId">The exact encoding on the friend, when the item has more than one.</param>
        public sealed record Grant(string ServerId, Guid RemoteItemId, string Token, DateTime ExpiresUtc, string? RemoteMediaSourceId = null);
    }
}
