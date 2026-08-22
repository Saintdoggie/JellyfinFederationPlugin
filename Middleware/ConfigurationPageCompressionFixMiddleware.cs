using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Middleware
{
    /// <summary>
    /// Works around a server-side ResponseCompression bug that makes the dashboard's
    /// plugin configuration page (<c>/web/ConfigurationPage</c>) unreadable when the
    /// client asks for <c>gzip</c> or <c>br</c>.
    /// <para>
    /// That endpoint is served by Jellyfin's own <c>DashboardController</c> via
    /// <c>File(stream, "text/html")</c> (a <c>FileStreamResult</c>). When the response
    /// is compressed, the compressed binary is later read back through a
    /// <c>StreamReader(Encoding.UTF8)</c> somewhere in the pipeline and re-encoded
    /// as UTF-8, which replaces any byte that is not valid UTF-8 with U+FFFD
    /// (<c>ef bf bd</c>) — the exact corruption seen live: a gzip file that should
    /// start <c>1f 8b 08</c> instead starts <c>1f ef bf bd 08</c> and fails to
    /// decompress in every browser (<c>ERR_CONTENT_DECODING_FAILED</c>), while the
    /// same HTML served via this plugin's own <c>/Plugins/Federation/Config</c>
    /// (<c>ContentResult</c> with a string) compresses correctly. The uncompressed
    /// (<c>identity</c>) response is always correct (172 KB, vs. 76 KB gzip / 66 KB br).
    /// </para>
    /// <para>
    /// The fix is to prevent compression for this single endpoint: strip
    /// <c>Accept-Encoding</c> before the request reaches the compression middleware,
    /// so the response is sent as <c>identity</c> (no <c>Content-Encoding</c>). The
    /// browser can handle an uncompressed response even though it advertised
    /// <c>gzip, br</c>; the page is only 172 KB and is admin-only, so the size
    /// difference is irrelevant. Other requests (including other plugin pages that
    /// suffer the same bug) are also fixed, but the change is harmless for them
    /// too — they are also small, HTML, admin-only pages.
    /// </para>
    /// </summary>
    public class ConfigurationPageCompressionFixMiddleware
    {
        private readonly RequestDelegate _next;

        public ConfigurationPageCompressionFixMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // DashboardController serves plugin config pages at /web/ConfigurationPage
            // (case-insensitive routing in ASP.NET Core, but be explicit). This is the
            // only endpoint known to produce the corrupted gzip/br bodies; see class doc
            // above for why.
            if (IsConfigurationPageRequest(context.Request.Path))
            {
                var originalAcceptEncoding = context.Request.Headers.AcceptEncoding;
                context.Request.Headers.AcceptEncoding = StringValues.Empty;

                try
                {
                    await _next(context).ConfigureAwait(false);
                }
                finally
                {
                    context.Request.Headers.AcceptEncoding = originalAcceptEncoding;
                }

                return;
            }

            await _next(context).ConfigureAwait(false);
        }

        private static bool IsConfigurationPageRequest(PathString path)
        {
            var value = path.Value;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            // Matches /web/ConfigurationPage and /web/configurationpage (case-insensitive)
            return value.Equals("/web/ConfigurationPage", StringComparison.OrdinalIgnoreCase)
                || value.Equals("/web/configurationpage", StringComparison.OrdinalIgnoreCase);
        }
    }
}
