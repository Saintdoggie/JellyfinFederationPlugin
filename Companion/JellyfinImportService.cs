using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

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
    private readonly HttpClient _streamHttp;

    public JellyfinImportService(HttpClient http)
        : this(http, http)
    {
    }

    public JellyfinImportService(HttpClient http, HttpClient streamHttp)
    {
        _http = http;
        _streamHttp = streamHttp;
    }

    public async Task<List<PeerLibrary>> GetLibrariesAsync(string peerUrl, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{peerUrl.TrimEnd('/')}/Plugins/Federation/Peer/Libraries");
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<PeerLibrariesResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return body?.Items ?? throw new JsonException("The friend returned an invalid library list. Existing imports were kept.");
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
                + $"&startIndex={startIndex}&limit={pageSize}&includeMediaSources=true";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(TokenHeader, token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<PeerItemsResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var page = body?.Items ?? throw new JsonException("The friend returned an incomplete catalog. Existing imports were kept.");
            if (page.Any(item => !Guid.TryParse(item.Id, out _)))
            {
                throw new JsonException("The friend returned invalid item identifiers. Existing imports were kept.");
            }

            var rawCount = page.Count;
            // Defense for older peers that accidentally shared federated items.
            page.RemoveAll(item => item.ProviderIds?.Keys.Any(key => string.Equals(key, "FederationKey", StringComparison.OrdinalIgnoreCase)) == true);
            items.AddRange(page);

            if (rawCount < pageSize)
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

    /// <summary>
    /// Builds the stable URL written into Plex's .strm file. The URL contains
    /// an item-bound HMAC capability, never the peer's standing federation
    /// token or a short-lived upstream playback token.
    /// </summary>
    public static string BuildCompanionStreamUrl(string companionBaseUrl, JellyfinImportPeer peer, string itemId)
    {
        var capability = CreateStreamCapability(peer.Id, itemId, peer.StreamSecret);
        return $"{companionBaseUrl.TrimEnd('/')}/stream/{Uri.EscapeDataString(peer.Id)}/{Uri.EscapeDataString(itemId)}?cap={capability}";
    }

    public static bool IsValidStreamCapability(JellyfinImportPeer peer, string itemId, string? capability)
    {
        if (string.IsNullOrWhiteSpace(peer.StreamSecret) || string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        try
        {
            var expected = CreateStreamCapability(peer.Id, itemId, peer.StreamSecret);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(capability));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Mints fresh upstream authorization at actual play time and relays the
    /// response to Plex. Range and validator headers are preserved so seeking,
    /// resume, direct play, and Plex's initial byte probes behave like a direct
    /// Jellyfin stream.
    /// </summary>
    public async Task RelayStreamAsync(
        JellyfinImportPeer peer,
        string itemId,
        HttpRequest incoming,
        HttpResponse outgoing,
        CancellationToken cancellationToken)
    {
        var minted = await GetPlaybackTokenAsync(peer.Url, peer.Token, itemId, cancellationToken).ConfigureAwait(false);
        if (minted == null)
        {
            outgoing.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var upstreamUrl = BuildStreamUrl(peer.Url, itemId, minted.Value.Token);
        using var request = new HttpRequestMessage(
            HttpMethods.IsHead(incoming.Method) ? HttpMethod.Head : HttpMethod.Get,
            upstreamUrl);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        CopyRequestHeader(incoming, request, "Range");
        CopyRequestHeader(incoming, request, "If-Range");
        CopyRequestHeader(incoming, request, "If-None-Match");
        CopyRequestHeader(incoming, request, "If-Modified-Since");

        using var response = await _streamHttp.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        outgoing.StatusCode = (int)response.StatusCode;
        CopyResponseHeader(response, outgoing, "Accept-Ranges");
        CopyResponseHeader(response, outgoing, "Content-Range");
        CopyResponseHeader(response, outgoing, "Content-Length");
        CopyResponseHeader(response, outgoing, "Content-Type");
        CopyResponseHeader(response, outgoing, "Content-Disposition");
        CopyResponseHeader(response, outgoing, "ETag");
        CopyResponseHeader(response, outgoing, "Last-Modified");
        outgoing.Headers.CacheControl = "private, no-store";

        if (!HttpMethods.IsHead(incoming.Method) && response.Content != null)
        {
            await response.Content.CopyToAsync(outgoing.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string CreateStreamCapability(string peerId, string itemId, string secret)
    {
        var key = Convert.FromHexString(secret);
        var message = Encoding.UTF8.GetBytes($"{peerId}\n{itemId}");
        return Convert.ToHexString(HMACSHA256.HashData(key, message));
    }

    private static void CopyRequestHeader(HttpRequest incoming, HttpRequestMessage outgoing, string name)
    {
        if (incoming.Headers.TryGetValue(name, out var value))
        {
            outgoing.Headers.TryAddWithoutValidation(name, value.ToArray());
        }
    }

    private static void CopyResponseHeader(HttpResponseMessage incoming, HttpResponse outgoing, string name)
    {
        if (incoming.Headers.TryGetValues(name, out var values)
            || (incoming.Content != null && incoming.Content.Headers.TryGetValues(name, out values)))
        {
            outgoing.Headers[name] = values.ToArray();
        }
    }

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
    [JsonPropertyName("MediaSources")]
    public List<PeerMediaSource>? MediaSources { get; set; }

    [JsonPropertyName("DateCreated")]
    public DateTime? DateCreated { get; set; }

    [JsonPropertyName("ProviderIds")]
    public Dictionary<string, string>? ProviderIds { get; set; }

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

    [JsonPropertyName("IndexNumberEnd")]
    public int? IndexNumberEnd { get; set; }

    [JsonPropertyName("ParentIndexNumber")]
    public int? ParentIndexNumber { get; set; }

    [JsonPropertyName("SeriesName")]
    public string? SeriesName { get; set; }
}

public sealed class PeerMediaSource
{
    public string? Container { get; set; }
    public long? Size { get; set; }
}
