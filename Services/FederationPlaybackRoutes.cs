using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>The route observed for a playback session, rather than the route preferred in configuration.</summary>
    public static class FederationPlaybackRoutes
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);
        private const int MaxSessions = 2048;
        private static readonly ConcurrentDictionary<string, (string Route, DateTime SeenUtc)> Routes = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, DateTime> CopyPlays = new(StringComparer.OrdinalIgnoreCase);

        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public static void Record(string? playSessionId, string route)
        {
            var key = Normalize(playSessionId);
            if (key == null || !IsKnown(route))
            {
                return;
            }

            var now = UtcNow();
            Prune(now);
            Routes[key] = (route, now);
        }

        public static string? Get(string? playSessionId)
        {
            var key = Normalize(playSessionId);
            if (key == null || !Routes.TryGetValue(key, out var observed))
            {
                return null;
            }

            if (UtcNow() - observed.SeenUtc > Lifetime)
            {
                Routes.TryRemove(key, out _);
                return null;
            }

            return observed.Route;
        }

        public static void Forget(string? playSessionId)
        {
            if (Normalize(playSessionId) is { } key)
            {
                Routes.TryRemove(key, out _);
                CopyPlays.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Notes that the friend serves this session as a copy play
        /// (<see cref="CopyPlay"/>): its file repackaged, nothing converted.
        /// The route stays <see cref="FederatedRoute.FriendConverts"/> (same path,
        /// same measurements); this only changes how the play is described.
        /// </summary>
        public static void RecordCopyPlay(string? playSessionId, bool copyPlay)
        {
            if (Normalize(playSessionId) is not { } key)
            {
                return;
            }

            if (copyPlay)
            {
                CopyPlays[key] = UtcNow();
            }
            else
            {
                CopyPlays.TryRemove(key, out _);
            }
        }

        /// <summary>Whether the session was last served as a copy play.</summary>
        public static bool IsCopyPlay(string? playSessionId)
            => Normalize(playSessionId) is { } key && CopyPlays.TryGetValue(key, out var seen) && UtcNow() - seen <= Lifetime;

        internal static void Reset()
        {
            Routes.Clear();
            CopyPlays.Clear();
        }

        private static string? Normalize(string? playSessionId)
        {
            var key = playSessionId?.Trim();
            return string.IsNullOrEmpty(key) || key.Length > 128 ? null : key;
        }

        private static bool IsKnown(string route)
            => route is FederatedRoute.Direct or FederatedRoute.Relay or FederatedRoute.FriendConverts or FederatedRoute.ConvertedHere;

        private static void Prune(DateTime now)
        {
            foreach (var pair in Routes)
            {
                if (now - pair.Value.SeenUtc > Lifetime || Routes.Count > MaxSessions)
                {
                    Routes.TryRemove(pair.Key, out _);
                }
            }

            foreach (var pair in CopyPlays)
            {
                if (now - pair.Value > Lifetime || CopyPlays.Count > MaxSessions)
                {
                    CopyPlays.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}
