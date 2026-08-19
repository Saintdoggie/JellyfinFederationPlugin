using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Middleware
{
    /// <summary>
    /// Rewrites the response body of requests for jellyfin-web's index.html to
    /// inject the federation badge script tag, without ever touching the file
    /// on disk. Complements <see cref="Services.WebClientInjector"/>, which
    /// patches index.html at startup but silently does nothing on read-only
    /// filesystems (e.g. some Docker deployments) - this middleware injects
    /// the same tag at serve-time on every response, so the badge/hide/download
    /// UI works regardless of filesystem permissions and self-heals instantly
    /// after a jellyfin-web upgrade replaces index.html, with no restart needed.
    /// </summary>
    public class BadgeScriptInjectionMiddleware
    {
        private const string Marker = "<!-- jellyfin-federation-badge -->";
        private const string ScriptTag = "<script defer src=\"/Plugins/Federation/ClientScript\"></script>" + Marker;

        private readonly RequestDelegate _next;
        private readonly ILogger<BadgeScriptInjectionMiddleware> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="BadgeScriptInjectionMiddleware"/> class.
        /// </summary>
        public BadgeScriptInjectionMiddleware(RequestDelegate next, ILogger<BadgeScriptInjectionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        /// <summary>
        /// Invokes the middleware.
        /// </summary>
        public async Task InvokeAsync(HttpContext context)
        {
            if (!IsLikelyIndexHtmlRequest(context.Request.Path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Force identity encoding so any downstream response-compression
            // middleware skips compressing this response - the raw HTML text
            // needs to be readable below, not gzip/br-encoded bytes.
            var originalAcceptEncoding = context.Request.Headers.AcceptEncoding;
            context.Request.Headers.AcceptEncoding = StringValues.Empty;

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalBody;
                context.Request.Headers.AcceptEncoding = originalAcceptEncoding;
            }

            buffer.Seek(0, SeekOrigin.Begin);

            var isHtml = context.Response.ContentType != null
                && context.Response.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);

            if (!isHtml || context.Response.StatusCode != StatusCodes.Status200OK)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            string html;
            using (var reader = new StreamReader(buffer, Encoding.UTF8, false, 4096, leaveOpen: true))
            {
                html = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (!html.Contains(Marker, StringComparison.Ordinal))
            {
                var bodyCloseIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyCloseIndex >= 0)
                {
                    html = html.Insert(bodyCloseIndex, ScriptTag);
                }
                else
                {
                    _logger.LogDebug("[Federation] index.html response has no </body> tag; leaving response unmodified");
                }
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }

        private static bool IsLikelyIndexHtmlRequest(PathString path)
        {
            var value = path.Value;
            if (string.IsNullOrEmpty(value) || value == "/")
            {
                return true;
            }

            return value.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)
                || value.Equals("/web", StringComparison.OrdinalIgnoreCase)
                || value.Equals("/web/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
