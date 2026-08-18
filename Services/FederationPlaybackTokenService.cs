using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Mints and validates short-lived, single-item-scoped playback tokens used by
    /// Direct-mode federated streaming (see <see cref="FederationMediaSourceProvider"/>
    /// and <c>FederationController.DirectStream</c>). Replaces embedding a friend
    /// server's real, long-lived API key directly in a stream URL handed to a browser
    /// client: any logged-in user on the receiving server could otherwise read that
    /// key straight out of dev tools/network tab and use it directly against the
    /// friend's server, far beyond what a single stream should have granted them.
    /// <para>
    /// Deliberately simple: purely in-memory, no revocation UI, no admin visibility,
    /// and no background sweep timer - each call opportunistically prunes anything
    /// already expired. A token just naturally expires, or gets superseded by a fresh
    /// mint the next time the client calls PlaybackInfo. A server restart clearing
    /// every outstanding token is fine for the same reason.
    /// </para>
    /// </summary>
    public class FederationPlaybackTokenService
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

        private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

        /// <summary>
        /// Mints a fresh token scoped to a single remote item id, valid for 24 hours.
        /// </summary>
        /// <param name="remoteItemId">
        /// The remote item id this token authorizes streaming for. Compared against
        /// the same string format the caller uses everywhere else (e.g. <c>src.RemoteItemId:N</c>)
        /// so it is stored and matched consistently.
        /// </param>
        /// <returns>The newly minted token.</returns>
        public string Issue(string remoteItemId)
        {
            Prune();

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _tokens[token] = new Entry(remoteItemId, DateTime.UtcNow + TokenLifetime);
            return token;
        }

        /// <summary>
        /// Validates a token against the remote item id it is being used for. True
        /// only when the token exists, has not expired, and was minted for exactly
        /// this item (case-insensitive, matching the hex-string convention item ids
        /// use elsewhere in this codebase).
        /// </summary>
        public bool TryValidate(string? token, string? remoteItemId)
        {
            Prune();

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(remoteItemId))
            {
                return false;
            }

            if (!_tokens.TryGetValue(token, out var entry))
            {
                return false;
            }

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                _tokens.TryRemove(token, out _);
                return false;
            }

            return string.Equals(entry.RemoteItemId, remoteItemId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Opportunistically drops expired entries. Called on every Issue/TryValidate
        /// rather than on a timer - deliberately simple, since this store is already
        /// bounded by normal usage volume and a server restart clears it anyway.
        /// </summary>
        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var key in _tokens.Where(kvp => kvp.Value.ExpiresUtc <= now).Select(kvp => kvp.Key).ToList())
            {
                _tokens.TryRemove(key, out _);
            }
        }

        private readonly record struct Entry(string RemoteItemId, DateTime ExpiresUtc);
    }
}
