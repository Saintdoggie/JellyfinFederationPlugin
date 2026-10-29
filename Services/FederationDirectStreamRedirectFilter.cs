using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Sends a native client's direct-play request for a federated title straight
    /// to the friend's server instead of relaying it through this one.
    /// </summary>
    /// <remarks>
    /// Apps such as Findroid do not play <c>MediaSourceInfo.Path</c>; they ask this
    /// server for <c>/Videos/{id}/stream?static=true</c>. For an Http source
    /// Jellyfin answers that by downloading the peer's file and re-sending every
    /// byte over this server's upload. When the chosen source is a Direct-mode
    /// peer source, this answers with a 302 to that source's own item-scoped
    /// DirectStream URL instead - the same URL PlaybackInfo already hands browsers.
    /// <para>
    /// Jellyfin's stream endpoint itself allows anonymous requests, so this only
    /// ever redirects for an authenticated user who can see the item; everything
    /// else (anonymous, API-key, transcode, live stream, Proxy-mode source, a
    /// scheme change a player may refuse to follow) falls through to Jellyfin
    /// unchanged. Any failure here also falls through.
    /// </para>
    /// </remarks>
    public sealed class FederationDirectStreamRedirectFilter : IAsyncActionFilter
    {
        private readonly IAuthorizationContext _authorization;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly IMediaSourceManager _mediaSources;
        private readonly ILogger<FederationDirectStreamRedirectFilter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDirectStreamRedirectFilter"/> class.
        /// </summary>
        public FederationDirectStreamRedirectFilter(
            IAuthorizationContext authorization,
            IUserManager users,
            ILibraryManager library,
            IMediaSourceManager mediaSources,
            ILogger<FederationDirectStreamRedirectFilter> logger)
        {
            _authorization = authorization;
            _users = users;
            _library = library;
            _mediaSources = mediaSources;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (IsStaticVideoStream(context))
            {
                try
                {
                    var target = await ResolveRedirectAsync(context).ConfigureAwait(false);
                    if (target != null)
                    {
                        FederationPlaybackRoutes.Record(RemoteMediaPath.QueryValue(context.HttpContext.Request.Query, "PlaySessionId"), FederatedRoute.Direct);
                        // The URL carries an item-scoped token; never log it.
                        _logger.LogInformation("[Federation] Direct-play request for federated item {ItemId} sent straight to the friend's server", context.ActionArguments["itemId"]);
                        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                        context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
                        context.Result = new RedirectResult(target, permanent: false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Direct-play redirect check failed; letting Jellyfin serve the request");
                }
            }

            await next().ConfigureAwait(false);
        }

        /// <summary>
        /// The URL to redirect a static stream request to, or null when the source
        /// must stay with Jellyfin: only a remote Http source on a peer's
        /// DirectStream gateway, and only when the redirect keeps the request's
        /// scheme (ExoPlayer, for one, refuses cross-protocol redirects by default).
        /// </summary>
        internal static string? RedirectTarget(MediaSourceInfo? source, string requestScheme)
        {
            if (source is not { IsRemote: true, Protocol: MediaProtocol.Http }
                || string.IsNullOrEmpty(source.Path)
                || !Uri.TryCreate(source.Path, UriKind.Absolute, out var uri)
                || !uri.AbsolutePath.Contains("/Plugins/Federation/DirectStream/", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return source.Path;
        }

        /// <summary>
        /// The client scheme after Jellyfin's trusted-proxy middleware has processed
        /// any forwarding headers.
        /// </summary>
        internal static string RequestScheme(HttpRequest request) => request.Scheme;

        private static bool IsStaticVideoStream(ActionExecutingContext context)
        {
            var values = context.ActionDescriptor.RouteValues;
            if (!values.TryGetValue("controller", out var controller)
                || !string.Equals(controller, "Videos", StringComparison.OrdinalIgnoreCase)
                || !values.TryGetValue("action", out var action)
                || (!string.Equals(action, "GetVideoStream", StringComparison.Ordinal)
                    && !string.Equals(action, "GetVideoStreamByContainer", StringComparison.Ordinal)))
            {
                return false;
            }

            return context.ActionArguments.TryGetValue("static", out var isStatic) && isStatic is true
                && !(context.ActionArguments.TryGetValue("liveStreamId", out var live) && live is string { Length: > 0 })
                && context.ActionArguments.TryGetValue("itemId", out var id) && id is Guid;
        }

        private async Task<string?> ResolveRedirectAsync(ActionExecutingContext context)
        {
            var itemId = (Guid)context.ActionArguments["itemId"]!;
            var item = _library.GetItemById(itemId);
            if (item == null || FederationLibraryManager.GetFederationKey(item) == null)
            {
                return null;
            }

            var info = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (info == null || !info.IsAuthenticated)
            {
                return null;
            }

            var user = info.User ?? (info.UserId != Guid.Empty ? _users.GetUserById(info.UserId) : null);
            if (user == null || _library.GetItemById<BaseItem>(itemId, user) == null)
            {
                return null;
            }

            var sources = await _mediaSources.GetPlaybackMediaSources(item, user, false, false, context.HttpContext.RequestAborted).ConfigureAwait(false);
            var requested = context.ActionArguments.TryGetValue("mediaSourceId", out var id) ? id as string : null;
            var source = string.IsNullOrEmpty(requested)
                ? sources.FirstOrDefault()
                : sources.FirstOrDefault(s => string.Equals(s.Id, requested, StringComparison.OrdinalIgnoreCase));
            return RedirectTarget(source, RequestScheme(context.HttpContext.Request));
        }
    }
}
