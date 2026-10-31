using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>One address a friend's direct link may be reached at.</summary>
    public class DirectLinkCandidate
    {
        /// <summary>Gets or sets <c>srflx</c> (public, from STUN) or <c>host</c> (a local address).</summary>
        public string Kind { get; set; } = "srflx";

        /// <summary>Gets or sets the IPv4 address.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Gets or sets the UDP port.</summary>
        public int Port { get; set; }
    }

    /// <summary>The initiator's half of the connection details (sent to <c>Peer/DirectLink/Offer</c>).</summary>
    public class DirectLinkOffer
    {
        /// <summary>Gets or sets the protocol version (1).</summary>
        public int Version { get; set; } = DirectLinkProtocol.Version;

        /// <summary>Gets or sets the session id, 32 hex characters.</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Gets or sets the initiator's certificate fingerprint.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Gets or sets the initiator's 32-byte session secret, base64.</summary>
        public string Secret { get; set; } = string.Empty;

        /// <summary>Gets or sets where the initiator may be reached.</summary>
        public List<DirectLinkCandidate> Candidates { get; set; } = new();

        /// <summary>Gets or sets the initiator's NAT kind.</summary>
        public string NatType { get; set; } = "unknown";
    }

    /// <summary>The responder's half (the answer to an offer).</summary>
    public class DirectLinkAnswer
    {
        /// <summary>Gets or sets the protocol version (1).</summary>
        public int Version { get; set; } = DirectLinkProtocol.Version;

        /// <summary>Gets or sets the responder's certificate fingerprint.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Gets or sets the responder's 32-byte session secret, base64.</summary>
        public string Secret { get; set; } = string.Empty;

        /// <summary>Gets or sets where the responder may be reached.</summary>
        public List<DirectLinkCandidate> Candidates { get; set; } = new();

        /// <summary>Gets or sets the responder's NAT kind.</summary>
        public string NatType { get; set; } = "unknown";
    }

    /// <summary>Protocol constants, key derivation and checks shared by both roles.</summary>
    public static class DirectLinkProtocol
    {
        /// <summary>Protocol version both sides speak.</summary>
        public const int Version = 1;

        /// <summary>The TLS application protocol name.</summary>
        public const string Alpn = "jf-fed-dl/1";

        /// <summary>Most candidates one side may send.</summary>
        public const int MaxCandidates = 8;

        /// <summary>
        /// Env var that lets private addresses through as public candidates and
        /// allows signaling over http. Only for the NAT smoke test, whose
        /// "internet" is a private container network.
        /// </summary>
        public const string TestAllowPrivateEnv = "FEDERATION_DIRECTLINK_TEST_ALLOW_PRIVATE";

        private static readonly IPNetwork Tailscale = IPNetwork.Parse("100.64.0.0/10");

        /// <summary>Gets a value indicating whether the isolated NAT test build's relaxations are on.</summary>
        public static bool TestMode
        {
            get
            {
#if FEDERATION_TEST_HOOKS
                return Environment.GetEnvironmentVariable(TestAllowPrivateEnv) == "1";
#else
                return false;
#endif
            }
        }

        /// <summary>A new random session id (32 hex characters).</summary>
        public static string NewSessionId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        /// <summary>A new 32-byte session secret.</summary>
        public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(32);

        /// <summary>True for a 32-character hex session id.</summary>
        public static bool IsSessionId(string? value)
            => value is { Length: 32 } && value.All(Uri.IsHexDigit);

        /// <summary>The 32-byte secret in <paramref name="base64"/>, or null.</summary>
        public static byte[]? ParseSecret(string? base64)
        {
            if (string.IsNullOrEmpty(base64) || base64.Length > 64)
            {
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(base64);
                return bytes.Length == 32 ? bytes : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// The session key both sides derive from both secrets. Probes, acks and
        /// the connect proof all use keys derived from this one, so only the two
        /// servers that exchanged this session's details can produce them.
        /// </summary>
        public static byte[] SessionKey(byte[] initiatorSecret, byte[] responderSecret, string sessionId)
        {
            var key = new byte[64];
            initiatorSecret.CopyTo(key, 0);
            responderSecret.CopyTo(key, 32);
            return HMACSHA256.HashData(key, Encoding.ASCII.GetBytes("jf-fed-dl/1|" + sessionId.ToLowerInvariant()));
        }

        /// <summary>A purpose-specific key derived from the session key.</summary>
        public static byte[] SubKey(byte[] sessionKey, string purpose)
            => HMACSHA256.HashData(sessionKey, Encoding.ASCII.GetBytes(purpose));

        /// <summary>
        /// The proof a role sends after QUIC connects: an HMAC over the role, the
        /// session and both pinned fingerprints, keyed with the session's proof key.
        /// </summary>
        public static byte[] ConnectProof(byte[] sessionKey, bool fromInitiator, string sessionId, string initiatorFingerprint, string responderFingerprint)
            => HMACSHA256.HashData(
                SubKey(sessionKey, "proof"),
                Encoding.ASCII.GetBytes((fromInitiator ? "initiator|" : "responder|") + sessionId.ToLowerInvariant() + "|" + initiatorFingerprint.ToLowerInvariant() + "|" + responderFingerprint.ToLowerInvariant()));

        /// <summary>True for an address in Tailscale's range (100.64.0.0/10).</summary>
        public static bool IsTailscale(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && Tailscale.Contains(address);

        /// <summary>
        /// The endpoint in a candidate when it's one this server may send
        /// packets to: IPv4, port 1024 or higher, never loopback, multicast,
        /// broadcast, link-local, unspecified or reserved/documentation ranges.
        /// Private (RFC 1918) addresses only with <paramref name="allowPrivate"/>
        /// (a friend configured by a LAN address, tests), and Tailscale's range
        /// only with <paramref name="allowTailscale"/> (a friend reached over
        /// Tailscale), so a friend can't point this server's packets at other
        /// machines on its LAN or tailnet.
        /// </summary>
        public static IPEndPoint? AcceptCandidate(DirectLinkCandidate? candidate, bool allowPrivate, bool allowTailscale = false)
        {
            if (candidate == null
                || candidate.Port is < 1024 or > 65535
                || !IPAddress.TryParse(candidate.Address, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork
                || !string.Equals(address.ToString(), candidate.Address, StringComparison.Ordinal))
            {
                return null;
            }

            var b = address.GetAddressBytes();
            if (b[0] == 0 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254)
                || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113))
            {
                return null;
            }

            if (IsTailscale(address) && !allowTailscale)
            {
                return null;
            }

            var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
            var kind = candidate.Kind?.ToLowerInvariant();
            if (kind != "srflx" && kind != "host")
            {
                return null;
            }

            if (isPrivate && !allowPrivate)
            {
                return null;
            }

            return new IPEndPoint(address, candidate.Port);
        }

        /// <summary>The acceptable endpoints of a candidate list (at most <see cref="MaxCandidates"/>, no duplicates).</summary>
        public static List<IPEndPoint> AcceptCandidates(IEnumerable<DirectLinkCandidate>? candidates, bool allowPrivate, bool allowTailscale = false)
            => (candidates ?? Enumerable.Empty<DirectLinkCandidate>())
                .Take(MaxCandidates)
                .Select(c => AcceptCandidate(c, allowPrivate, allowTailscale))
                .OfType<IPEndPoint>()
                .Distinct()
                .ToList();

        /// <summary>The wire form of a NAT kind.</summary>
        public static string NatName(NatKind kind) => kind.ToString().ToLowerInvariant();

        /// <summary>The NAT kind a wire string names (unknown when garbled).</summary>
        public static NatKind ParseNat(string? value)
            => string.Equals(value, "cone", StringComparison.OrdinalIgnoreCase) ? NatKind.Cone
                : string.Equals(value, "symmetric", StringComparison.OrdinalIgnoreCase) ? NatKind.Symmetric
                : NatKind.Unknown;

        /// <summary>
        /// Whether the friend channel at <paramref name="url"/> is safe for direct
        /// link details: https, or a Tailscale address (encrypted by Tailscale).
        /// A plain http channel could have the fingerprints swapped in transit.
        /// </summary>
        public static bool IsSignalingSafe(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                return true;
            }

            if (uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(uri.Host, out var ip) && IsTailscale(ip);
        }

        /// <summary>Formats an endpoint as a candidate.</summary>
        public static DirectLinkCandidate ToCandidate(IPEndPoint endpoint, string kind)
            => new() { Kind = kind, Address = endpoint.Address.ToString(), Port = endpoint.Port };

        /// <summary>Invariant formatting for log lines and status.</summary>
        public static string Describe(IPEndPoint? endpoint)
            => endpoint == null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{endpoint.Address}:{endpoint.Port}");

        // Keyed per process: the same address gets the same tag in one run's logs, and
        // a tag can't be turned back into an address by hashing every IPv4 address.
        private static readonly byte[] RedactKey = RandomNumberGenerator.GetBytes(32);

        /// <summary>
        /// An endpoint for logs and the status page. A public address is a home's
        /// internet address, so it becomes a short tag (<c>public #3f9a2c:51876</c>);
        /// private, LAN, loopback and Tailscale addresses identify no home and stay as
        /// they are, for troubleshooting.
        /// </summary>
        /// <returns>The text.</returns>
        public static string Redact(IPEndPoint? endpoint)
            => endpoint == null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{Redact(endpoint.Address)}:{endpoint.Port}");

        /// <summary>An address for logs and the status page (see <see cref="Redact(IPEndPoint?)"/>).</summary>
        /// <returns>The text.</returns>
        public static string Redact(IPAddress? address)
        {
            if (address == null)
            {
                return string.Empty;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (!IsPublic(address))
            {
                return address.ToString();
            }

            var tag = HMACSHA256.HashData(RedactKey, address.GetAddressBytes());
            return "public #" + Convert.ToHexString(tag, 0, 3).ToLowerInvariant();
        }

        private static readonly System.Text.RegularExpressions.Regex Ipv4InText = new(
            @"(?<![0-9.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9.])",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        /// <summary>
        /// Free text (an error message, a detail line) with every public IPv4 address in
        /// it replaced by its tag, as <see cref="Redact(IPAddress?)"/> does.
        /// </summary>
        /// <returns>The text.</returns>
        public static string RedactText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            try
            {
                return Ipv4InText.Replace(text, m => IPAddress.TryParse(m.Value, out var ip) ? Redact(ip) : m.Value);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return "(details hidden)";
            }
        }

        private static bool IsPublic(IPAddress address)
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                // 0/8, multicast and reserved aren't anyone's home (and 0.x looks like a version number).
                return !(b[0] == 0 || b[0] >= 224 || b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
            }

            // IPv6: unique-local (fc00::/7) and link-local stay; global ones are redacted.
            var v6 = address.GetAddressBytes();
            return !((v6[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal);
        }
    }
}
