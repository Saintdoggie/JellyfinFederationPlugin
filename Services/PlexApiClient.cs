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
                return Array.Empty<PlexSection>();
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("MediaContainer", out var container)
                    || !container.TryGetProperty("Directory", out var dirs)
                    || dirs.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<PlexSection>();
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
                    break;
                }

                var pageCount = 0;
                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("MediaContainer", out var container)
                        || !container.TryGetProperty("Metadata", out var metadata)
                        || metadata.ValueKind != JsonValueKind.Array)
                    {
                        break;
                    }

                    foreach (var m in metadata.EnumerateArray())
                    {
                        pageCount++;
                        var ratingKey = GetString(m, "ratingKey");
                        var dto = ToDto(m);
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
        /// Builds the absolute, token-bearing URL for a part key. Internal use
        /// only - the token authenticates against the whole Plex server, so this
        /// URL must never be handed to a client (see
        /// <see cref="ServerKind.Plex"/>); it is only ever fetched server-side by
        /// <see cref="FederationStreamHandler"/>.
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
        private static BaseItemDto? ToDto(JsonElement m)
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
                Overview = GetString(m, "summary"),
                ProviderIds = ReadGuids(m),
                OfficialRating = GetString(m, "contentRating")
            };

            if (GetInt(m, "year") is int year)
            {
                dto.ProductionYear = year;
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
                    ApplyMediaDetails(m, dto);
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
                    ApplyMediaDetails(m, dto);
                    break;

                default:
                    return null;
            }

            return dto;
        }

        /// <summary>
        /// Copies container/codec/resolution details off the Plex media entry so
        /// Jellyfin's own client-compatibility check can certify direct play
        /// without probing the remote file first - the same reason
        /// <c>FederationMediaSourceProvider.FetchRemoteSourceAsync</c> carries
        /// them across for Jellyfin sources.
        /// </summary>
        private static void ApplyMediaDetails(JsonElement m, BaseItemDto dto)
        {
            if (!m.TryGetProperty("Media", out var media)
                || media.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var med in media.EnumerateArray())
            {
                dto.Container = GetString(med, "container");

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

                if (streams.Count > 0)
                {
                    dto.MediaStreams = streams.ToArray();
                }

                return;
            }
        }

        /// <summary>
        /// Reads Plex's external id list (<c>includeGuids=1</c>) into the same
        /// provider-id dictionary shape Jellyfin uses, which is what lets a movie
        /// present on both a Plex friend and a Jellyfin friend dedup into one
        /// federated item instead of appearing twice.
        /// </summary>
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
    }

    /// <summary>
    /// One Plex library section (Plex's equivalent of a Jellyfin library).
    /// </summary>
    public sealed record PlexSection(string Key, string Title, string Type);
}
