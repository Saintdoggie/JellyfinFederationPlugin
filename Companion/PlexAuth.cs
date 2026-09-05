using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FederationCompanion;

/// <summary>
/// Plex's account-level OAuth PIN flow: mint a PIN, send the user to Plex's
/// own sign-in page linked to it, then poll until Plex reports the PIN was
/// claimed and hands back an account auth token. Nothing about a user's
/// actual Plex password ever passes through this app.
/// </summary>
public sealed class PlexAuth
{
    private const string PlexTvBaseUrl = "https://plex.tv";

    private readonly HttpClient _http;
    private readonly string _clientIdentifier;

    public PlexAuth(HttpClient http, string clientIdentifier)
    {
        _http = http;
        _clientIdentifier = clientIdentifier;
    }

    /// <summary>
    /// Starts a sign-in attempt: mints a PIN and returns both its id (to poll
    /// with) and the URL the user should be sent to in their browser to
    /// approve it.
    /// </summary>
    public async Task<(int PinId, string SignInUrl)> StartSignInAsync(string appBaseUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PlexTvBaseUrl}/api/v2/pins");
        ApplyHeaders(request);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["strong"] = "true"
        });

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var pin = await response.Content.ReadFromJsonAsync<PlexPinResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Plex did not return a PIN.");

        var signInUrl = "https://app.plex.tv/auth#?"
            + $"clientID={Uri.EscapeDataString(_clientIdentifier)}"
            + $"&code={Uri.EscapeDataString(pin.Code)}"
            + "&context%5Bdevice%5D%5Bproduct%5D=Federation%20Companion"
            + $"&forwardUrl={Uri.EscapeDataString(appBaseUrl)}";

        return (pin.Id, signInUrl);
    }

    /// <summary>
    /// Checks whether a PIN has been claimed yet. Returns null while still
    /// waiting - PINs expire after a few minutes unclaimed, at which point
    /// the caller should start a fresh sign-in rather than keep polling one
    /// that can never succeed.
    /// </summary>
    public async Task<string?> TryCompleteSignInAsync(int pinId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PlexTvBaseUrl}/api/v2/pins/{pinId}");
        ApplyHeaders(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A 404 here means the PIN expired or was never valid - either
            // way, nothing left to poll for.
            return null;
        }

        var pin = await response.Content.ReadFromJsonAsync<PlexPinResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(pin?.AuthToken) ? null : pin.AuthToken;
    }

    /// <summary>
    /// Lists the Plex Media Servers this account can reach, so the user can
    /// pick which one this app manages (almost always their own, but Plex
    /// accounts can have access to more than one).
    /// </summary>
    public async Task<List<PlexResource>> GetOwnedServersAsync(string accountToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PlexTvBaseUrl}/api/v2/resources?includeHttps=1");
        ApplyHeaders(request);
        request.Headers.TryAddWithoutValidation("X-Plex-Token", accountToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var resources = await response.Content.ReadFromJsonAsync<List<PlexResource>>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? new List<PlexResource>();

        return resources.Where(r => string.Equals(r.Provides, "server", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Selects the address most likely to work from a friend's Jellyfin server.
    /// A public direct HTTPS connection wins over a LAN address; Plex Relay is
    /// the fallback. The previous local-first choice made catalog sync appear
    /// healthy on the Companion machine while every remote movie play failed.
    /// </summary>
    public static PlexConnection? SelectFederationConnection(PlexResource server)
        => OrderFederationConnections(server).FirstOrDefault();

    public static IEnumerable<PlexConnection> OrderFederationConnections(PlexResource server)
    {
        return server.Connections
            .Where(c => Uri.TryCreate(c.Uri, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .OrderBy(c => ConnectionRank(c));
    }

    private static int ConnectionRank(PlexConnection connection)
    {
        var https = Uri.TryCreate(connection.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;

        if (!connection.Local && !connection.Relay && https)
        {
            return 0;
        }

        if (!connection.Local && !connection.Relay)
        {
            return 1;
        }

        if (connection.Relay && https)
        {
            return 2;
        }

        if (!connection.Local)
        {
            return 3;
        }

        return https ? 4 : 5;
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", _clientIdentifier);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "Federation Companion");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    private sealed class PlexPinResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("authToken")]
        public string? AuthToken { get; set; }
    }
}

public sealed class PlexResource
{
    [JsonPropertyName("clientIdentifier")]
    public string MachineIdentifier { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("owned")]
    public bool Owned { get; set; }

    [JsonPropertyName("provides")]
    public string Provides { get; set; } = string.Empty;

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("connections")]
    public List<PlexConnection> Connections { get; set; } = new();
}

public sealed class PlexConnection
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("local")]
    public bool Local { get; set; }

    [JsonPropertyName("relay")]
    public bool Relay { get; set; }
}
