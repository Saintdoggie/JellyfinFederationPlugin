using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Mints and validates per-remote-user streaming session tokens - the second
    /// tier alongside the per-friend federation token (<see cref="FederationTokenAuth"/>).
    /// The federation token proves "this request is from friend X" and is used for
    /// admin-ish/browsing calls (Friends/*, Peer/Libraries, Peer/Items,
    /// Peer/PlaybackInfo); it is never accepted for streaming. A friend's own
    /// RemoteServerClient instead registers one of its own local users the moment
    /// that user actually starts playing something (see <c>RegisterUserSession</c>),
    /// and streams from then on using the resulting session token - so a per-remote-user
    /// <see cref="RemoteUserAccessRule"/> is enforced against a token this server
    /// itself minted for a specific, named user, not a self-reported header riding
    /// on the shared friend-wide token.
    /// <para>
    /// Deliberately simple, same shape as <see cref="FederationPlaybackTokenService"/>:
    /// purely in-memory, no revocation UI, opportunistic pruning on every call. A
    /// server restart clearing every outstanding session is fine - the next play
    /// just re-registers.
    /// </para>
    /// </summary>
    public class FederationUserSessionTokenService
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(6);

        private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

        /// <summary>
        /// Mints a fresh session token scoped to one of a friend's own local
        /// users, valid for 6 hours - long enough to cover a normal viewing
        /// session without needing to re-register on every single play, short
        /// enough that a stale session doesn't linger indefinitely.
        /// </summary>
        /// <param name="federationId">The calling friend's persistent federation id (<see cref="Configuration.RemoteServer.FederationId"/>), used to re-resolve which friend this session belongs to at stream time.</param>
        /// <param name="remoteUserId">The friend's own local user id this session is scoped to.</param>
        public string Issue(string federationId, string remoteUserId)
        {
            Prune();

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _tokens[token] = new Entry(federationId, remoteUserId, DateTime.UtcNow + TokenLifetime);
            return token;
        }

        /// <summary>
        /// Validates a session token, returning the friend/user it was issued for
        /// when valid and not expired. Callers must still re-check
        /// <see cref="FederationPeerAccessService.IsItemVisible(Configuration.RemoteServer, string?, Guid)"/>
        /// per item at stream time - registration only confirms the user wasn't
        /// blocked outright at the moment they started playing, not that every
        /// item they might request with this token stays visible for its whole
        /// lifetime.
        /// </summary>
        public bool TryValidate(string? token, out string? federationId, out string? remoteUserId)
        {
            federationId = null;
            remoteUserId = null;
            Prune();

            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry))
            {
                return false;
            }

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                _tokens.TryRemove(token, out _);
                return false;
            }

            federationId = entry.FederationId;
            remoteUserId = entry.RemoteUserId;
            return true;
        }

        /// <summary>
        /// Opportunistically drops expired entries. See <see cref="FederationPlaybackTokenService.Prune"/>
        /// for the same reasoning - this store is bounded by normal usage volume,
        /// so a timer would be unnecessary overhead.
        /// </summary>
        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var key in _tokens.Where(kvp => kvp.Value.ExpiresUtc <= now).Select(kvp => kvp.Key).ToList())
            {
                _tokens.TryRemove(key, out _);
            }
        }

        private readonly record struct Entry(string FederationId, string RemoteUserId, DateTime ExpiresUtc);
    }
}
