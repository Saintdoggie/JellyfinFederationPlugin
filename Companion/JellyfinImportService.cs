using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FederationCompanion;

/// <summary>
/// Client for a Jellyfin Federation server's own peer protocol (the same
/// <c>Peer/*</c> endpoints a friend's Federation plugin already calls) -
/// this app authenticates the exact same way, with the connect code's
/// federation token in the <c>X-Federation-Token</c> header, but never
/// installs a Federation plugin of its own. Deliberately thin: this app
/// only ever needs a flat list of movies/episodes and a playable url per
/// item, never the full browsing/metadata surface a Jellyfin client uses.
/// </summary>
public sealed class JellyfinImportService
{
    private const string TokenHeader = "X-Federation-Token";

    private readonly HttpClient _http;

    public JellyfinImportService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<PeerLibrary>> GetLibrariesAsync(string peerUrl, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{peerUrl.TrimEnd('/')}/Plugins/Federation/Peer/Libraries");
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PeerLibrariesResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return body?.Items ?? new List<PeerLibrary>();
    }

    /// <summary>
    /// Fetches every movie/episode under one library folder, paging until the
    /// server returns fewer than a full page - <c>Recursive=true</c> is
    /// already hardcoded server-side (see <c>GetPeerItems</c>), so one call
    /// per folder (per media type) is enough to get everything under it,
    /// with no need to separately walk series/seasons.
    /// </summary>
    public async Task<List<PeerItem>> GetItemsAsync(string peerUrl, string token, string parentId, string mediaType, CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var items = new List<PeerItem>();
        var startIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"{peerUrl.TrimEnd('/')}/Plugins/Federation/Peer/Items"
                + $"?parentId={Uri.EscapeDataString(parentId)}&mediaType={Uri.EscapeDataString(mediaType)}"
                + $"&startIndex={startIndex}&limit={pageSize}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(TokenHeader, token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<PeerItemsResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var page = body?.Items ?? new List<PeerItem>();
            items.AddRange(page);

            if (page.Count < pageSize)
            {
                break;
            }

            startIndex += pageSize;
        }

        return items;
    }

    public async Task<(string Token, DateTime ExpiresUtc)?> GetPlaybackTokenAsync(string peerUrl, string token, string itemId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{peerUrl.TrimEnd('/')}/Plugins/Federation/PlaybackToken")
        {
            Content = JsonContent.Create(new { ItemId = itemId })
        };
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<PlaybackTokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (body?.Token == null)
        {
            return null;
        }

        return (body.Token, body.ExpiresUtc);
    }

    public static string BuildStreamUrl(string peerUrl, string itemId, string playbackToken)
        => $"{peerUrl.TrimEnd('/')}/Plugins/Federation/DirectStream/{itemId}?token={Uri.EscapeDataString(playbackToken)}";

    private sealed class PeerLibrariesResponse
    {
        [JsonPropertyName("Items")]
        public List<PeerLibrary>? Items { get; set; }
    }

    private sealed class PeerItemsResponse
    {
        [JsonPropertyName("Items")]
        public List<PeerItem>? Items { get; set; }
    }

    private sealed class PlaybackTokenResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("expiresUtc")]
        public DateTime ExpiresUtc { get; set; }
    }
}

public sealed class PeerLibrary
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("CollectionType")]
    public string? CollectionType { get; set; }
}

public sealed class PeerItem
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("ProductionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("IndexNumber")]
    public int? IndexNumber { get; set; }

    [JsonPropertyName("ParentIndexNumber")]
    public int? ParentIndexNumber { get; set; }

    [JsonPropertyName("SeriesName")]
    public string? SeriesName { get; set; }
}
