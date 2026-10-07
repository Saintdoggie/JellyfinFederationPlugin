using System;
using System.Collections.Concurrent;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Shared passive observations. Scoring performs no network requests and stores no credentials.</summary>
public sealed class AdaptiveSourceRanking
{
    private readonly ConcurrentDictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    public AdaptiveSourceRanking() : this(TimeProvider.System) { }
    internal AdaptiveSourceRanking(TimeProvider clock) => _clock = clock;

    public void ObserveProbe(string serverId, bool success, double? latencyMs = null)
    {
        var observation = _observations.GetOrAdd(serverId, _ => new Observation());
        lock (observation)
        {
            var now = _clock.GetUtcNow();
            var old = Fresh(observation.HealthAt, now) ? observation.Reliability : 0.85;
            observation.Reliability = old * 0.75 + (success ? 0.25 : 0);
            observation.HealthAt = now;
            if (success && latencyMs is >= 0 && double.IsFinite(latencyMs.Value))
            {
                observation.LatencyMs = Math.Min(latencyMs.Value, 10_000);
            }
        }
    }

    // Count at most one relay stall per server per 10 seconds. Many viewers
    // experiencing the same outage must not multiply the penalty indefinitely.
    public void ObserveStall(string serverId)
    {
        var observation = _observations.GetOrAdd(serverId, _ => new Observation());
        lock (observation)
        {
            var now = _clock.GetUtcNow();
            if (now - observation.StallAt < TimeSpan.FromSeconds(10)) return;
            observation.StallAt = now;
            observation.Reliability = (Fresh(observation.HealthAt, now) ? observation.Reliability : 0.85) * 0.65;
            observation.HealthAt = now;
        }
    }

    // Active upstream read time excludes client writes and our artificial cap.
    // Tiny cache bursts are not evidence of sustainable throughput.
    public void ObserveTransfer(string serverId, long bytes, TimeSpan readTime)
    {
        if (bytes < 1_048_576 || readTime.TotalSeconds < 1 || !double.IsFinite(readTime.TotalSeconds)) return;
        var mbps = bytes * 8.0 / readTime.TotalSeconds / 1_000_000;
        if (!double.IsFinite(mbps) || mbps <= 0) return;
        var observation = _observations.GetOrAdd(serverId, _ => new Observation());
        lock (observation)
        {
            var now = _clock.GetUtcNow();
            if (now - observation.TransferAt < TimeSpan.FromSeconds(5)) return;
            mbps = Math.Clamp(mbps, 0.01, 1000);
            observation.Mbps = Fresh(observation.TransferAt, now) && observation.Mbps.HasValue
                ? observation.Mbps.Value * 0.6 + mbps * 0.4 : mbps;
            observation.TransferAt = now;
        }
    }

    public double Score(RemoteServer server, long bitrate, int height, int? capMbps)
    {
        var reliability = 0.85;
        double? measured = null;
        var latency = 0.7;
        if (_observations.TryGetValue(server.Id, out var observation))
        {
            lock (observation)
            {
                var now = _clock.GetUtcNow();
                if (Fresh(observation.HealthAt, now))
                {
                    reliability = observation.Reliability;
                    if (observation.LatencyMs.HasValue) latency = 1 / (1 + observation.LatencyMs.Value / 500);
                }
                if (Fresh(observation.TransferAt, now)) measured = observation.Mbps;
            }
        }
        // A manual cap is a ceiling even when the physical link is faster.
        var capacity = capMbps is > 0 ? (double?)capMbps.Value : null;
        if (measured.HasValue) capacity = capacity.HasValue ? Math.Min(capacity.Value, measured.Value * 0.8) : measured.Value * 0.8;
        var fit = bitrate <= 0 ? 0.45 : !capacity.HasValue ? 0.85 : Math.Clamp(capacity.Value * 1_000_000 / bitrate, 0, 1);
        var quality = Math.Clamp(height / 2160.0, 0, 1);
        return Math.Round(50 * reliability + 35 * fit + 12 * quality + 3 * latency, 3);
    }

    public void Forget(string serverId) => _observations.TryRemove(serverId, out _);
    private static bool Fresh(DateTimeOffset at, DateTimeOffset now) => now - at < TimeSpan.FromMinutes(5);
    private sealed class Observation
    {
        public double Reliability = 0.85;
        public double? Mbps;
        public double? LatencyMs;
        public DateTimeOffset HealthAt;
        public DateTimeOffset TransferAt;
        public DateTimeOffset StallAt;
    }
}
