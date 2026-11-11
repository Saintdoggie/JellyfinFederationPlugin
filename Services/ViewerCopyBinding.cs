using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Which copy a title's own media source id means for one viewer's device.
    /// </summary>
    /// <remarks>
    /// Players ask to play "the title" (the source whose id is the item's id).
    /// Which friend's copy that is can now differ per device: a browser gets the
    /// copy it plays as stored, a TV may get another. The choice is made when the
    /// player sends its capabilities (PlaybackInfo) and must then stay the same
    /// for every later request of that play - stream, playlist, segment, progress
    /// - which arrive without the capabilities. In memory only; holds ids, never
    /// addresses or tokens.
    /// </remarks>
    public sealed class ViewerCopyBinding
    {
        /// <summary>A binding nobody used for this long is forgotten (longer than any film).</summary>
        internal static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);

        private const int Max = 4096;

        private readonly ConcurrentDictionary<string, Bound> _bound = new(StringComparer.Ordinal);

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>The shared instance every playback path consults.</summary>
        internal static ViewerCopyBinding Shared { get; } = new();

        /// <summary>Binds the title's own source id to a copy for this viewer's device.</summary>
        internal void Bind(Guid userId, string? deviceId, Guid itemId, string copyKey, string? remoteMediaSourceId)
        {
            var now = UtcNow();
            _bound[Scope(userId, deviceId, itemId)] = new Bound(copyKey, remoteMediaSourceId, now);
            if (_bound.Count > Max)
            {
                foreach (var stale in _bound.OrderBy(p => p.Value.SeenUtc).Take(_bound.Count - (Max * 3 / 4)).ToList())
                {
                    _bound.TryRemove(stale.Key, out _);
                }
            }
        }

        /// <summary>The copy bound for this viewer's device, refreshed by being asked for; null when none.</summary>
        internal (string CopyKey, string? RemoteMediaSourceId)? Find(Guid userId, string? deviceId, Guid itemId)
        {
            var scope = Scope(userId, deviceId, itemId);
            if (!_bound.TryGetValue(scope, out var bound))
            {
                return null;
            }

            var now = UtcNow();
            if (now - bound.SeenUtc > ForgetAfter)
            {
                _bound.TryRemove(scope, out _);
                return null;
            }

            _bound[scope] = bound with { SeenUtc = now };
            return (bound.CopyKey, bound.RemoteMediaSourceId);
        }

        /// <summary>Drops the binding, for example when its copy stopped answering.</summary>
        internal void Forget(Guid userId, string? deviceId, Guid itemId)
            => _bound.TryRemove(Scope(userId, deviceId, itemId), out _);

        internal void Reset() => _bound.Clear();

        private static string Scope(Guid userId, string? deviceId, Guid itemId)
        {
            var device = deviceId?.Trim().ToLowerInvariant() ?? string.Empty;
            return userId.ToString("N") + "|" + (device.Length <= 128 ? device : device.Substring(0, 128)) + "|" + itemId.ToString("N");
        }

        private sealed record Bound(string CopyKey, string? RemoteMediaSourceId, DateTime SeenUtc);
    }

    /// <summary>
    /// Keeps the capabilities a player sent with its play request where the media
    /// source provider can read them. Jellyfin has already read the request body
    /// by the time the provider runs, so this is the only place to catch them.
    /// </summary>
    public sealed class FederationPlaybackProfileFilter : IAsyncActionFilter
    {
        private const string ItemKey = "Federation.DeviceProfile";
        private const string SourceKey = "Federation.RequestedMediaSourceId";

        /// <summary>The device profile sent with this request, or null.</summary>
        internal static DeviceProfile? Get(HttpContext? context)
            => context != null && context.Items.TryGetValue(ItemKey, out var value) ? value as DeviceProfile : null;

        /// <summary>The media source id named in the body of this play request, or null.</summary>
        internal static string? GetRequestedMediaSourceId(HttpContext? context)
            => context != null && context.Items.TryGetValue(SourceKey, out var value) ? value as string : null;

        /// <summary>Test seam and internal callers: attaches a profile to a request.</summary>
        internal static void Set(HttpContext context, DeviceProfile profile) => context.Items[ItemKey] = profile;

        /// <inheritdoc />
        public async System.Threading.Tasks.Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            OnActionExecuting(context);
            var executed = await next().ConfigureAwait(false);
            OnActionExecuted(executed);
            try
            {
                await ApplySoundHelpAsync(context, executed).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The response Jellyfin built is still a valid one.
                context.HttpContext.RequestServices.GetService<Microsoft.Extensions.Logging.ILogger<FederationPlaybackProfileFilter>>()
                    ?.LogDebug(ex, "[Federation] Sound help check failed");
            }
        }

        /// <summary>Reads what the player sent before Jellyfin answers (see <see cref="Get"/>).</summary>
        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (!context.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller)
                || !string.Equals(controller, "MediaInfo", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var argument in context.ActionArguments.Values)
            {
                if (argument == null)
                {
                    continue;
                }

                try
                {
                    // The DTO type lives in Jellyfin's API assembly; read it by shape.
                    var type = argument.GetType();
                    if (type.GetProperty("DeviceProfile")?.GetValue(argument) is DeviceProfile profile)
                    {
                        Set(context.HttpContext, profile);
                        if (type.GetProperty("MediaSourceId")?.GetValue(argument) is string sourceId && sourceId.Length > 0)
                        {
                            context.HttpContext.Items[SourceKey] = sourceId;
                        }

                        return;
                    }
                }
                catch (Exception)
                {
                    // Without the profile the choice simply falls back to the device-neutral one.
                }
            }
        }

        /// <summary>Reorders Jellyfin's answer where a friend's copy must come first.</summary>
        public void OnActionExecuted(ActionExecutedContext context)
        {
            // A title this server owns, with a friend's copy offered beside the own
            // file (see OwnedTitleFriendCopies): Jellyfin lists the own file first
            // even when it has to be converted, and many players take the first.
            try
            {
                if (context.HttpContext.Items.TryGetValue(OwnedTitleFriendCopies.OfferKey, out var marker)
                    && marker is IReadOnlyCollection<string> offered
                    && context.Result is Microsoft.AspNetCore.Mvc.ObjectResult { Value: MediaBrowser.Model.MediaInfo.PlaybackInfoResponse response }
                    && response.MediaSources is { Count: > 1 } sources)
                {
                    response.MediaSources = OwnedTitleFriendCopies.Order(sources, offered);
                }
            }
            catch (Exception)
            {
                // The response Jellyfin built is still a valid one.
            }
        }
        // A friend's title this server may not convert, on a device that plays
        // its picture: copy the picture and fix the sound (SoundHelpService).
        private static async System.Threading.Tasks.Task ApplySoundHelpAsync(ActionExecutingContext context, ActionExecutedContext executed)
        {
            if (TranscodePolicy.MayConvertForOwnViewers(Plugin.Instance?.Configuration)
                || executed.Result is not Microsoft.AspNetCore.Mvc.ObjectResult { Value: MediaBrowser.Model.MediaInfo.PlaybackInfoResponse response }
                || !context.ActionArguments.TryGetValue("itemId", out var rawItem) || rawItem is not Guid itemId)
            {
                return;
            }

            var services = context.HttpContext.RequestServices;
            var soundHelp = services.GetService<SoundHelpService>();
            var library = services.GetService<MediaBrowser.Controller.Library.ILibraryManager>();
            var authorization = services.GetService<MediaBrowser.Controller.Net.IAuthorizationContext>();
            if (soundHelp == null || library == null || authorization == null
                || FederationLibraryManager.GetFederationKey(library.GetItemById(itemId)) == null)
            {
                return;
            }

            var info = await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (info?.UserId is not { } userId || userId == Guid.Empty)
            {
                return;
            }

            int? audioIndex = null;
            long? startTicks = null;
            foreach (var argument in context.ActionArguments)
            {
                if (argument.Value == null)
                {
                    continue;
                }

                switch (argument.Key.ToLowerInvariant())
                {
                    case "audiostreamindex" when argument.Value is int index:
                        audioIndex ??= index;
                        break;
                    case "starttimeticks" when argument.Value is long ticks:
                        startTicks ??= ticks;
                        break;
                    default:
                        var type = argument.Value.GetType();
                        if (type.GetProperty("DeviceProfile") != null)
                        {
                            audioIndex = type.GetProperty("AudioStreamIndex")?.GetValue(argument.Value) as int? ?? audioIndex;
                            startTicks = type.GetProperty("StartTimeTicks")?.GetValue(argument.Value) as long? ?? startTicks;
                        }

                        break;
                }
            }

            await soundHelp.ApplyAsync(
                response,
                new SoundHelpService.SoundHelpRequest(userId, itemId, Get(context.HttpContext), audioIndex, startTicks, info.User?.AudioLanguagePreference),
                context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
