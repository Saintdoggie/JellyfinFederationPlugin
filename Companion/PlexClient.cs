using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FederationCompanion;

/// <summary>
/// Minimal Plex Media Server client for what this app needs: listing library
/// sections. Deliberately not the full protocol client the Jellyfin plugin's
/// PlexApiClient is (no item sync, no streaming) - this app never serves
/// media itself, it only manages what a Jellyfin Federation peer is allowed
/// to pull directly from the user's own Plex server.
/// </summary>
public sealed class PlexClient
{
    private readonly HttpClient _http;

    public PlexClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<CompanionLibrary>> GetSectionsAsync(string baseUrl, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/library/sections");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PlexSectionsResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var directories = body?.MediaContainer?.Directory ?? new List<PlexDirectory>();

        return directories
            .Where(d => d.Type is "movie" or "show")
            .Select(d => new CompanionLibrary { SectionKey = d.Key, Title = d.Title, Type = d.Type })
            .ToList();
    }

    /// <summary>
    /// Kicks off a partial scan of one library section, so Plex picks up a
    /// sync's changes without waiting for its own scheduled scan interval.
    /// Best-effort: a failure here (Plex restarting, section already mid-scan)
    /// isn't worth failing the whole import sync over - Plex's own schedule
    /// still catches it eventually.
    /// </summary>
    public async Task RefreshSectionAsync(string baseUrl, string token, string sectionKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/library/sections/{sectionKey}/refresh");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private sealed class PlexSectionsResponse
    {
        [JsonPropertyName("MediaContainer")]
        public PlexMediaContainer? MediaContainer { get; set; }
    }

    private sealed class PlexMediaContainer
    {
        [JsonPropertyName("Directory")]
        public List<PlexDirectory>? Directory { get; set; }
    }

    private sealed class PlexDirectory
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }
}
