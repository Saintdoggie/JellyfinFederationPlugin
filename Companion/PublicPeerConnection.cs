using System.Net;
using System.Net.Sockets;

namespace FederationCompanion;

/// <summary>Connect to the addresses we validated, preventing DNS rebinding on
/// Companion requests and imports. Local Plex itself uses a separate client.</summary>
internal static class PublicPeerConnection
{
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is > 0 and < 224 && bytes[0] != 127 && bytes[0] != 10
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x02);
    }

    internal static HttpClient CreateClient(bool streaming = false) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectAsync,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 8
    })
    { Timeout = streaming ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(5) };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a))) throw new HttpRequestException("Companion must resolve to public addresses.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Companion could not be reached.");
    }
}
