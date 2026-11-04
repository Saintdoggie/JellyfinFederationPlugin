using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Mints and validates per-remote-user streaming session tokens - the second
    /// tier alongside the per-friend federation token (<see cref="FederationTokenAuth"/>).
    /// Only SHA-256 hashes are retained in memory and on disk. Persisting those
    /// hashes lets a friend keep using its cached token across an owner restart.
    /// </summary>
    public class FederationUserSessionTokenService : IDisposable
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(6);

        internal const int MaxTokensPerFriend = 200;

        private const int MaxStoredTokens = 20000;

        private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
        private readonly string? _storePath;
        private readonly Timer? _saveTimer;
        private readonly object _saveGate = new();
        private readonly ILogger<FederationUserSessionTokenService>? _logger;
        private int _saveScheduled;

        /// <summary>Initializes a service that keeps hashes in the plugin data folder.</summary>
        public FederationUserSessionTokenService(ILogger<FederationUserSessionTokenService>? logger = null)
            : this(DefaultStorePath(), logger)
        {
        }

        internal FederationUserSessionTokenService(string? storePath, ILogger<FederationUserSessionTokenService>? logger = null)
        {
            _storePath = storePath;
            _logger = logger;
            if (storePath != null)
            {
                Load(storePath);
                _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>Mints a token for one user on one friend, valid for six hours.</summary>
        public string Issue(string federationId, string remoteUserId)
        {
            Prune();

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

        /// <summary>Validates a live token and returns its bound friend and user.</summary>
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

        /// <summary>Flushes a token minted just before shutdown.</summary>
        public void Dispose()
        {
            _saveTimer?.Dispose();
            if (Volatile.Read(ref _saveScheduled) != 0)
            {
                Save();
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>Writes outstanding token hashes; internal for tests.</summary>
        internal void Save()
        {
            lock (_saveGate)
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    _logger?.LogWarning(ex, "[Federation] Could not persist viewer session tokens; affected friends may need fresh playback sessions after a restart");
                }
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
                foreach (var t in stored
                    .Where(t => !string.IsNullOrEmpty(t.Key)
                        && !string.IsNullOrWhiteSpace(t.FederationId)
                        && !string.IsNullOrWhiteSpace(t.RemoteUserId)
                        && t.Key.Length == 64
                        && t.FederationId.Length <= 256
                        && t.RemoteUserId.Length <= 256
                        && t.ExpiresUtc > now)
                    .OrderByDescending(t => t.ExpiresUtc)
                    .Take(MaxStoredTokens))
                {
                    _tokens[t.Key] = new Entry(t.FederationId, t.RemoteUserId, DateTime.SpecifyKind(t.ExpiresUtc, DateTimeKind.Utc));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _logger?.LogWarning(ex, "[Federation] Could not restore viewer session tokens; affected friends will register fresh sessions");
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
