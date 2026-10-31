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
    /// Same shape as <see cref="FederationPlaybackTokenService"/>: no revocation
    /// UI, opportunistic pruning on every call, and outstanding tokens survive a
    /// restart. Friends cache a session token for hours, and a direct play sends
    /// the viewer's player straight here with it, so nothing on the friend's side
    /// notices a refusal and fetches a new one: a restart that forgot them refused
    /// that friend's direct plays until its cache ran out. Only a SHA-256 of each
    /// token is kept, in memory and in <c>federation-session-tokens.json</c>.
    /// </para>
    /// </summary>
    public class FederationUserSessionTokenService : IDisposable
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(6);

        internal const int MaxTokensPerFriend = 200;

        private const int MaxStoredTokens = 20000;

        private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
        private readonly string? _storePath;
        private readonly Timer? _saveTimer;
        private int _saveScheduled;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationUserSessionTokenService"/> class
        /// that keeps its tokens in the plugin's data folder (none in tests).
        /// </summary>
        public FederationUserSessionTokenService()
            : this(DefaultStorePath())
        {
        }

        internal FederationUserSessionTokenService(string? storePath)
        {
            _storePath = storePath;
            if (storePath != null)
            {
                Load(storePath);
                _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Mints a session token for <paramref name="remoteUserId"/> on the friend
        /// identified by <paramref name="federationId"/>, valid for 6 hours.
        /// </summary>
        public string Issue(string federationId, string remoteUserId)
        {
            Prune();

            // A friend that keeps registering without playing would otherwise grow this
            // store without limit for six hours; past the cap its oldest tokens go first.
            var mine = _tokens.Where(kvp => string.Equals(kvp.Value.FederationId, federationId, StringComparison.Ordinal))
                .OrderBy(kvp => kvp.Value.ExpiresUtc)
                .ToList();
            foreach (var stale in mine.Take(Math.Max(0, mine.Count - MaxTokensPerFriend + 1)))
            {
                _tokens.TryRemove(stale.Key, out _);
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _tokens[Key(token)] = new Entry(federationId, remoteUserId, DateTime.UtcNow + TokenLifetime);
            ScheduleSave();
            return token;
        }

        /// <summary>
        /// Whether <paramref name="token"/> is a live session token; if so, which
        /// friend and which of its users it was minted for.
        /// </summary>
        public bool TryValidate(string? token, out string? federationId, out string? remoteUserId)
        {
            federationId = null;
            remoteUserId = null;
            Prune();

            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(Key(token), out var entry))
            {
                return false;
            }

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                _tokens.TryRemove(Key(token), out _);
                return false;
            }

            federationId = entry.FederationId;
            remoteUserId = entry.RemoteUserId;
            return true;
        }

        /// <summary>
        /// Writes a pending save at shutdown: a token minted in the last moments
        /// before a restart would otherwise be lost while the friend keeps using
        /// it, and every direct play it starts is refused until the friend's
        /// cached copy runs out.
        /// </summary>
        public void Dispose()
        {
            _saveTimer?.Dispose();
            if (Volatile.Read(ref _saveScheduled) != 0)
            {
                Save();
            }

            GC.SuppressFinalize(this);
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
                        FederationId = kv.Value.FederationId,
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

        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var key in _tokens.Where(kvp => kvp.Value.ExpiresUtc <= now).Select(kvp => kvp.Key).ToList())
            {
                _tokens.TryRemove(key, out _);
            }
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
                    _tokens[t.Key] = new Entry(t.FederationId, t.RemoteUserId, DateTime.SpecifyKind(t.ExpiresUtc, DateTimeKind.Utc));
                }
            }
            catch (Exception)
            {
                // A damaged file only means friends register again, as after any
                // expiry; never a reason to fail startup.
            }
        }

        private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        private static string? DefaultStorePath()
        {
            var dir = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "federation-session-tokens.json");
        }

        private readonly record struct Entry(string FederationId, string RemoteUserId, DateTime ExpiresUtc);

        private sealed class StoredToken
        {
            public string Key { get; set; } = string.Empty;

            public string FederationId { get; set; } = string.Empty;

            public string RemoteUserId { get; set; } = string.Empty;

            public DateTime ExpiresUtc { get; set; }
        }
    }
}
