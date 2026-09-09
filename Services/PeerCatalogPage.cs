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
        public RemoteItemPage(IEnumerable<BaseItemDto> items, int? totalRecordCount)
            : base(items)
        {
            TotalRecordCount = totalRecordCount;
        }

        public int? TotalRecordCount { get; }

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
        for (var attempt = 0; attempt < 1000 && items.Count < limit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = Math.Min(200, limit - items.Count);
            var page = await fetch(cursor, requested).ConfigureAwait(false);
            if (page == null) return null;
            cursor = checked(cursor + page.Count);
            items.AddRange(page.Where(IsOwned));
            if (page.Count < requested || items.Count >= limit) return new Result(items, cursor);
        }
        // Incomplete enumeration is never presented as an empty/end-of-library result.
        return null;
    }
}
