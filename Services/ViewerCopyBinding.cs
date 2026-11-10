using System;
using System.Collections.Concurrent;
using System.Linq;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

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
    public sealed class FederationPlaybackProfileFilter : IActionFilter
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

        /// <inheritdoc />
        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }
}
