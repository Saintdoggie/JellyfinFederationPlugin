using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Federation.Services
{
    public enum FederationTokenPurpose
    {
        Playback = 0,
        Download = 1,
        BulkDownload = 2,
        Image = 3
    }

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
        private static readonly TimeSpan PlaybackTokenLifetime = TimeSpan.FromHours(24);
        private static readonly TimeSpan DownloadTokenLifetime = TimeSpan.FromMinutes(15);
        internal static readonly TimeSpan OrdinaryDownloadWindow = TimeSpan.FromHours(1);
        internal const int OrdinaryDownloadLimit = 3;

        private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
        private readonly object _downloadGate = new();
        private readonly Dictionary<string, Dictionary<string, DateTime>> _ordinaryDownloads = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Mints a fresh token scoped to a single remote item id and the friend
        /// relationship it was minted through, valid for 24 hours. Binding the
        /// friendship id means the token dies with the relationship: previously an
        /// item token minted just before an unfriend kept working for up to 24
        /// hours afterwards, because nothing at stream time ever re-resolved which
        /// friend the minting call came from.
        /// </summary>
        /// <param name="remoteItemId">
        /// The remote item id this token authorizes streaming for. Compared against
        /// the same string format the caller uses everywhere else (e.g. <c>src.RemoteItemId:N</c>)
        /// so it is stored and matched consistently.
        /// </param>
        /// <param name="federationId">
        /// The calling friend's persistent federation id (<see cref="Configuration.RemoteServer.FederationId"/>),
        /// used to re-resolve which friend this token belongs to at stream time.
        /// </param>
        /// <returns>The newly minted token.</returns>
        public string Issue(string remoteItemId, string federationId)
            => Issue(remoteItemId, federationId, FederationTokenPurpose.Playback, null);

        public string Issue(
            string remoteItemId,
            string federationId,
            FederationTokenPurpose purpose,
            string? remoteUserId = null)
        {
            Prune();

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _tokens[token] = new Entry(remoteItemId, federationId, purpose, remoteUserId, DateTime.UtcNow + GetLifetime(purpose));
            return token;
        }

        /// <summary>
        /// Applies the source-owned safety limit for friends that have ordinary
        /// downloads enabled but not bulk downloads. The caller cannot evade it
        /// by splitting a library pull into repeated one-item requests: only
        /// three distinct items per rolling hour are authorized until the source
        /// admin explicitly enables bulk access. Re-authorizing the same item is
        /// allowed so an interrupted transfer can retry.
        /// </summary>
        public bool TryReserveOrdinaryDownload(
            string federationId,
            string remoteItemId,
            out TimeSpan retryAfter)
        {
            var now = DateTime.UtcNow;
            lock (_downloadGate)
            {
                if (!_ordinaryDownloads.TryGetValue(federationId, out var reservations))
                {
                    reservations = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    _ordinaryDownloads[federationId] = reservations;
                }

                foreach (var expired in reservations
                    .Where(pair => now - pair.Value >= OrdinaryDownloadWindow)
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    reservations.Remove(expired);
                }

                if (reservations.ContainsKey(remoteItemId))
                {
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                if (reservations.Count >= OrdinaryDownloadLimit)
                {
                    var oldest = reservations.Values.Min();
                    retryAfter = (oldest + OrdinaryDownloadWindow) - now;
                    return false;
                }

                reservations[remoteItemId] = now;
                retryAfter = TimeSpan.Zero;
                return true;
            }
        }

        public static TimeSpan GetLifetime(FederationTokenPurpose purpose)
            => purpose is FederationTokenPurpose.Download or FederationTokenPurpose.BulkDownload
                ? DownloadTokenLifetime
                : PlaybackTokenLifetime;

        /// <summary>
        /// DirectStream video/audio (and <c>download=true</c> file transfers).
        /// Image tokens are deliberately excluded so a poster URL cannot fetch
        /// the full media file.
        /// </summary>
        internal static bool AllowsDirectStream(FederationTokenPurpose purpose, bool download)
        {
            if (download)
            {
                return purpose == FederationTokenPurpose.Download || purpose == FederationTokenPurpose.BulkDownload;
            }

            return purpose == FederationTokenPurpose.Playback;
        }

        /// <summary>
        /// DirectImage / Peer/Images. Playback tokens are rejected so an
        /// <c>&lt;img&gt;</c> URL never reuses a capability that DirectStream
        /// would honor for the whole file.
        /// </summary>
        internal static bool AllowsDirectImage(FederationTokenPurpose purpose)
            => purpose == FederationTokenPurpose.Image;

        /// <summary>
        /// Validates a token against the remote item id it is being used for. True
        /// only when the token exists, has not expired, and was minted for exactly
        /// this item (case-insensitive, matching the hex-string convention item ids
        /// use elsewhere in this codebase). The minting friendship id is returned
        /// via <paramref name="federationId"/> - callers must additionally check
        /// that the friendship still exists (see the controller's
        /// <c>IsStreamTokenAuthorized</c>) so an unfriend revokes outstanding
        /// tokens immediately rather than at expiry.
        /// </summary>
        public bool TryValidate(string? token, string? remoteItemId, out string? federationId)
            => TryValidate(token, remoteItemId, out federationId, out _);

        public bool TryValidate(
            string? token,
            string? remoteItemId,
            out string? federationId,
            out FederationTokenPurpose purpose)
            => TryValidate(token, remoteItemId, out federationId, out purpose, out _);

        public bool TryValidate(
            string? token,
            string? remoteItemId,
            out string? federationId,
            out FederationTokenPurpose purpose,
            out string? remoteUserId)
        {
            federationId = null;
            purpose = FederationTokenPurpose.Playback;
            remoteUserId = null;
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

            federationId = entry.FederationId;
            purpose = entry.Purpose;
            remoteUserId = entry.RemoteUserId;
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

        private readonly record struct Entry(
            string RemoteItemId,
            string FederationId,
            FederationTokenPurpose Purpose,
            string? RemoteUserId,
            DateTime ExpiresUtc);
    }
}
