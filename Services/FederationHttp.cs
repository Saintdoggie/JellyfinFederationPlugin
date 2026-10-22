using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// The one place friend requests are connected (docs/PAIRING.md, "Routing").
    /// A paired friend's address is <c>http://[100::…]</c> in the IPv6 discard prefix
    /// <c>100::/64</c>: no DNS, never routed. Every friend-facing handler connects
    /// through <see cref="ConnectAsync"/>, which sends a request for such an address
    /// only over that friend's link and refuses everything that mixes the two:
    /// a redirect into a link, a paired friend's request going anywhere else, or a
    /// proxy in between.
    /// </summary>
    public static class FederationHttp
    {
        /// <summary>
        /// Opens a stream on the link for a paired friend's address (set by the pairing
        /// service), or throws when that friend has no link up.
        /// </summary>
        public static Func<IPAddress, CancellationToken, ValueTask<Stream>>? OpenLinkStream { get; set; }

        /// <summary>The address of a paired friend.</summary>
        /// <returns>An http URL on the <c>100::/64</c> host for <paramref name="pairingId"/>.</returns>
        public static string PairUrl(ulong pairingId)
        {
            var bytes = new byte[16];
            bytes[0] = 0x01;
            for (var i = 0; i < 8; i++)
            {
                bytes[8 + i] = (byte)(pairingId >> (56 - (8 * i)));
            }

            return "http://[" + new IPAddress(bytes).ToString() + "]";
        }

        /// <summary>Whether <paramref name="host"/> (with or without brackets) is a paired friend's address.</summary>
        /// <returns>True for any address in <c>100::/64</c>.</returns>
        public static bool IsPairHost(string? host) => TryPairAddress(host, out _);

        /// <summary>Whether a URL points at a paired friend's address.</summary>
        /// <returns>True when its host is in <c>100::/64</c>.</returns>
        public static bool IsPairUrl(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsPairHost(uri.Host);

        /// <summary>
        /// A handler for friend-facing clients: stock <see cref="SocketsHttpHandler"/>
        /// settings plus <see cref="ConnectAsync"/>, under <see cref="Stamp"/>.
        /// </summary>
        /// <returns>The handler.</returns>
        public static HttpMessageHandler Handler(bool allowAutoRedirect = true) => Stamp(Sockets(allowAutoRedirect));

        /// <summary>
        /// The bare sockets handler with <see cref="ConnectAsync"/>. On its own it can't
        /// reach a paired friend: wrap it with <see cref="Stamp"/>.
        /// </summary>
        /// <returns>The handler.</returns>
        public static SocketsHttpHandler Sockets(bool allowAutoRedirect = true)
            => new() { AllowAutoRedirect = allowAutoRedirect, ConnectCallback = ConnectAsync };

        /// <summary>
        /// Records where each request was first sent, before any redirect. .NET follows
        /// redirects by rewriting the request's address in place, so the connect hook
        /// can't tell a redirected request from one that started there without this.
        /// </summary>
        /// <returns>The wrapping handler.</returns>
        public static HttpMessageHandler Stamp(HttpMessageHandler inner) => new StampHandler(inner);

        /// <summary>The connect hook: a link stream for a paired friend, a TCP socket for everyone else.</summary>
        /// <returns>The connection stream.</returns>
        public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
            => ConnectAsync(context, DefaultTcpConnectAsync, cancellationToken);

        /// <summary>
        /// Wraps another connect hook (for example one that only allows public
        /// addresses) with the paired-friend rules.
        /// </summary>
        /// <returns>A connect hook.</returns>
        public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Guard(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> inner)
            => (context, token) => ConnectAsync(context, inner, token);

        private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> inner, CancellationToken cancellationToken)
        {
            var request = context.InitialRequestMessage;
            var targetIsPair = TryPairAddress(context.DnsEndPoint.Host, out var target);
            var currentIsPair = TryPairAddress(request.RequestUri?.Host, out var current);
            var stamped = request.Options.TryGetValue(OriginKey, out var origin);
            var originIsPair = stamped && TryPairAddress(origin, out _);
            if (!targetIsPair && !currentIsPair && !originIsPair)
            {
                return await inner(context, cancellationToken).ConfigureAwait(false);
            }

            // Only a request that was first sent to this paired friend's address, and is
            // connecting to that same address, may use its link. Refused: no stamp, a
            // redirect from elsewhere into a link, a paired friend's request redirected or
            // proxied elsewhere, and a proxy for a paired address.
            if (!stamped || !targetIsPair || !currentIsPair || !originIsPair
                || !target!.Equals(current)
                || !TryPairAddress(origin, out var originAddress) || !target.Equals(originAddress))
            {
                throw new HttpRequestException("A paired friend's request can only go over its own link.");
            }

            var open = OpenLinkStream ?? throw new HttpRequestException("Paired friends aren't available on this server.");
            return await open(target, cancellationToken).ConfigureAwait(false);
        }

        private static readonly HttpRequestOptionsKey<string> OriginKey = new("Federation.OriginHost");

        private static bool TryPairAddress(string? host, out IPAddress? address)
        {
            address = null;
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            var bare = host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host.Substring(1, host.Length - 2) : host;
            if (!IPAddress.TryParse(bare, out var parsed))
            {
                return false;
            }

            if (parsed.IsIPv4MappedToIPv6 || parsed.AddressFamily != AddressFamily.InterNetworkV6 || parsed.ScopeId != 0)
            {
                return false;
            }

            var bytes = parsed.GetAddressBytes();
            if (bytes[0] != 0x01 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0 || bytes[4] != 0 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0)
            {
                return false;
            }

            address = parsed;
            return true;
        }

        /// <summary>The pairing id in a paired friend's address.</summary>
        /// <returns>The id, or null when the address isn't one.</returns>
        public static ulong? PairingIdOf(IPAddress address)
        {
            if (!TryPairAddress(address.ToString(), out _))
            {
                return null;
            }

            var bytes = address.GetAddressBytes();
            ulong id = 0;
            for (var i = 8; i < 16; i++)
            {
                id = (id << 8) | bytes[i];
            }

            return id;
        }

        /// <summary>The pairing id as the 16 hex digits used in config.</summary>
        /// <returns>Hex text.</returns>
        public static string FormatPairingId(ulong id) => id.ToString("x16", CultureInfo.InvariantCulture);

        private sealed class StampHandler : DelegatingHandler
        {
            public StampHandler(HttpMessageHandler inner)
                : base(inner)
            {
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Set once, from the address the caller asked for: a redirect followed
                // further down rewrites RequestUri but never this.
                request.Options.Set(OriginKey, request.RequestUri?.Host ?? string.Empty);
                return base.SendAsync(request, cancellationToken);
            }

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Options.Set(OriginKey, request.RequestUri?.Host ?? string.Empty);
                return base.Send(request, cancellationToken);
            }
        }

        private static async ValueTask<Stream> DefaultTcpConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            // What SocketsHttpHandler does without a hook.
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
