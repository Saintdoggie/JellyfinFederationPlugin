using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Receiver side: whether each friend currently lets this server hand
    /// conversion of its titles to it (remote-transcode handoff). Learned from
    /// every fresh Playback token (<see cref="PlaybackTokenResponse.RemoteTranscode"/>),
    /// so it costs no extra request. A gateway refusal turns it off for a while,
    /// so a friend that just disabled it (or hit its limit) isn't asked again on
    /// every play; the next token mint after that re-learns it.
    /// </summary>
    public static class PeerTranscodeCapability
    {
        /// <summary>How long a gateway refusal keeps the handoff off for that friend.</summary>
        internal static readonly TimeSpan RefusalBackoff = TimeSpan.FromMinutes(2);

        private static readonly ConcurrentDictionary<string, (bool Supported, DateTime? RefusedUntil)> State = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, bool> SharedSegments = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clock, replaceable in tests.</summary>
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Records what a fresh token said.</summary>
        public static void Record(string? serverId, bool supported)
        {
            if (string.IsNullOrEmpty(serverId))
            {
                return;
            }

            State.AddOrUpdate(serverId, _ => (supported, null), (_, old) => (supported, old.RefusedUntil));
        }

        /// <summary>Notes that the friend's gateway just refused (403/429/5xx or unreachable).</summary>
        public static void RecordRefusal(string? serverId)
        {
            if (string.IsNullOrEmpty(serverId))
            {
                return;
            }

            var until = UtcNow() + RefusalBackoff;
            State.AddOrUpdate(serverId, _ => (true, until), (_, old) => (old.Supported, until));
        }

        /// <summary>True when the handoff should be tried for this friend right now.</summary>
        public static bool IsAvailable(string? serverId)
            => !string.IsNullOrEmpty(serverId)
                && State.TryGetValue(serverId, out var state)
                && state.Supported
                && (state.RefusedUntil == null || state.RefusedUntil <= UtcNow());

        /// <summary>True when a negative answer must be refreshed rather than reused from a token cache.</summary>
        public static bool NeedsRefresh(string? serverId)
            => !string.IsNullOrEmpty(serverId) && State.TryGetValue(serverId, out var state) && !state.Supported;

        /// <summary>Records whether the friend shares repeated segment requests (see <see cref="PlaybackTokenResponse.SharedSegments"/>).</summary>
        public static void RecordSharedSegments(string? serverId, bool shared)
        {
            if (!string.IsNullOrEmpty(serverId))
            {
                SharedSegments[serverId] = shared;
            }
        }

        /// <summary>
        /// True when asking this friend for a segment early is safe: a second
        /// request for it joins the first instead of restarting the conversion.
        /// </summary>
        public static bool HasSharedSegments(string? serverId)
            => !string.IsNullOrEmpty(serverId) && SharedSegments.TryGetValue(serverId, out var shared) && shared;

        /// <summary>Test hook.</summary>
        internal static void Reset()
        {
            State.Clear();
            SharedSegments.Clear();
        }
    }
}
