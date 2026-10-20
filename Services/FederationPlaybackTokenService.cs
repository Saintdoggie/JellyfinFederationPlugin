using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

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
    /// Deliberately simple: no revocation UI, no admin visibility, and no
    /// background sweep timer - each call opportunistically prunes anything
    /// already expired. A token just naturally expires, or gets superseded by a
    /// fresh mint the next time the client calls PlaybackInfo.
    /// </para>
    /// <para>
    /// Outstanding tokens survive a restart: friends cache them, and a restart
    /// that forgot them refused every play in progress or about to start until
    /// the friend's cache ran out. Only a SHA-256 of each token is kept, in
    /// memory and in <c>federation-playback-tokens.json</c>, so the file holds
    /// nothing that could be used as a token.
    /// </para>
    /// </summary>
    public class FederationPlaybackTokenService
    {
        private static readonly TimeSpan PlaybackTokenLifetime = TimeSpan.FromHours(24);
        private static readonly TimeSpan DownloadTokenLifetime = TimeSpan.FromMinutes(15);

        // A viewer saving a title to their own device: the transfer can take
        // hours and the app resumes it with fresh range requests on the same
        // URL. Every request still re-checks friendship, sharing and download
        // permission, so the longer life only covers the resume window.
        private static readonly TimeSpan ViewerDownloadTokenLifetime = TimeSpan.FromHours(12);
        internal static readonly TimeSpan OrdinaryDownloadWindow = TimeSpan.FromHours(1);
        internal const int OrdinaryDownloadLimit = 3;

        // Viewer downloads are personal (an episode or a season to a phone), so
        // they get their own allowance instead of the friend-wide three items
        // an hour meant for a friend's server copying titles. The friend-wide
        // cap keeps a friend server from using made-up viewers as a bulk pull.
        internal const int ViewerDownloadLimit = 30;
        internal const int ViewerDownloadFriendLimit = 120;

        private const int MaxStoredTokens = 20000;

        private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
        private readonly string? _storePath;
        private readonly Timer? _saveTimer;
        private int _saveScheduled;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationPlaybackTokenService"/> class
        /// that keeps its tokens in the plugin's data folder (none in tests).
        /// </summary>
        public FederationPlaybackTokenService()
            : this(DefaultStorePath())
        {
        }

        internal FederationPlaybackTokenService(string? storePath)
        {
            _storePath = storePath;
            if (storePath != null)
            {
                Load(storePath);
                _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }
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
            _tokens[Key(token)] = new Entry(remoteItemId, federationId, purpose, remoteUserId, DateTime.UtcNow + GetLifetime(purpose, !string.IsNullOrWhiteSpace(remoteUserId)));
            ScheduleSave();
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
            => TryReserve(federationId, remoteItemId, OrdinaryDownloadLimit, out retryAfter);

        /// <summary>
        /// The allowance for one viewer on a friend's server saving titles to
        /// their own device, when the friend has ordinary but not bulk
        /// downloads: <see cref="ViewerDownloadLimit"/> distinct items per
        /// viewer per rolling hour, and <see cref="ViewerDownloadFriendLimit"/>
        /// across all of that friend's viewers. Re-authorizing an item already
        /// counted (a resumed transfer) is always allowed.
        /// </summary>
        public bool TryReserveViewerDownload(
            string federationId,
            string remoteUserId,
            string remoteItemId,
            out TimeSpan retryAfter)
        {
            lock (_downloadGate)
            {
                var viewerBucket = federationId + "|viewer|" + remoteUserId.Trim().ToLowerInvariant();
                var friendBucket = federationId + "|viewers";
                var itemKey = remoteUserId.Trim().ToLowerInvariant() + "|" + remoteItemId;
                if (!TryReserve(viewerBucket, remoteItemId, ViewerDownloadLimit, out retryAfter, commit: false)
                    || !TryReserve(friendBucket, itemKey, ViewerDownloadFriendLimit, out retryAfter, commit: false))
                {
                    return false;
                }

                TryReserve(viewerBucket, remoteItemId, ViewerDownloadLimit, out _);
                TryReserve(friendBucket, itemKey, ViewerDownloadFriendLimit, out _);
                retryAfter = TimeSpan.Zero;
                return true;
            }
        }

        private bool TryReserve(string bucket, string remoteItemId, int limit, out TimeSpan retryAfter, bool commit = true)
        {
            var now = DateTime.UtcNow;
            lock (_downloadGate)
            {
                if (!_ordinaryDownloads.TryGetValue(bucket, out var reservations))
                {
                    reservations = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    _ordinaryDownloads[bucket] = reservations;
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

                if (reservations.Count >= limit)
                {
                    var oldest = reservations.Values.Min();
                    retryAfter = (oldest + OrdinaryDownloadWindow) - now;
                    return false;
                }

                if (commit)
                {
                    reservations[remoteItemId] = now;
                }

                retryAfter = TimeSpan.Zero;
                return true;
            }
        }

        public static TimeSpan GetLifetime(FederationTokenPurpose purpose)
            => GetLifetime(purpose, viewerBound: false);

        /// <summary>
        /// Token lifetime by purpose. An ordinary Download token minted for a
        /// named viewer (a device download) lives long enough for the app to
        /// resume an interrupted transfer; a server copy keeps the short life.
        /// </summary>
        public static TimeSpan GetLifetime(FederationTokenPurpose purpose, bool viewerBound)
            => purpose switch
            {
                FederationTokenPurpose.Download when viewerBound => ViewerDownloadTokenLifetime,
                FederationTokenPurpose.Download or FederationTokenPurpose.BulkDownload => DownloadTokenLifetime,
                _ => PlaybackTokenLifetime
            };

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

            var key = Key(token);
            if (!_tokens.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                _tokens.TryRemove(key, out _);
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

        private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        private static string? DefaultStorePath()
        {
            var dir = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "federation-playback-tokens.json");
        }

        private void ScheduleSave()
        {
            if (_saveTimer != null && Interlocked.Exchange(ref _saveScheduled, 1) == 0)
            {
                _saveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
        }

        private void Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var stored = JsonSerializer.Deserialize<List<StoredToken>>(File.ReadAllText(path)) ?? new List<StoredToken>();
                foreach (var t in stored.Where(t => !string.IsNullOrEmpty(t.Key) && t.ExpiresUtc > now))
                {
                    _tokens[t.Key] = new Entry(t.RemoteItemId, t.FederationId, t.Purpose, t.RemoteUserId, DateTime.SpecifyKind(t.ExpiresUtc, DateTimeKind.Utc));
                }
            }
            catch (Exception)
            {
                // A damaged file only means friends mint new tokens, as after
                // any expiry; never a reason to fail startup.
            }
        }

        /// <summary>Writes the outstanding tokens' hashes; internal for tests.</summary>
        internal void Save()
        {
            Interlocked.Exchange(ref _saveScheduled, 0);
            if (_storePath == null)
            {
                return;
            }

            try
            {
                Prune();
                var list = _tokens
                    .OrderByDescending(kv => kv.Value.ExpiresUtc)
                    .Take(MaxStoredTokens)
                    .Select(kv => new StoredToken
                    {
                        Key = kv.Key,
                        RemoteItemId = kv.Value.RemoteItemId,
                        FederationId = kv.Value.FederationId,
                        Purpose = kv.Value.Purpose,
                        RemoteUserId = kv.Value.RemoteUserId,
                        ExpiresUtc = kv.Value.ExpiresUtc
                    })
                    .ToList();
                var temp = _storePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(list));
                File.Move(temp, _storePath, overwrite: true);
            }
            catch (Exception)
            {
                // Best effort, see Load.
            }
        }

        private sealed class StoredToken
        {
            public string Key { get; set; } = string.Empty;

            public string RemoteItemId { get; set; } = string.Empty;

            public string FederationId { get; set; } = string.Empty;

            public FederationTokenPurpose Purpose { get; set; }

            public string? RemoteUserId { get; set; }

            public DateTime ExpiresUtc { get; set; }
        }

        private readonly record struct Entry(
            string RemoteItemId,
            string FederationId,
            FederationTokenPurpose Purpose,
            string? RemoteUserId,
            DateTime ExpiresUtc);
    }
}
