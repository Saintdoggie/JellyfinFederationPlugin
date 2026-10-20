using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Receiver side: playback sessions whose conversion this server handed to a
    /// friend, so the viewer's pause/stop (which arrive here, where they are signed
    /// in) can be relayed to the friend that is actually running the job.
    /// </summary>
    public sealed class RemoteTranscodeHandoffs
    {
        /// <summary>Minimum spacing of relayed pings per session.</summary>
        internal static readonly TimeSpan PingSpacing = TimeSpan.FromSeconds(20);

        private static readonly TimeSpan Forget = TimeSpan.FromHours(12);

        private readonly ConcurrentDictionary<string, Handoff> _sessions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Records that <paramref name="playSessionId"/> is converted by <paramref name="serverId"/>.</summary>
        public void Record(string? playSessionId, string serverId)
        {
            if (string.IsNullOrWhiteSpace(playSessionId))
            {
                return;
            }

            var now = UtcNow();
            foreach (var kv in _sessions)
            {
                if (now - kv.Value.LastSeenUtc > Forget)
                {
                    _sessions.TryRemove(kv.Key, out _);
                }
            }

            _sessions.AddOrUpdate(
                playSessionId,
                _ => new Handoff(serverId) { LastSeenUtc = now },
                (_, existing) =>
                {
                    existing.LastSeenUtc = now;
                    return existing.ServerId == serverId ? existing : new Handoff(serverId) { LastSeenUtc = now };
                });
        }

        /// <summary>
        /// The friend to ping for this session now, or null when the session isn't
        /// handed off or was pinged less than <see cref="PingSpacing"/> ago
        /// (a pause/resume change is always relayed).
        /// </summary>
        public string? TakePing(string? playSessionId, bool isPaused)
        {
            if (string.IsNullOrWhiteSpace(playSessionId) || !_sessions.TryGetValue(playSessionId, out var handoff))
            {
                return null;
            }

            var now = UtcNow();
            lock (handoff)
            {
                if (handoff.LastPaused == isPaused && now - handoff.LastPingUtc < PingSpacing)
                {
                    return null;
                }

                handoff.LastPingUtc = now;
                handoff.LastPaused = isPaused;
                handoff.LastSeenUtc = now;
                return handoff.ServerId;
            }
        }

        /// <summary>The friend converting <paramref name="playSessionId"/>, or null when it isn't handed off.</summary>
        public string? HandedOffTo(string? playSessionId)
            => !string.IsNullOrWhiteSpace(playSessionId) && _sessions.TryGetValue(playSessionId, out var handoff) ? handoff.ServerId : null;

        /// <summary>Forgets the session and returns the friend running it, or null.</summary>
        public string? TakeStop(string? playSessionId)
            => !string.IsNullOrWhiteSpace(playSessionId) && _sessions.TryRemove(playSessionId, out var handoff) ? handoff.ServerId : null;

        private sealed class Handoff
        {
            public Handoff(string serverId) => ServerId = serverId;

            public string ServerId { get; }

            public DateTime LastSeenUtc { get; set; }

            public DateTime LastPingUtc { get; set; }

            public bool? LastPaused { get; set; }
        }
    }
}
