using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Receiver side: finds web-server (reverse proxy) headers that make a browser
    /// refuse a friend's video, which otherwise fails without any server error -
    /// the player just keeps loading and the friend sees a viewer stuck at 0:00.
    /// <list type="bullet">
    /// <item>On the friend: a second (or a list, or the wrong) <c>Access-Control-Allow-Origin</c>,
    /// typically an nginx <c>add_header</c> on top of Jellyfin's own. hls.js cannot read
    /// a single segment the friend converted.</item>
    /// <item>On the friend: <c>Cross-Origin-Resource-Policy</c> that blocks this site, so
    /// the video element cannot load a direct play.</item>
    /// <item>On this server's own web page: a <c>Content-Security-Policy</c> whose
    /// <c>connect-src</c>/<c>media-src</c> (or <c>default-src</c>) leaves the friend out,
    /// or <c>Cross-Origin-Embedder-Policy: require-corp</c>.</item>
    /// </list>
    /// A problem for converted video makes the handoff fall back to converting here
    /// (same site, so none of this applies); a direct-play problem can only be
    /// fixed in the web-server config, so it is logged. Each change is logged once,
    /// as a warning in the plugin's Log tab. Probes are cheap GETs of public URLs,
    /// cached per friend; a probe that fails is "don't know", never a problem.
    /// </summary>
    public static class BrowserPlaybackHeaders
    {
        /// <summary>How long a clean result is trusted.</summary>
        internal static readonly TimeSpan CleanFor = TimeSpan.FromMinutes(30);

        /// <summary>How long a problem (or an unanswered probe) is trusted, so a fixed config is noticed soon.</summary>
        internal static readonly TimeSpan ProblemFor = TimeSpan.FromMinutes(5);

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        private const string ProbeItem = "00000000000000000000000000000000";

        private static readonly ConcurrentDictionary<string, (DateTime Until, Task<Report> Check)> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, string> Logged = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clock, replaceable in tests.</summary>
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Test seam: the HTTP send used for probes.</summary>
        internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; }
            = (request, ct) => HandoffPlaylistLinks.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        /// <summary>
        /// What a browser on <paramref name="playerOrigin"/> would refuse from this
        /// friend: null fields mean nothing found (or the probe could not tell).
        /// </summary>
        public sealed record Report(string? ConvertedProblem, string? DirectPlayProblem);

        /// <summary>
        /// The cached (or a fresh) check of <paramref name="server"/> at
        /// <paramref name="peerBase"/> for players on <paramref name="playerOrigin"/>.
        /// Never throws.
        /// </summary>
        public static Task<Report> CheckAsync(RemoteServer server, string peerBase, string playerOrigin, ILogger? logger)
        {
            var key = server.Id + "|" + peerBase + "|" + playerOrigin;
            var now = UtcNow();
            if (Cache.TryGetValue(key, out var cached) && cached.Until > now)
            {
                return cached.Check;
            }

            var check = RunAsync(server, peerBase, playerOrigin, logger);
            // Until the probe answers, callers share it; once done the entry gets its real lifetime.
            Cache[key] = (now + ProbeTimeout + ProbeTimeout, check);
            _ = check.ContinueWith(
                t =>
                {
                    var clean = t.Result.ConvertedProblem == null && t.Result.DirectPlayProblem == null;
                    Cache[key] = (UtcNow() + (clean ? CleanFor : ProblemFor), t);
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
            return check;
        }

        /// <summary>
        /// The problem converted video from this friend would hit right now, or
        /// null; waits at most a few seconds for a first probe.
        /// </summary>
        public static async Task<string?> ConvertedProblemAsync(RemoteServer server, string peerBase, string playerOrigin, ILogger? logger)
        {
            var check = CheckAsync(server, peerBase, playerOrigin, logger);
            var done = await Task.WhenAny(check, Task.Delay(ProbeTimeout + ProbeTimeout)).ConfigureAwait(false);
            return done == check ? check.Result.ConvertedProblem : null;
        }

        /// <summary>
        /// The origin browsers load this server's web client from: the public
        /// <c>ServerUrl</c> (what friends and the reverse proxy use), else null.
        /// </summary>
        public static string? OwnOrigin() => Origin(Plugin.Instance?.Configuration?.ServerUrl);

        /// <summary><c>scheme://host[:port]</c> of an http(s) URL, or null.</summary>
        internal static string? Origin(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
            {
                return null;
            }

            return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}".ToLowerInvariant() : $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant();
        }

        /// <summary>
        /// Why these <c>Access-Control-Allow-Origin</c> values fail a CORS read from
        /// <paramref name="origin"/>, or null when they pass (or none were sent and
        /// <paramref name="required"/> is off).
        /// </summary>
        internal static string? CorsProblem(IReadOnlyList<string> values, string origin, bool required)
        {
            if (values.Count == 0)
            {
                return required ? "its web server sends no Access-Control-Allow-Origin header (something on the way removes it)" : null;
            }

            if (values.Count > 1 || values[0].Contains(',', StringComparison.Ordinal))
            {
                return $"its web server sends Access-Control-Allow-Origin {values.Count} times; browsers accept only one. "
                    + "Usually the reverse proxy adds its own (nginx add_header) on top of Jellyfin's: remove the proxy's";
            }

            var value = values[0].Trim();
            if (value == "*" || string.Equals(value.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return "its web server sends an Access-Control-Allow-Origin for a different origin. Remove that header from the reverse proxy (Jellyfin sends the right one)";
        }

        /// <summary>
        /// Why this <c>Cross-Origin-Resource-Policy</c> blocks a page on
        /// <paramref name="playerOrigin"/> from loading <paramref name="peerOrigin"/>'s
        /// video, or null.
        /// </summary>
        internal static string? CorpProblem(IReadOnlyList<string> values, string playerOrigin, string peerOrigin)
        {
            foreach (var raw in values)
            {
                var value = raw.Trim().ToLowerInvariant();
                var blocked = value switch
                {
                    "same-origin" => !string.Equals(playerOrigin, peerOrigin, StringComparison.OrdinalIgnoreCase),
                    "same-site" => !SameSite(playerOrigin, peerOrigin),
                    _ => false
                };
                if (blocked)
                {
                    return $"its web server sends Cross-Origin-Resource-Policy: {value}, so a browser on this server cannot load its video. Remove it, or use cross-origin";
                }
            }

            return null;
        }

        /// <summary>
        /// True when every policy in <paramref name="policies"/> lets a page on
        /// <paramref name="selfOrigin"/> use <paramref name="target"/> for
        /// <paramref name="directive"/> (falling back to <c>default-src</c>). Paths,
        /// nonces and other details are read generously: only a clear refusal counts.
        /// </summary>
        internal static bool CspAllows(IEnumerable<string> policies, string directive, string target, string selfOrigin)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || !Uri.TryCreate(selfOrigin, UriKind.Absolute, out var self))
            {
                return true;
            }

            foreach (var policy in policies.SelectMany(p => p.Split(',')))
            {
                var directives = policy.Split(';')
                    .Select(d => d.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(d => d.Length > 0)
                    .GroupBy(d => d[0].ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.First().Skip(1).ToArray());
                if (!directives.TryGetValue(directive, out var sources) && !directives.TryGetValue("default-src", out sources))
                {
                    continue;
                }

                if (!sources.Any(s => SourceMatches(s, url, self)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Test hook.</summary>
        internal static void Reset()
        {
            Cache.Clear();
            Logged.Clear();
        }

        private static async Task<Report> RunAsync(RemoteServer server, string peerBase, string playerOrigin, ILogger? logger)
        {
            Report report;
            using var timeout = new CancellationTokenSource(ProbeTimeout + ProbeTimeout);
            try
            {
                report = await ProbeAsync(peerBase.TrimEnd('/'), playerOrigin, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "[Federation] Browser header check of {Server} failed", server.Name);
                report = new Report(null, null);
            }

            LogChange(server, "converted", report.ConvertedProblem, "its titles that need converting are converted on this server", logger);
            LogChange(server, "direct", report.DirectPlayProblem, "browsers cannot direct-play its titles (switching this friend to Proxy works around it)", logger);
            return report;
        }

        private static async Task<Report> ProbeAsync(string peerBase, string playerOrigin, CancellationToken cancellationToken)
        {
            var peerOrigin = Origin(peerBase) ?? peerBase;
            var gateway = $"{peerBase}{HandoffPlaylistLinks.GatewayPath}header-check/videos/{ProbeItem}/hls1/main/0.ts";
            var converted = new List<string>();
            var direct = new List<string>();

            // The gateway answers even a bad token with Jellyfin-plugin CORS headers,
            // so whatever else arrives was added on the way. A 200 page catches proxies
            // that only add headers to successful answers (nginx without "always").
            using var gatewayGet = await HeadersAsync(HttpMethod.Get, gateway, playerOrigin, preflight: false, cancellationToken).ConfigureAwait(false);
            using var publicInfo = await HeadersAsync(HttpMethod.Get, peerBase + "/System/Info/Public", playerOrigin, preflight: false, cancellationToken).ConfigureAwait(false);
            using var preflight = await HeadersAsync(HttpMethod.Options, gateway, playerOrigin, preflight: true, cancellationToken).ConfigureAwait(false);
            using var directStream = await HeadersAsync(HttpMethod.Get, $"{peerBase}/Plugins/Federation/DirectStream/{ProbeItem}?token=header-check", playerOrigin, preflight: false, cancellationToken).ConfigureAwait(false);

            // The plugin's own refusal always carries Allow-Methods with its Allow-Origin:
            // one without the other means something on the way removed it.
            var pluginAnswered = gatewayGet != null && Values(gatewayGet, "Access-Control-Allow-Methods").Count > 0;
            if (gatewayGet != null && CorsProblem(Values(gatewayGet, "Access-Control-Allow-Origin"), playerOrigin, required: pluginAnswered) is { } g)
            {
                converted.Add(g);
            }
            else if (publicInfo != null && CorsProblem(Values(publicInfo, "Access-Control-Allow-Origin"), playerOrigin, required: false) is { } p)
            {
                converted.Add(p);
            }
            else if (preflight != null && CorsProblem(Values(preflight, "Access-Control-Allow-Origin"), playerOrigin, required: false) is { } o)
            {
                converted.Add(o + " (in the answer to the browser's preflight request)");
            }

            foreach (var headers in new[] { directStream, publicInfo })
            {
                if (headers != null && CorpProblem(Values(headers, "Cross-Origin-Resource-Policy"), playerOrigin, peerOrigin) is { } c)
                {
                    direct.Add(c);
                    break;
                }
            }

            // This server's own web page: what its reverse proxy tells the browser
            // it may load. Only probed on the configured public address.
            if (string.Equals(playerOrigin, OwnOrigin(), StringComparison.OrdinalIgnoreCase))
            {
                using var page = await HeadersAsync(HttpMethod.Get, playerOrigin + "/web/index.html", null, preflight: false, cancellationToken).ConfigureAwait(false);
                if (page != null)
                {
                    var csp = Values(page, "Content-Security-Policy");
                    if (!CspAllows(csp, "connect-src", gateway, playerOrigin))
                    {
                        converted.Add("this server's own Content-Security-Policy (connect-src) does not allow the friend's origin");
                    }

                    if (!CspAllows(csp, "media-src", gateway, playerOrigin))
                    {
                        direct.Add("this server's own Content-Security-Policy (media-src) does not allow the friend's origin");
                    }

                    if (Values(page, "Cross-Origin-Embedder-Policy").Any(v => v.Trim().StartsWith("require-corp", StringComparison.OrdinalIgnoreCase))
                        && !(directStream != null && Values(directStream, "Cross-Origin-Resource-Policy").Any(v => v.Trim().Equals("cross-origin", StringComparison.OrdinalIgnoreCase))))
                    {
                        direct.Add("this server's own Cross-Origin-Embedder-Policy: require-corp blocks it: remove it, or use credentialless");
                    }
                }
            }

            return new Report(converted.Count > 0 ? string.Join("; ", converted) : null, direct.Count > 0 ? string.Join("; ", direct) : null);
        }

        private static async Task<HttpResponseMessage?> HeadersAsync(HttpMethod method, string url, string? origin, bool preflight, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (origin != null)
                {
                    request.Headers.TryAddWithoutValidation("Origin", origin);
                }

                if (preflight)
                {
                    request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
                    request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "range");
                }

                var response = await Send(request, timeout.Token).ConfigureAwait(false);
                response.Content?.Dispose();
                return response;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<string> Values(HttpResponseMessage response, string name)
        {
            var values = new List<string>();
            if (response.Headers.TryGetValues(name, out var headerValues))
            {
                values.AddRange(headerValues);
            }

            if (response.Content != null && response.Content.Headers.TryGetValues(name, out var contentValues))
            {
                values.AddRange(contentValues);
            }

            return values;
        }

        private static void LogChange(RemoteServer server, string kind, string? problem, string consequence, ILogger? logger)
        {
            var key = server.Id + "|" + kind;
            var previous = Logged.TryGetValue(key, out var p) ? p : null;
            if (string.Equals(previous, problem, StringComparison.Ordinal))
            {
                return;
            }

            if (problem != null)
            {
                Logged[key] = problem;
                logger?.LogWarning("[Federation] Browser playback from {Server}: {Problem}. Until that is fixed, {Consequence}", server.Name, problem, consequence);
            }
            else if (previous != null)
            {
                Logged.TryRemove(key, out _);
                logger?.LogInformation("[Federation] Browser playback from {Server}: the web server headers are fine again", server.Name);
            }
        }

        private static bool SourceMatches(string source, Uri url, Uri self)
        {
            var s = source.Trim();
            var lower = s.ToLowerInvariant();
            if (lower == "*")
            {
                return url.Scheme is "http" or "https";
            }

            if (lower == "'none'")
            {
                return false;
            }

            if (lower == "'self'")
            {
                return string.Equals(url.Host, self.Host, StringComparison.OrdinalIgnoreCase)
                    && url.Port == self.Port
                    && SchemeAllows(self.Scheme, url.Scheme);
            }

            if (lower.StartsWith('\''))
            {
                // Nonces, hashes, 'unsafe-*' and the like never allow a URL.
                return false;
            }

            if (lower.EndsWith(':') && !lower.Contains('/', StringComparison.Ordinal))
            {
                return SchemeAllows(lower.TrimEnd(':'), url.Scheme);
            }

            var scheme = self.Scheme;
            var rest = lower;
            var sep = rest.IndexOf("://", StringComparison.Ordinal);
            if (sep >= 0)
            {
                scheme = rest[..sep];
                rest = rest[(sep + 3)..];
            }

            if (!SchemeAllows(scheme, url.Scheme))
            {
                return false;
            }

            var slash = rest.IndexOf('/');
            var hostPort = slash >= 0 ? rest[..slash] : rest;
            string host = hostPort;
            string? port = null;
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0)
            {
                host = hostPort[..colon];
                port = hostPort[(colon + 1)..];
            }

            var urlHost = url.Host.ToLowerInvariant();
            var hostOk = host.StartsWith("*.", StringComparison.Ordinal)
                ? urlHost.EndsWith(host[1..], StringComparison.Ordinal)
                : host == "*" || urlHost == host;
            if (!hostOk)
            {
                return false;
            }

            if (port == null)
            {
                return url.IsDefaultPort;
            }

            return port == "*" || port == url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool SchemeAllows(string sourceScheme, string urlScheme)
            => string.Equals(sourceScheme, urlScheme, StringComparison.OrdinalIgnoreCase)
                || (sourceScheme.Equals("http", StringComparison.OrdinalIgnoreCase) && urlScheme.Equals("https", StringComparison.OrdinalIgnoreCase));

        private static bool SameSite(string a, string b)
        {
            if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) || !Uri.TryCreate(b, UriKind.Absolute, out var ub))
            {
                return true;
            }

            if (!string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ua.HostNameType != UriHostNameType.Dns || ub.HostNameType != UriHostNameType.Dns)
            {
                return string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase);
            }

            // Last two labels: no public-suffix list here, and erring towards "same
            // site" only ever means not reporting a problem.
            static string Site(string host) => string.Join('.', host.ToLowerInvariant().Split('.').TakeLast(2));
            return Site(ua.Host) == Site(ub.Host);
        }
    }
}
