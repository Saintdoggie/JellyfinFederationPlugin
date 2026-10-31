using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace FederationCompanion;

internal static class PoolCallbackClient
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpRequestOptionsKey<IPAddress[]> ResolvedAddressesKey = new("FederationCompanion.PoolCallback.Addresses");

    internal static HttpClient CreateClient()
        => new(CreateHandler()) { Timeout = OverallTimeout };

    internal static SocketsHttpHandler CreateHandler()
        => new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            ConnectCallback = ConnectAsync
        };

    internal static bool TryNormalizeBaseUrl(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && !IsPublicAddress(address)))
        {
            return false;
        }

        normalized = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        return true;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && bytes[0] < 224
                && !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0)
        {
            return false;
        }

        return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x02);
    }

    internal static async Task<PoolCallbackResult> SendAsync(
        HttpClient http,
        string? callbackUrl,
        string callbackToken,
        string noticePath,
        object payload,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveAsync = null)
    {
        if (!TryNormalizeBaseUrl(callbackUrl, out var normalized))
        {
            return new PoolCallbackResult(PoolCallbackOutcome.PolicyRejected, 0);
        }

        var host = new Uri(normalized, UriKind.Absolute).DnsSafeHost;
        var addresses = await (resolveAsync ?? Dns.GetHostAddressesAsync)(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            return new PoolCallbackResult(PoolCallbackOutcome.PolicyRejected, 0);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, normalized + noticePath)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("X-Federation-Token", callbackToken);
        request.Options.Set(ResolvedAddressesKey, addresses);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new PoolCallbackResult(PoolCallbackOutcome.Success, (int)response.StatusCode)
            : new PoolCallbackResult(PoolCallbackOutcome.HttpError, (int)response.StatusCode);
    }

    internal static async ValueTask<Stream> ConnectResolvedAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException("Pool callback did not resolve exclusively to public addresses.");
        }

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Could not connect to the pool callback.");
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(ResolvedAddressesKey, out var addresses))
        {
            throw new HttpRequestException("Pool callback addresses were not validated.");
        }

        // Connect only to the addresses checked for this request, not a second DNS answer.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        return await ConnectResolvedAsync(addresses, context.DnsEndPoint.Port, timeout.Token).ConfigureAwait(false);
    }
}

internal enum PoolCallbackOutcome
{
    Success,
    HttpError,
    PolicyRejected
}

internal readonly record struct PoolCallbackResult(PoolCallbackOutcome Outcome, int StatusCode);
