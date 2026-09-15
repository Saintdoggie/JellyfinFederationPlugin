using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Receiving-side protection when an older peer exports other friends' items.</summary>
internal static class PeerCatalogPage
{
    internal sealed record Result(List<BaseItemDto> Items, int NextStartIndex);

    /// <summary>
    /// One page from a peer catalog, including the remote's reported total when present.
    /// </summary>
    internal sealed class RemoteItemPage : List<BaseItemDto>
    {
        public RemoteItemPage(IEnumerable<BaseItemDto> items, int? totalRecordCount, bool? hasMore = null)
            : base(items)
        {
            TotalRecordCount = totalRecordCount;
            // Preferred paging signal once both sides run a version that sends
            // it: whether more rows exist after this page, independent of how
            // TotalRecordCount was computed (true catalog total vs. page echo).
            HasMore = hasMore;
        }

        public int? TotalRecordCount { get; }

        public bool? HasMore { get; }

        /// <summary>
        /// True when the remote's total says items remain after this page.
        /// </summary>
        public static bool HasMoreRecords(int startIndex, int pageCount, int? totalRecordCount)
            => totalRecordCount is int total && startIndex + pageCount < total;
    }

    public static bool IsOwned(BaseItemDto item)
        => !FederationLibraryManager.IsIneligibleForOutgoingShare(item.ProviderIds);

    public static async Task<Result?> ReadAsync(
        Func<int, int, Task<List<BaseItemDto>?>> fetch,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = new List<BaseItemDto>();
        var cursor = Math.Max(0, startIndex);
        // Remembers the freshest HasMore signal seen while filling this page so
        // Browse callers can skip their old limit=1 end-of-catalog probe fetch.
        bool? hasMore = null;
        for (var attempt = 0; attempt < 1000 && items.Count < limit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = Math.Min(200, limit - items.Count);
            var page = await fetch(cursor, requested).ConfigureAwait(false);
            if (page == null) return null;
            if (page is RemoteItemPage remotePage && remotePage.HasMore.HasValue)
            {
                hasMore = remotePage.HasMore;
            }
            cursor = checked(cursor + page.Count);
            items.AddRange(page.Where(IsOwned));
            // A full HasMore=false page is provably the last one: stop without
            // the extra probe fetch the old page-count heuristic needed.
            if (hasMore == false && items.Count >= limit) return new Result(items, cursor);
            if (page.Count < requested || items.Count >= limit) return new Result(items, cursor);
        }
        // Incomplete enumeration is never presented as an empty/end-of-library result.
        return null;
    }
}
