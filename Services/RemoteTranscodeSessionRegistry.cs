using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Owner side of the remote-transcode handoff: which playback sessions each
    /// friend is converting here right now. It enforces
    /// <see cref="Configuration.PluginConfiguration.MaxRemoteTranscodesPerFriend"/>
    /// and limits the friend's ping/stop control calls to sessions that friend
    /// actually started through the gateway (a guessed session id of someone else
    /// is ignored).
    /// </summary>
    public sealed class RemoteTranscodeSessionRegistry
    {
        /// <summary>How long a session counts as active after its last request or ping.</summary>
        internal static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(90);

        /// <summary>How long a session stays known for control calls (a long pause).</summary>
        internal static readonly TimeSpan KnownWindow = TimeSpan.FromHours(12);

        /// <summary>How long a stopped session stays known, so its final log can be fetched.</summary>
        internal static readonly TimeSpan EndedWindow = TimeSpan.FromMinutes(30);

        private readonly ConcurrentDictionary<(string Friend, string Session), Entry> _sessions = new();

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>
        /// Records a gateway request for <paramref name="playSessionId"/>, unless it
        /// would start a new session beyond <paramref name="limit"/> (0 = none).
        /// Returns false when refused.
        /// </summary>
        public bool TryTouch(string friendFederationId, string playSessionId, string? deviceId, int limit, Guid itemId = default)
        {
            Prune();
            var key = (friendFederationId, playSessionId.ToLowerInvariant());
            var now = UtcNow();
            if (_sessions.TryGetValue(key, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.DeviceId ??= deviceId;
                existing.Ended = false;
                if (existing.ItemId == Guid.Empty)
                {
                    existing.ItemId = itemId;
                }

                return true;
            }

            if (limit > 0 && ActiveCount(friendFederationId) >= limit)
            {
                return false;
            }

            _sessions[key] = new Entry { DeviceId = deviceId, LastSeenUtc = now, StartedUtc = now, ItemId = itemId };
            return true;
        }

        /// <summary>
        /// The title and start time of a session this friend started here (also
        /// for a while after it stopped), for finding its conversion log. False
        /// when this friend never started it.
        /// </summary>
        public bool TryGetForLog(string friendFederationId, string playSessionId, out Guid itemId, out DateTime startedUtc)
        {
            itemId = Guid.Empty;
            startedUtc = default;
            if (!_sessions.TryGetValue((friendFederationId, playSessionId.ToLowerInvariant()), out var entry) || entry.ItemId == Guid.Empty)
            {
                return false;
            }

            itemId = entry.ItemId;
            startedUtc = entry.StartedUtc;
            return true;
        }

        /// <summary>Marks a known session as alive (a pause ping). False when unknown.</summary>
        public bool TryPing(string friendFederationId, string playSessionId)
        {
            if (_sessions.TryGetValue((friendFederationId, playSessionId.ToLowerInvariant()), out var entry) && !entry.Ended)
            {
                entry.LastSeenUtc = UtcNow();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Forgets a session the friend stopped and returns the device id it used
        /// (needed to kill its job). False when this friend never started it.
        /// </summary>
        public bool TryEnd(string friendFederationId, string playSessionId, out string? deviceId)
        {
            deviceId = null;
            // Kept (marked ended) so the friend can still fetch the finished
            // conversion log; it no longer counts toward the limit.
            if (_sessions.TryGetValue((friendFederationId, playSessionId.ToLowerInvariant()), out var entry) && !entry.Ended)
            {
                entry.Ended = true;
                entry.LastSeenUtc = UtcNow();
                deviceId = entry.DeviceId;
                return true;
            }

            return false;
        }

        /// <summary>Sessions of this friend active in the last <see cref="ActiveWindow"/>.</summary>
        public int ActiveCount(string friendFederationId)
        {
            var since = UtcNow() - ActiveWindow;
            return _sessions.Count(kv => kv.Key.Friend == friendFederationId && !kv.Value.Ended && kv.Value.LastSeenUtc >= since);
        }

        private void Prune()
        {
            var now = UtcNow();
            foreach (var kv in _sessions)
            {
                if (kv.Value.LastSeenUtc < now - (kv.Value.Ended ? EndedWindow : KnownWindow))
                {
                    _sessions.TryRemove(kv.Key, out _);
                }
            }
        }

        private sealed class Entry
        {
            public string? DeviceId { get; set; }

            public DateTime LastSeenUtc { get; set; }

            public DateTime StartedUtc { get; set; }

            public Guid ItemId { get; set; }

            public bool Ended { get; set; }
        }
    }
}
