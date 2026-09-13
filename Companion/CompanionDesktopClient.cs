using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FederationCompanion;

/// <summary>Native desktop commands are confined to this process's loopback API.
/// No cookies, redirects, browser engine or upstream credentials are involved.</summary>
internal sealed class CompanionDesktopClient : IDisposable
{
    private readonly HttpClient _http;
    public CompanionDesktopClient(int port, string ownerKey, HttpMessageHandler? handler = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add("X-Companion-Admin", ownerKey);
    }

    public async Task<JsonNode> SendAsync(string path, object? body = null, HttpMethod? method = null, CancellationToken ct = default)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('#') || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Desktop commands must use a local API path.", nameof(path));
        using var request = new HttpRequestMessage(method ?? (body == null ? HttpMethod.Get : HttpMethod.Post), path);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        JsonNode? result;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            result = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException) { throw new InvalidOperationException("Companion returned an incomplete response. Please retry."); }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(result?["error"]?.GetValue<string>() ?? "Companion could not complete this action. Refresh status and retry.");
        return result ?? new JsonObject();
    }

    public void Dispose() => _http.Dispose();
}
