using System;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Native client identity and transport capabilities, not a codec blacklist.</summary>
    public static class NativeClientCapabilities
    {
        public static (string Client, string Device) Identify(HttpRequest? request, AuthorizationInfo? info)
        {
            var authorization = request?.Headers.Authorization.ToString();
            var client = First(info?.Client, Header(request, "X-Emby-Client"), Value(authorization, "Client"));
            var device = First(info?.Device, Header(request, "X-Emby-Device-Name"), Value(authorization, "Device"));
            return (client, device);
        }

        public static bool SupportsRawHttpDirectPlay(HttpRequest? request, string? client, string? device)
        {
            if (string.IsNullOrWhiteSpace(client)
                || request?.Headers.Origin.Count > 0
                || request?.Headers.UserAgent.ToString().StartsWith("Mozilla/", StringComparison.Ordinal) == true)
            {
                return false;
            }

            var app = client.Trim().ToLowerInvariant();
            if (app.Contains("web", StringComparison.Ordinal)
                || app.Contains("browser", StringComparison.Ordinal)
                || app.Contains("chrome", StringComparison.Ordinal)
                || app.Contains("firefox", StringComparison.Ordinal)
                || app == "android"
                || app.Contains("jellyfin") && app.Contains("android", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static string Header(HttpRequest? request, string name)
            => request?.Headers.TryGetValue(name, out var value) == true ? value.ToString() : string.Empty;

        private static string Value(string? authorization, string name)
        {
            if (string.IsNullOrEmpty(authorization))
            {
                return string.Empty;
            }

            var marker = name + "=\"";
            var start = authorization.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return string.Empty;
            }

            start += marker.Length;
            var end = authorization.IndexOf('"', start);
            return end > start ? authorization.Substring(start, end - start) : string.Empty;
        }

        private static string First(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }
    }
}
