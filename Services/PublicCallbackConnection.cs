using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services;

internal static class PublicCallbackConnection
{
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (ConfigValidator.IsPrivateOrLoopbackHost(new UriBuilder("http", address.ToString()).Uri.ToString())) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] != 0 && bytes[0] != 127 && bytes[0] < 224
                && !(bytes[0] == 198 && bytes[1] is 18 or 19);
        // Only globally routable IPv6 unicast; exclude mapped/NAT64, local,
        // unspecified and multicast addresses (mapped v4 was checked above).
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x02); // 6to4 can encapsulate private IPv4.
    }

    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        return await ConnectResolvedAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
    }

    internal static async ValueTask<Stream> ConnectResolvedAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a)))
            throw new HttpRequestException("Friend callback did not resolve exclusively to public addresses.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Could not connect to the public friend callback.");
    }
}
