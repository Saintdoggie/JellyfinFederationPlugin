using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Bounded, expiring measurements for one viewer device's actual route to a friend.</summary>
    public sealed class PlaybackRouteMetrics
    {
        internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan StallPenaltyFor = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TelemetrySpacing = TimeSpan.FromSeconds(2);
        private const int MaxRoutes = 512;

        private readonly ConcurrentDictionary<RouteKey, Observation> _routes = new();

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public bool AcceptTelemetry(Guid userId, string? deviceId, string serverId, string route)
        {
            var key = Key(userId, deviceId, serverId, route);
            var now = UtcNow();
            var observation = _routes.GetOrAdd(key, _ => new Observation());
            lock (observation)
            {
                if (now - observation.LastTelemetryUtc < TelemetrySpacing)
                {
                    return false;
                }

                observation.LastTelemetryUtc = now;
                observation.UpdatedUtc = now;
                Prune(now);
                return true;
            }
        }

        public void ObserveTransfer(Guid userId, string? deviceId, string serverId, string route, long bytes, double durationMs)
        {
            if (bytes < 65_536 || bytes > 1_000_000_000 || !double.IsFinite(durationMs) || durationMs < 250 || durationMs > 300_000)
            {
                return;
            }

            var mbps = Math.Clamp(bytes * 8.0 / (durationMs / 1000) / 1_000_000, 0.01, 1000);
            var observation = _routes.GetOrAdd(Key(userId, deviceId, serverId, route), _ => new Observation());
            lock (observation)
            {
                var now = UtcNow();
                observation.Mbps = observation.Mbps.HasValue && now - observation.MeasuredUtc <= FreshFor
                    ? observation.Mbps.Value * 0.6 + mbps * 0.4
                    : mbps;
                observation.MeasuredUtc = now;
                observation.UpdatedUtc = now;
            }
        }

        public void ObserveHealth(Guid userId, string? deviceId, string serverId, string route, bool stalled, double? bufferedSeconds, long? droppedFrames, long? totalFrames)
        {
            if (bufferedSeconds is < 0 or > 86_400 || droppedFrames is < 0 or > 1_000_000_000 || totalFrames is < 0 or > 1_000_000_000)
            {
                return;
            }

            var observation = _routes.GetOrAdd(Key(userId, deviceId, serverId, route), _ => new Observation());
            lock (observation)
            {
                var now = UtcNow();
                var excessiveDrops = droppedFrames > 0 && totalFrames > 0 && droppedFrames.Value / (double)totalFrames.Value > 0.05;
                if (stalled || excessiveDrops)
                {
                    observation.LastStallUtc = now;
                }
                else if (bufferedSeconds >= 2)
                {
                    observation.LastHealthyUtc = now;
                }

                observation.BufferedSeconds = bufferedSeconds;
                observation.DroppedFrames = droppedFrames;
                observation.TotalFrames = totalFrames;
                observation.UpdatedUtc = now;
            }
        }

        public int? GetCapMbps(Guid userId, string? deviceId, string serverId, string route, int? fallbackMbps)
        {
            var snapshot = Snapshot(userId, deviceId, serverId, route);
            if (snapshot?.MeasuredMbps is not > 0)
            {
                return fallbackMbps;
            }

            var candidate = snapshot.MeasuredMbps.Value * 0.75;
            if (snapshot.RecentlyStalled)
            {
                candidate *= 0.5;
            }

            var cap = Math.Max(1, (int)Math.Floor(candidate));
            return fallbackMbps is > 0 ? Math.Min(fallbackMbps.Value, cap) : cap;
        }

        public PlaybackRouteSnapshot? Snapshot(Guid userId, string? deviceId, string serverId, string route)
        {
            if (!_routes.TryGetValue(Key(userId, deviceId, serverId, route), out var observation))
            {
                return null;
            }

            lock (observation)
            {
                var now = UtcNow();
                if (now - observation.UpdatedUtc > FreshFor)
                {
                    return null;
                }

                return new PlaybackRouteSnapshot(
                    now - observation.MeasuredUtc <= FreshFor ? observation.Mbps : null,
                    observation.MeasuredUtc == default ? null : (int)Math.Max(0, (now - observation.MeasuredUtc).TotalSeconds),
                    observation.BufferedSeconds,
                    observation.DroppedFrames,
                    observation.TotalFrames,
                    now - observation.LastStallUtc <= StallPenaltyFor,
                    now - observation.LastHealthyUtc <= TimeSpan.FromMinutes(1));
            }
        }

        internal void Reset() => _routes.Clear();

        private static RouteKey Key(Guid userId, string? deviceId, string serverId, string route)
            => new(userId, Normalize(deviceId, 128), Normalize(serverId, 128), Normalize(route, 32));

        private static string Normalize(string? value, int max)
        {
            var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
            return normalized.Length <= max ? normalized : normalized.Substring(0, max);
        }

        private void Prune(DateTime now)
        {
            foreach (var pair in _routes)
            {
                if (now - pair.Value.UpdatedUtc > FreshFor || _routes.Count > MaxRoutes)
                {
                    _routes.TryRemove(pair.Key, out _);
                }
            }
        }

        private sealed record RouteKey(Guid UserId, string DeviceId, string ServerId, string Route);

        private sealed class Observation
        {
            public double? Mbps { get; set; }
            public DateTime MeasuredUtc { get; set; }
            public DateTime LastStallUtc { get; set; }
            public DateTime LastHealthyUtc { get; set; }
            public DateTime LastTelemetryUtc { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public double? BufferedSeconds { get; set; }
            public long? DroppedFrames { get; set; }
            public long? TotalFrames { get; set; }
        }
    }

    public sealed record PlaybackRouteSnapshot(
        double? MeasuredMbps,
        int? MeasurementAgeSeconds,
        double? BufferedSeconds,
        long? DroppedFrames,
        long? TotalFrames,
        bool RecentlyStalled,
        bool RecentlyHealthy);
}
