using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace FederationCompanion;

/// <summary>
/// Revocable, library-scoped Plex facade used by a connected Jellyfin server.
/// The real Plex credential never leaves Companion. Each friend receives a
/// separate token, and every catalog or media request is checked against the
/// libraries that the Plex owner currently shares.
/// </summary>
public sealed class PlexFederationRelay
{
    private readonly CompanionState _state;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _approvedResources = new();

    public PlexFederationRelay(CompanionState state, HttpClient http)
    {
        _state = state;
        _http = http;
    }

    public void ForgetPeer(string peerId)
        => _approvedResources.TryRemove(peerId, out _);

    public async Task RelayAsync(
        CompanionPeer peer,
        string? catchAllPath,
        HttpRequest incoming,
        HttpResponse outgoing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_state.ServerBaseUrl)
            || string.IsNullOrWhiteSpace(_state.ServerAccessToken)
            || !TryNormalizePath(catchAllPath, out var path))
        {
            outgoing.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (path == "/")
        {
            await RelayRawAsync(path, incoming, outgoing, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path == "/library/sections")
        {
            await RelayFilteredSectionsAsync(incoming, outgoing, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TrySectionItemsPath(path, out var sectionKey))
        {
            if (!IsSharedSection(sectionKey))
            {
                outgoing.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await RelayRawAsync(path, incoming, outgoing, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TryMetadataPath(path, out var ratingKey, out var isRootMetadata))
        {
            var metadata = await FetchBufferedAsync($"/library/metadata/{ratingKey}", incoming.Query, cancellationToken).ConfigureAwait(false);
            if (metadata == null)
            {
                outgoing.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            if (!metadata.IsSuccessStatusCode || !IsSharedMetadata(metadata.Body))
            {
                outgoing.StatusCode = metadata.IsSuccessStatusCode
                    ? StatusCodes.Status403Forbidden
                    : metadata.StatusCode;
                return;
            }

            RememberApprovedResources(peer.Id, metadata.Body);
            if (isRootMetadata)
            {
                await WriteBufferedAsync(metadata, incoming, outgoing, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RelayRawAsync(path, incoming, outgoing, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (path.StartsWith("/library/parts/", StringComparison.Ordinal)
            && _approvedResources.TryGetValue(peer.Id, out var resources)
            && resources.TryGetValue(path, out var resourceSection)
            && IsSharedSection(resourceSection))
        {
            var isDownload = string.Equals(incoming.Query["federationDownload"], "true", StringComparison.OrdinalIgnoreCase);
            var isBulkDownload = string.Equals(incoming.Query["federationBulk"], "true", StringComparison.OrdinalIgnoreCase);
            if (isDownload && (!peer.AllowDownloads || (isBulkDownload && !peer.AllowBulkDownloads)))
            {
                outgoing.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await RelayRawAsync(path, incoming, outgoing, cancellationToken).ConfigureAwait(false);
            return;
        }

        outgoing.StatusCode = StatusCodes.Status403Forbidden;
    }

    internal bool IsSharedMetadata(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("MediaContainer", out var container)
                || !container.TryGetProperty("Metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in metadata.EnumerateArray())
            {
                if (TryGetString(item, "librarySectionID", out var sectionId)
                    && IsSharedSection(sectionId))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed upstream response is never allowed through as media.
        }

        return false;
    }

    private bool IsSharedSection(string sectionKey)
        => _state.Libraries.Any(l => l.Shared && string.Equals(l.SectionKey, sectionKey, StringComparison.Ordinal));

    private async Task RelayFilteredSectionsAsync(
        HttpRequest incoming,
        HttpResponse outgoing,
        CancellationToken cancellationToken)
    {
        var response = await FetchBufferedAsync("/library/sections", incoming.Query, cancellationToken).ConfigureAwait(false);
        if (response == null)
        {
            outgoing.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var root = JsonNode.Parse(response.Body);
                var directories = root?["MediaContainer"]?["Directory"]?.AsArray();
                if (directories != null)
                {
                    foreach (var directory in directories.ToList())
                    {
                        var key = directory?["key"]?.GetValue<string>();
                        if (key == null || !IsSharedSection(key))
                        {
                            directories.Remove(directory);
                        }
                    }

                    response = response with { Body = JsonSerializer.SerializeToUtf8Bytes(root) };
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                outgoing.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
        }

        await WriteBufferedAsync(response, incoming, outgoing, cancellationToken).ConfigureAwait(false);
    }

    private void RememberApprovedResources(string peerId, byte[] metadataBody)
    {
        var resources = _approvedResources.GetOrAdd(peerId, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        try
        {
            using var document = JsonDocument.Parse(metadataBody);
            if (!document.RootElement.TryGetProperty("MediaContainer", out var container)
                || !container.TryGetProperty("Metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in metadata.EnumerateArray())
            {
                if (TryGetString(item, "librarySectionID", out var sectionId)
                    && IsSharedSection(sectionId))
                {
                    RememberStrings(item, sectionId, resources);
                }
            }
        }
        catch (JsonException)
        {
            // IsSharedMetadata already rejected malformed JSON; this is defensive.
        }
    }

    private static void RememberStrings(
        JsonElement element,
        string sectionId,
        ConcurrentDictionary<string, string> resources)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (value != null && value.StartsWith("/library/parts/", StringComparison.Ordinal))
                    {
                        resources[value.Split('?', 2)[0]] = sectionId;
                    }
                }

                RememberStrings(property.Value, sectionId, resources);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RememberStrings(child, sectionId, resources);
            }
        }
    }

    private async Task RelayRawAsync(
        string path,
        HttpRequest incoming,
        HttpResponse outgoing,
        CancellationToken cancellationToken)
    {
        using var request = BuildUpstreamRequest(path, incoming.Query, incoming.Method);
        CopyRequestHeader(incoming, request, "Range");
        CopyRequestHeader(incoming, request, "If-Range");
        CopyRequestHeader(incoming, request, "If-None-Match");
        CopyRequestHeader(incoming, request, "If-Modified-Since");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        outgoing.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, outgoing);

        if (!HttpMethods.IsHead(incoming.Method))
        {
            await response.Content.CopyToAsync(outgoing.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<BufferedPlexResponse?> FetchBufferedAsync(
        string path,
        IQueryCollection query,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildUpstreamRequest(path, query, HttpMethods.Get);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new BufferedPlexResponse(
                (int)response.StatusCode,
                response.IsSuccessStatusCode,
                response.Content.Headers.ContentType?.ToString(),
                body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private HttpRequestMessage BuildUpstreamRequest(string path, IQueryCollection query, string method)
    {
        var baseUri = new Uri(_state.ServerBaseUrl!.TrimEnd('/') + "/", UriKind.Absolute);
        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + path,
            Query = string.Join("&", query
                .Where(pair => !string.Equals(pair.Key, "X-Plex-Token", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pair.Key, "federationDownload", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pair.Key, "federationBulk", StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value.Select(value => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value ?? string.Empty)}")))
        };
        var request = new HttpRequestMessage(HttpMethods.IsHead(method) ? HttpMethod.Head : HttpMethod.Get, builder.Uri);
        request.Headers.TryAddWithoutValidation("X-Plex-Token", _state.ServerAccessToken);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, */*");
        return request;
    }

    private static async Task WriteBufferedAsync(
        BufferedPlexResponse response,
        HttpRequest incoming,
        HttpResponse outgoing,
        CancellationToken cancellationToken)
    {
        outgoing.StatusCode = response.StatusCode;
        if (!string.IsNullOrWhiteSpace(response.ContentType))
        {
            outgoing.ContentType = response.ContentType;
        }

        outgoing.ContentLength = response.Body.LongLength;
        outgoing.Headers.CacheControl = "private, no-store";
        if (!HttpMethods.IsHead(incoming.Method))
        {
            await outgoing.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryNormalizePath(string? input, out string path)
    {
        var candidate = "/" + (input ?? string.Empty).TrimStart('/');
        if (candidate.Contains("..", StringComparison.Ordinal)
            || candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Contains('\0', StringComparison.Ordinal))
        {
            path = string.Empty;
            return false;
        }

        path = candidate;
        return true;
    }

    private static bool TrySectionItemsPath(string path, out string sectionKey)
    {
        const string prefix = "/library/sections/";
        const string suffix = "/all";
        if (path.StartsWith(prefix, StringComparison.Ordinal)
            && path.EndsWith(suffix, StringComparison.Ordinal))
        {
            sectionKey = path.Substring(prefix.Length, path.Length - prefix.Length - suffix.Length);
            return sectionKey.Length > 0 && !sectionKey.Contains('/');
        }

        sectionKey = string.Empty;
        return false;
    }

    private static bool TryMetadataPath(string path, out string ratingKey, out bool isRoot)
    {
        const string prefix = "/library/metadata/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            ratingKey = string.Empty;
            isRoot = false;
            return false;
        }

        var remainder = path.Substring(prefix.Length);
        var slash = remainder.IndexOf('/');
        ratingKey = slash < 0 ? remainder : remainder.Substring(0, slash);
        isRoot = slash < 0;
        return ratingKey.Length is > 0 and <= 20 && ratingKey.All(char.IsDigit);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
        return value.Length > 0;
    }

    private static void CopyRequestHeader(HttpRequest incoming, HttpRequestMessage outgoing, string name)
    {
        if (incoming.Headers.TryGetValue(name, out var value))
        {
            outgoing.Headers.TryAddWithoutValidation(name, value.ToArray());
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage incoming, HttpResponse outgoing)
    {
        foreach (var name in new[] { "Accept-Ranges", "Content-Range", "Content-Length", "Content-Type", "Content-Disposition", "ETag", "Last-Modified" })
        {
            if (incoming.Headers.TryGetValues(name, out var values)
                || incoming.Content.Headers.TryGetValues(name, out values))
            {
                outgoing.Headers[name] = values.ToArray();
            }
        }

        outgoing.Headers.CacheControl = "private, no-store";
    }

    internal sealed record BufferedPlexResponse(int StatusCode, bool IsSuccessStatusCode, string? ContentType, byte[] Body);
}
