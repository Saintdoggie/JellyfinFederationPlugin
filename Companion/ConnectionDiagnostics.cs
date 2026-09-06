using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FederationCompanion;

public sealed record ConnectionCheck(string Stage, bool Success, string Message);

public static class ConnectionDiagnostics
{
    public static async Task<ConnectionCheck> ProbeAsync(HttpClient http, string baseUrl, string token, string section, string type, string stage, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            async Task<JsonDocument> Json(string path)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + path);
                request.Headers.Add("X-Plex-Token", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await http.SendAsync(request, deadline.Token);
                response.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            }
            using var catalog = await Json($"/library/sections/{Uri.EscapeDataString(section)}/all?type={(type == "show" ? 4 : 1)}&X-Plex-Container-Size=1");
            if (!catalog.RootElement.GetProperty("MediaContainer").TryGetProperty("Metadata", out var items) || items.GetArrayLength() == 0)
                return new(stage, false, "This shared library has no video to test. Choose a library containing a movie or episode.");
            var id = items[0].GetProperty("ratingKey").GetString();
            using var metadata = await Json("/library/metadata/" + Uri.EscapeDataString(id!));
            var part = metadata.RootElement.GetProperty("MediaContainer").GetProperty("Metadata")[0].GetProperty("Media")[0].GetProperty("Part")[0];
            var partKey = part.GetProperty("key").GetString();
            if (partKey == null || !partKey.StartsWith("/library/parts/", StringComparison.Ordinal))
                return new(stage, false, "Plex returned no readable video file. Analyze the source item in Plex.");
            using var probe = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + partKey);
            probe.Headers.Add("X-Plex-Token", token);
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var media = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            media.EnsureSuccessStatusCode();
            using var stream = await media.Content.ReadAsStreamAsync(deadline.Token);
            var count = await stream.ReadAsync(new byte[1], deadline.Token);
            var seeking = media.StatusCode == HttpStatusCode.PartialContent && media.Content.Headers.ContentRange?.From == 0;
            return new(stage, count == 1 && seeking, count != 1 ? "The media file returned no bytes. Check its disk is online."
                : seeking ? "Video bytes and byte-range seeking passed."
                : "Video is reachable, but byte-range seeking failed. Check the proxy forwards Range headers.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return new(stage, false, stage == "Public media path"
                ? "The Funnel media path failed. Check Funnel points to Companion's current port, then retry."
                : "Companion could not read a video from Plex. Check the local Plex address, token, and source file.");
        }
    }
}
