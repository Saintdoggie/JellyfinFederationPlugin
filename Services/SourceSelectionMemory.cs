using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Remembers which copy each viewer's device was last given for a title, and which
    /// servers just failed, so the choice does not flip between servers on every
    /// speed sample. In memory only, bounded, and holds ids - never addresses or keys.
    /// </summary>
    public sealed class SourceSelectionMemory
    {
        /// <summary>A copy that was just chosen is kept at least this long.</summary>
        internal static readonly TimeSpan MinimumDwell = TimeSpan.FromSeconds(20);

        /// <summary>After this long without a play the previous choice no longer counts.</summary>
        internal static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(30);

        /// <summary>A server that just failed is treated as unreliable for this long.</summary>
        internal static readonly TimeSpan FailureBackOff = TimeSpan.FromSeconds(60);

        private const int MaxChoices = 2048;
        private const int MaxFailures = 256;

        private readonly ConcurrentDictionary<string, Choice> _choices = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _failures = new(StringComparer.OrdinalIgnoreCase);

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>The copy chosen last time and whether it may be replaced yet.</summary>
        internal (string? Key, bool MayLeave) Current(Guid userId, string? deviceId, Guid itemId)
            => Current(Scope(userId, deviceId, itemId));

        /// <summary>The copy chosen last time under an arbitrary scope (for example one catalog entry).</summary>
        internal (string? Key, bool MayLeave) Current(string scope)
        {
            if (!_choices.TryGetValue(scope, out var choice))
            {
                return (null, true);
            }

            var now = UtcNow();
            if (now - choice.SeenUtc > RememberFor)
            {
                return (null, true);
            }

            return (choice.Key, now - choice.ChosenUtc >= MinimumDwell);
        }

        /// <summary>Records the copy that was put first. An unchanged choice keeps its original time.</summary>
        internal void Remember(Guid userId, string? deviceId, Guid itemId, string key, string reason)
            => Remember(Scope(userId, deviceId, itemId), key, reason);

        /// <summary>Records the copy that was put first under an arbitrary scope.</summary>
        internal void Remember(string scope, string key, string reason)
        {
            var now = UtcNow();
            _choices.AddOrUpdate(
                scope,
                _ => new Choice(key, now, now, reason),
                (_, old) => string.Equals(old.Key, key, StringComparison.Ordinal)
                    ? old with { SeenUtc = now, Reason = reason }
                    : new Choice(key, now, now, reason));
            if (_choices.Count > MaxChoices)
            {
                foreach (var stale in _choices.OrderBy(p => p.Value.SeenUtc).Take(_choices.Count - (MaxChoices * 3 / 4)).ToList())
                {
                    _choices.TryRemove(stale.Key, out _);
                }
            }
        }

        /// <summary>The sentence explaining the last choice for this viewer and title, if still remembered.</summary>
        internal string? Reason(Guid userId, string? deviceId, Guid itemId)
            => Reason(Scope(userId, deviceId, itemId));

        /// <summary>The sentence explaining the last choice under a scope, if still remembered.</summary>
        internal string? Reason(string scope)
            => _choices.TryGetValue(scope, out var choice)
                && UtcNow() - choice.SeenUtc <= RememberFor
                    ? choice.Reason
                    : null;

        /// <summary>Notes that a server could not serve a title just now.</summary>
        internal void NoteFailure(string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId))
            {
                return;
            }

            var now = UtcNow();
            _failures[serverId] = now;
            if (_failures.Count > MaxFailures)
            {
                foreach (var old in _failures.Where(p => now - p.Value > FailureBackOff).ToList())
                {
                    _failures.TryRemove(old.Key, out _);
                }
            }
        }

        /// <summary>Whether the server failed within the back-off window.</summary>
        internal bool RecentlyFailed(string serverId)
            => _failures.TryGetValue(serverId, out var at) && UtcNow() - at <= FailureBackOff;

        internal void Reset()
        {
            _choices.Clear();
            _failures.Clear();
        }

        private static string Normalize(string? deviceId)
        {
            var value = deviceId?.Trim().ToLowerInvariant() ?? string.Empty;
            return value.Length <= 128 ? value : value.Substring(0, 128);
        }

        private static string Scope(Guid userId, string? deviceId, Guid itemId)
            => userId.ToString("N") + "|" + Normalize(deviceId) + "|" + itemId.ToString("N");

        private sealed record Choice(string Key, DateTime ChosenUtc, DateTime SeenUtc, string Reason);
    }
}
