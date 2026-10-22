using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// The responder's gate for each request a friend sends over the direct link:
    /// a strict HTTP/1.1 request-head parser plus the short list of friend routes
    /// the link may carry. Everything the routes return is already authorized by
    /// the friend's token in the request, exactly as over the normal address.
    /// Jellyfin sees these requests as local (they arrive on loopback), so the
    /// manager applies Jellyfin's remote-access and IP rules to the friend's real
    /// UDP address before a link is accepted.
    /// </summary>
    public sealed class DirectLinkRequestHead
    {
        /// <summary>Largest request head accepted.</summary>
        public const int MaxHeadBytes = 16 * 1024;

        private const int MaxHeaders = 100;

        private static readonly string[] AllowedPrefixes =
        {
            "/Plugins/Federation/Peer/Play/",
            "/Plugins/Federation/DirectStream/",
        };

        private static readonly string[] AllowedExact =
        {
            "/Plugins/Federation/Peer/BitrateTest",
        };

        // The only friend headers forwarded to Jellyfin: what the link's own
        // callers send (relay, handoff, downloads, speed test). Everything else is
        // dropped, so no proxy or client-address header (X-Forwarded-*, Forwarded,
        // X-Real-IP, CF-Connecting-IP, ...) or routing header (Host, Connection,
        // Upgrade) a friend adds ever reaches this server's Jellyfin, including
        // ones nobody has thought of yet.
        private static readonly HashSet<string> Passed = new(StringComparer.OrdinalIgnoreCase)
        {
            "Range", "If-Range", "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since",
            "Accept", "Accept-Encoding", "Accept-Language", "User-Agent", "Cache-Control", "Pragma",
            FederationTokenAuth.Header,
        };

        private DirectLinkRequestHead(string method, string target, List<KeyValuePair<string, string>> headers)
        {
            Method = method;
            Target = target;
            Headers = headers;
        }

        /// <summary>Gets the method (GET or HEAD).</summary>
        public string Method { get; }

        /// <summary>Gets the request target (path and query).</summary>
        public string Target { get; }

        /// <summary>Gets the headers, in order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>
        /// Index just past the blank line ending the head in <paramref name="buffer"/>, or -1.
        /// </summary>
        public static int FindHeadEnd(ReadOnlySpan<byte> buffer)
        {
            var at = buffer.IndexOf("\r\n\r\n"u8);
            return at < 0 ? -1 : at + 4;
        }

        /// <summary>
        /// Parses and checks a request head. Null (with a reason) for anything that
        /// isn't a plain GET/HEAD to an allowed friend route.
        /// </summary>
        public static DirectLinkRequestHead? Parse(ReadOnlySpan<byte> head, out string? refusal)
        {
            refusal = null;
            if (head.Length > MaxHeadBytes)
            {
                refusal = "request head too large";
                return null;
            }

            foreach (var b in head)
            {
                // Plain ASCII only; no NULs or other control characters except CR/LF/TAB.
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
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || requestLine[2] != "HTTP/1.1")
            {
                refusal = "bad request line";
                return null;
            }

            var method = requestLine[0];
            if (method != "GET" && method != "HEAD")
            {
                refusal = "method not allowed";
                return null;
            }

            var target = requestLine[1];
            if (!IsAllowedTarget(target))
            {
                refusal = "route not allowed over the direct link";
                return null;
            }

            var headers = new List<KeyValuePair<string, string>>();
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line.Contains('\n', StringComparison.Ordinal) || line.Contains('\r', StringComparison.Ordinal))
                {
                    // Folded or stray lines are how request smuggling starts.
                    refusal = "bad header line";
                    return null;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    refusal = "bad header line";
                    return null;
                }

                var name = line.Substring(0, colon);
                if (name.Any(c => !IsTokenChar(c)))
                {
                    refusal = "bad header name";
                    return null;
                }

                var value = line.Substring(colon + 1).Trim(' ', '\t');
                if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0"))
                {
                    refusal = "a body isn't allowed";
                    return null;
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

            return new DirectLinkRequestHead(method, target, headers);
        }

        /// <summary>True when the target is an origin-form path to an allowed route.</summary>
        internal static bool IsAllowedTarget(string target)
        {
            if (!IsCleanTarget(target, out var path))
            {
                return false;
            }

            return AllowedExact.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase))
                || AllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase) && path.Length > p.Length);
        }

        /// <summary>
        /// The target checks every link gate shares: an origin-form path with no
        /// controls, spaces, dot segments, doubled or encoded slashes, encoded dots,
        /// NULs or fragments. <paramref name="path"/> is the part before any query.
        /// </summary>
        internal static bool IsCleanTarget(string target, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrEmpty(target) || target[0] != '/' || target.Length > 8192)
            {
                return false;
            }

            foreach (var c in target)
            {
                // No spaces, tabs, CR/LF or other controls: they'd let a target
                // split the forwarded request line.
                if (c <= ' ' || c >= 0x7F)
                {
                    return false;
                }
            }

            var query = target.IndexOf('?', StringComparison.Ordinal);
            path = query < 0 ? target : target.Substring(0, query);
            if (path.Contains('\\', StringComparison.Ordinal)
                || path.Contains("//", StringComparison.Ordinal)
                || path.Contains('#', StringComparison.Ordinal)
                || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
                || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
                || path.Contains("%2e", StringComparison.OrdinalIgnoreCase)
                || path.Contains("%00", StringComparison.Ordinal)
                || path.Split('/').Any(segment => segment == "." || segment == ".."))
            {
                return false;
            }

            return !target.Contains('#', StringComparison.Ordinal);
        }

        /// <summary>
        /// The head to send to this server's own Jellyfin: the same request line,
        /// the friend's headers that are on the short list, and this side's own
        /// Host, Connection and marker headers.
        /// </summary>
        public byte[] BuildForward(string localHostHeader, string basePath)
        {
            var builder = new StringBuilder();
            builder.Append(Method).Append(' ').Append(basePath.TrimEnd('/')).Append(Target).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(localHostHeader).Append("\r\n");
            foreach (var header in Headers)
            {
                if (Passed.Contains(header.Key))
                {
                    builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            builder.Append("Connection: close\r\nX-Federation-Direct-Link: 1\r\n\r\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        private static bool IsTokenChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf(c, StringComparison.Ordinal) >= 0;
    }
}
