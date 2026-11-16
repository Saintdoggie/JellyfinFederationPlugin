using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Makes the library "This server" tab filter on the server again.
    /// </summary>
    /// <remarks>
    /// The tab (see <c>Web/client/federation-badge.js</c>) asks for
    /// <c>ExcludeItemTags=Federated</c>, but Jellyfin 12's items endpoint no longer
    /// has that parameter and silently ignores it. Every page of results then came
    /// back mostly federated and the browser hid those cards, so the tab showed a
    /// few titles, an empty page, or "No titles" depending on what each page held.
    /// When an items query carries that marker, this adds every federated item to
    /// the query's own <c>excludeItemIds</c>, so paging and counts are right.
    /// Any other request is untouched, and a failure leaves the request as it was.
    /// <para>
    /// A server's own tab asks with <c>federationOrigin=&lt;server name&gt;</c>. The
    /// query is then held to the titles that server has: its titles shown here,
    /// and titles this server owns that it also holds (those have one card, the
    /// own one, so no tag could find them). The list is put in the query's own
    /// <c>ids</c>, so every page is full and the total is that server's total.
    /// </para>
    /// </remarks>
    public sealed class FederationLocalOnlyItemsFilter : IAsyncActionFilter
    {
        internal const string Marker = "Federated";

        /// <summary>Query parameter naming the server whose titles a library page wants.</summary>
        internal const string OriginParameter = "federationOrigin";

        /// <summary>An id no item has: what "this server has none of these" is asked as.</summary>
        internal static readonly Guid NoMatch = new("fedfedfe-0000-4000-8000-00000000fed0");

        private static readonly BaseItemKind[] DefaultKinds = { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum };
        private static readonly TimeSpan MemoLife = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan UnavailableLife = TimeSpan.FromSeconds(10);
        private static readonly ConcurrentDictionary<string, (DateTime AtUtc, Guid[] Ids)> Memo = new(StringComparer.OrdinalIgnoreCase);

        private readonly ILibraryManager _library;
        private readonly ILogger<FederationLocalOnlyItemsFilter> _logger;
        private readonly FederationLibraryManager? _federation;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationLocalOnlyItemsFilter"/> class.
        /// </summary>
        public FederationLocalOnlyItemsFilter(ILibraryManager library, ILogger<FederationLocalOnlyItemsFilter> logger, FederationLibraryManager? federation = null)
        {
            _library = library;
            _logger = logger;
            _federation = federation;
        }

        /// <summary>Test seam: the settings the server tab is answered from.</summary>
        internal Func<Configuration.PluginConfiguration?> Settings { get; set; } = () => Plugin.Instance?.Configuration;

        /// <summary>Test seam: the catalogue, when no federation manager is given.</summary>
        internal FederationItemCache? CacheOverride { get; set; }

        internal static void ResetMemo()
        {
            Memo.Clear();
            _unavailableMemo = default;
        }

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var origin = context.HttpContext.Request.Query[OriginParameter].FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (IsHomeRow(context))
            {
                await AnswerHomeRowAsync(context, next, origin?.Trim()).ConfigureAwait(false);
                return;
            }

            if (origin != null && IsItemsQuery(context) && context.ActionArguments.ContainsKey("ids"))
            {
                try
                {
                    var onServer = ItemIdsOnServer(
                        origin.Trim(),
                        context.ActionArguments.TryGetValue("parentId", out var parent) ? parent as Guid? : null,
                        context.ActionArguments.TryGetValue("includeItemTypes", out var kinds) ? kinds as BaseItemKind[] : null);
                    var ids = NarrowTo(context.ActionArguments["ids"] as Guid[], onServer);
                    context.ActionArguments["ids"] = ids;

                    // A page number kept from a longer list would show nothing.
                    if (context.ActionArguments.TryGetValue("startIndex", out var start) && start is int index && index >= ids.Length)
                    {
                        context.ActionArguments["startIndex"] = 0;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Could not apply the server tab's library filter");
                }
            }
            else if (IsItemsQuery(context) && context.ActionArguments.ContainsKey("excludeItemIds") && AsksForLocalOnly(context.HttpContext.Request.Query["ExcludeItemTags"]))
            {
                try
                {
                    var federated = _library.GetItemIds(new InternalItemsQuery
                    {
                        Tags = new[] { Marker },
                        Recursive = true
                    });
                    context.ActionArguments["excludeItemIds"] = MergeExcluded(
                        context.ActionArguments.TryGetValue("excludeItemIds", out var existing) ? existing as Guid[] : null,
                        federated);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Could not apply the local-only library filter");
                }
            }

            // Titles whose every server is offline are left out by the server, so a
            // page is full and the count is right. The browser used to hide their
            // cards after the page had been filled, which left short pages.
            if (IsItemsQuery(context) && context.ActionArguments.ContainsKey("excludeItemIds"))
            {
                try
                {
                    var unavailable = UnavailableItemIds();
                    if (unavailable.Length > 0)
                    {
                        context.ActionArguments["excludeItemIds"] = MergeExcluded(
                            context.ActionArguments.TryGetValue("excludeItemIds", out var existing) ? existing as Guid[] : null,
                            unavailable);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Could not leave offline servers' titles out of a library query");
                }
            }

            var executed = await next().ConfigureAwait(false);

            // A search does not honour the id list (Jellyfin answers it from the
            // search index), so a server tab's search is narrowed in the answer.
            if (origin != null && IsItemsQuery(context)
                && context.HttpContext.Request.Query.Any(q => string.Equals(q.Key, "searchTerm", StringComparison.OrdinalIgnoreCase) && q.Value.Any(v => !string.IsNullOrWhiteSpace(v)))
                && executed.Result is Microsoft.AspNetCore.Mvc.ObjectResult { Value: MediaBrowser.Model.Querying.QueryResult<BaseItemDto> found } answer)
            {
                try
                {
                    if (HomeRowRule(origin.Trim(), false) is { } keep)
                    {
                        var items = NarrowRow(found.Items, keep, null);
                        answer.Value = new MediaBrowser.Model.Querying.QueryResult<BaseItemDto>(found.StartIndex, items.Count, items);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Could not narrow a search for the server tab");
                }
            }
        }

        /// <summary>Test seam: whether a server is known to be offline.</summary>
        internal Func<string, bool> IsOffline { get; set; } = id => FederationItemPersistenceService.AvailabilityOverride?.IsOffline(id) == true;

        /// <summary>Test seam: the library id of a catalogue entry.</summary>
        internal Func<FederatedCacheEntry, Guid>? ItemIdOf { get; set; }

        private static (DateTime AtUtc, Guid[] Ids) _unavailableMemo;

        /// <summary>
        /// Library ids of the titles nobody can play right now: every server that
        /// holds one is switched off or known to be offline (a server not checked
        /// yet hides nothing). Remembered for a few seconds.
        /// </summary>
        internal Guid[] UnavailableItemIds()
        {
            var now = DateTime.UtcNow;
            var memo = _unavailableMemo;
            if (memo.Ids != null && now - memo.AtUtc < UnavailableLife)
            {
                return memo.Ids;
            }

            var config = Settings();
            var cache = CacheOverride ?? _federation?.Cache;
            var idOf = ItemIdOf ?? (_federation == null ? null : new Func<FederatedCacheEntry, Guid>(_federation.ComputeItemId));
            if (config == null || cache == null || idOf == null)
            {
                return Array.Empty<Guid>();
            }

            var servers = (config.RemoteServers ?? new List<Configuration.RemoteServer>())
                .Where(s => !string.IsNullOrEmpty(s.Id))
                .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Enabled && !IsOffline(g.Key), StringComparer.OrdinalIgnoreCase);
            var ids = servers.Values.All(live => live)
                ? Array.Empty<Guid>()
                : cache.GetAllEntries()
                    .Where(entry => !entry.GetSourcesSnapshot().Any(source => servers.TryGetValue(source.ServerId, out var live) && live))
                    .Select(idOf)
                    .Distinct()
                    .ToArray();
            _unavailableMemo = (now, ids);
            return ids;
        }

        /// <summary>True when an ExcludeItemTags value (comma or pipe separated) names the federated tag.</summary>
        internal static bool AsksForLocalOnly(IEnumerable<string?> values)
            => values.Any(value => !string.IsNullOrEmpty(value)
                && value.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(tag => string.Equals(tag, Marker, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The query's own exclusions plus every federated item, without duplicates.</summary>
        internal static Guid[] MergeExcluded(Guid[]? existing, IEnumerable<Guid> federated)
            => (existing ?? Array.Empty<Guid>()).Concat(federated).Distinct().ToArray();

        /// <summary>The query's own ids held to <paramref name="onServer"/>; never empty, because an empty list means "no filter".</summary>
        internal static Guid[] NarrowTo(Guid[]? existing, IReadOnlyCollection<Guid> onServer)
        {
            var ids = existing is { Length: > 0 } ? existing.Intersect(onServer).ToArray() : onServer.ToArray();
            return ids.Length == 0 ? new[] { NoMatch } : ids;
        }

        /// <summary>
        /// Every title of the asked kinds, under the asked library, that the named
        /// server has: shown from it, or owned here and also held there.
        /// </summary>
        internal Guid[] ItemIdsOnServer(string serverName, Guid? parentId, BaseItemKind[]? kinds)
        {
            var config = Settings();
            var cache = CacheOverride ?? _federation?.Cache;
            if (config == null || cache == null)
            {
                return Array.Empty<Guid>();
            }

            var serverIds = (config.RemoteServers ?? new List<Configuration.RemoteServer>())
                .Where(s => s.Enabled
                    && string.Equals((s.Name ?? string.Empty).Trim(), serverName, StringComparison.OrdinalIgnoreCase)
                    && !LinkCatalog.IsHiddenLinkedSource(s))
                .Select(s => s.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (serverIds.Count == 0)
            {
                return Array.Empty<Guid>();
            }

            var wantedKinds = kinds is { Length: > 0 } ? kinds : DefaultKinds;
            var memoKey = string.Join("|", serverIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
                + "/" + (parentId?.ToString("N") ?? string.Empty)
                + "/" + string.Join(",", wantedKinds.OrderBy(k => k));
            var now = DateTime.UtcNow;
            if (Memo.TryGetValue(memoKey, out var memo) && now - memo.AtUtc < MemoLife)
            {
                return memo.Ids;
            }

            var query = new InternalItemsQuery { Recursive = true, IncludeItemTypes = wantedKinds };
            if (parentId is { } parentValue && parentValue != Guid.Empty)
            {
                query.ParentId = parentValue;
            }

            var result = new List<Guid>();
            foreach (var item in _library.GetItemList(query))
            {
                var key = FederationLibraryManager.GetFederationKey(item);
                var entry = key != null
                    ? cache.GetEntryByKey(key)
                    : OwnedTitleFriendCopies.Listed(config) ? OwnedTitleFriendCopies.FindEntry(cache, config, item) : null;
                if (entry != null && entry.GetSourcesSnapshot().Any(s => serverIds.Contains(s.ServerId)))
                {
                    result.Add(item.Id);
                }
            }

            var found = result.ToArray();
            if (Memo.Count > 64)
            {
                Memo.Clear();
            }

            Memo[memoKey] = (now, found);
            return found;
        }

        /// <summary>
        /// The home page's rows (Recently Added, Continue Watching, Next Up). Their
        /// endpoints take no list of ids to hold the answer to, so the answer itself
        /// is narrowed: more is asked for, what does not belong is taken out, and the
        /// row is cut back to the length the page wanted. The browser used to hide
        /// those cards, which left short rows and showed apps the unfiltered list.
        /// </summary>
        private async Task AnswerHomeRowAsync(ActionExecutingContext context, ActionExecutionDelegate next, string? origin)
        {
            Func<BaseItemDto, bool>? keep = null;
            int? wanted = null;
            try
            {
                keep = HomeRowRule(origin, AsksForLocalOnly(context.HttpContext.Request.Query["ExcludeItemTags"]));
                if (keep != null && context.ActionArguments.TryGetValue("limit", out var limit) && limit is int asked && asked > 0)
                {
                    wanted = asked;
                    context.ActionArguments["limit"] = WidenedLimit(asked);
                }
            }
            catch (Exception ex)
            {
                keep = null;
                _logger.LogDebug(ex, "[Federation] Could not prepare a home row for the server tab");
            }

            var executed = await next().ConfigureAwait(false);
            if (keep == null || executed.Result is not Microsoft.AspNetCore.Mvc.ObjectResult answer)
            {
                return;
            }

            try
            {
                if (answer.Value is MediaBrowser.Model.Querying.QueryResult<BaseItemDto> page)
                {
                    var items = NarrowRow(page.Items, keep, wanted);
                    answer.Value = new MediaBrowser.Model.Querying.QueryResult<BaseItemDto>(page.StartIndex, items.Count, items);
                }
                else if (answer.Value is IEnumerable<BaseItemDto> list)
                {
                    answer.Value = NarrowRow(list, keep, wanted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not narrow a home row for the server tab");
            }
        }

        /// <summary>How many to ask Jellyfin for so a narrowed row can still be full.</summary>
        internal static int WidenedLimit(int asked) => Math.Min(Math.Max(asked * 5, 60), 300);

        /// <summary>The titles of <paramref name="items"/> that pass, cut to the asked length.</summary>
        internal static List<BaseItemDto> NarrowRow(IEnumerable<BaseItemDto> items, Func<BaseItemDto, bool> keep, int? wanted)
        {
            var kept = items.Where(keep);
            return (wanted is { } count ? kept.Take(count) : kept).ToList();
        }

        /// <summary>
        /// What a home row keeps: under a server's tab the titles that server has,
        /// under "This server" everything not shown from another server, and always
        /// nothing whose every server is offline. An episode or a track goes by its
        /// show or album. Null when there is nothing to take out.
        /// </summary>
        internal Func<BaseItemDto, bool>? HomeRowRule(string? origin, bool localOnly)
        {
            var unavailable = UnavailableItemIds();
            HashSet<Guid>? onServer = null;
            HashSet<Guid>? shownFromElsewhere = null;
            if (!string.IsNullOrEmpty(origin))
            {
                onServer = ItemIdsOnServer(origin, null, null).ToHashSet();
            }
            else if (localOnly)
            {
                shownFromElsewhere = _library.GetItemIds(new InternalItemsQuery { Tags = new[] { Marker }, Recursive = true }).ToHashSet();
            }

            if (onServer == null && shownFromElsewhere == null && unavailable.Length == 0)
            {
                return null;
            }

            var offline = unavailable.ToHashSet();
            bool In(HashSet<Guid> set, BaseItemDto dto)
                => set.Contains(dto.Id)
                    || (dto.SeriesId is { } series && set.Contains(series))
                    || (dto.AlbumId is { } album && set.Contains(album));

            return dto => dto != null
                && !In(offline, dto)
                && (onServer == null || In(onServer, dto))
                && (shownFromElsewhere == null || !In(shownFromElsewhere, dto));
        }

        private static bool IsHomeRow(ActionExecutingContext context)
        {
            var values = context.ActionDescriptor.RouteValues;
            if (!values.TryGetValue("controller", out var controller) || !values.TryGetValue("action", out var action))
            {
                return false;
            }

            return (string.Equals(controller, "UserLibrary", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(action, "GetLatestMedia", StringComparison.Ordinal) || string.Equals(action, "GetLatestMediaLegacy", StringComparison.Ordinal)))
                || (string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(action, "GetResumeItems", StringComparison.Ordinal) || string.Equals(action, "GetResumeItemsLegacy", StringComparison.Ordinal)))
                || (string.Equals(controller, "TvShows", StringComparison.OrdinalIgnoreCase) && string.Equals(action, "GetNextUp", StringComparison.Ordinal));
        }

        private static bool IsItemsQuery(ActionExecutingContext context)
        {
            var values = context.ActionDescriptor.RouteValues;
            return values.TryGetValue("controller", out var controller)
                && string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
                && values.TryGetValue("action", out var action)
                && (string.Equals(action, "GetItems", StringComparison.Ordinal)
                    || string.Equals(action, "GetItemsByUserIdLegacy", StringComparison.Ordinal));
        }
    }
}
