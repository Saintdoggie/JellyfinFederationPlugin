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
        /// Whether other servers' copies of an owned title are listed as versions
        /// beside the own file, can be picked there, and a linked server's better
        /// copy is put first. Needs only the merging of copies.
        /// </summary>
        internal static bool Listed(PluginConfiguration? config) => config != null && config.EnableDedup;

        /// <summary>
        /// The catalogue entry holding other servers' copies of a title this server
        /// owns: the same provider-id key the sync merges copies on, in any enabled
        /// library. Null when no other server has the title.
        /// </summary>
        internal static FederatedCacheEntry? FindEntry(FederationItemCache cache, PluginConfiguration config, MediaBrowser.Controller.Entities.BaseItem item)
        {
            if (item.ProviderIds == null || item.ProviderIds.Count == 0)
            {
                return null;
            }

            var kind = item.GetBaseItemKind().ToString();
            foreach (var providerName in config.DedupProviderIds ?? new List<string>())
            {
                if (!FederationLibraryManager.TryGetProviderId(item.ProviderIds, providerName, out var value) || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                foreach (var mapping in config.LibraryMappings ?? new List<LibraryMapping>())
                {
                    if (!mapping.Enabled || string.IsNullOrEmpty(mapping.LocalLibraryName))
                    {
                        continue;
                    }

                    var entry = cache.GetEntryByKey(FederationItemCache.BuildProviderKey(mapping.LocalLibraryName, providerName, value));
                    if (entry != null && string.Equals(entry.ItemType, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Whether another copy is clearly better than the own one: a picture at
        /// least a fifth wider (width, because a cropped film is less tall at the
        /// same quality), or the same picture size at one and a half times the
        /// bitrate or more. Never when either picture size is unknown.
        /// </summary>
        internal static bool IsBetterCopy(IEnumerable<MediaBrowser.Model.Entities.MediaStream>? own, long? ownBitrate, IEnumerable<MediaBrowser.Model.Entities.MediaStream>? other, long? otherBitrate)
        {
            var (ownWidth, ownRate) = Picture(own, ownBitrate);
            var (otherWidth, otherRate) = Picture(other, otherBitrate);
            if (ownWidth <= 0 || otherWidth <= 0)
            {
                return false;
            }

            if (otherWidth >= ownWidth * 1.2)
            {
                return true;
            }

            return otherWidth >= ownWidth * 0.9 && ownRate > 0 && otherRate >= ownRate * 1.5;
        }

        private static (int Width, long Bitrate) Picture(IEnumerable<MediaBrowser.Model.Entities.MediaStream>? streams, long? fileBitrate)
        {
            var video = streams?
                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video)
                .OrderByDescending(s => s.Width ?? 0)
                .FirstOrDefault();
            if (video == null)
            {
                return (0, 0);
            }

            var width = video.Width is > 0 ? video.Width.Value : (video.Height ?? 0) * 16 / 9;
            return (width, video.BitRate is > 0 ? video.BitRate.Value : fileBitrate ?? 0);
        }

        /// <summary>Request item naming the copy picked in the version list (see <see cref="FederationPlaybackProfileFilter"/>).</summary>
        internal const string PickKey = "Federation.OwnedTitlePick";

        /// <summary>Request item naming the offered copies that go before the own file when the device plays them as stored.</summary>
        internal const string PreferredKey = "Federation.OwnedTitlePreferred";

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
        /// <remarks>
        /// <paramref name="preferredIds"/> are offered copies that go first whenever
        /// the device plays them as stored: the one picked in the version list, or
        /// a linked server's better copy.
        /// </remarks>
        internal static IReadOnlyList<MediaSourceInfo> Order(IReadOnlyList<MediaSourceInfo> sources, IReadOnlyCollection<string> offeredIds, IReadOnlyCollection<string>? preferredIds = null)
        {
            bool Offered(MediaSourceInfo s) => s.Id != null && offeredIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase);
            bool PlaysAsStored(MediaSourceInfo s) => s.SupportsDirectPlay || s.SupportsDirectStream;
            bool Preferred(MediaSourceInfo s) => preferredIds != null && s.Id != null && preferredIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase) && PlaysAsStored(s);

            var kept = sources.Any(s => Offered(s) && !PlaysAsStored(s)) && sources.Any(s => !Offered(s))
                ? sources.Where(s => !Offered(s) || PlaysAsStored(s)).ToList()
                : sources;
            if (kept.Any(Preferred))
            {
                return kept.Where(Preferred).Concat(kept.Where(s => !Preferred(s))).ToList();
            }

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
