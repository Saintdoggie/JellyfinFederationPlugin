using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Talks to a Plex Media Server's own HTTP API and translates what it returns
    /// into the <see cref="BaseItemDto"/> shape the rest of this plugin already
    /// speaks, so Plex-sourced content flows through the existing sync, cache,
    /// materialization and stream-relay pipeline unchanged rather than needing a
    /// parallel one of its own.
    /// <para>
    /// Plex identifies items by an integer <c>ratingKey</c>, not a Guid, so every
    /// id is mapped through <see cref="RatingKeyToGuid"/> - a deterministic hash,
    /// so the same Plex item keeps the same federated identity across syncs (and
    /// therefore the same local Jellyfin item, watch state and all). The original
    /// ratingKey is kept on <see cref="FederatedItemMetadata.RemoteNativeId"/>
    /// because that mapping is one-way.
    /// </para>
    /// </summary>
    public class PlexApiClient
    {
        /// <summary>
        /// Plex's own numeric library type codes, used as the <c>type=</c> query
        /// parameter when listing a section's contents.
        /// </summary>
        private const int PlexTypeMovie = 1;
        private const int PlexTypeShow = 2;
        private const int PlexTypeEpisode = 4;

        // Plex caps how much it will return in one response regardless of what is
        // asked for; paging in explicit chunks keeps a large library from
        // silently truncating.
        private const int PageSize = 200;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _token;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlexApiClient"/> class.
        /// </summary>
        public PlexApiClient(string baseUrl, string token, HttpClient http, ILogger logger)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _token = token;
            _http = http;
            _logger = logger;
        }

        /// <summary>
        /// Maps a Plex <c>ratingKey</c> to a stable Guid. Deterministic (an MD5 of
        /// a namespaced form of the key, same technique
        /// <c>FederationMediaSourceProvider.BuildSourceId</c> already uses) so the
        /// same Plex item resolves to the same federated item on every sync
        /// instead of being torn down and recreated - which would lose watch
        /// state. MD5 is a non-security use here: it is an identity mapping, not
        /// an integrity or authentication check.
        /// </summary>
        public static Guid RatingKeyToGuid(string ratingKey)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes("plex-item:" + ratingKey));
            return new Guid(bytes);
        }

        /// <summary>
        /// Stable Guid for a synthesized season, which Plex does return as a real
        /// item but this client never fetches directly - the episode listing
        /// already carries everything a season entry needs (see
        /// <c>FederationSyncService.UpsertEpisodeSeason</c>), so seasons are
        /// derived from their episodes rather than paged for separately.
        /// </summary>
        public static Guid SeasonGuid(string showRatingKey, int seasonNumber)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"plex-season:{showRatingKey}:{seasonNumber}")));
            return new Guid(bytes);
        }

        /// <summary>
        /// Lists the server's library sections (Plex's equivalent of a Jellyfin
        /// library), so the admin can pick which ones to federate.
        /// </summary>
        public async Task<IReadOnlyList<PlexSection>> GetSectionsAsync(CancellationToken cancellationToken)
        {
            var doc = await GetJsonAsync("/library/sections", cancellationToken).ConfigureAwait(false);
            if (doc == null)
            {
                throw new InvalidOperationException("Plex libraries could not be read. Cached items have been kept.");
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("MediaContainer", out var container)
                    || !container.TryGetProperty("Directory", out var dirs)
                    || dirs.ValueKind != JsonValueKind.Array)
                {
                    if (container.ValueKind == JsonValueKind.Object && container.TryGetProperty("size", out var size) && size.TryGetInt32(out var count) && count == 0) return Array.Empty<PlexSection>();
                    throw new InvalidOperationException("Plex returned an invalid library list. Cached items have been kept.");
                }

                var sections = new List<PlexSection>();
                foreach (var d in dirs.EnumerateArray())
                {
                    var key = GetString(d, "key");
                    var title = GetString(d, "title");
                    var type = GetString(d, "type");
                    if (key != null && title != null && type != null)
                    {
                        sections.Add(new PlexSection(key, title, type));
                    }
                }

                return sections;
            }
        }

        /// <summary>
        /// Fetches every item in a section, already converted to
        /// <see cref="BaseItemDto"/>. A "show" section is fetched as its shows
        /// followed by its episodes (in that order, because
        /// <c>FederationSyncService.UpsertEpisodeSeason</c> skips any episode
        /// whose series isn't already in the cache), with seasons synthesized
        /// from the episodes themselves.
        /// </summary>
        public async Task<IReadOnlyList<ExternalItem>> GetSectionItemsAsync(PlexSection section, CancellationToken cancellationToken)
        {
            var results = new List<ExternalItem>();

            if (string.Equals(section.Type, "show", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(await GetTypedAsync(section.Key, PlexTypeShow, cancellationToken).ConfigureAwait(false));
                results.AddRange(await GetTypedAsync(section.Key, PlexTypeEpisode, cancellationToken).ConfigureAwait(false));
                return results;
            }

            results.AddRange(await GetTypedAsync(section.Key, PlexTypeMovie, cancellationToken).ConfigureAwait(false));
            return results;
        }

        private async Task<List<ExternalItem>> GetTypedAsync(string sectionKey, int plexType, CancellationToken cancellationToken)
        {
            var items = new List<ExternalItem>();
            var start = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = string.Create(
                    CultureInfo.InvariantCulture,
                    $"/library/sections/{sectionKey}/all?type={plexType}&includeGuids=1&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}");

                var doc = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
                if (doc == null)
                {
                    throw new InvalidOperationException("Plex catalog could not be read completely. Cached items have been kept.");
                }

                var pageCount = 0;
                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("MediaContainer", out var container))
                    {
                        throw new InvalidOperationException("Plex returned an invalid catalog. Cached items have been kept.");
                    }

                    if (!container.TryGetProperty("Metadata", out var metadata))
                    {
                        if (container.TryGetProperty("size", out var size) && size.TryGetInt32(out var count) && count == 0)
                        {
                            break;
                        }

                        throw new InvalidOperationException("Plex returned an incomplete catalog. Cached items have been kept.");
                    }

                    if (metadata.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException("Plex returned an invalid catalog. Cached items have been kept.");
                    }

                    foreach (var m in metadata.EnumerateArray())
                    {
                        pageCount++;
                        var ratingKey = GetString(m, "ratingKey");
                        var dto = await ToDtoAsync(m, cancellationToken).ConfigureAwait(false);
                        if (dto != null && ratingKey != null)
                        {
                            items.Add(new ExternalItem(dto, ratingKey));
                        }
                    }
                }

                if (pageCount < PageSize)
                {
                    break;
                }

                start += PageSize;
            }

            return items;
        }

        /// <summary>
        /// Resolves the current streamable file path for a Plex item, asked for
        /// at play time rather than cached: a Plex part id changes whenever that
        /// server re-scans or the file moves, and a stale one 404s. Returns null
        /// when the item is gone or has no playable part.
        /// </summary>
        public async Task<string?> GetPartKeyAsync(string ratingKey, CancellationToken cancellationToken)
        {
            var doc = await GetJsonAsync($"/library/metadata/{ratingKey}", cancellationToken).ConfigureAwait(false);
            if (doc == null)
            {
                return null;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("MediaContainer", out var container)
                    || !container.TryGetProperty("Metadata", out var metadata)
                    || metadata.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var m in metadata.EnumerateArray())
                {
                    var part = FirstPartKey(m);
                    if (part != null)
                    {
                        return part;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves an item's cover art (<c>thumb</c>) and backdrop (<c>art</c>)
        /// paths, asked for at request time rather than cached: like a part key,
        /// Plex's own path for these includes a timestamp that changes on every
        /// rescan. Returns null when the item is gone.
        /// </summary>
        public async Task<(string? Thumb, string? Art)?> GetImagePathsAsync(string ratingKey, CancellationToken cancellationToken)
        {
            var doc = await GetJsonAsync($"/library/metadata/{ratingKey}", cancellationToken).ConfigureAwait(false);
            if (doc == null)
            {
                return null;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("MediaContainer", out var container)
                    || !container.TryGetProperty("Metadata", out var metadata)
                    || metadata.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var m in metadata.EnumerateArray())
                {
                    // thumb is whatever Plex is actually showing - including a
                    // custom uploaded poster. Fall back to parent/show art for
                    // episodes/seasons that have no still of their own.
                    var thumb = FirstNonEmptyString(m, "thumb", "parentThumb", "grandparentThumb");
                    var art = FirstNonEmptyString(m, "art", "parentArt", "grandparentArt");
                    return (thumb, art);
                }
            }

            return null;
        }

        /// <summary>
        /// Opens the current Plex poster using the token as a request header, so
        /// the credential never becomes part of a URL passed into another
        /// component. The caller owns the returned response.
        /// </summary>
        public async Task<HttpResponseMessage?> GetPrimaryImageResponseAsync(string ratingKey, CancellationToken cancellationToken)
        {
            var paths = await GetImagePathsAsync(ratingKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(paths?.Thumb))
            {
                return null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + paths.Value.Thumb);
                request.Headers.TryAddWithoutValidation("X-Plex-Token", _token);
                var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                _logger.LogWarning(
                    "[Federation] Plex poster request for item {RatingKey} failed with {Status}",
                    ratingKey,
                    (int)response.StatusCode);
                response.Dispose();
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Plex poster request for item {RatingKey} failed", ratingKey);
                return null;
            }
        }

        /// <summary>
        /// Measures real download throughput from this Plex server by reading a few
        /// megabytes from the middle of one file it already shares (the caller picks a
        /// ratingKey from the consented catalog). Only an HTTP 206 is accepted: a 200
        /// would mean the server ignored the range and is sending the whole file, so the
        /// response is dropped unread. Returns null when the sample could not be taken.
        /// </summary>
        /// <param name="ratingKey">An item from the already-imported catalog.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Megabits per second, or null.</returns>
        public async Task<double?> MeasureBandwidthMbpsAsync(string ratingKey, CancellationToken cancellationToken)
        {
            const int sampleBytes = 5_000_000;
            var partKey = await GetPartKeyAsync(ratingKey, cancellationToken).ConfigureAwait(false);
            if (partKey == null)
            {
                return null;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // A little way in, so the sample is real media rather than a container header
            // the server may have cached; falls back to the start for a very small file.
            foreach (var offset in new long[] { 20_000_000, 0 })
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, BuildStreamUrl(partKey));
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + sampleBytes - 1);
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        continue;
                    }

                    if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    {
                        return null;
                    }

                    await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while (total < sampleBytes && (read = await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                    }

                    stopwatch.Stop();
                    return total == 0 || stopwatch.Elapsed.TotalSeconds <= 0
                        ? null
                        : total * 8.0 / stopwatch.Elapsed.TotalSeconds / 1_000_000.0;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug(ex, "[Federation] Plex bandwidth sample failed");
                    return null;
                }
            }

            return null;
        }

        /// <summary>What a short Plex transcode probe observed.</summary>
        /// <param name="HttpStatus">Plex's response status, or 0 when the request never completed.</param>
        /// <param name="FirstByteMs">Milliseconds until the first media byte arrived, or null.</param>
        /// <param name="Bytes">Media bytes received during the sample.</param>
        /// <param name="SampleSeconds">How long data was read for.</param>
        /// <param name="Mbps">Observed delivery rate in Mbps, or null when nothing arrived.</param>
        /// <param name="ContentType">The response content type.</param>
        /// <param name="Detail">A short human-readable outcome (never contains the token).</param>
        /// <param name="PlexDecision">What Plex's own transcode decision call said, if it answered.</param>
        /// <param name="PlexMessage">A short, token-free excerpt of Plex's reply when it refused.</param>
        public sealed record TranscodeProbeResult(int HttpStatus, long? FirstByteMs, long Bytes, double SampleSeconds, double? Mbps, string? ContentType, string Detail, string? PlexDecision = null, string? PlexMessage = null);

        /// <summary>
        /// Builds the token-bearing URL that asks Plex to transcode one item to a capped
        /// H.264/AAC Matroska stream over plain HTTP. Internal use only (see
        /// <see cref="BuildStreamUrl"/>): the URL carries the server token and must never
        /// reach a client.
        /// </summary>
        /// <param name="ratingKey">The Plex item.</param>
        /// <param name="maxVideoKbps">Video bitrate ceiling in kilobits per second.</param>
        /// <param name="session">A unique id so Plex can tell this stream from others and stop it.</param>
        /// <param name="offsetSeconds">Where in the title to start.</param>
        /// <returns>An absolute URL.</returns>
        public string BuildTranscodeUrl(string ratingKey, int maxVideoKbps, string session, int offsetSeconds = 0)
            => $"{_baseUrl}/video/:/transcode/universal/start.mkv?{BuildTranscodeQuery(ratingKey, maxVideoKbps, session, offsetSeconds)}";

        private string BuildTranscodeQuery(string ratingKey, int maxVideoKbps, string session, int offsetSeconds)
        {
            var q = new List<KeyValuePair<string, string>>
            {
                new("hasMDE", "1"),
                new("path", "/library/metadata/" + ratingKey),
                new("mediaIndex", "0"),
                new("partIndex", "0"),
                new("protocol", "http"),
                new("offset", offsetSeconds.ToString(CultureInfo.InvariantCulture)),
                new("fastSeek", "1"),
                new("directPlay", "0"),
                new("directStream", "0"),
                new("videoQuality", "100"),
                new("maxVideoBitrate", maxVideoKbps.ToString(CultureInfo.InvariantCulture)),
                new("videoBitrate", maxVideoKbps.ToString(CultureInfo.InvariantCulture)),
                new("audioChannelCount", "2"),
                new("session", session),
                new("X-Plex-Session-Identifier", session),
                new("X-Plex-Client-Identifier", "jellyfin-federation"),
                new("X-Plex-Product", "Jellyfin Federation"),
                new("X-Plex-Platform", "Chrome"),
                new("X-Plex-Device", "Jellyfin Federation"),
                new("X-Plex-Token", _token),
            };
            return string.Join("&", q.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }

        // Plex's own "would you transcode this, and if not why" call. Returns a short,
        // token-free summary, or null when it did not answer usefully.
        private async Task<string?> AskTranscodeDecisionAsync(string ratingKey, int maxVideoKbps, string session, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{_baseUrl}/video/:/transcode/universal/decision?{BuildTranscodeQuery(ratingKey, maxVideoKbps, session, 0)}");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var parts = new List<string> { "HTTP " + (int)response.StatusCode };
                foreach (var name in new[] { "generalDecisionCode", "generalDecisionText", "directPlayDecisionText", "mdeDecisionText", "transcodeDecisionCode", "transcodeDecisionText" })
                {
                    var match = System.Text.RegularExpressions.Regex.Match(body, name + "=\"([^\"]{0,160})\"");
                    if (match.Success)
                    {
                        parts.Add(name + "=" + match.Groups[1].Value);
                    }
                }

                return parts.Count > 1 ? Redact(string.Join("; ", parts)) : "HTTP " + (int)response.StatusCode + "; " + Excerpt(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return null;
            }
        }

        // A short plain-text excerpt of a Plex reply, with the token removed.
        private string Excerpt(string body)
        {
            var flat = System.Text.RegularExpressions.Regex.Replace(body ?? string.Empty, "\\s+", " ").Trim();
            return Redact(flat.Length <= 240 ? flat : flat[..240]);
        }

        private string Redact(string text)
            => string.IsNullOrEmpty(_token) ? text : text.Replace(_token, "<token>", StringComparison.Ordinal);

        /// <summary>
        /// Asks Plex to transcode an item at a capped bitrate for a few seconds and reports
        /// whether it can, how quickly the first byte arrives and how fast data flows.
        /// Diagnostic only; the stream is stopped before returning.
        /// </summary>
        /// <param name="ratingKey">The Plex item.</param>
        /// <param name="maxVideoKbps">Video bitrate ceiling in kilobits per second.</param>
        /// <param name="sampleSeconds">How long to read before stopping.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>What was observed.</returns>
        public async Task<TranscodeProbeResult> ProbeTranscodeAsync(string ratingKey, int maxVideoKbps, int sampleSeconds, CancellationToken cancellationToken)
        {
            var session = Guid.NewGuid().ToString("N");
            var decision = await AskTranscodeDecisionAsync(ratingKey, maxVideoKbps, session, cancellationToken).ConfigureAwait(false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long bytes = 0;
            long? firstByteMs = null;
            string? contentType = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildTranscodeUrl(ratingKey, maxVideoKbps, session));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                contentType = response.Content.Headers.ContentType?.ToString();
                if (!response.IsSuccessStatusCode)
                {
                    var refusal = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return new TranscodeProbeResult((int)response.StatusCode, null, 0, 0, null, contentType, "Plex refused to start a transcode.", decision, Excerpt(refusal));
                }

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[65536];
                using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                window.CancelAfter(TimeSpan.FromSeconds(sampleSeconds + 25));
                var readUntil = TimeSpan.Zero;
                while (true)
                {
                    var read = await body.ReadAsync(buffer, window.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (firstByteMs == null)
                    {
                        firstByteMs = watch.ElapsedMilliseconds;
                        readUntil = watch.Elapsed + TimeSpan.FromSeconds(sampleSeconds);
                    }

                    bytes += read;
                    if (watch.Elapsed >= readUntil)
                    {
                        break;
                    }
                }

                var seconds = firstByteMs == null ? 0 : Math.Max(0.001, (watch.ElapsedMilliseconds - firstByteMs.Value) / 1000.0);
                return new TranscodeProbeResult(
                    (int)response.StatusCode,
                    firstByteMs,
                    bytes,
                    seconds,
                    seconds > 0 && bytes > 0 ? bytes * 8.0 / seconds / 1_000_000.0 : null,
                    contentType,
                    firstByteMs == null ? "Plex accepted the request but sent no media." : "Plex produced a transcoded stream.",
                    decision);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new TranscodeProbeResult(0, firstByteMs, bytes, 0, null, contentType, "Timed out waiting for Plex to produce data.");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "[Federation] Plex transcode probe failed");
                return new TranscodeProbeResult(0, firstByteMs, bytes, 0, null, contentType, "Could not reach Plex's transcoder.");
            }
            finally
            {
                await StopTranscodeAsync(session).ConfigureAwait(false);
            }
        }

        // Politely ends the Plex transcode session so it stops using the friend's CPU.
        private async Task StopTranscodeAsync(string session)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{_baseUrl}/video/:/transcode/universal/stop?session={Uri.EscapeDataString(session)}");
                request.Headers.TryAddWithoutValidation("X-Plex-Token", _token);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not stop Plex transcode session");
            }
        }

        /// <summary>
        /// Builds the absolute, token-bearing URL for a part or image path.
        /// Internal use only - the token authenticates against the whole Plex
        /// server, so this URL must never be handed to a client (see
        /// <see cref="ServerKind.Plex"/>); it is only ever fetched server-side, by
        /// <see cref="FederationStreamHandler"/> for playback or by Jellyfin's own
        /// image-caching pipeline (<c>IRemoteImageProvider.GetImageResponse</c>)
        /// for cover art.
        /// </summary>
        public string BuildStreamUrl(string partKey)
        {
            var separator = partKey.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return $"{_baseUrl}{partKey}{separator}X-Plex-Token={Uri.EscapeDataString(_token)}";
        }

        /// <summary>
        /// Verifies the server is reachable and the token works, returning its
        /// reported friendly name (or null when it isn't usable).
        /// </summary>
        public async Task<string?> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var doc = await GetJsonAsync("/", cancellationToken).ConfigureAwait(false);
            if (doc == null)
            {
                return null;
            }

            using (doc)
            {
                return doc.RootElement.TryGetProperty("MediaContainer", out var container)
                    ? GetString(container, "friendlyName") ?? "Plex Media Server"
                    : null;
            }
        }

        private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
                request.Headers.TryAddWithoutValidation("X-Plex-Token", _token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[Federation] Plex request to {Path} failed with {Status}",
                        path,
                        (int)response.StatusCode);
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonDocument.Parse(body);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Plex request to {Path} failed", path);
                return null;
            }
        }

        /// <summary>
        /// Converts one Plex metadata entry into a <see cref="BaseItemDto"/>.
        /// Returns null for anything without the fields the sync pipeline needs
        /// (a ratingKey and a title), or of a type this plugin doesn't federate.
        /// </summary>
        private async Task<BaseItemDto?> ToDtoAsync(JsonElement m, CancellationToken cancellationToken)
        {
            var ratingKey = GetString(m, "ratingKey");
            var title = GetString(m, "title");
            var plexType = GetString(m, "type");
            if (ratingKey == null || title == null || plexType == null)
            {
                return null;
            }

            var dto = new BaseItemDto
            {
                Id = RatingKeyToGuid(ratingKey),
                Name = title,
                OriginalTitle = GetString(m, "originalTitle"),
                Overview = GetString(m, "summary"),
                ProviderIds = ReadGuids(m),
                OfficialRating = GetString(m, "contentRating"),
                Genres = ReadTagArray(m, "Genre"),
                People = ReadPeople(m)
            };

            // Keep Plex's current poster identity in the catalog snapshot. The
            // path includes Plex's image timestamp, so it changes when the owner
            // selects or uploads a different poster. Reconciliation uses that
            // tag to fetch the exact Plex image once and to replace it only when
            // Plex reports a newer selection.
            var thumb = FirstNonEmptyString(m, "thumb", "parentThumb", "grandparentThumb");
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                dto.ImageTags = new Dictionary<ImageType, string>
                {
                    [ImageType.Primary] = thumb
                };
            }

            var studio = GetString(m, "studio");
            if (!string.IsNullOrWhiteSpace(studio))
            {
                dto.Studios = new[] { new NameGuidPair { Name = studio } };
            }

            var premiere = GetString(m, "originallyAvailableAt");
            if (!string.IsNullOrWhiteSpace(premiere) && DateTime.TryParse(premiere, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var premiereDate))
            {
                dto.PremiereDate = premiereDate.ToUniversalTime();
            }

            if (GetInt(m, "year") is int year)
            {
                dto.ProductionYear = year;
            }

            if (GetLong(m, "addedAt") is long addedAt && addedAt > 0)
            {
                dto.DateCreated = DateTimeOffset.FromUnixTimeSeconds(addedAt).UtcDateTime;
            }

            // Plex reports durations in milliseconds; Jellyfin counts ticks.
            if (GetLong(m, "duration") is long durationMs && durationMs > 0)
            {
                dto.RunTimeTicks = durationMs * TimeSpan.TicksPerMillisecond;
            }

            if (GetDouble(m, "rating") is double rating)
            {
                dto.CommunityRating = (float)rating;
            }

            switch (plexType.ToLowerInvariant())
            {
                case "movie":
                    dto.Type = Jellyfin.Data.Enums.BaseItemKind.Movie;
                    await ApplyMediaDetailsAsync(ratingKey, m, dto, cancellationToken).ConfigureAwait(false);
                    break;

                case "show":
                    dto.Type = Jellyfin.Data.Enums.BaseItemKind.Series;
                    break;

                case "episode":
                    var showRatingKey = GetString(m, "grandparentRatingKey");
                    var seasonNumber = GetInt(m, "parentIndex");
                    if (showRatingKey == null || seasonNumber == null)
                    {
                        // Without its series and season an episode can only be
                        // orphaned, which the sync pipeline would reject anyway.
                        return null;
                    }

                    dto.Type = Jellyfin.Data.Enums.BaseItemKind.Episode;
                    dto.SeriesName = GetString(m, "grandparentTitle");
                    dto.SeriesId = RatingKeyToGuid(showRatingKey);
                    dto.SeasonId = SeasonGuid(showRatingKey, seasonNumber.Value);
                    dto.ParentIndexNumber = seasonNumber;
                    dto.IndexNumber = GetInt(m, "index");
                    await ApplyMediaDetailsAsync(ratingKey, m, dto, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    return null;
            }

            return dto;
        }

        /// <summary>
        /// Copies container/codec/resolution/HDR details off the Plex item so
        /// Jellyfin's own client-compatibility check can certify direct play
        /// without probing the remote file first - the same reason
        /// <c>FederationMediaSourceProvider.FetchRemoteSourceAsync</c> carries
        /// them across for Jellyfin sources. Fetches the item's own detail
        /// endpoint for a real per-stream breakdown (HDR/Dolby Vision color
        /// data and every audio track), because the bulk section-listing
        /// endpoint this method's caller already used to fetch <paramref
        /// name="m"/> only ever reports one summarized codec/channel pair for
        /// the whole item, never real color/HDR data at all - confirmed
        /// against a real 4K Dolby Vision/HDR10 remux, which the summary
        /// endpoint doesn't distinguish from an ordinary SDR file.
        /// </summary>
        private async Task ApplyMediaDetailsAsync(string ratingKey, JsonElement m, BaseItemDto dto, CancellationToken cancellationToken)
        {
            var summary = ReadMediaSource(m);
            var detailed = await GetMediaSourceAsync(ratingKey, cancellationToken).ConfigureAwait(false);
            var source = detailed ?? summary;
            if (source == null) return;

            // Older PMS responses sometimes omit container on the detail endpoint.
            // Only borrow the listing's container when it identifies the same part.
            if (detailed != null && summary != null && detailed.Id == summary.Id)
            {
                source.Container ??= summary.Container;
            }

            dto.Container = source.Container;
            dto.MediaStreams = source.MediaStreams.ToArray();
            dto.MediaSources = new[] { source };
            if (source.RunTimeTicks is > 0) dto.RunTimeTicks = source.RunTimeTicks;
        }

        /// <summary>
        /// Fallback used when the detail endpoint couldn't be reached or
        /// didn't return a per-stream breakdown: the coarse video/audio
        /// summary already present on the section-listing entry every caller
        /// already has - no color/HDR data, but still enough for direct-play
        /// container/codec/channel compatibility checks.
        /// </summary>
        private static List<MediaStream> BuildSummaryStreams(JsonElement med)
        {
            // Plex reports one combined bitrate for the whole Media entry, in
            // kbps, not split per stream. Attributed to the video stream since
            // it normally accounts for the large majority of it.
            var bitrateKbps = GetInt(med, "bitrate");

            var streams = new List<MediaStream>();
            var videoCodec = GetString(med, "videoCodec");
            if (videoCodec != null)
            {
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Codec = videoCodec,
                    Width = GetInt(med, "width"),
                    Height = GetInt(med, "height"),
                    BitRate = bitrateKbps.HasValue ? bitrateKbps.Value * 1000 : null,
                    Index = 0,
                    IsDefault = true
                });
            }

            var audioCodec = GetString(med, "audioCodec");
            if (audioCodec != null)
            {
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Codec = audioCodec,
                    Channels = GetInt(med, "audioChannels"),
                    Index = streams.Count,
                    IsDefault = true
                });
            }

            return streams;
        }

        /// <summary>
        /// Reads the current first playable part's size, container, runtime,
        /// bitrate and stream details together. Returns null when the item or
        /// its media information could not be read, allowing catalog sync to
        /// fall back to the section listing.
        /// </summary>
        public async Task<MediaSourceInfo?> GetMediaSourceAsync(string ratingKey, CancellationToken cancellationToken)
        {
            using var doc = await GetJsonAsync($"/library/metadata/{ratingKey}", cancellationToken).ConfigureAwait(false);
            if (doc == null || !doc.RootElement.TryGetProperty("MediaContainer", out var container)
                || !container.TryGetProperty("Metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in metadata.EnumerateArray())
            {
                if (GetString(item, "ratingKey") == ratingKey) return ReadMediaSource(item);
            }
            return null;
        }

        private static MediaSourceInfo? ReadMediaSource(JsonElement item)
        {
            if (!item.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Array) return null;
            var hasPlayablePart = media.EnumerateArray().Any(m => m.TryGetProperty("Part", out var parts)
                && parts.ValueKind == JsonValueKind.Array
                && parts.EnumerateArray().Any(p => !string.IsNullOrWhiteSpace(GetString(p, "key"))));
            foreach (var med in media.EnumerateArray())
            {
                JsonElement? chosenPart = null;
                if (med.TryGetProperty("Part", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (!string.IsNullOrWhiteSpace(GetString(part, "key")))
                        {
                            chosenPart = part;
                            break;
                        }
                    }
                }

                // Match FirstPartKey: a media version without a usable part cannot
                // describe a later version's bytes. Summary-only entries remain a
                // fallback for old peers, but never override a playable version.
                if (chosenPart == null && (hasPlayablePart || med.TryGetProperty("Part", out _))) continue;
                var streams = new List<MediaStream>();
                if (chosenPart is JsonElement selected && selected.TryGetProperty("Stream", out var streamJson)
                    && streamJson.ValueKind == JsonValueKind.Array)
                {
                    var ordinal = 0;
                    foreach (var stream in streamJson.EnumerateArray())
                    {
                        // Plex's id identifies a database row; index identifies the
                        // stream inside the file. Keep gaps made by subtitle/data tracks.
                        var index = GetInt(stream, "index") ?? ordinal;
                        ordinal++;
                        switch (GetInt(stream, "streamType"))
                        {
                            case 1: streams.Add(BuildVideoStream(stream, index)); break;
                            case 2: streams.Add(BuildAudioStream(stream, index)); break;
                            case 3:
                                // Sidecar subtitles require a separate authenticated
                                // relay; don't claim they are embedded in the media.
                                if (GetString(stream, "key") != null) break;
                                streams.Add(new MediaStream
                                {
                                    Type = MediaStreamType.Subtitle, Index = index,
                                    Codec = GetString(stream, "codec") == "srt" ? "subrip" : GetString(stream, "codec"),
                                    Language = GetString(stream, "languageTag") ?? GetString(stream, "languageCode"),
                                    Title = GetString(stream, "title"),
                                    IsDefault = GetBool(stream, "default") == true,
                                    IsForced = GetBool(stream, "forced") == true
                                });
                                break;
                        }
                    }
                }
                if (streams.Count == 0) streams = BuildSummaryStreams(med);
                if (!streams.Any(s => s.Type == MediaStreamType.Audio && s.IsDefault))
                {
                    var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
                    if (audio != null) audio.IsDefault = true;
                }
                var partKey = chosenPart is JsonElement p ? GetString(p, "key") : null;
                var size = chosenPart is JsonElement sizePart ? GetLong(sizePart, "size") : null;
                var duration = chosenPart is JsonElement durationPart ? GetLong(durationPart, "duration") : null;
                duration ??= GetLong(med, "duration") ?? GetLong(item, "duration");
                return new MediaSourceInfo
                {
                    Id = partKey ?? GetString(med, "id"),
                    Container = chosenPart is JsonElement containerPart
                        ? FirstNonEmptyString(containerPart, "container") ?? FirstNonEmptyString(med, "container")
                        : FirstNonEmptyString(med, "container"),
                    Size = size is > 0 ? size : null,
                    Bitrate = GetInt(med, "bitrate") is > 0 and var kbps ? (int)Math.Min((long)kbps * 1000, int.MaxValue) : null,
                    RunTimeTicks = duration is > 0 && duration <= long.MaxValue / TimeSpan.TicksPerMillisecond
                        ? duration * TimeSpan.TicksPerMillisecond : null,
                    MediaStreams = streams
                };
            }
            return null;
        }

        /// <summary>
        /// Builds the video <see cref="MediaStream"/> from a Plex Stream
        /// element, including real HDR/Dolby Vision data - the whole reason
        /// this plugin fetches per-item detail instead of using the bulk
        /// section-listing summary, which only ever reports a bare codec name
        /// with no color/HDR information at all. Only the raw fields
        /// (ColorTransfer, the Dv* Dolby Vision fields) are set here -
        /// <see cref="MediaStream.VideoRange"/>/<see cref="MediaStream.VideoRangeType"/>
        /// are read-only, computed by Jellyfin itself from exactly these
        /// fields, confirmed by direct inspection (e.g. ColorTransfer=
        /// "smpte2084" alone already resolves to VideoRangeType.HDR10; adding
        /// DvProfile resolves to DOVIWithHDR10).
        /// </summary>
        private static MediaStream BuildVideoStream(JsonElement s, int index)
        {
            return new MediaStream
            {
                Type = MediaStreamType.Video,
                Codec = GetString(s, "codec") ?? string.Empty,
                Width = GetInt(s, "width"),
                Height = GetInt(s, "height"),
                BitRate = GetInt(s, "bitrate") is int kbps ? kbps * 1000 : null,
                BitDepth = GetInt(s, "bitDepth"),
                Profile = GetString(s, "profile"),
                Level = GetInt(s, "level"),
                RefFrames = GetInt(s, "refFrames"),
                ColorPrimaries = GetString(s, "colorPrimaries"),
                ColorSpace = GetString(s, "colorSpace"),
                ColorTransfer = GetString(s, "colorTrc"),
                ColorRange = GetString(s, "colorRange"),
                DvProfile = GetInt(s, "DOVIProfile"),
                DvLevel = GetInt(s, "DOVILevel"),
                DvBlSignalCompatibilityId = GetInt(s, "DOVIBLCompatID"),
                BlPresentFlag = BoolToFlag(GetBool(s, "DOVIBLPresent")),
                RpuPresentFlag = BoolToFlag(GetBool(s, "DOVIRPUPresent")),
                ElPresentFlag = BoolToFlag(GetBool(s, "DOVIELPresent")),
                Index = index,
                IsDefault = true
            };
        }

        /// <summary>
        /// Builds one audio <see cref="MediaStream"/> from a Plex Stream
        /// element. Unlike the bulk section-listing summary (one codec/
        /// channel pair for the whole item), the detail endpoint's Part.Stream
        /// array lists every audio track, so a file with several dubs/mixes -
        /// e.g. a 5.1 default plus a stereo commentary track - is no longer
        /// reduced to just one of them.
        /// </summary>
        private static MediaStream BuildAudioStream(JsonElement s, int index)
        {
            return new MediaStream
            {
                Type = MediaStreamType.Audio,
                Codec = GetString(s, "codec") ?? string.Empty,
                Channels = GetInt(s, "channels"),
                BitRate = GetInt(s, "bitrate") is int kbps ? kbps * 1000 : null,
                SampleRate = GetInt(s, "samplingRate"),
                BitDepth = GetInt(s, "bitDepth"),
                Profile = GetString(s, "profile"),
                Language = GetString(s, "languageTag") ?? GetString(s, "languageCode"),
                Title = GetString(s, "title"),
                ChannelLayout = GetString(s, "audioChannelLayout"),
                Index = index,

                // "selected" is this Plex server's own current pick for the
                // track to play by default; "default" is the file's own
                // embedded flag. Either is a reasonable signal - prefer
                // "selected" since it reflects what actually plays there today.
                IsDefault = GetBool(s, "selected") == true || GetBool(s, "default") == true
            };
        }

        /// <summary>
        /// Converts a Plex boolean flag to the 1/0 int Jellyfin's Dolby Vision
        /// presence fields use, or null when Plex didn't report it at all -
        /// distinct from "reported false", since Plex only includes these
        /// fields on the file's actual base/enhancement/RPU layers.
        /// </summary>
        private static int? BoolToFlag(bool? value) => value.HasValue ? (value.Value ? 1 : 0) : null;

        /// <summary>
        /// Reads Plex's external id list (<c>includeGuids=1</c>) into the same
        /// provider-id dictionary shape Jellyfin uses, which is what lets a movie
        /// present on both a Plex friend and a Jellyfin friend dedup into one
        /// federated item instead of appearing twice.
        /// </summary>
        private static string[] ReadTagArray(JsonElement m, string property)
        {
            if (!m.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return arr.EnumerateArray()
                .Select(e => GetString(e, "tag"))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToArray();
        }

        private static BaseItemPerson[] ReadPeople(JsonElement m)
        {
            var people = new List<BaseItemPerson>();
            AddPeople(m, "Role", PersonKind.Actor, people);
            AddPeople(m, "Director", PersonKind.Director, people);
            AddPeople(m, "Writer", PersonKind.Writer, people);
            AddPeople(m, "Producer", PersonKind.Producer, people);
            return people.ToArray();
        }

        private static void AddPeople(JsonElement m, string property, PersonKind kind, List<BaseItemPerson> people)
        {
            if (!m.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var e in arr.EnumerateArray())
            {
                var name = GetString(e, "tag");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                people.Add(new BaseItemPerson
                {
                    Name = name,
                    Role = GetString(e, "role"),
                    Type = kind
                });
            }
        }

        private static Dictionary<string, string> ReadGuids(JsonElement m)
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!m.TryGetProperty("Guid", out var guids) || guids.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (var g in guids.EnumerateArray())
            {
                var id = GetString(g, "id");
                if (id == null)
                {
                    continue;
                }

                // Shaped like "imdb://tt0298203" / "tmdb://65" / "tvdb://1366".
                var sep = id.IndexOf("://", StringComparison.Ordinal);
                if (sep <= 0)
                {
                    continue;
                }

                var provider = id.Substring(0, sep);
                var value = id.Substring(sep + 3);
                if (!string.IsNullOrEmpty(value))
                {
                    ids[provider] = value;
                }
            }

            return ids;
        }

        private static string? FirstPartKey(JsonElement m)
        {
            if (!m.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var med in media.EnumerateArray())
            {
                if (!med.TryGetProperty("Part", out var parts) || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var p in parts.EnumerateArray())
                {
                    var key = GetString(p, "key");
                    if (!string.IsNullOrEmpty(key))
                    {
                        return key;
                    }
                }
            }

            return null;
        }

        private static string? FirstNonEmptyString(JsonElement item, params string[] fields)
        {
            foreach (var field in fields)
            {
                var value = GetString(item, field);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static string? GetString(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
            {
                return null;
            }

            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null
            };
        }

        private static int? GetInt(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
            {
                return null;
            }

            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var i) => i,
                JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
                _ => null
            };
        }

        private static long? GetLong(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
            {
                return null;
            }

            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var i) => i,
                JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
                _ => null
            };
        }

        private static double? GetDouble(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
            {
                return null;
            }

            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetDouble(out var d) => d,
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => s,
                _ => null
            };
        }

        private static bool? GetBool(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
            {
                return null;
            }

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
                _ => null
            };
        }
    }

    /// <summary>
    /// One Plex library section (Plex's equivalent of a Jellyfin library).
    /// </summary>
    public sealed record PlexSection(string Key, string Title, string Type);
}
