using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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

        internal static void ResetMemo() => Memo.Clear();

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var origin = context.HttpContext.Request.Query[OriginParameter].FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
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

            await next().ConfigureAwait(false);
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
