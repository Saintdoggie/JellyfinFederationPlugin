using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FederationCompanion;

/// <summary>
/// Minimal Plex Media Server client for listing/creating/refreshing library
/// sections. Catalog and media relaying for Jellyfin friends lives in
/// <see cref="PlexFederationRelay"/> so the source credential remains local.
/// </summary>
public sealed class PlexClient
{
    private readonly HttpClient _http;

    public PlexClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<string?> GetMachineIdentifierAsync(string baseUrl, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/identity");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PlexIdentityResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return body?.MediaContainer?.MachineIdentifier;
    }

    public Task<string?> GetServerNameAsync(string baseUrl, string token, CancellationToken cancellationToken)
        => Task.FromResult<string?>("Plex Media Server");

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
            .Select(d => new CompanionLibrary
            {
                SectionKey = d.Key,
                Title = d.Title,
                Type = d.Type,
                Locations = (d.Location ?? new List<PlexLocation>()).Select(l => l.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            })
            .ToList();
    }

    /// <summary>
    /// Creates a Plex library section if one with this name/type/path does not
    /// already exist, then returns its key. Used by the import manager so a
    /// Plex owner does not have to click through Plex Settings to see a
    /// Jellyfin friend's content.
    /// </summary>
    public async Task<string> EnsureSectionAsync(
        string baseUrl,
        string token,
        string name,
        string type,
        string location,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePlexPath(location);
        var existing = (await GetSectionsAsync(baseUrl, token, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(s =>
                string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase)
                && s.Locations.Any(p => string.Equals(NormalizePlexPath(p), normalized, StringComparison.OrdinalIgnoreCase)));
        if (existing != null)
        {
            return existing.SectionKey;
        }

        var url = BuildCreateSectionUrl(baseUrl, name, type, normalized);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var created = (await GetSectionsAsync(baseUrl, token, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(s =>
                string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase)
                && s.Locations.Any(p => string.Equals(NormalizePlexPath(p), normalized, StringComparison.OrdinalIgnoreCase)));

        return created?.SectionKey
            ?? throw new InvalidOperationException("Plex accepted the new library but it did not show up in the section list.");
    }

    public static string BuildCreateSectionUrl(string baseUrl, string name, string type, string location)
    {
        var isShow = string.Equals(type, "show", StringComparison.OrdinalIgnoreCase);
        var query = new Dictionary<string, string>
        {
            ["name"] = name,
            ["type"] = isShow ? "show" : "movie",
            ["agent"] = isShow ? "tv.plex.agents.series" : "tv.plex.agents.movie",
            ["scanner"] = isShow ? "Plex TV Series" : "Plex Movie",
            ["language"] = "en-US",
            ["location"] = NormalizePlexPath(location)
        };
        return $"{baseUrl.TrimEnd('/')}/library/sections?{string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))}";
    }

    public static string MapExportPathForPlex(string exportPath, string? plexVisibleRoot)
    {
        if (string.IsNullOrWhiteSpace(plexVisibleRoot))
        {
            return NormalizePlexPath(exportPath);
        }

        return NormalizePlexPath(plexVisibleRoot);
    }

    public static string NormalizePlexPath(string path)
        => path.Replace('\\', '/').TrimEnd('/');

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

        [JsonPropertyName("Location")]
        public List<PlexLocation>? Location { get; set; }
    }

    private sealed class PlexLocation
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }

    private sealed class PlexIdentityResponse
    {
        [JsonPropertyName("MediaContainer")]
        public PlexIdentityContainer? MediaContainer { get; set; }
    }

    private sealed class PlexIdentityContainer
    {
        [JsonPropertyName("machineIdentifier")]
        public string? MachineIdentifier { get; set; }
    }
}
