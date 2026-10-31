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

        public BandwidthAllocation Allocate(string serverId, string? playSessionId, int? requestedCapMbps, int? sharedCapacityMbps)
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

                if (_reservations.Count >= MaxReservations)
                {
                    return new BandwidthAllocation(false, null);
                }

                var used = _reservations.Values.Where(r => r.Server == server).Sum(r => r.Mbps);
                var remaining = Math.Max(0, sharedCapacityMbps.Value - used);
                if (remaining < 1)
                {
                    return new BandwidthAllocation(false, null);
                }

                var allocated = Math.Min(requestedCapMbps.Value, remaining);
                _reservations[key] = new Reservation(server, allocated, now);
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
            public Reservation(string server, int mbps, DateTime seen)
            {
                Server = server;
                Mbps = mbps;
                LastSeenUtc = seen;
            }

            public string Server { get; }
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
