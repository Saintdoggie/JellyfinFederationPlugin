using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    /// </remarks>
    public sealed class FederationLocalOnlyItemsFilter : IAsyncActionFilter
    {
        internal const string Marker = "Federated";

        private readonly ILibraryManager _library;
        private readonly ILogger<FederationLocalOnlyItemsFilter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationLocalOnlyItemsFilter"/> class.
        /// </summary>
        public FederationLocalOnlyItemsFilter(ILibraryManager library, ILogger<FederationLocalOnlyItemsFilter> logger)
        {
            _library = library;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (IsItemsQuery(context) && AsksForLocalOnly(context.HttpContext.Request.Query["ExcludeItemTags"]))
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

        private static bool IsItemsQuery(ActionExecutingContext context)
        {
            var values = context.ActionDescriptor.RouteValues;
            return values.TryGetValue("controller", out var controller)
                && string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
                && values.TryGetValue("action", out var action)
                && (string.Equals(action, "GetItems", StringComparison.Ordinal)
                    || string.Equals(action, "GetItemsByUserIdLegacy", StringComparison.Ordinal))
                && context.ActionArguments.ContainsKey("excludeItemIds");
        }
    }
}
