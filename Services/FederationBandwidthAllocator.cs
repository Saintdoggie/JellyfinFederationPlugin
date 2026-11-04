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
        /// A device normally replaces its previous play when it retries or advances an episode.
        /// Measured capacity is an estimate, not an admission-control guarantee: active plays
        /// are rebalanced instead of refusing a new viewer merely because the estimate is full.
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
                var who = string.IsNullOrWhiteSpace(viewer) ? null : Normalize(viewer, 256);
                if (_reservations.TryGetValue(key, out var existing))
                {
                    existing.LastSeenUtc = now;
                    existing.RequestedMbps = Math.Min(existing.RequestedMbps, requestedCapMbps.Value);
                    Rebalance(server, sharedCapacityMbps.Value);
                    return new BandwidthAllocation(true, existing.Mbps);
                }

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

                _reservations[key] = new Reservation(server, requestedCapMbps.Value, now, who);
                Rebalance(server, sharedCapacityMbps.Value);
                return new BandwidthAllocation(true, _reservations[key].Mbps);
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

        /// <summary>
        /// Water-fills the requested caps without exceeding the estimate while there
        /// are fewer viewers than whole Mbps. One Mbps is the unavoidable floor when
        /// there are more viewers than Mbps; refusing them would repeat the 429 bug.
        /// </summary>
        private void Rebalance(string server, int sharedCapacityMbps)
        {
            var remaining = _reservations.Values.Where(r => r.Server == server).ToList();
            if (remaining.Count == 0)
            {
                return;
            }

            var available = sharedCapacityMbps;
            while (remaining.Count > 0)
            {
                var share = Math.Max(1, available / remaining.Count);
                var satisfied = remaining.Where(r => r.RequestedMbps <= share).ToList();
                if (satisfied.Count == 0)
                {
                    foreach (var reservation in remaining)
                    {
                        reservation.Mbps = Math.Min(reservation.RequestedMbps, share);
                    }

                    return;
                }

                foreach (var reservation in satisfied)
                {
                    reservation.Mbps = reservation.RequestedMbps;
                    available -= reservation.Mbps;
                    remaining.Remove(reservation);
                }
            }
        }

        private static string Normalize(string? value, int max)
        {
            var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
            return normalized.Length <= max ? normalized : normalized.Substring(0, max);
        }

        private sealed class Reservation
        {
            public Reservation(string server, int requestedMbps, DateTime seen, string? viewer = null)
            {
                Server = server;
                RequestedMbps = requestedMbps;
                Mbps = requestedMbps;
                LastSeenUtc = seen;
                Viewer = viewer;
            }

            public string Server { get; }

            public string? Viewer { get; }

            public int RequestedMbps { get; set; }

            public int Mbps { get; set; }

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
