using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// A title this server owns and a friend also holds. The own file always
    /// plays when the device can play it as stored (a friend's copy of an owned
    /// title is never added to the library at all). When the own file would need
    /// converting for a device, a friend's copy that the same device plays as
    /// stored is offered beside it, and Jellyfin's own ordering then prefers it
    /// over converting. A friend's copy is never offered for conversion here.
    /// </summary>
    /// <remarks>
    /// The decision needs the device's capabilities, which only the PlaybackInfo
    /// request carries. What was offered is remembered per viewer, device and
    /// title, so the stream request that follows (without capabilities) finds the
    /// same copies.
    /// </remarks>
    internal sealed class OwnedTitleFriendCopies
    {
        /// <summary>How long an offer is honoured for follow-up requests.</summary>
        internal static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);

        private const int MaxOffers = 2048;

        private readonly ConcurrentDictionary<string, (DateTime AtUtc, HashSet<string> SourceIds)> _offers = new(StringComparer.Ordinal);

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>The process-wide memory.</summary>
        internal static OwnedTitleFriendCopies Shared { get; } = new();

        /// <summary>True when nothing is remembered: lets every other play skip all work.</summary>
        internal bool IsEmpty => _offers.IsEmpty;

        /// <summary>
        /// Same switch as the other automatic choices: on with automatic copy
        /// choice, and always on a server that may not convert (there an own file
        /// the device cannot play as stored otherwise cannot play at all).
        /// </summary>
        internal static bool Wanted(PluginConfiguration? config)
            => config != null
                && config.EnableDedup
                && (config.SmartSourceSelection || !TranscodePolicy.MayConvertForOwnViewers(config));

        /// <summary>
        /// Whether a friend's copy is looked for: only when the device is known not
        /// to play the own file as stored.
        /// </summary>
        internal static bool OwnFileNeedsHelp(DevicePlayFitResult own)
            => own.Fit is PlayFit.Convert or PlayFit.AudioOnly;

        /// <summary>Request item naming the friend copies offered in this PlaybackInfo response.</summary>
        internal const string OfferKey = "Federation.OwnedTitleOffer";

        /// <summary>
        /// What a player should see. An offered friend copy that Jellyfin found the
        /// device cannot play as stored is dropped (a friend's copy of an owned
        /// title is never converted; the own file is). When no copy of this
        /// server's own plays without converting, the remaining friend copies go
        /// first. Otherwise the order is left alone.
        /// </summary>
        internal static IReadOnlyList<MediaSourceInfo> Order(IReadOnlyList<MediaSourceInfo> sources, IReadOnlyCollection<string> offeredIds)
        {
            bool Offered(MediaSourceInfo s) => s.Id != null && offeredIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase);
            bool PlaysAsStored(MediaSourceInfo s) => s.SupportsDirectPlay || s.SupportsDirectStream;

            var kept = sources.Any(s => Offered(s) && !PlaysAsStored(s)) && sources.Any(s => !Offered(s))
                ? sources.Where(s => !Offered(s) || PlaysAsStored(s)).ToList()
                : sources;
            if (kept.Any(s => !Offered(s) && PlaysAsStored(s)) || !kept.Any(Offered))
            {
                return kept;
            }

            return kept.Where(Offered).Concat(kept.Where(s => !Offered(s))).ToList();
        }

        /// <summary>Remembers the copies offered for one viewer, device and title.</summary>
        internal void Remember(Guid userId, string? deviceId, Guid itemId, IEnumerable<string> sourceIds)
        {
            var now = UtcNow();
            if (_offers.Count >= MaxOffers)
            {
                foreach (var old in _offers.Where(p => now - p.Value.AtUtc > ForgetAfter).Select(p => p.Key).ToList())
                {
                    _offers.TryRemove(old, out _);
                }

                foreach (var oldest in _offers.OrderBy(p => p.Value.AtUtc).Take(Math.Max(0, _offers.Count - MaxOffers + 1)).Select(p => p.Key).ToList())
                {
                    _offers.TryRemove(oldest, out _);
                }
            }

            _offers[Key(userId, deviceId, itemId)] = (now, new HashSet<string>(sourceIds, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>The source ids offered earlier, or null when none (or too long ago).</summary>
        internal IReadOnlySet<string>? Offered(Guid userId, string? deviceId, Guid itemId)
            => _offers.TryGetValue(Key(userId, deviceId, itemId), out var offer) && UtcNow() - offer.AtUtc <= ForgetAfter
                ? offer.SourceIds
                : null;

        /// <summary>Drops the offer: the device now plays the own file as stored.</summary>
        internal void Forget(Guid userId, string? deviceId, Guid itemId)
            => _offers.TryRemove(Key(userId, deviceId, itemId), out _);

        internal void Reset() => _offers.Clear();

        private static string Key(Guid userId, string? deviceId, Guid itemId)
            => userId.ToString("N") + "|" + (deviceId ?? string.Empty) + "|" + itemId.ToString("N");
    }
}
