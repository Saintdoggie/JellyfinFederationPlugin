using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Puts friends' watch parties into SyncPlay's own group list (<c>/SyncPlay/List</c>)
    /// and answers a join of one (<c>/SyncPlay/Join</c>) by joining this server's copy
    /// of the party, so every app that can watch together finds and joins them like any
    /// other group. Requests about this server's own groups are untouched, and a failure
    /// leaves the native answer as it was.
    /// </summary>
    public sealed class FederatedPartyFilter : IAsyncActionFilter, IAsyncResultFilter
    {
        private readonly IAuthorizationContext _authorization;
        private readonly IUserManager _users;
        private readonly FederatedPartyService _parties;
        private readonly ILogger<FederatedPartyFilter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederatedPartyFilter"/> class.
        /// </summary>
        public FederatedPartyFilter(IAuthorizationContext authorization, IUserManager users, FederatedPartyService parties, ILogger<FederatedPartyFilter> logger)
        {
            _authorization = authorization;
            _users = users;
            _parties = parties;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (IsAction(context.ActionDescriptor.RouteValues, "SyncPlayJoinGroup")
                && context.ActionArguments.TryGetValue("requestData", out var body)
                && GroupIdOf(body) is { } groupId
                && groupId != Guid.Empty)
            {
                try
                {
                    if (await _parties.IsFriendPartyAsync(groupId).ConfigureAwait(false))
                    {
                        var info = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
                        var user = info?.User ?? (info != null && info.UserId != Guid.Empty ? _users.GetUserById(info.UserId) : null);
                        if (user == null || info == null)
                        {
                            context.Result = new UnauthorizedResult();
                            return;
                        }

                        var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
                        var result = await _parties.JoinAsync(user, info, ip, groupId).ConfigureAwait(false);
                        context.Result = result.Status == 200
                            ? new NoContentResult()
                            : new ObjectResult(new { error = result.Message }) { StatusCode = result.Status };
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Joining a friend's watch party failed");
                    context.Result = new ObjectResult(new { error = "The watch party couldn't be joined. Try again." }) { StatusCode = 502 };
                    return;
                }
            }

            await next().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            if (IsAction(context.ActionDescriptor.RouteValues, "SyncPlayGetGroups")
                && context.Result is ObjectResult { Value: IEnumerable<GroupInfoDto> groups } result)
            {
                try
                {
                    var info = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
                    var user = info?.User ?? (info != null && info.UserId != Guid.Empty ? _users.GetUserById(info.UserId) : null);
                    if (user != null)
                    {
                        var friends = await _parties.ListAsync(user).ConfigureAwait(false);
                        if (friends.Count > 0)
                        {
                            result.Value = Merge(groups, friends);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Could not add friends' watch parties to the group list");
                }
            }

            await next().ConfigureAwait(false);
        }

        /// <summary>The native groups followed by friends' parties not already listed.</summary>
        internal static List<GroupInfoDto> Merge(IEnumerable<GroupInfoDto> native, IEnumerable<GroupInfoDto> friends)
        {
            var list = native.ToList();
            var ids = list.Select(g => g.GroupId).ToHashSet();
            list.AddRange(friends.Where(f => ids.Add(f.GroupId)));
            return list;
        }

        /// <summary>The group id of SyncPlay's join body (a Jellyfin.Api type, read by name).</summary>
        internal static Guid? GroupIdOf(object? body)
            => body?.GetType().GetProperty("GroupId")?.GetValue(body) is Guid id ? id : null;

        private static bool IsAction(IDictionary<string, string?> values, string action)
            => values.TryGetValue("controller", out var controller)
                && string.Equals(controller, "SyncPlay", StringComparison.OrdinalIgnoreCase)
                && values.TryGetValue("action", out var name)
                && string.Equals(name, action, StringComparison.Ordinal);
    }
}
