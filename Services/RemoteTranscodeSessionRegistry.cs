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

        private readonly ConcurrentDictionary<(string Friend, string Session), Entry> _sessions = new();

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>
        /// Records a gateway request for <paramref name="playSessionId"/>, unless it
        /// would start a new session beyond <paramref name="limit"/> (0 = none).
        /// Returns false when refused.
        /// </summary>
        public bool TryTouch(string friendFederationId, string playSessionId, string? deviceId, int limit)
        {
            Prune();
            var key = (friendFederationId, playSessionId.ToLowerInvariant());
            var now = UtcNow();
            if (_sessions.TryGetValue(key, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.DeviceId ??= deviceId;
                return true;
            }

            if (limit > 0 && ActiveCount(friendFederationId) >= limit)
            {
                return false;
            }

            _sessions[key] = new Entry { DeviceId = deviceId, LastSeenUtc = now };
            return true;
        }

        /// <summary>Marks a known session as alive (a pause ping). False when unknown.</summary>
        public bool TryPing(string friendFederationId, string playSessionId)
        {
            if (_sessions.TryGetValue((friendFederationId, playSessionId.ToLowerInvariant()), out var entry))
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
            if (_sessions.TryRemove((friendFederationId, playSessionId.ToLowerInvariant()), out var entry))
            {
                deviceId = entry.DeviceId;
                return true;
            }

            return false;
        }

        /// <summary>Sessions of this friend active in the last <see cref="ActiveWindow"/>.</summary>
        public int ActiveCount(string friendFederationId)
        {
            var since = UtcNow() - ActiveWindow;
            return _sessions.Count(kv => kv.Key.Friend == friendFederationId && kv.Value.LastSeenUtc >= since);
        }

        private void Prune()
        {
            var cutoff = UtcNow() - KnownWindow;
            foreach (var kv in _sessions)
            {
                if (kv.Value.LastSeenUtc < cutoff)
                {
                    _sessions.TryRemove(kv.Key, out _);
                }
            }
        }

        private sealed class Entry
        {
            public string? DeviceId { get; set; }

            public DateTime LastSeenUtc { get; set; }
        }
    }
}
