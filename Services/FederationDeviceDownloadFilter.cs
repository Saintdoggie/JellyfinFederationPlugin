using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Answers Jellyfin's own <c>/Items/{id}/Download</c> for a friend's title, the
    /// request every app's Download button makes (web, Android, Findroid, Swiftfin).
    /// </summary>
    /// <remarks>
    /// Jellyfin would try to send a local file that doesn't exist and fail. This gets
    /// a download permit for the viewer from the friend that owns the file and sends
    /// the app straight there, so the file never passes through this server. When it
    /// can't (an older friend that doesn't name the file, or an https app and a
    /// friend with no reachable https address) the file is relayed through this server
    /// instead. Refusals say why (downloads off, the friend's hourly limit, offline).
    /// Requests for this server's own files are untouched.
    /// </remarks>
    public sealed class FederationDeviceDownloadFilter : IAsyncActionFilter
    {
        // One activity entry per viewer and title per hour: apps resume a large
        // download with many requests, and each would otherwise be logged.
        private static readonly TimeSpan ActivityWindow = TimeSpan.FromHours(1);
        private static readonly ConcurrentDictionary<string, DateTime> Logged = new(StringComparer.Ordinal);

        private readonly IAuthorizationContext _authorization;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly FederationDeviceDownloadService _downloads;
        private readonly FederationStreamHandler _streams;
        private readonly IActivityManager? _activity;
        private readonly ILocalizationManager? _localization;
        private readonly ILogger<FederationDeviceDownloadFilter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDeviceDownloadFilter"/> class.
        /// </summary>
        public FederationDeviceDownloadFilter(
            IAuthorizationContext authorization,
            IUserManager users,
            ILibraryManager library,
            FederationDeviceDownloadService downloads,
            FederationStreamHandler streams,
            ILogger<FederationDeviceDownloadFilter> logger,
            IActivityManager? activity = null,
            ILocalizationManager? localization = null)
        {
            _authorization = authorization;
            _users = users;
            _library = library;
            _downloads = downloads;
            _streams = streams;
            _logger = logger;
            _activity = activity;
            _localization = localization;
        }

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!IsDownload(context, out var itemId))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var item = _library.GetItemById(itemId);
            if (FederationLibraryManager.GetFederationKey(item) == null)
            {
                await next().ConfigureAwait(false);
                return;
            }

            var http = context.HttpContext;
            try
            {
                var info = await _authorization.GetAuthorizationInfo(http).ConfigureAwait(false);
                var user = info?.User ?? (info != null && info.UserId != Guid.Empty ? _users.GetUserById(info.UserId) : null);
                if (user == null)
                {
                    context.Result = Refuse(http, DeviceDownloadPlan.Refused(403, "Sign in to download."));
                    return;
                }

                if (_library.GetItemById<BaseItem>(itemId, user) == null)
                {
                    context.Result = Refuse(http, DeviceDownloadPlan.Refused(404, "Item not found."));
                    return;
                }

                var plan = await _downloads.PlanAsync(user, item!, FederationDirectStreamRedirectFilter.RequestScheme(http.Request), http.RequestAborted).ConfigureAwait(false);
                if (plan.Status != 200 || plan.Url == null)
                {
                    context.Result = Refuse(http, plan);
                    return;
                }

                await LogActivityAsync(user, item!, info, plan.ServerName).ConfigureAwait(false);
                http.Response.Headers.CacheControl = "private, no-store";
                if (plan.Route == DeviceDownloadRoute.Redirect)
                {
                    // The URL carries the viewer's download permit; never log it.
                    _logger.LogInformation("[Federation] {User} is downloading {Item} straight from {Server}", user.Username, item!.Name, plan.ServerName);
                    context.Result = new RedirectResult(plan.Url, permanent: false);
                    return;
                }

                _logger.LogInformation("[Federation] {User} is downloading {Item} from {Server} through this server because {Reason}", user.Username, item!.Name, plan.ServerName, plan.RelayReason ?? "a direct link wasn't possible");
                http.Response.Headers.ContentDisposition = DeviceDownload.AttachmentHeader(plan.FileName);
                await _streams.HandleDirectGatewayAsync(plan.Url, http.Request, http.Response, http.RequestAborted).ConfigureAwait(false);
                if (http.Response.StatusCode is 401 or 403 && !http.Response.HasStarted)
                {
                    // Revoked between the permit and the first byte: say so plainly.
                    _logger.LogInformation("[Federation] {Server} refused a relayed download of {Item}", plan.ServerName, item.Name);
                    http.Response.Headers.ContentDisposition = default;
                    context.Result = Refuse(http, DeviceDownloadPlan.Refused(403, $"{plan.ServerName} no longer allows downloading this title."));
                    return;
                }

                context.Result = new EmptyResult();
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                context.Result = new EmptyResult();
            }
            catch (Exception ex)
            {
                // Never fall through: Jellyfin would try to send a local file that
                // doesn't exist and answer with a server error.
                _logger.LogWarning(ex, "[Federation] Download of federated item {ItemId} failed", itemId);
                if (!http.Response.HasStarted)
                {
                    context.Result = Refuse(http, DeviceDownloadPlan.Refused(502, "The download couldn't be started. Try again."));
                }
                else
                {
                    context.Result = new EmptyResult();
                }
            }
        }

        /// <summary>True for Jellyfin's native download action with an item id.</summary>
        internal static bool IsDownload(ActionExecutingContext context, out Guid itemId)
        {
            itemId = Guid.Empty;
            var values = context.ActionDescriptor.RouteValues;
            if (!values.TryGetValue("controller", out var controller)
                || !string.Equals(controller, "Library", StringComparison.OrdinalIgnoreCase)
                || !values.TryGetValue("action", out var action)
                || !string.Equals(action, "GetDownload", StringComparison.Ordinal)
                || !context.ActionArguments.TryGetValue("itemId", out var id)
                || id is not Guid guid
                || guid == Guid.Empty)
            {
                return false;
            }

            itemId = guid;
            return true;
        }

        /// <summary>The JSON error answer, with Retry-After when the friend's limit frees up.</summary>
        internal static IActionResult Refuse(Microsoft.AspNetCore.Http.HttpContext http, DeviceDownloadPlan plan)
        {
            if (plan.RetryAfterSeconds is > 0)
            {
                http.Response.Headers.RetryAfter = plan.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
            }

            http.Response.Headers.CacheControl = "no-store";
            return new ObjectResult(new { error = plan.Error, retryAfterSeconds = plan.RetryAfterSeconds })
            {
                StatusCode = plan.Status
            };
        }

        private async Task LogActivityAsync(User user, BaseItem item, AuthorizationInfo? info, string? serverName)
        {
            if (_activity == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var key = user.Id.ToString("N") + "|" + item.Id.ToString("N");
            if (Logged.TryGetValue(key, out var last) && now - last < ActivityWindow)
            {
                return;
            }

            Logged[key] = now;
            foreach (var expired in Logged)
            {
                if (now - expired.Value >= ActivityWindow)
                {
                    Logged.TryRemove(expired.Key, out _);
                }
            }

            try
            {
                var name = string.IsNullOrEmpty(serverName) ? item.Name : $"{item.Name} (from {serverName})";
                var template = _localization?.GetServerLocalizedString("UserDownloadingItemWithValues");
                var appTemplate = _localization?.GetServerLocalizedString("AppDeviceValues");
                await _activity.CreateAsync(new ActivityLog(
                    string.Format(CultureInfo.InvariantCulture, string.IsNullOrEmpty(template) ? "{0} is downloading {1}" : template, user.Username, name),
                    "UserDownloadingContent",
                    user.Id)
                {
                    ShortOverview = info == null || string.IsNullOrEmpty(appTemplate)
                        ? null
                        : string.Format(CultureInfo.InvariantCulture, appTemplate, info.Client, info.Device),
                    ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture)
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not record the download in the activity log");
            }
        }
    }
}
