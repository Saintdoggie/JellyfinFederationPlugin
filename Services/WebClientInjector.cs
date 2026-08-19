using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Injects a script tag into jellyfin-web's <c>index.html</c> so the federation
    /// badge script (see <c>Web/federation-badge.js</c> and
    /// <see cref="Api.FederationController.GetClientScript"/>) actually runs in the
    /// browser. Jellyfin has no server-side templating for its web client - it
    /// serves index.html as a static file - so this is the same technique other
    /// community plugins use to add client-side behavior without a jellyfin-web
    /// fork. Idempotent and safe to call on every server start: it looks for its
    /// own marker comment first and does nothing if already present, and a
    /// jellyfin-web upgrade that replaces index.html just means the marker is
    /// gone and the next startup re-injects it.
    /// </summary>
    public class WebClientInjector
    {
        private const string Marker = "<!-- jellyfin-federation-badge -->";
        private const string ScriptTag = "<script defer src=\"/Plugins/Federation/ClientScript\"></script>" + Marker;

        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger<WebClientInjector> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebClientInjector"/> class.
        /// </summary>
        public WebClientInjector(IApplicationPaths applicationPaths, ILogger<WebClientInjector> logger)
        {
            _applicationPaths = applicationPaths;
            _logger = logger;
        }

        /// <summary>
        /// Ensures the badge script tag is present in the served index.html. Never
        /// throws - a failure here (read-only filesystem, unexpected web client
        /// layout, index.html not hosted at all) must not prevent the rest of the
        /// plugin from starting; it just means the badge feature is silently
        /// unavailable.
        /// </summary>
        public void EnsureBadgeScriptInjected()
        {
            try
            {
                var webPath = _applicationPaths.WebPath;
                if (string.IsNullOrEmpty(webPath))
                {
                    _logger.LogDebug("[Federation] No WebPath reported (server not hosting static web content); skipping badge script injection");
                    return;
                }

                var indexPath = Path.Combine(webPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("[Federation] index.html not found at {Path}; skipping badge script injection", indexPath);
                    return;
                }

                var html = File.ReadAllText(indexPath);
                if (html.Contains(Marker, StringComparison.Ordinal))
                {
                    return;
                }

                var bodyCloseIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyCloseIndex < 0)
                {
                    _logger.LogWarning("[Federation] index.html has no </body> tag; skipping badge script injection");
                    return;
                }

                var updated = html.Insert(bodyCloseIndex, ScriptTag);
                File.WriteAllText(indexPath, updated);
                _logger.LogInformation("[Federation] Injected badge script tag into {Path}", indexPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not inject badge script into index.html; the server-icon badge will not appear, everything else is unaffected");
            }
        }

        /// <summary>
        /// Removes the badge script tag from index.html, if present. Called from
        /// <see cref="Plugin.OnUninstalling"/> so uninstalling this plugin doesn't
        /// leave a &lt;script src="/Plugins/Federation/ClientScript"&gt; tag pointing
        /// at a route that no longer exists permanently baked into jellyfin-web.
        /// Never throws, for the same reason as <see cref="EnsureBadgeScriptInjected"/>:
        /// a failure here (read-only filesystem, index.html already gone) must not
        /// block the rest of uninstall.
        /// </summary>
        public void RemoveBadgeScriptInjection()
        {
            try
            {
                var webPath = _applicationPaths.WebPath;
                if (string.IsNullOrEmpty(webPath))
                {
                    return;
                }

                var indexPath = Path.Combine(webPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    return;
                }

                var html = File.ReadAllText(indexPath);
                if (!html.Contains(Marker, StringComparison.Ordinal))
                {
                    return;
                }

                var updated = html.Replace(ScriptTag, string.Empty, StringComparison.Ordinal);
                File.WriteAllText(indexPath, updated);
                _logger.LogInformation("[Federation] Removed badge script tag from {Path}", indexPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not remove badge script from index.html on uninstall; a leftover <script> tag pointing at a now-missing route may remain");
            }
        }
    }
}
