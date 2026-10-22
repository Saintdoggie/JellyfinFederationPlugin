using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Federation.Services.DirectLink;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>How a paired-link route authenticates (docs/PAIRING.md, "What a paired friend may call").</summary>
    public enum PairedAuth
    {
        /// <summary>The friend's <c>X-Federation-Token</c>, which must be the link friend's own.</summary>
        HeaderToken,

        /// <summary>An item token in the path or query, bound to the owning friend by the handler.</summary>
        ItemToken,

        /// <summary>The link itself (pairing handshake routes); open before the safety check.</summary>
        LinkOnly
    }

    /// <summary>
    /// The gate for requests a paired friend sends over its link: one exact table of
    /// method, path and auth, strict HTTP/1.1 heads (shared rules with direct links), and
    /// bodies only on the POST rows, as one Content-Length of at most 64 KB.
    /// </summary>
    public sealed class PairedRequestGate
    {
        /// <summary>Largest request body accepted.</summary>
        public const int MaxBodyBytes = 64 * 1024;

        private const int MaxHeaders = 100;
        private const string Seg = "[A-Za-z0-9_-]{1,128}";
        private const string P = "/Plugins/Federation/";

        private static readonly (string[] Methods, Regex Path, PairedAuth Auth)[] Routes =
        {
            (Get, R(P + "Peer/(SystemInfo|Libraries|Items|UserRules|Favorites|Popular|Users|BitrateTest|PlayLog|Hardware)"), PairedAuth.HeaderToken),
            (Get, R(P + "Peer/(Items|PlaybackInfo)/" + Seg), PairedAuth.HeaderToken),
            (GetHead, R(P + "Peer/Play/" + Seg + "/[A-Za-z0-9._,~/-]{1,1024}"), PairedAuth.ItemToken),
            (Get, R(P + "Peer/Images/" + Seg + "/" + Seg + "(/[0-9]{1,4})?"), PairedAuth.ItemToken),
            (GetHead, R(P + "DirectStream/" + Seg), PairedAuth.ItemToken),
            (Post, R(P + "Peer/(CatalogChanged|PlayControl|Viewers|AvailabilitySchedule)"), PairedAuth.HeaderToken),
            (Post, R(P + "Peer/Party/(Announce|" + Seg + "/(Join|Leave|Event))"), PairedAuth.HeaderToken),
            (Post, R(P + "(PlaybackToken|RegisterUserSession)"), PairedAuth.HeaderToken),
            (Post, R(P + "Friends/(RemoteUserRules|Unfriend)"), PairedAuth.HeaderToken),
            (Post, R(P + "Pools/(InviteNotice|AcceptNotice|RejectNotice|Notice)"), PairedAuth.HeaderToken),
            (Post, R(P + "Pairing/Peer/(Hello|Confirmed)"), PairedAuth.LinkOnly),
        };

        private static string[] Get => new[] { "GET" };

        private static string[] GetHead => new[] { "GET", "HEAD" };

        private static string[] Post => new[] { "POST" };

        // The only friend headers forwarded to Jellyfin: what this plugin's own friend
        // requests carry. Everything else is dropped (proxy and client-address headers,
        // Jellyfin's own auth headers, cookies, the link headers only the forwarder
        // sets), including ones nobody has thought of yet.
        private static readonly HashSet<string> Passed = new(StringComparer.OrdinalIgnoreCase)
        {
            "Range", "If-Range", "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since",
            "Accept", "Accept-Encoding", "Accept-Language", "User-Agent", "Cache-Control", "Pragma",
            "Content-Type", "Origin",
            FederationTokenAuth.Header, RemoteServerClient.RemoteUserIdHeader, RemoteServerClient.RemoteUserNameHeader,
        };

        private PairedRequestGate(string method, string target, PairedAuth auth, int contentLength, List<KeyValuePair<string, string>> headers)
        {
            Method = method;
            Target = target;
            Auth = auth;
            ContentLength = contentLength;
            Headers = headers;
        }

        /// <summary>Gets the method.</summary>
        public string Method { get; }

        /// <summary>Gets the request target.</summary>
        public string Target { get; }

        /// <summary>Gets how the route authenticates.</summary>
        public PairedAuth Auth { get; }

        /// <summary>Gets the body length (0 when there is none).</summary>
        public int ContentLength { get; }

        /// <summary>Gets the headers as sent (before forwarding rules).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>The friend's federation token, when it sent exactly one.</summary>
        public string? FederationToken
        {
            get
            {
                var tokens = Headers.Where(h => string.Equals(h.Key, FederationTokenAuth.Header, StringComparison.OrdinalIgnoreCase)).ToList();
                return tokens.Count == 1 && tokens[0].Value.Length > 0 ? tokens[0].Value : null;
            }
        }

        /// <summary>
        /// Parses and checks a request head. Before this side's safety number is
        /// confirmed, only <see cref="PairedAuth.LinkOnly"/> routes pass.
        /// </summary>
        /// <returns>The head, or null with the reason.</returns>
        public static PairedRequestGate? Parse(ReadOnlySpan<byte> head, bool confirmed, out string? refusal)
        {
            refusal = null;
            if (head.Length > DirectLinkRequestHead.MaxHeadBytes)
            {
                refusal = "request head too large";
                return null;
            }

            foreach (var b in head)
            {
                if (b > 0x7E || (b < 0x20 && b != (byte)'\r' && b != (byte)'\n' && b != (byte)'\t'))
                {
                    refusal = "invalid characters";
                    return null;
                }
            }

            var text = Encoding.ASCII.GetString(head);
            if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                refusal = "incomplete head";
                return null;
            }

            var lines = text.Substring(0, text.Length - 4).Split("\r\n");
            if (lines.Any(l => l.Contains('\r', StringComparison.Ordinal) || l.Contains('\n', StringComparison.Ordinal)))
            {
                // A bare CR or LF splits lines differently for whoever reads next:
                // how injected headers get past this gate.
                refusal = "bare CR or LF";
                return null;
            }

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || requestLine[2] != "HTTP/1.1")
            {
                refusal = "bad request line";
                return null;
            }

            var method = requestLine[0];
            var target = requestLine[1];
            if (!DirectLinkRequestHead.IsCleanTarget(target, out var path))
            {
                refusal = "bad target";
                return null;
            }

            var route = Routes.FirstOrDefault(r => r.Methods.Contains(method, StringComparer.Ordinal) && r.Path.IsMatch(path));
            if (route.Methods == null)
            {
                refusal = "route not allowed over a paired link";
                return null;
            }

            if (!confirmed && route.Auth != PairedAuth.LinkOnly)
            {
                refusal = "the safety number isn't confirmed yet";
                return null;
            }

            var headers = new List<KeyValuePair<string, string>>();
            string? length = null;
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] == ' ' || line[0] == '\t')
                {
                    refusal = "bad header line";
                    return null;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || line.Substring(0, colon).Any(c => !IsTokenChar(c)))
                {
                    refusal = "bad header line";
                    return null;
                }

                var name = line.Substring(0, colon);
                var value = line.Substring(colon + 1).Trim(' ', '\t');
                if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Expect", StringComparison.OrdinalIgnoreCase))
                {
                    refusal = "only a plain Content-Length body is allowed";
                    return null;
                }

                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (length != null)
                    {
                        refusal = "more than one Content-Length";
                        return null;
                    }

                    length = value;
                }

                headers.Add(new KeyValuePair<string, string>(name, value));
                if (headers.Count > MaxHeaders)
                {
                    refusal = "too many headers";
                    return null;
                }
            }

            if (headers.Count(h => string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase)) > 1)
            {
                refusal = "duplicate Host";
                return null;
            }

            var contentLength = 0;
            if (length != null)
            {
                if (length.Length is 0 or > 6 || length.Any(c => c is < '0' or > '9') || !int.TryParse(length, out contentLength) || contentLength > MaxBodyBytes)
                {
                    refusal = "bad Content-Length";
                    return null;
                }
            }

            if (contentLength > 0 && method != "POST")
            {
                refusal = "a body is only allowed on POST";
                return null;
            }

            return new PairedRequestGate(method, target, route.Auth, contentLength, headers);
        }

        /// <summary>
        /// The head to send to this server's own Jellyfin: the friend's headers that are
        /// on the short list, then the link headers
        /// set here, the exact body length and <c>Connection: close</c>.
        /// </summary>
        /// <returns>ASCII bytes.</returns>
        public byte[] BuildForward(string localHostHeader, string basePath, string friendId, string peerAddress)
        {
            var builder = new StringBuilder();
            builder.Append(Method).Append(' ').Append(basePath.TrimEnd('/')).Append(Target).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(localHostHeader).Append("\r\n");
            foreach (var header in Headers)
            {
                if (Passed.Contains(header.Key) && !PairedLinkContext.IsReserved(header.Key))
                {
                    builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            if (Method == "POST")
            {
                builder.Append("Content-Length: ").Append(ContentLength).Append("\r\n");
            }

            builder.Append(PairedLinkContext.SecretHeader).Append(": ").Append(PairedLinkContext.Secret).Append("\r\n");
            builder.Append(PairedLinkContext.FriendHeader).Append(": ").Append(friendId).Append("\r\n");
            builder.Append(PairedLinkContext.PeerHeader).Append(": ").Append(peerAddress).Append("\r\n");
            builder.Append("Connection: close\r\n\r\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        /// <summary>The route table, for tests.</summary>
        /// <returns>Method lists, patterns and auth classes.</returns>
        internal static IEnumerable<(string[] Methods, string Pattern, PairedAuth Auth)> Table()
            => Routes.Select(r => (r.Methods, r.Path.ToString(), r.Auth));

        private static Regex R(string pattern) => new("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        private static bool IsTokenChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf(c, StringComparison.Ordinal) >= 0;
    }
}
