using System;

namespace Jellyfin.Plugin.Federation.Services;

internal static class FederationClientScriptHtml
{
    internal const string Marker = "<!-- jellyfin-federation-badge -->";
    internal const string Deferred = "<script defer src=\"/Plugins/Federation/ClientScript\"></script>" + Marker;
    internal const string Early = "<script src=\"/Plugins/Federation/ClientScript\"></script>" + Marker;

    internal static string Inject(string html, bool adaptive)
    {
        var cleaned = html.Replace(Deferred, string.Empty, StringComparison.Ordinal).Replace(Early, string.Empty, StringComparison.Ordinal);
        var body = cleaned.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (body < 0) return html;
        // The window plugin definition must exist before jellyfin-web loads its
        // config.json. Blocking only in the opt-in player preview avoids that race.
        var firstScript = cleaned.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        return cleaned.Insert(adaptive && firstScript >= 0 ? firstScript : body, adaptive ? Early : Deferred);
    }
}
