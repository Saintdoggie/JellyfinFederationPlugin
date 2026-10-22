using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>
    /// Which endpoints from a code (or a reconnect message) this server will send packets
    /// to. A hostile code must not be able to point it at its own LAN, loopback, a
    /// well-known service or a range that isn't a real internet address.
    /// </summary>
    public static class PairingEndpoints
    {
        /// <summary>Lowest port accepted (no well-known services).</summary>
        public const int MinPort = 1024;

        private static readonly (uint Network, int Bits)[] NeverPublic =
        {
            (Ip(0, 0, 0, 0), 8),         // "this network"
            (Ip(10, 0, 0, 0), 8),        // private
            (Ip(100, 64, 0, 0), 10),     // carrier-grade NAT (and Tailscale)
            (Ip(127, 0, 0, 0), 8),       // loopback
            (Ip(169, 254, 0, 0), 16),    // link-local
            (Ip(172, 16, 0, 0), 12),     // private
            (Ip(192, 0, 0, 0), 24),      // protocol assignments
            (Ip(192, 0, 2, 0), 24),      // documentation
            (Ip(192, 88, 99, 0), 24),    // 6to4 relay
            (Ip(192, 168, 0, 0), 16),    // private
            (Ip(198, 18, 0, 0), 15),     // benchmarking
            (Ip(198, 51, 100, 0), 24),   // documentation
            (Ip(203, 0, 113, 0), 24),    // documentation
            (Ip(224, 0, 0, 0), 4),       // multicast
            (Ip(240, 0, 0, 0), 4),       // reserved and broadcast
        };

        private static readonly (uint Network, int Bits)[] Private =
        {
            (Ip(10, 0, 0, 0), 8),
            (Ip(172, 16, 0, 0), 12),
            (Ip(192, 168, 0, 0), 16),
        };

        /// <summary>
        /// Whether to send to <paramref name="endpoint"/>: a public IPv4 address, or a
        /// private address inside one of this server's own subnets (two friends on one
        /// LAN), on a port of at least 1024. <paramref name="allowPrivate"/> treats private
        /// ranges as public (the NAT test harness only).
        /// </summary>
        /// <returns>True when allowed.</returns>
        public static bool IsAcceptable(IPEndPoint endpoint, IReadOnlyCollection<(IPAddress Address, int PrefixLength)> ownSubnets, bool allowPrivate = false)
        {
            if (endpoint.AddressFamily != AddressFamily.InterNetwork || endpoint.Port < MinPort || endpoint.Port > 65535)
            {
                return false;
            }

            var ip = ToUInt(endpoint.Address);
            if (allowPrivate && Private.Any(r => In(ip, r.Network, r.Bits)))
            {
                return true;
            }

            if (!NeverPublic.Any(r => In(ip, r.Network, r.Bits)))
            {
                return true;
            }

            // A LAN candidate: private, and inside a subnet this server is on, but never
            // this server's own address.
            return Private.Any(r => In(ip, r.Network, r.Bits))
                && ownSubnets.Any(s => s.Address.AddressFamily == AddressFamily.InterNetwork
                    && s.PrefixLength is >= 8 and <= 30
                    && !s.Address.Equals(endpoint.Address)
                    && In(ip, ToUInt(s.Address) & Mask(s.PrefixLength), s.PrefixLength));
        }

        /// <summary>The acceptable endpoints of a list, in order, without duplicates.</summary>
        /// <returns>The filtered list.</returns>
        public static List<IPEndPoint> Filter(IEnumerable<IPEndPoint> endpoints, IReadOnlyCollection<(IPAddress Address, int PrefixLength)> ownSubnets, bool allowPrivate = false)
            => endpoints.Where(e => IsAcceptable(e, ownSubnets, allowPrivate)).Distinct().ToList();

        private static bool In(uint ip, uint network, int bits) => (ip & Mask(bits)) == (network & Mask(bits));

        private static uint Mask(int bits) => bits == 0 ? 0 : uint.MaxValue << (32 - bits);

        private static uint ToUInt(IPAddress address)
        {
            var b = address.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static uint Ip(byte a, byte b, byte c, byte d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
    }
}
