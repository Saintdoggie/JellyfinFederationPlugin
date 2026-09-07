using System.Net.Http.Headers;

namespace FederationCompanion;

/// <summary>
/// Funnel is a public door for one local app. Owners often Funnel Plex itself,
/// so https://name.ts.net is Plex, not Companion. Share codes must not claim
/// that URL as Companion or the Jellyfin friend will POST /api/link/complete
/// into Plex and fail.
/// </summary>
public static class CompanionSelfCheck
{
    public static bool LooksLikeUs(string? body, int statusCode)
    {
        if (statusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("PlexCompanion", StringComparison.OrdinalIgnoreCase)
            || body.Contains("federationPluginVersion", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikePlex(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("MediaContainer", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Plex Media Server", StringComparison.OrdinalIgnoreCase)
            || body.Contains("X-Plex-", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> PublicUrlIsThisCompanionAsync(HttpClient http, string? publicUrl, CancellationToken cancellationToken)
    {
        if (!PlexRemoteEndpoint.IsPublicHttpsUrl(publicUrl))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, publicUrl!.Trim().TrimEnd('/') + "/api/federation/info");
            if (!http.DefaultRequestHeaders.UserAgent.Any())
            {
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FederationCompanion", "1.0"));
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return LooksLikeUs(body, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }
}
