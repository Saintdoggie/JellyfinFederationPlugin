using System.Net;
using System.Net.Sockets;

namespace FederationCompanion;

/// <summary>
/// Picks a Plex connection a Jellyfin friend can actually reach when they are
/// not on this machine's LAN or Tailscale tailnet. Companion itself should keep
/// using a local connection to talk to Plex; only the address handed to a
/// remote friend goes through here.
/// </summary>
public static class PlexRemoteEndpoint
{
    /// <summary>
    /// True when <paramref name="url"/> is loopback, RFC 1918, link-local,
    /// unique-local IPv6, or CGNAT/Tailscale (100.64/10). A hostname is never
    /// flagged: <c>*.ts.net</c> Funnel addresses and <c>*.plex.direct</c>
    /// remote-access URLs are the intended public paths.
    /// </summary>
    public static bool IsLanOrPrivateHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return true;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return (b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80);
        }

        return false;
    }

    /// <summary>
    /// True when this is an https URL whose host is not a private/CGNAT IP.
    /// Used for Companion's own Funnel/public address, which a remote Jellyfin
    /// server must be able to call without joining this Tailscale tailnet.
    /// </summary>
    public static bool IsPublicHttpsUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !IsLanOrPrivateHost(url);

    /// <summary>
    /// Friend-facing Plex connections, best first: public HTTPS, then Plex Relay,
    /// never LAN or Tailscale CGNAT. Probe-from-here is deliberately not required
    /// — Companion often cannot hairpin its own public plex.direct URL.
    /// </summary>
    public static IReadOnlyList<PlexConnection> RankFriendFacing(IEnumerable<PlexConnection> connections)
    {
        static int Score(PlexConnection connection)
        {
            var https = IsHttps(connection);
            if (!connection.Relay && https)
            {
                return 0;
            }

            if (https)
            {
                return 1;
            }

            return connection.Relay ? 2 : 3;
        }

        return connections
            .Where(c => !c.Local && !string.IsNullOrWhiteSpace(c.Uri) && !IsLanOrPrivateHost(c.Uri))
            .OrderBy(Score)
            .ToList();
    }

    public static PlexConnection? SelectFriendFacing(IEnumerable<PlexConnection> connections)
        => RankFriendFacing(connections).FirstOrDefault();

    private static bool IsHttps(PlexConnection connection)
        => string.Equals(connection.Protocol, "https", StringComparison.OrdinalIgnoreCase)
            || connection.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
