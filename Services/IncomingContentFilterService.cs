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
        // OfficialRating ranking: lower index = more permissive. Unknown ratings fail open.
        private static readonly Dictionary<string, int> RatingRank = new(StringComparer.OrdinalIgnoreCase)
        {
            ["G"] = 0,
            ["PG"] = 1,
            ["PG-13"] = 2,
            ["R"] = 3,
            ["NC-17"] = 4,
            ["TV-Y"] = 0,
            ["TV-Y7"] = 1,
            ["TV-G"] = 2,
            ["TV-PG"] = 3,
            ["TV-14"] = 4,
            ["TV-MA"] = 5
        };

        /// <summary>
        /// Whether <paramref name="remoteItem"/> passes the global <paramref name="filter"/>.
        /// </summary>
        public static bool IsAllowedByIncomingFilter(IncomingContentFilter? filter, BaseItemDto remoteItem)
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
                var itemRating = remoteItem.OfficialRating;
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
        /// Unknown ratings return true (fail open).
        /// </summary>
        public static bool IsWithinRatingCeiling(string rating, string ceiling)
        {
            if (!RatingRank.TryGetValue(ceiling.Trim(), out var ceilingRank))
            {
                return true;
            }

            if (!RatingRank.TryGetValue(rating.Trim(), out var itemRank))
            {
                return true;
            }

            return itemRank <= ceilingRank;
        }

        /// <summary>
        /// Returns true if the more restrictive of two ceilings is the given item rating.
        /// Used to combine global and per-user ceilings (stricter wins).
        /// </summary>
        public static bool IsAllowedByRatingCeilings(string? itemRating, string? globalCeiling, string? perUserCeiling)
        {
            if (string.IsNullOrWhiteSpace(itemRating))
            {
                return true;
            }

            string? effective = null;
            if (!string.IsNullOrWhiteSpace(globalCeiling) && !string.IsNullOrWhiteSpace(perUserCeiling))
            {
                // Stricter = lower rank
                if (RatingRank.TryGetValue(globalCeiling!.Trim(), out var gr) && RatingRank.TryGetValue(perUserCeiling!.Trim(), out var pr))
                {
                    effective = gr <= pr ? globalCeiling : perUserCeiling;
                }
                else
                {
                    effective = perUserCeiling;
                }
            }
            else
            {
                effective = string.IsNullOrWhiteSpace(perUserCeiling) ? globalCeiling : perUserCeiling;
            }

            if (string.IsNullOrWhiteSpace(effective))
            {
                return true;
            }

            return IsWithinRatingCeiling(itemRating!, effective!);
        }
    }
}
