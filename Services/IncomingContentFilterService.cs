using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Evaluates <see cref="IncomingContentFilter"/> against a remote item before
    /// it is upserted into the federation cache. Used by <see cref="FederationSyncService"/>
    /// (receiving side) and as a second check in <see cref="RemoteAccessControlService"/>
    /// for per-user rating ceilings. Empty filter = allow everything.
    /// </summary>
    public static class IncomingContentFilterService
    {
        // MPAA and TV ratings are different scales, so they share one table only through an
        // approximate cross-scale mapping (TV-Y/TV-G~G, TV-Y7/TV-PG~PG, TV-14~PG-13, TV-MA~R).
        // Lower = more permissive. Items whose rating is not in the table are NOT ranked (see
        // IsWithinRatingCeiling for why that stays fail-open).
        private static readonly Dictionary<string, int> RatingRank = new(StringComparer.OrdinalIgnoreCase)
        {
            ["G"] = 0,
            ["TV-Y"] = 0,
            ["TV-G"] = 0,
            ["PG"] = 1,
            ["TV-Y7"] = 1,
            ["TV-PG"] = 1,
            ["PG-13"] = 2,
            ["TV-14"] = 2,
            ["R"] = 3,
            ["TV-MA"] = 3,
            ["NC-17"] = 4
        };

        private static readonly HashSet<string> LoggedUnknownCeilings = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Optional log sink so the static helper can report an unrecognised admin ceiling once.</summary>
        public static Action<string>? UnknownCeilingLogger { get; set; }

        private static bool TryRankCeiling(string ceiling, out int rank)
        {
            var key = ceiling.Trim();
            if (RatingRank.TryGetValue(key, out rank))
            {
                return true;
            }

            bool first;
            lock (LoggedUnknownCeilings)
            {
                first = LoggedUnknownCeilings.Add(key);
            }

            if (first)
            {
                UnknownCeilingLogger?.Invoke($"[Federation] Rating ceiling '{key}' is not a recognised rating (G, PG, PG-13, R, NC-17, TV-Y, TV-Y7, TV-G, TV-PG, TV-14, TV-MA); rated items are blocked until it is fixed.");
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="remoteItem"/> passes the global <paramref name="filter"/>.
        /// </summary>
        public static bool IsAllowedByIncomingFilter(IncomingContentFilter? filter, BaseItemDto remoteItem, string? inheritedRating = null)
        {
            if (filter == null)
            {
                return true;
            }

            // Item type
            if (filter.AllowedItemTypes != null && filter.AllowedItemTypes.Count > 0)
            {
                var type = remoteItem.Type.ToString();
                if (!filter.AllowedItemTypes.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Rating ceiling
            if (!string.IsNullOrWhiteSpace(filter.MaxAllowedRating))
            {
                var itemRating = string.IsNullOrWhiteSpace(remoteItem.OfficialRating) ? inheritedRating : remoteItem.OfficialRating;
                if (!string.IsNullOrWhiteSpace(itemRating) && !IsWithinRatingCeiling(itemRating!, filter.MaxAllowedRating))
                {
                    return false;
                }
            }

            // Blocked tags (exact, case-insensitive)
            if (filter.BlockedTags != null && filter.BlockedTags.Count > 0 && remoteItem.Tags != null)
            {
                var blocked = new HashSet<string>(filter.BlockedTags, StringComparer.OrdinalIgnoreCase);
                if (remoteItem.Tags.Any(t => blocked.Contains(t ?? string.Empty)))
                {
                    return false;
                }
            }

            // Blocked genres
            if (filter.BlockedGenres != null && filter.BlockedGenres.Count > 0 && remoteItem.Genres != null)
            {
                var blocked = new HashSet<string>(filter.BlockedGenres, StringComparer.OrdinalIgnoreCase);
                if (remoteItem.Genres.Any(g => blocked.Contains(g ?? string.Empty)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether <paramref name="rating"/> is at or below <paramref name="ceiling"/> in rank.
        /// An unrecognised ceiling fails closed for rated items (a typo must not switch filtering off).
        /// An unrecognised non-empty ITEM rating also fails closed: a ceiling is an access rule,
        /// so NR and unfamiliar regional labels must not bypass it.
        /// </summary>
        public static bool IsWithinRatingCeiling(string rating, string ceiling)
        {
            if (!RatingRank.TryGetValue(rating.Trim(), out var itemRank))
            {
                return false;
            }

            if (!TryRankCeiling(ceiling, out var ceilingRank))
            {
                return false;
            }

            return itemRank <= ceilingRank;
        }

        /// <summary>
        /// Combines global and per-user ceilings (the stricter KNOWN one wins) and tests the item.
        /// </summary>
        public static bool IsAllowedByRatingCeilings(string? itemRating, string? globalCeiling, string? perUserCeiling)
        {
            if (string.IsNullOrWhiteSpace(itemRating))
            {
                return true;
            }

            string? effective = null;
            var effectiveRank = int.MaxValue;
            foreach (var c in new[] { globalCeiling, perUserCeiling })
            {
                if (string.IsNullOrWhiteSpace(c))
                {
                    continue;
                }

                if (TryRankCeiling(c!, out var r) && r < effectiveRank)
                {
                    effective = c;
                    effectiveRank = r;
                }
            }

            if (effective == null)
            {
                // Either no ceiling at all (allow) or only unrecognised ones (fail closed via the helper).
                var unknown = !string.IsNullOrWhiteSpace(globalCeiling) ? globalCeiling : perUserCeiling;
                return string.IsNullOrWhiteSpace(unknown) || IsWithinRatingCeiling(itemRating!, unknown!);
            }

            return IsWithinRatingCeiling(itemRating!, effective);
        }
    }
}
