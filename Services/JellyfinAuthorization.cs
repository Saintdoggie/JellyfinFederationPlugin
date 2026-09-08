using System;
using System.Net.Http.Headers;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Native Jellyfin authentication, including servers with legacy auth disabled.</summary>
internal static class JellyfinAuthorization
{
    internal static AuthenticationHeaderValue Create(string token) =>
        new("MediaBrowser", $"Token=\"{Uri.EscapeDataString(token)}\"");
}
