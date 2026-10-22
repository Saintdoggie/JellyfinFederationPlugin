using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>How a home router maps this server's UDP port to the internet.</summary>
    public enum NatKind
    {
        /// <summary>Not measured (one STUN server answered, or none).</summary>
        Unknown,

        /// <summary>The same public port for every destination: punching works.</summary>
        Cone,

        /// <summary>A different public port per destination: punching only works if the friend's side is not.</summary>
        Symmetric
    }

    /// <summary>
    /// Minimal STUN client (RFC 5389 Binding request): asks a public STUN server
    /// which public address and port a UDP socket's packets come from. Used with
    /// the socket that later carries the direct link, so the answer is the
    /// address the friend must aim at.
    /// </summary>
    public static class StunClient
    {
        /// <summary>The STUN magic cookie.</summary>
        internal const uint MagicCookie = 0x2112A442;

        private const ushort BindingRequest = 0x0001;
        private const ushort BindingSuccess = 0x0101;
        private const ushort MappedAddress = 0x0001;
        private const ushort XorMappedAddress = 0x0020;

        /// <summary>Builds a Binding request with a fresh 12-byte transaction id.</summary>
        internal static byte[] BuildRequest(out byte[] transactionId)
        {
            transactionId = RandomNumberGenerator.GetBytes(12);
            var packet = new byte[20];
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), BindingRequest);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
            transactionId.CopyTo(packet, 8);
            return packet;
        }

        /// <summary>
        /// The IPv4 address and port a Binding success response reports, or null
        /// when the packet isn't a well-formed answer to <paramref name="transactionId"/>.
        /// </summary>
        internal static IPEndPoint? ParseResponse(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> transactionId)
        {
            if (packet.Length < 20
                || BinaryPrimitives.ReadUInt16BigEndian(packet) != BindingSuccess
                || BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4)) != MagicCookie
                || !packet.Slice(8, 12).SequenceEqual(transactionId))
            {
                return null;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2));
            if (length + 20 > packet.Length || (length & 3) != 0)
            {
                return null;
            }

            IPEndPoint? mapped = null;
            var body = packet.Slice(20, length);
            while (body.Length >= 4)
            {
                var type = BinaryPrimitives.ReadUInt16BigEndian(body);
                int attrLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(2));
                if (attrLength + 4 > body.Length)
                {
                    return null;
                }

                var value = body.Slice(4, attrLength);
                if ((type == XorMappedAddress || type == MappedAddress) && value.Length >= 8 && value[1] == 0x01)
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2));
                    var address = value.Slice(4, 4).ToArray();
                    if (type == XorMappedAddress)
                    {
                        port ^= (ushort)(MagicCookie >> 16);
                        Span<byte> cookie = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(cookie, MagicCookie);
                        for (var i = 0; i < 4; i++)
                        {
                            address[i] ^= cookie[i];
                        }

                        // XOR-MAPPED-ADDRESS wins over the legacy attribute.
                        return new IPEndPoint(new IPAddress(address), port);
                    }

                    mapped ??= new IPEndPoint(new IPAddress(address), port);
                }

                var padded = (attrLength + 3) & ~3;
                body = body.Length >= 4 + padded ? body.Slice(4 + padded) : ReadOnlySpan<byte>.Empty;
            }

            return mapped;
        }

        /// <summary>Parses <c>host:port</c> (port defaults to 3478).</summary>
        internal static (string Host, int Port)? ParseServer(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();
            if (text.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(5);
            }

            var colon = text.LastIndexOf(':');
            if (colon < 0)
            {
                return (text, 3478);
            }

            return int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 && colon > 0
                ? (text.Substring(0, colon), port)
                : null;
        }

        /// <summary>
        /// Asks one STUN server for <paramref name="socket"/>'s public endpoint.
        /// Three tries, 500 ms apart. Null when it doesn't answer. Packets that
        /// aren't the answer are skipped (the socket may already be getting
        /// other traffic).
        /// </summary>
        public static async Task<IPEndPoint?> QueryAsync(Socket socket, IPEndPoint server, CancellationToken cancellationToken)
        {
            var request = BuildRequest(out var transactionId);
            var buffer = new byte[1500];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await socket.SendToAsync(request, SocketFlags.None, server, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // No route to this STUN server right now.
                    return null;
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    while (true)
                    {
                        var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token).ConfigureAwait(false);
                        if (received.RemoteEndPoint is IPEndPoint from && from.Equals(server)
                            && ParseResponse(buffer.AsSpan(0, received.ReceivedBytes), transactionId) is { } mapped)
                        {
                            return mapped;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // No answer this round; ask again.
                }
                catch (SocketException)
                {
                    // ICMP unreachable surfaces here on Linux; try again.
                }
            }

            return null;
        }

        /// <summary>Resolves STUN servers to IPv4 endpoints (unresolvable ones are skipped).</summary>
        public static async Task<List<IPEndPoint>> ResolveAsync(IEnumerable<string> servers, CancellationToken cancellationToken)
        {
            var result = new List<IPEndPoint>();
            foreach (var entry in servers)
            {
                if (ParseServer(entry) is not { } parsed)
                {
                    continue;
                }

                try
                {
                    if (IPAddress.TryParse(parsed.Host, out var literal))
                    {
                        if (literal.AddressFamily == AddressFamily.InterNetwork)
                        {
                            result.Add(new IPEndPoint(literal, parsed.Port));
                        }

                        continue;
                    }

                    var addresses = await Dns.GetHostAddressesAsync(parsed.Host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
                    if (addresses.FirstOrDefault() is { } first)
                    {
                        result.Add(new IPEndPoint(first, parsed.Port));
                    }
                }
                catch (SocketException)
                {
                    // Unknown host: the next server may do.
                }
            }

            return result;
        }

        /// <summary>
        /// The public endpoint of <paramref name="socket"/> and the NAT kind, from
        /// up to two STUN servers. Same public port from both means a cone NAT.
        /// </summary>
        public static async Task<(IPEndPoint? Public, NatKind Nat)> DiscoverAsync(Socket socket, IReadOnlyList<IPEndPoint> servers, CancellationToken cancellationToken)
        {
            IPEndPoint? first = null;
            IPEndPoint? firstServer = null;
            IPEndPoint? second = null;
            foreach (var server in servers)
            {
                if (firstServer != null && firstServer.Equals(server))
                {
                    continue;
                }

                var mapped = await QueryAsync(socket, server, cancellationToken).ConfigureAwait(false);
                if (mapped == null)
                {
                    continue;
                }

                if (first == null)
                {
                    first = mapped;
                    firstServer = server;
                }
                else
                {
                    second = mapped;
                    break;
                }
            }

            return (first, Classify(first, second));
        }

        /// <summary>The NAT kind two STUN answers imply.</summary>
        internal static NatKind Classify(IPEndPoint? first, IPEndPoint? second)
            => first == null || second == null ? NatKind.Unknown
                : first.Equals(second) ? NatKind.Cone
                : NatKind.Symmetric;
    }
}
