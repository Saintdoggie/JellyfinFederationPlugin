using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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

        private const int MaxOthers = 4;

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
                    if (existing.ServerId == serverId)
                    {
                        return existing;
                    }

                    // The play moved: the server it left may still be converting,
                    // so it stays on the list the stop is sent to.
                    var moved = new Handoff(serverId) { LastSeenUtc = now };
                    lock (existing)
                    {
                        moved.Others.UnionWith(existing.Others);
                    }

                    moved.Others.Add(existing.ServerId);
                    moved.Others.Remove(serverId);
                    return moved;
                });
        }

        /// <summary>
        /// Notes a second friend offered for this play (see <see cref="HandoffStandby"/>).
        /// It gets the stop too, and may become the converting server through
        /// <see cref="Activate"/>. Ignored for a session that is not handed off.
        /// </summary>
        public void AddStandby(string? playSessionId, string serverId)
        {
            if (string.IsNullOrWhiteSpace(playSessionId) || string.IsNullOrWhiteSpace(serverId)
                || !_sessions.TryGetValue(playSessionId, out var handoff))
            {
                return;
            }

            lock (handoff)
            {
                if (!string.Equals(handoff.ServerId, serverId, StringComparison.OrdinalIgnoreCase) && handoff.Others.Count < MaxOthers)
                {
                    handoff.Others.Add(serverId);
                }
            }
        }

        /// <summary>
        /// The player asked a standby for its playlist: that friend is now the one
        /// pause pings go to. True only when <paramref name="serverId"/> was offered
        /// for this session and was not already the current server, so a made-up
        /// session or server changes nothing.
        /// </summary>
        public bool Activate(string? playSessionId, string serverId)
        {
            if (string.IsNullOrWhiteSpace(playSessionId) || !_sessions.TryGetValue(playSessionId, out var handoff))
            {
                return false;
            }

            lock (handoff)
            {
                if (!handoff.Others.Contains(serverId))
                {
                    return false;
                }
            }

            Record(playSessionId, serverId);
            return true;
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

        /// <summary>
        /// Forgets the session and returns every friend that may be running it:
        /// the current one first, then any it left or that stood by.
        /// </summary>
        public IReadOnlyList<string> TakeStopAll(string? playSessionId)
        {
            if (string.IsNullOrWhiteSpace(playSessionId) || !_sessions.TryRemove(playSessionId, out var handoff))
            {
                return Array.Empty<string>();
            }

            lock (handoff)
            {
                var all = new List<string>(1 + handoff.Others.Count) { handoff.ServerId };
                all.AddRange(handoff.Others);
                return all;
            }
        }

        private sealed class Handoff
        {
            public Handoff(string serverId) => ServerId = serverId;

            public HashSet<string> Others { get; } = new(StringComparer.OrdinalIgnoreCase);

            public string ServerId { get; }

            public DateTime LastSeenUtc { get; set; }

            public DateTime LastPingUtc { get; set; }

            public bool? LastPaused { get; set; }
        }
    }
}
