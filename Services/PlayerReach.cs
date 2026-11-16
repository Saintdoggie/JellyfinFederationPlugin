using System;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Whether a viewer's device can be sent to a friend's address.
    /// <para>
    /// This server often knows a friend by an address only it can open: a LAN
    /// address, a Tailscale address, or a local name. Handing that address to a
    /// phone on mobile data (a download redirect, a direct-play source) gives the
    /// phone something that never answers, and shows it a private address. Such a
    /// friend's bytes go through this server instead.
    /// </para>
    /// Pure: no DNS lookups. An ordinary host name is taken as public, because a
    /// name that resolves privately here (a Funnel name seen from inside the
    /// tailnet, split DNS) usually resolves publicly for the device.
    /// </summary>
    public static class PlayerReach
    {
        /// <summary>
        /// Environment switch for a server whose every viewer is on its own LAN or
        /// tailnet (also what the container checks in <c>scripts/</c> use, where
        /// servers know each other by container name): set to <c>1</c> and private
        /// addresses are given to players as before 0.0.227.
        /// </summary>
        public const string EveryPlayerIsLocalEnv = "FEDERATION_PLAYERS_REACH_PRIVATE";

        private static readonly bool EveryPlayerIsLocal =
            string.Equals(Environment.GetEnvironmentVariable(EveryPlayerIsLocalEnv), "1", StringComparison.Ordinal);

        /// <summary>Whether a friend's private address may be handed to a device for a download.</summary>
        public static bool DownloadMayUse(string? url) => EveryPlayerIsLocal || !IsPrivateAddress(url);

        /// <summary>
        /// True when <paramref name="url"/> names a host only this server's own
        /// networks can open: a loopback, LAN, link-local or Tailscale address, a
        /// name without a dot, a <c>.local/.lan/.internal/.home.arpa/.localhost</c>
        /// name, or a tailnet name over plain http (Funnel is always https).
        /// </summary>
        public static bool IsPrivateAddress(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (ConfigValidator.IsPrivateOrLoopbackHost(url))
            {
                return true;
            }

            if (uri.HostNameType != UriHostNameType.Dns)
            {
                return false;
            }

            var host = uri.Host.TrimEnd('.').ToLowerInvariant();
            return !host.Contains('.', StringComparison.Ordinal)
                || host.EndsWith(".local", StringComparison.Ordinal)
                || host.EndsWith(".lan", StringComparison.Ordinal)
                || host.EndsWith(".internal", StringComparison.Ordinal)
                || host.EndsWith(".home.arpa", StringComparison.Ordinal)
                || host.EndsWith(".localhost", StringComparison.Ordinal)
                || (uri.Scheme == Uri.UriSchemeHttp && host.EndsWith(".ts.net", StringComparison.Ordinal));
        }

        /// <summary>
        /// Whether a player at <paramref name="client"/> may be given
        /// <paramref name="url"/> to open itself. A public address always; a
        /// private one only when the player is plainly on the same network: both
        /// on Tailscale, or both in the same IPv4 /24. A player whose address is
        /// unknown or loopback (a reverse proxy on this machine) is taken to be
        /// somewhere else.
        /// </summary>
        public static bool PlayerCanOpen(string? url, IPAddress? client)
        {
            if (EveryPlayerIsLocal || !IsPrivateAddress(url))
            {
                return true;
            }

            if (client == null
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !IPAddress.TryParse(uri.Host, out var friend))
            {
                return false;
            }

            if (client.IsIPv4MappedToIPv6)
            {
                client = client.MapToIPv4();
            }

            if (friend.IsIPv4MappedToIPv6)
            {
                friend = friend.MapToIPv4();
            }

            if (client.AddressFamily != AddressFamily.InterNetwork
                || friend.AddressFamily != AddressFamily.InterNetwork
                || IPAddress.IsLoopback(client)
                || IPAddress.IsLoopback(friend))
            {
                return false;
            }

            var c = client.GetAddressBytes();
            var f = friend.GetAddressBytes();
            if (IsTailscale(c) || IsTailscale(f))
            {
                return IsTailscale(c) && IsTailscale(f);
            }

            return c[0] == f[0] && c[1] == f[1] && c[2] == f[2];
        }

        private static bool IsTailscale(byte[] b) => b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
