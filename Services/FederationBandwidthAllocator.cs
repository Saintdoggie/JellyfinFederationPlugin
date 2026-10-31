using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Shares one friend's measured capacity between active playback sessions.</summary>
    public sealed class FederationBandwidthAllocator
    {
        internal static readonly TimeSpan IdleFor = TimeSpan.FromSeconds(90);
        private const int MaxReservations = 512;
        private readonly object _sync = new();
        private readonly Dictionary<(string Server, string Session), Reservation> _reservations = new();

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>
        /// Books bandwidth from <paramref name="serverId"/>'s measured capacity for one play.
        /// <paramref name="viewer"/> (user and device) identifies who plays: a device plays
        /// one thing at a time, so its new play replaces its earlier reservation (the next
        /// episode, or jellyfin-web retrying with a new play session after a failed start).
        /// The capacity is an estimate, so a play is never refused for it: once it is used
        /// up, a new play gets an equal share instead.
        /// </summary>
        public BandwidthAllocation Allocate(string serverId, string? playSessionId, int? requestedCapMbps, int? sharedCapacityMbps, string? viewer = null)
        {
            if (string.IsNullOrWhiteSpace(playSessionId) || sharedCapacityMbps is not > 0 || requestedCapMbps is not > 0)
            {
                return new BandwidthAllocation(true, requestedCapMbps);
            }

            var server = Normalize(serverId, 128);
            var session = Normalize(playSessionId, 128);
            if (server.Length == 0 || session.Length == 0)
            {
                return new BandwidthAllocation(true, requestedCapMbps);
            }

            lock (_sync)
            {
                var now = UtcNow();
                Prune(now);
                var key = (server, session);
                if (_reservations.TryGetValue(key, out var existing))
                {
                    existing.LastSeenUtc = now;
                    return new BandwidthAllocation(true, Math.Min(requestedCapMbps.Value, existing.Mbps));
                }

                var who = string.IsNullOrWhiteSpace(viewer) ? null : Normalize(viewer, 256);
                if (who != null)
                {
                    foreach (var earlier in _reservations.Where(pair => pair.Key.Server == server && pair.Value.Viewer == who).Select(pair => pair.Key).ToArray())
                    {
                        _reservations.Remove(earlier);
                    }
                }

                if (_reservations.Count >= MaxReservations)
                {
                    return new BandwidthAllocation(false, null);
                }

                var others = _reservations.Values.Where(r => r.Server == server).ToList();
                var remaining = sharedCapacityMbps.Value - others.Sum(r => r.Mbps);
                var allocated = remaining >= 1
                    ? Math.Min(requestedCapMbps.Value, remaining)
                    : Math.Max(1, Math.Min(requestedCapMbps.Value, sharedCapacityMbps.Value / (others.Count + 1)));
                _reservations[key] = new Reservation(server, allocated, now, who);
                return new BandwidthAllocation(true, allocated);
            }
        }

        public void Release(string serverId, string? playSessionId)
        {
            if (string.IsNullOrWhiteSpace(playSessionId))
            {
                return;
            }

            lock (_sync)
            {
                _reservations.Remove((Normalize(serverId, 128), Normalize(playSessionId, 128)));
            }
        }

        public void ReleaseEverywhere(string? playSessionId)
        {
            if (string.IsNullOrWhiteSpace(playSessionId))
            {
                return;
            }

            var session = Normalize(playSessionId, 128);
            lock (_sync)
            {
                foreach (var key in _reservations.Keys.Where(key => key.Session == session).ToArray())
                {
                    _reservations.Remove(key);
                }
            }
        }

        public int ReservedMbps(string serverId)
        {
            lock (_sync)
            {
                Prune(UtcNow());
                var server = Normalize(serverId, 128);
                return _reservations.Values.Where(r => r.Server == server).Sum(r => r.Mbps);
            }
        }

        internal void Reset()
        {
            lock (_sync)
            {
                _reservations.Clear();
            }
        }

        private void Prune(DateTime now)
        {
            foreach (var pair in _reservations.Where(pair => now - pair.Value.LastSeenUtc > IdleFor).ToArray())
            {
                _reservations.Remove(pair.Key);
            }
        }

        private static string Normalize(string? value, int max)
        {
            var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
            return normalized.Length <= max ? normalized : normalized.Substring(0, max);
        }

        private sealed class Reservation
        {
            public Reservation(string server, int mbps, DateTime seen, string? viewer = null)
            {
                Server = server;
                Mbps = mbps;
                LastSeenUtc = seen;
                Viewer = viewer;
            }

            public string Server { get; }

            public string? Viewer { get; }
            public int Mbps { get; }
            public DateTime LastSeenUtc { get; set; }
        }
    }

    public sealed record BandwidthAllocation(bool Accepted, int? CapMbps);

    public sealed class FederationBandwidthExceededException : Exception
    {
        public FederationBandwidthExceededException()
            : base("The friend has no unallocated bandwidth for this playback.")
        {
        }
    }
}
