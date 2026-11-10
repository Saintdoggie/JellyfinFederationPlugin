namespace Jellyfin.Plugin.Federation.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Linq;

    /// <summary>
    /// Measures the gap the earlier "[Federation][timing]" lines could not see:
    /// from the player asking for a handed-off HLS master playlist to its first
    /// segment being answered. That span contains the friend's conversion start,
    /// which is where a slow start actually lives. One pending entry per play
    /// session, bounded and swept, so a play that never reaches a segment costs
    /// nothing but a small dictionary entry for a few minutes.
    /// </summary>
    internal static class FirstSegmentTiming
    {
        private const int MaxPending = 256;
        private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<string, DateTimeOffset> Pending = new(StringComparer.Ordinal);
        private static long lastSweep;

        /// <summary>Test hook; production always uses the system clock.</summary>
        internal static TimeProvider Clock { get; set; } = TimeProvider.System;

        public static void RecordPlaylist(string? playSessionId)
        {
            if (string.IsNullOrEmpty(playSessionId))
            {
                return;
            }

            var now = Clock.GetUtcNow();
            Pending[playSessionId] = now;
            Sweep(now);
        }

        /// <summary>Milliseconds since the playlist, or null when this is not the first segment.</summary>
        public static long? CompleteSegment(string? playSessionId)
        {
            if (string.IsNullOrEmpty(playSessionId) || !Pending.TryRemove(playSessionId, out var started))
            {
                return null;
            }

            return (long)(Clock.GetUtcNow() - started).TotalMilliseconds;
        }

        private static void Sweep(DateTimeOffset now)
        {
            // Cheap, rare sweep: at most once a minute, and only when the map is
            // getting big or has had time to accumulate abandoned plays.
            var ticks = now.UtcTicks;
            if (Pending.Count < MaxPending && System.Threading.Interlocked.Read(ref lastSweep) > ticks - TimeSpan.FromMinutes(1).Ticks)
            {
                return;
            }

            System.Threading.Interlocked.Exchange(ref lastSweep, ticks);
            foreach (var pair in Pending)
            {
                if (now - pair.Value > KeepFor)
                {
                    Pending.TryRemove(pair.Key, out _);
                }
            }

            if (Pending.Count > MaxPending)
            {
                foreach (var pair in Pending.OrderBy(p => p.Value).Take(Pending.Count - MaxPending))
                {
                    Pending.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}
