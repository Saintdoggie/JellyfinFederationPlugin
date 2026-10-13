using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Lets the owner of a Plex server use it as the source of artwork and
    /// descriptions for their own Jellyfin libraries: plex.tv sign-in by PIN,
    /// server discovery, and matching a local Jellyfin item to its Plex twin.
    /// Matching prefers external ids (tmdb/imdb/tvdb) and falls back to
    /// title + year, so it works for any library regardless of folder layout.
    /// </summary>
    public class PlexMetadataSourceService
    {
        private const string Product = "Jellyfin Federation";
        private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(30);
        private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private readonly ILogger<PlexMetadataSourceService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly SemaphoreSlim _indexLock = new(1, 1);
        private Index? _index;
        private string _indexFingerprint = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlexMetadataSourceService"/> class.
        /// </summary>
        public PlexMetadataSourceService(
            ILogger<PlexMetadataSourceService> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        /// <summary>Test seam: replaces the HTTP client used for Plex server calls.</summary>
        internal HttpClient? HttpClientOverride { get; set; }

        /// <summary>A Plex server the signed-in account can reach.</summary>
        public sealed record PlexServerChoice(string Name, string MachineId, bool Owned, string Token, IReadOnlyList<PlexConnectionChoice> Connections);

        /// <summary>One address a Plex server is reachable at.</summary>
        public sealed record PlexConnectionChoice(string Uri, bool Local, bool Relay);

        /// <summary>The Plex item a local item matched.</summary>
        public sealed record PlexMatch(string RatingKey, BaseItemDto Dto, PlexImageKind ImageKind = PlexImageKind.Item);

        /// <summary>Which Plex image a match's artwork comes from.</summary>
        public enum PlexImageKind
        {
            /// <summary>The item's own poster and backdrop.</summary>
            Item,

            /// <summary>A season: its poster is the parent poster of one of its episodes.</summary>
            Season,
        }

        private sealed record Index(
            DateTime BuiltUtc,
            Dictionary<string, PlexMatch> ById,
            Dictionary<string, PlexMatch> ByTitleYear,
            Dictionary<(Guid Series, int Season, int Episode), PlexMatch> Episodes,
            Dictionary<(Guid Series, int Season), PlexMatch> Seasons);

        /// <summary>Test seam: replaces the plugin configuration as the source of settings.</summary>
        internal Func<PlexMetadataSourceSettings>? SettingsOverride { get; set; }

        private PlexMetadataSourceSettings Settings
            => SettingsOverride?.Invoke() ?? Plugin.Instance?.Configuration.PlexMetadataSource ?? new PlexMetadataSourceSettings();

        /// <summary>A stable client id so plex.tv lists one "device" for this server instead of one per sign-in.</summary>
        private static string ClientId()
        {
            var id = Plugin.Instance?.Configuration.LocalFederationId;
            return string.IsNullOrWhiteSpace(id) ? "jellyfin-federation" : "jellyfin-federation-" + id;
        }

        private static HttpRequestMessage PlexTv(HttpMethod method, string url, string? token = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientId());
            request.Headers.TryAddWithoutValidation("X-Plex-Product", Product);
            request.Headers.TryAddWithoutValidation("X-Plex-Version", "1");
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            }

            return request;
        }

        /// <summary>Starts plex.tv sign-in. The admin opens <c>AuthUrl</c>, approves, and the page polls <see cref="PollSignInAsync"/>.</summary>
        public async Task<(long PinId, string AuthUrl)?> StartSignInAsync(CancellationToken cancellationToken)
        {
            using var request = PlexTv(HttpMethod.Post, "https://plex.tv/api/v2/pins?strong=true");
            using var response = await SharedHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Federation] plex.tv refused a sign-in PIN request: HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var id = doc.RootElement.GetProperty("id").GetInt64();
            var code = doc.RootElement.GetProperty("code").GetString() ?? string.Empty;
            var authUrl = "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(ClientId())
                + "&code=" + Uri.EscapeDataString(code)
                + "&context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(Product);
            return (id, authUrl);
        }

        /// <summary>Returns the Plex account token once the admin has approved the PIN, otherwise null.</summary>
        public async Task<string?> PollSignInAsync(long pinId, CancellationToken cancellationToken)
        {
            using var request = PlexTv(HttpMethod.Get, "https://plex.tv/api/v2/pins/" + pinId.ToString(CultureInfo.InvariantCulture));
            using var response = await SharedHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("authToken", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(t.GetString())
                ? t.GetString()
                : null;
        }

        /// <summary>Lists the Plex Media Servers the account owns or has been shared, with every address plex.tv knows for each.</summary>
        public async Task<IReadOnlyList<PlexServerChoice>> ListServersAsync(string accountToken, CancellationToken cancellationToken)
        {
            using var request = PlexTv(HttpMethod.Get, "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1", accountToken);
            using var response = await SharedHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseServers(doc.RootElement, accountToken);
        }

        internal static IReadOnlyList<PlexServerChoice> ParseServers(JsonElement resources, string accountToken)
        {
            var servers = new List<PlexServerChoice>();
            if (resources.ValueKind != JsonValueKind.Array)
            {
                return servers;
            }

            foreach (var r in resources.EnumerateArray())
            {
                var provides = Str(r, "provides") ?? string.Empty;
                if (!provides.Split(',').Contains("server"))
                {
                    continue;
                }

                var connections = new List<PlexConnectionChoice>();
                if (r.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in conns.EnumerateArray())
                    {
                        var uri = Str(c, "uri");
                        if (!string.IsNullOrEmpty(uri))
                        {
                            connections.Add(new PlexConnectionChoice(uri!, Bool(c, "local"), Bool(c, "relay")));
                        }
                    }
                }

                // Reachability order: same-network first, then direct remote, relay last.
                var ordered = connections.OrderBy(c => c.Relay).ThenByDescending(c => c.Local).ToList();
                servers.Add(new PlexServerChoice(
                    Str(r, "name") ?? "Plex Media Server",
                    Str(r, "clientIdentifier") ?? string.Empty,
                    Bool(r, "owned"),
                    Str(r, "accessToken") ?? accountToken,
                    ordered));
            }

            return servers.OrderByDescending(s => s.Owned).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Picks the first address of <paramref name="server"/> that answers, preferring same-network ones.</summary>
        public async Task<(string Url, string Name)?> FindReachableAddressAsync(PlexServerChoice server, CancellationToken cancellationToken)
        {
            foreach (var connection in server.Connections)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                try
                {
                    var name = await CreateClient(connection.Uri, server.Token).TestConnectionAsync(timeout.Token).ConfigureAwait(false);
                    if (name != null)
                    {
                        return (connection.Uri, name);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // That address timed out; try the next.
                }
                catch (HttpRequestException)
                {
                }
            }

            return null;
        }

        /// <summary>Tests an address + token pair (manual setup path). Returns the Plex server's name, or null.</summary>
        public Task<string?> TestAsync(string url, string token, CancellationToken cancellationToken)
            => CreateClient(url, token).TestConnectionAsync(cancellationToken);

        /// <summary>Lists the movie and show sections of a Plex server.</summary>
        public async Task<IReadOnlyList<PlexSection>> ListSectionsAsync(string url, string token, CancellationToken cancellationToken)
        {
            var all = await CreateClient(url, token).GetSectionsAsync(cancellationToken).ConfigureAwait(false);
            return all.Where(s => IsSupportedSection(s.Type)).ToList();
        }

        private static bool IsSupportedSection(string type)
            => string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "show", StringComparison.OrdinalIgnoreCase);

        internal PlexApiClient CreateClient(string url, string token)
            => new(url, token, HttpClientOverride ?? SharedHttpClient, _logger);

        /// <summary>
        /// Whether Plex should be consulted for <paramref name="item"/>: the source is enabled, the item is a
        /// movie or series, is not one of this plugin's own federated items, and sits in a selected library.
        /// </summary>
        public bool AppliesTo(BaseItem item)
        {
            var settings = Settings;
            if (!settings.Enabled || string.IsNullOrEmpty(settings.ServerUrl) || string.IsNullOrEmpty(settings.Token))
            {
                return false;
            }

            if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode) || FederationLibraryManager.GetFederationKey(item) != null)
            {
                return false;
            }

            if (settings.JellyfinLibraryIds.Count == 0)
            {
                return true;
            }

            var folders = _libraryManager.GetCollectionFolders(item);
            return folders.Any(f => settings.JellyfinLibraryIds.Contains(f.Id.ToString("N"), StringComparer.OrdinalIgnoreCase)
                || settings.JellyfinLibraryIds.Contains(f.Id.ToString(), StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Finds the Plex item matching a local item, or null.</summary>
        public async Task<PlexMatch?> FindMatchAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            if (index == null)
            {
                return null;
            }

            switch (item)
            {
                case MediaBrowser.Controller.Entities.TV.Episode episode:
                    return LookupEpisode(index, episode.Series?.ProviderIds, episode.Series?.Name, episode.Series?.ProductionYear, episode.ParentIndexNumber, episode.IndexNumber);
                case MediaBrowser.Controller.Entities.TV.Season season:
                    return LookupSeason(index, season.Series?.ProviderIds, season.Series?.Name, season.Series?.ProductionYear, season.IndexNumber);
                default:
                    return Lookup(index, item.ProviderIds, item.Name, item.ProductionYear);
            }
        }

        /// <summary>Finds the Plex episode for a series identity plus season and episode number.</summary>
        public async Task<PlexMatch?> FindEpisodeAsync(IReadOnlyDictionary<string, string>? seriesIds, string? seriesName, int? seriesYear, int? season, int? episode, CancellationToken cancellationToken)
        {
            var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            return index == null ? null : LookupEpisode(index, seriesIds, seriesName, seriesYear, season, episode);
        }

        /// <summary>Finds the Plex season for a series identity plus season number.</summary>
        public async Task<PlexMatch?> FindSeasonAsync(IReadOnlyDictionary<string, string>? seriesIds, string? seriesName, int? seriesYear, int? season, CancellationToken cancellationToken)
        {
            var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            return index == null ? null : LookupSeason(index, seriesIds, seriesName, seriesYear, season);
        }

        private static PlexMatch? LookupEpisode(Index index, IReadOnlyDictionary<string, string>? seriesIds, string? seriesName, int? seriesYear, int? season, int? episode)
        {
            var series = Lookup(index, seriesIds, seriesName, seriesYear);
            return series == null || season == null || episode == null
                ? null
                : index.Episodes.GetValueOrDefault((PlexApiClient.RatingKeyToGuid(series.RatingKey), season.Value, episode.Value));
        }

        private static PlexMatch? LookupSeason(Index index, IReadOnlyDictionary<string, string>? seriesIds, string? seriesName, int? seriesYear, int? season)
        {
            var series = Lookup(index, seriesIds, seriesName, seriesYear);
            return series == null || season == null
                ? null
                : index.Seasons.GetValueOrDefault((PlexApiClient.RatingKeyToGuid(series.RatingKey), season.Value));
        }

        /// <summary>Same lookup keyed by raw identity, used by providers that only get an info object.</summary>
        public async Task<PlexMatch?> FindMatchAsync(IReadOnlyDictionary<string, string>? providerIds, string? name, int? year, CancellationToken cancellationToken)
        {
            var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            return index == null ? null : Lookup(index, providerIds, name, year);
        }

        private static PlexMatch? Lookup(Index index, IReadOnlyDictionary<string, string>? providerIds, string? name, int? year)
        {
            foreach (var key in IdKeys(providerIds))
            {
                if (index.ById.TryGetValue(key, out var byId))
                {
                    return byId;
                }
            }

            var titleKey = TitleYearKey(name, year);
            return titleKey != null && index.ByTitleYear.TryGetValue(titleKey, out var byTitle) ? byTitle : null;
        }

        internal static IEnumerable<string> IdKeys(IReadOnlyDictionary<string, string>? ids)
        {
            if (ids == null)
            {
                yield break;
            }

            foreach (var provider in new[] { "tmdb", "imdb", "tvdb" })
            {
                foreach (var pair in ids)
                {
                    if (string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        yield return provider + ":" + pair.Value.Trim().ToLowerInvariant();
                    }
                }
            }
        }

        internal static string? TitleYearKey(string? title, int? year)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var normalized = new string(title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return normalized.Length == 0 ? null : normalized + "|" + (year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }

        /// <summary>Drops the cached Plex catalog so the next lookup re-reads it (used by "Pull now").</summary>
        public void Invalidate() => _index = null;

        private async Task<Index?> GetIndexAsync(CancellationToken cancellationToken)
        {
            var settings = Settings;
            var fingerprint = settings.ServerUrl + "|" + string.Join(",", settings.SectionKeys);
            var current = _index;
            if (current != null && _indexFingerprint == fingerprint && DateTime.UtcNow - current.BuiltUtc < IndexLifetime)
            {
                return current;
            }

            await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                current = _index;
                if (current != null && _indexFingerprint == fingerprint && DateTime.UtcNow - current.BuiltUtc < IndexLifetime)
                {
                    return current;
                }

                var client = CreateClient(settings.ServerUrl, settings.Token);
                var sections = (await client.GetSectionsAsync(cancellationToken).ConfigureAwait(false)).Where(s => IsSupportedSection(s.Type));
                if (settings.SectionKeys.Count > 0)
                {
                    sections = sections.Where(s => settings.SectionKeys.Contains(s.Key));
                }

                var byId = new Dictionary<string, PlexMatch>(StringComparer.Ordinal);
                var byTitle = new Dictionary<string, PlexMatch>(StringComparer.Ordinal);
                var episodes = new Dictionary<(Guid, int, int), PlexMatch>();
                var seasons = new Dictionary<(Guid, int), PlexMatch>();
                foreach (var section in sections)
                {
                    foreach (var external in await client.GetSectionItemsAsync(section, cancellationToken).ConfigureAwait(false))
                    {
                        if (external.Dto.Type == BaseItemKind.Episode)
                        {
                            if (external.Dto.SeriesId is { } seriesGuid && external.Dto.ParentIndexNumber is { } seasonNumber)
                            {
                                var episodeMatch = new PlexMatch(external.NativeId, external.Dto);
                                if (external.Dto.IndexNumber is { } episodeNumber)
                                {
                                    episodes.TryAdd((seriesGuid, seasonNumber, episodeNumber), episodeMatch);
                                }

                                // A season has no Plex item of its own in this listing; its poster is the
                                // parent poster of any one of its episodes.
                                seasons.TryAdd((seriesGuid, seasonNumber), new PlexMatch(external.NativeId, external.Dto, PlexImageKind.Season));
                            }

                            continue;
                        }

                        var match = new PlexMatch(external.NativeId, external.Dto);
                        foreach (var key in IdKeys(external.Dto.ProviderIds))
                        {
                            byId.TryAdd(key, match);
                        }

                        var titleKey = TitleYearKey(external.Dto.Name, external.Dto.ProductionYear);
                        if (titleKey != null)
                        {
                            byTitle.TryAdd(titleKey, match);
                        }
                    }
                }

                _index = new Index(DateTime.UtcNow, byId, byTitle, episodes, seasons);
                _indexFingerprint = fingerprint;
                _logger.LogInformation("[Federation] Plex metadata index built: {Ids} id keys, {Titles} title keys, {Episodes} episodes", byId.Count, byTitle.Count, episodes.Count);
                return _index;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep serving the previous index (if any) rather than failing every refresh while Plex is down.
                _logger.LogWarning(ex, "[Federation] Could not read the Plex catalog for metadata matching");
                return _index;
            }
            finally
            {
                _indexLock.Release();
            }
        }

        /// <summary>Opens the poster or backdrop for a Plex item. The caller owns the response.</summary>
        public async Task<HttpResponseMessage?> GetImageAsync(string ratingKey, string imageName, CancellationToken cancellationToken)
        {
            var settings = Settings;
            if (string.IsNullOrEmpty(settings.ServerUrl) || string.IsNullOrEmpty(settings.Token))
            {
                return null;
            }

            var client = CreateClient(settings.ServerUrl, settings.Token);
            var path = await client.GetImagePathAsync(ratingKey, imageName, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(path) ? null : await client.GetImageAsync(path!, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The owner's ordinary (non-federated) movie and show libraries, for the library picker.</summary>
        public IReadOnlyList<(string Id, string Name)> ListEligibleLibraries()
            => _libraryManager.GetVirtualFolders()
                .Where(vf => IsPlexEligible(vf))
                .Select(vf => (vf.ItemId, vf.Name))
                .ToList();

        private static bool IsPlexEligible(MediaBrowser.Model.Entities.VirtualFolderInfo vf)
        {
            if (vf.CollectionType is not (MediaBrowser.Model.Entities.CollectionTypeOptions.movies or MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows))
            {
                return false;
            }

            // Libraries this plugin built for friends only ever use the "Federation" fetcher.
            var options = vf.LibraryOptions?.TypeOptions ?? Array.Empty<TypeOptions>();
            return !options.Any(t => (t.MetadataFetchers ?? Array.Empty<string>()).Contains("Federation"));
        }

        /// <summary>
        /// Puts the Plex fetcher first (so Plex wins) on the chosen libraries' movie/series type options, or removes it
        /// from every eligible library when <paramref name="enable"/> is false. Libraries with no explicit fetcher list
        /// already use every provider, so only existing lists are edited.
        /// </summary>
        public async Task ApplyFetchersAsync(IReadOnlyCollection<string> libraryIds, bool enable)
        {
            foreach (var vf in _libraryManager.GetVirtualFolders().Where(IsPlexEligible))
            {
                var selected = enable && (libraryIds.Count == 0 || libraryIds.Contains(vf.ItemId, StringComparer.OrdinalIgnoreCase));
                if (_libraryManager.GetItemById(Guid.Parse(vf.ItemId)) is not CollectionFolder folder)
                {
                    continue;
                }

                var options = folder.GetLibraryOptions();
                var changed = false;
                foreach (var type in options.TypeOptions ?? Array.Empty<TypeOptions>())
                {
                    if (type.Type is not ("Movie" or "Series" or "Season" or "Episode"))
                    {
                        continue;
                    }

                    changed |= Adjust(type.MetadataFetchers, selected && Settings.PullText, v => type.MetadataFetchers = v);
                    changed |= Adjust(type.ImageFetchers, selected && Settings.PullArtwork, v => type.ImageFetchers = v);
                }

                if (changed)
                {
                    folder.UpdateLibraryOptions(options);
                    _logger.LogInformation("[Federation] Plex metadata fetcher {State} on library {Library}", selected ? "enabled" : "removed", vf.Name);
                }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static bool Adjust(string[]? current, bool wantPlex, Action<string[]> set)
        {
            // An empty list means "every provider, default order": nothing to edit, Plex is already in play.
            if (current == null || current.Length == 0)
            {
                return false;
            }

            var without = current.Where(f => !string.Equals(f, PlexProviderName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var desired = wantPlex ? new[] { PlexProviderName }.Concat(without).ToArray() : without;
            if (desired.SequenceEqual(current))
            {
                return false;
            }

            set(desired);
            return true;
        }

        private const string PlexProviderName = "Plex";

        /// <summary>Re-reads Plex and queues a full metadata and image refresh of every movie and series in the chosen libraries.</summary>
        public int QueueFullRefresh()
        {
            Invalidate();
            var queued = 0;
            var settings = Settings;
            var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode },
                Recursive = true,
            });

            foreach (var item in items.Where(AppliesTo))
            {
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = settings.PullArtwork ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
                    ReplaceAllImages = settings.PullArtwork,
                    ReplaceAllMetadata = settings.PullText,
                };
                _providerManager.QueueRefresh(item.Id, options, RefreshPriority.Low);
                queued++;
            }

            return queued;
        }

        private static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
