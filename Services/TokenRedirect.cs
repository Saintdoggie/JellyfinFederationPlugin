using System;
using System.Net;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Sends a request that carries a federation token without letting the HTTP
    /// client follow redirects on its own (which would hand the token to whatever
    /// host the answer points at). One redirect is followed here, and only to the
    /// same host (or between the apex and a subdomain of one site over https, like
    /// example.com and www.example.com), so a friend's http to https upgrade or
    /// www redirect still works.
    /// </summary>
    internal static class TokenRedirect
    {
        public static async Task<HttpResponseMessage> SendAsync(
            HttpClient client,
            Func<HttpRequestMessage> createRequest,
            CancellationToken cancellationToken)
        {
            using var first = createRequest();
            var response = await client.SendAsync(first, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location == null || first.RequestUri == null)
            {
                return response;
            }

            var target = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(first.RequestUri, response.Headers.Location);
            if (!IsAllowedHop(first.RequestUri, target))
            {
                return response;
            }

            response.Dispose();
            using var second = createRequest();
            second.RequestUri = target;
            return await client.SendAsync(second, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The host a 3xx response points at when it is a different host than the
        /// one asked (i.e. a redirect the token must not follow), otherwise null.
        /// </summary>
        internal static string? CrossHostRedirectTarget(HttpResponseMessage response, Uri requested)
        {
            if (!IsRedirect(response.StatusCode) || response.Headers.Location == null)
            {
                return null;
            }

            var target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(requested, response.Headers.Location);
            return IsAllowedHop(requested, target) || string.Equals(requested.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase)
                ? null
                : target.IdnHost;
        }

        /// <summary>Same-host hop, or an https to https hop between hosts of one site (apex and www).</summary>
        internal static bool IsAllowedHop(Uri from, Uri to)
            => IsSameHostHop(from, to)
                || (from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttps
                    && from.Port == to.Port && SameSite(from.IdnHost, to.IdnHost));

        /// <summary>
        /// True when one host is the other or a subdomain of it, or both sit under
        /// the same registrable domain. Conservative: IP addresses never match, and
        /// public suffixes (co.uk) and shared hosting suffixes (duckdns.org, ts.net)
        /// are never treated as a site, so two strangers' subdomains do not match.
        /// </summary>
        internal static bool SameSite(string a, string b)
        {
            if (System.Net.IPAddress.TryParse(a, out _) || System.Net.IPAddress.TryParse(b, out _))
            {
                return false;
            }

            var siteA = RegistrableDomain(a);
            return siteA != null && string.Equals(siteA, RegistrableDomain(b), StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] SharedSuffixes =
        {
            "co.uk", "org.uk", "ac.uk", "gov.uk", "com.au", "net.au", "org.au", "co.nz", "co.jp", "co.za", "com.br", "com.mx", "co.in", "co.il",
            "duckdns.org", "ts.net", "trycloudflare.com", "no-ip.org", "no-ip.com", "ddns.net", "dyndns.org", "myftp.org", "hopto.org", "zapto.org",
            "sytes.net", "github.io", "herokuapp.com", "vercel.app", "netlify.app", "pages.dev", "workers.dev", "cloudfront.net", "azurewebsites.net",
            "ngrok.io", "ngrok-free.app", "ngrok.app", "loca.lt", "serveo.net", "tunnelto.dev", "pinggy.io", "playit.gg", "gg.gg", "nip.io", "sslip.io",
            "fly.dev", "onrender.com", "web.app", "appspot.com", "firebaseapp.com", "azurecontainer.io", "run.app", "railway.app", "render.com",
        };

        private static string? RegistrableDomain(string host)
        {
            var labels = host.TrimEnd('.').ToLowerInvariant().Split('.');
            if (labels.Length < 2 || labels.Any(l => l.Length == 0))
            {
                return null;
            }

            var suffixLabels = 1;
            foreach (var suffix in SharedSuffixes)
            {
                var count = suffix.Count(c => c == '.') + 1;
                if (count > suffixLabels && labels.Length >= count && string.Join('.', labels.Skip(labels.Length - count)) == suffix)
                {
                    suffixLabels = count;
                }
            }

            // The host is itself a suffix (co.uk, ts.net): not a site anyone owns.
            return labels.Length <= suffixLabels ? null : string.Join('.', labels.Skip(labels.Length - suffixLabels - 1));
        }

        internal static bool IsSameHostHop(Uri from, Uri to)
        {
            if (!string.Equals(from.IdnHost, to.IdnHost, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (to.Scheme != Uri.UriSchemeHttp && to.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            // Same scheme and port, or an upgrade to https; never a downgrade.
            return (from.Scheme == to.Scheme && from.Port == to.Port)
                || (from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps);
        }

        internal static bool IsRedirect(HttpStatusCode code)
            => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

        /// <summary>
        /// For HttpClients that carry the federation token as a default header: the
        /// stock redirect handling would resend that header to any host the answer
        /// names. This handler follows only <see cref="IsAllowedHop"/> redirects
        /// (up to 5) and hands any other redirect back as the response.
        /// </summary>
        internal sealed class FollowHandler : DelegatingHandler
        {
            private readonly Action<Uri, Uri>? _onBlocked;

            public FollowHandler(Action<Uri, Uri>? onBlocked = null)
                : base(FederationHttp.Handler(allowAutoRedirect: false))
            {
                _onBlocked = onBlocked;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // The plugin's bodies are small JSON; buffered so an allowed 307/308
                // (an http address upgraded to https) can resend them, as the stock
                // handler did.
                byte[]? body = null;
                var original = request.Content;
                if (original != null)
                {
                    await original.LoadIntoBufferAsync().ConfigureAwait(false);
                    body = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }

                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                for (var hop = 0; hop < 5 && IsRedirect(response.StatusCode) && response.Headers.Location != null && request.RequestUri != null; hop++)
                {
                    var target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(request.RequestUri, response.Headers.Location);
                    if (!IsAllowedHop(request.RequestUri, target))
                    {
                        if (!string.Equals(request.RequestUri.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase))
                        {
                            _onBlocked?.Invoke(request.RequestUri, target);
                        }

                        return response;
                    }

                    // 307/308 keep the method and body; 301/302/303 turn a POST into
                    // a GET without one (what browsers and the stock handler do).
                    var keepsMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
                    var method = keepsMethod || request.Method == HttpMethod.Get || request.Method == HttpMethod.Head
                        ? request.Method
                        : HttpMethod.Get;

                    response.Dispose();
                    var next = new HttpRequestMessage(method, target);
                    foreach (var header in request.Headers)
                    {
                        next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (keepsMethod && body != null && original != null)
                    {
                        next.Content = new ByteArrayContent(body);
                        foreach (var header in original.Headers)
                        {
                            next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }

                    request = next;
                    response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }

                return response;
            }
        }
    }
}
