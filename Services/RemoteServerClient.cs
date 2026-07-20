using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Client for communicating with remote Jellyfin servers. Uses a shared HttpClient
    /// supplied by <see cref="IRemoteServerClientFactory"/> so sockets are reused.
    /// </summary>
    public class RemoteServerClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly RemoteServer _server;
        private bool _ownsHttpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteServerClient"/> class with its own HttpClient.
        /// Prefer using <see cref="IRemoteServerClientFactory"/> to share HttpClients across requests.
        /// </summary>
        /// <param name="server">The remote server configuration.</param>
        /// <param name="logger">Logger instance.</param>
        public RemoteServerClient(RemoteServer server, ILogger logger)
            : this(server, logger, CreateDefaultHttpClient(server))
        {
            _ownsHttpClient = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteServerClient"/> class using a shared HttpClient.
        /// </summary>
        /// <param name="server">The remote server configuration.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="httpClient">A shared HttpClient configured with the remote server's base address and auth header.</param>
        public RemoteServerClient(RemoteServer server, ILogger logger, HttpClient httpClient)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>
        /// Gets the server configuration.
        /// </summary>
        public RemoteServer ServerConfig => _server;

        /// <summary>
        /// Gets items from the remote server, including ProviderIds and People.
        /// Returns null when the request fails (callers must treat null as
        /// "sync failed" and preserve any existing cached data).
        /// </summary>
        public async Task<List<BaseItemDto>?> GetItemsAsync(
            string? userId = null,
            string? mediaType = null,
            string? parentId = null,
            int? startIndex = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var userIdToUse = userId ?? _server.UserId;
                if (string.IsNullOrEmpty(userIdToUse))
                {
                    _logger.LogWarning("No user ID specified for remote server {ServerName}", _server.Name);
                    return null;
                }

                var queryParams = new List<string>
                {
                    "Recursive=true",
                    "Fields=BasicSyncInfo,Path,MediaSources,Overview,Genres,Tags,Studios,People,ProviderIds,OriginalTitle,ProductionYear",
                    "EnableImageTypes=Primary,Backdrop,Banner,Thumb"
                };

                if (!string.IsNullOrEmpty(mediaType))
                {
                    queryParams.Add($"IncludeItemTypes={mediaType}");
                }

                if (!string.IsNullOrEmpty(parentId))
                {
                    queryParams.Add($"ParentId={parentId}");
                }

                if (startIndex.HasValue)
                {
                    queryParams.Add($"StartIndex={startIndex.Value}");
                }

                if (limit.HasValue)
                {
                    queryParams.Add($"Limit={limit.Value}");
                }

                var url = $"/Users/{userIdToUse}/Items?{string.Join("&", queryParams)}";

                _logger.LogDebug("[Federation] Requesting items from {ServerName}: {Url}", _server.Name, url);

                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("TotalRecordCount", out var totalProp) && totalProp.TryGetInt32(out var totalCount))
                {
                    _logger.LogDebug("[Federation] TotalRecordCount from API: {Count}", totalCount);
                }

                if (!root.TryGetProperty("Items", out var itemsElement))
                {
                    _logger.LogWarning("[Federation] No Items property in response from {ServerName}", _server.Name);
                    return null;
                }

                var items = new List<BaseItemDto>();
                foreach (var itemElement in itemsElement.EnumerateArray())
                {
                    try
                    {
                        items.Add(ParseItem(itemElement));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Error parsing item from {ServerName}", _server.Name);
                    }
                }

                _logger.LogDebug("[Federation] Retrieved {Count} items from remote server {ServerName}", items.Count, _server.Name);
                return items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting items from remote server {ServerName}", _server.Name);
                return null;
            }
        }

        /// <summary>
        /// Gets a specific item by ID from the remote server.
        /// </summary>
        public async Task<BaseItemDto?> GetItemAsync(
            string itemId,
            string? userId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var userIdToUse = userId ?? _server.UserId;
                if (string.IsNullOrEmpty(userIdToUse))
                {
                    _logger.LogWarning("No user ID specified for remote server {ServerName}", _server.Name);
                    return null;
                }

                var url = $"/Users/{userIdToUse}/Items/{itemId}";
                _logger.LogDebug("Getting item {ItemId} from {ServerName}", itemId, _server.Name);

                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                return ParseItem(doc.RootElement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting item {ItemId} from remote server {ServerName}", itemId, _server.Name);
                return null;
            }
        }

        /// <summary>
        /// Gets playback information for a specific item.
        /// </summary>
        public async Task<PlaybackInfoResponse?> GetPlaybackInfoAsync(
            string itemId,
            string? userId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var userIdToUse = userId ?? _server.UserId;
                if (string.IsNullOrEmpty(userIdToUse))
                {
                    _logger.LogWarning("No user ID specified for remote server {ServerName}", _server.Name);
                    return null;
                }

                var url = $"/Items/{itemId}/PlaybackInfo?UserId={userIdToUse}";
                _logger.LogDebug("Getting playback info for item {ItemId} from {ServerName}", itemId, _server.Name);

                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<PlaybackInfoResponse>(content, JsonOpts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting playback info for item {ItemId} from remote server {ServerName}", itemId, _server.Name);
                return null;
            }
        }

        /// <summary>
        /// Gets system information from the remote server.
        /// </summary>
        public async Task<SystemInfo?> GetSystemInfoAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("/System/Info", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<SystemInfo>(content, JsonOpts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting system info from remote server {ServerName}", _server.Name);
                return null;
            }
        }

        /// <summary>
        /// Gets users from the remote server.
        /// </summary>
        public async Task<List<UserDto>?> GetUsersAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("/Users", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<List<UserDto>>(content, JsonOpts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting users from remote server {ServerName}", _server.Name);
                return null;
            }
        }

        /// <summary>
        /// Gets libraries (user views) from the remote server.
        /// </summary>
        public async Task<List<BaseItemDto>?> GetLibrariesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var userIdToUse = _server.UserId;
                if (string.IsNullOrEmpty(userIdToUse))
                {
                    var users = await GetUsersAsync(cancellationToken).ConfigureAwait(false);
                    if (users == null || users.Count == 0)
                    {
                        _logger.LogWarning("No users found on remote server {ServerName}", _server.Name);
                        return null;
                    }

                    userIdToUse = users[0].Id;
                }

                var url = $"/Users/{userIdToUse}/Views";
                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (!root.TryGetProperty("Items", out var itemsElement))
                {
                    return new List<BaseItemDto>();
                }

                var libraries = new List<BaseItemDto>();
                foreach (var item in itemsElement.EnumerateArray())
                {
                    try
                    {
                        var library = new BaseItemDto();
                        if (item.TryGetProperty("Id", out var idProp) && Guid.TryParse(idProp.GetString(), out var guid))
                        {
                            library.Id = guid;
                        }

                        if (item.TryGetProperty("Name", out var nameProp))
                        {
                            library.Name = nameProp.GetString();
                        }

                        if (item.TryGetProperty("CollectionType", out var typeProp) && typeProp.ValueKind != JsonValueKind.Null)
                        {
                            var typeStr = typeProp.GetString();
                            if (!string.IsNullOrEmpty(typeStr) && Enum.TryParse<Jellyfin.Data.Enums.CollectionType>(typeStr, true, out var collectionType))
                            {
                                library.CollectionType = collectionType;
                            }
                        }

                        if (item.TryGetProperty("ChildCount", out var countProp) && countProp.TryGetInt32(out var count))
                        {
                            library.ChildCount = count;
                        }

                        libraries.Add(library);
                    }
                    catch (Exception itemEx)
                    {
                        _logger.LogError(itemEx, "[Federation] Error parsing library item");
                    }
                }

                return libraries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting libraries from remote server {ServerName}", _server.Name);
                return null;
            }
        }

        /// <summary>
        /// Builds a direct stream URL for a remote item (with embedded api_key).
        /// </summary>
        public string BuildDirectStreamUrl(string itemId)
        {
            return $"{_server.Url.TrimEnd('/')}/Videos/{itemId}/stream?api_key={Uri.EscapeDataString(_server.ApiKey)}&Static=true";
        }

        /// <summary>
        /// Tests the connection to the remote server.
        /// </summary>
        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("/System/Info/Public", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to remote server {ServerName}", _server.Name);
                return false;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }

        private BaseItemDto ParseItem(JsonElement itemElement)
        {
            var item = new BaseItemDto();

            if (itemElement.TryGetProperty("Id", out var idProp))
            {
                var idStr = idProp.GetString();
                if (!Guid.TryParse(idStr, out var guid))
                {
                    // A random fallback id would produce unstable cache keys and
                    // duplicate entries on every refresh; reject the item instead.
                    throw new FormatException($"Remote item has an unparseable Id: '{idStr}'");
                }

                item.Id = guid;
            }

            if (itemElement.TryGetProperty("Name", out var nameProp))
            {
                item.Name = nameProp.GetString();
            }

            if (itemElement.TryGetProperty("OriginalTitle", out var originalTitleProp) && originalTitleProp.ValueKind != JsonValueKind.Null)
            {
                item.OriginalTitle = originalTitleProp.GetString();
            }

            if (itemElement.TryGetProperty("Type", out var typeProp))
            {
                var typeStr = typeProp.GetString();
                if (!string.IsNullOrEmpty(typeStr) &&
                    Enum.TryParse<Jellyfin.Data.Enums.BaseItemKind>(typeStr, true, out var itemKind))
                {
                    item.Type = itemKind;
                }
            }

            if (itemElement.TryGetProperty("Overview", out var overviewProp) && overviewProp.ValueKind != JsonValueKind.Null)
            {
                item.Overview = overviewProp.GetString();
            }

            if (itemElement.TryGetProperty("CommunityRating", out var ratingProp) && ratingProp.ValueKind == JsonValueKind.Number)
            {
                item.CommunityRating = (float?)ratingProp.GetDouble();
            }

            if (itemElement.TryGetProperty("OfficialRating", out var officialRatingProp) && officialRatingProp.ValueKind != JsonValueKind.Null)
            {
                item.OfficialRating = officialRatingProp.GetString();
            }

            if (itemElement.TryGetProperty("PremiereDate", out var premiereProp) && premiereProp.ValueKind != JsonValueKind.Null)
            {
                if (DateTime.TryParse(premiereProp.GetString(), out var premiereDate))
                {
                    item.PremiereDate = premiereDate;
                }
            }

            if (itemElement.TryGetProperty("ProductionYear", out var yearProp) && yearProp.TryGetInt32(out var year))
            {
                item.ProductionYear = year;
            }

            if (itemElement.TryGetProperty("RunTimeTicks", out var runtimeProp) && runtimeProp.TryGetInt64(out var runtime))
            {
                item.RunTimeTicks = runtime;
            }

            if (itemElement.TryGetProperty("Genres", out var genresProp) && genresProp.ValueKind == JsonValueKind.Array)
            {
                item.Genres = genresProp.EnumerateArray()
                    .Where(g => g.ValueKind == JsonValueKind.String)
                    .Select(g => g.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }

            if (itemElement.TryGetProperty("Tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                item.Tags = tagsProp.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }

            if (itemElement.TryGetProperty("SeriesName", out var seriesNameProp) && seriesNameProp.ValueKind != JsonValueKind.Null)
            {
                item.SeriesName = seriesNameProp.GetString();
            }

            if (itemElement.TryGetProperty("ParentIndexNumber", out var parentIndexProp) && parentIndexProp.TryGetInt32(out var parentIndex))
            {
                item.ParentIndexNumber = parentIndex;
            }

            if (itemElement.TryGetProperty("IndexNumber", out var indexProp) && indexProp.TryGetInt32(out var indexNum))
            {
                item.IndexNumber = indexNum;
            }

            if (itemElement.TryGetProperty("Album", out var albumProp) && albumProp.ValueKind != JsonValueKind.Null)
            {
                item.Album = albumProp.GetString();
            }

            if (itemElement.TryGetProperty("AlbumArtist", out var albumArtistProp) && albumArtistProp.ValueKind != JsonValueKind.Null)
            {
                item.AlbumArtist = albumArtistProp.GetString();
            }

            if (itemElement.TryGetProperty("Artists", out var artistsProp) && artistsProp.ValueKind == JsonValueKind.Array)
            {
                item.Artists = artistsProp.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }

            if (itemElement.TryGetProperty("Studios", out var studiosProp) && studiosProp.ValueKind == JsonValueKind.Array)
            {
                item.Studios = studiosProp.EnumerateArray()
                    .Where(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String)
                    .Select(s => new NameGuidPair { Name = s.GetProperty("Name").GetString() ?? string.Empty })
                    .ToArray();
            }

            if (itemElement.TryGetProperty("ProviderIds", out var providerIdsProp) && providerIdsProp.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in providerIdsProp.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            dict[prop.Name] = val;
                        }
                    }
                }

                item.ProviderIds = dict;
            }

            if (itemElement.TryGetProperty("People", out var peopleProp) && peopleProp.ValueKind == JsonValueKind.Array)
            {
                item.People = peopleProp.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Object)
                    .Select(p =>
                    {
                        var person = new BaseItemPerson();
                        if (p.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String)
                        {
                            person.Name = n.GetString() ?? string.Empty;
                        }

                        if (p.TryGetProperty("Role", out var r) && r.ValueKind == JsonValueKind.String)
                        {
                            person.Role = r.GetString();
                        }

                        if (p.TryGetProperty("Type", out var tp) && tp.ValueKind == JsonValueKind.String)
                        {
                            if (Enum.TryParse<Jellyfin.Data.Enums.PersonKind>(tp.GetString(), true, out var kind))
                            {
                                person.Type = kind;
                            }
                        }

                        return person;
                    })
                    .Where(p => !string.IsNullOrEmpty(p.Name))
                    .ToArray();
            }

            if (itemElement.TryGetProperty("ImageTags", out var imageTagsProp) && imageTagsProp.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<MediaBrowser.Model.Entities.ImageType, string>();
                foreach (var prop in imageTagsProp.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(prop.Name, true, out var imgType))
                    {
                        dict[imgType] = prop.Value.GetString() ?? string.Empty;
                    }
                }

                item.ImageTags = dict;
            }

            if (itemElement.TryGetProperty("BackdropImageTags", out var backdropProp) && backdropProp.ValueKind == JsonValueKind.Array)
            {
                item.BackdropImageTags = backdropProp.EnumerateArray()
                    .Where(b => b.ValueKind == JsonValueKind.String)
                    .Select(b => b.GetString() ?? string.Empty)
                    .ToArray();
            }

            return item;
        }

        private static HttpClient CreateDefaultHttpClient(RemoteServer server)
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri(server.Url.TrimEnd('/')),
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.Add("X-Emby-Token", server.ApiKey);
            return client;
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// Playback information response.
    /// </summary>
    public class PlaybackInfoResponse
    {
        /// <summary>
        /// Gets or sets the media sources.
        /// </summary>
        public List<MediaSourceInfo>? MediaSources { get; set; }

        /// <summary>
        /// Gets or sets the play session ID.
        /// </summary>
        public string? PlaySessionId { get; set; }

        /// <summary>
        /// Gets or sets error code.
        /// </summary>
        public string? ErrorCode { get; set; }
    }

    /// <summary>
    /// System information.
    /// </summary>
    public class SystemInfo
    {
        /// <summary>
        /// Gets or sets the server name.
        /// </summary>
        public string? ServerName { get; set; }

        /// <summary>
        /// Gets or sets the version.
        /// </summary>
        public string? Version { get; set; }

        /// <summary>
        /// Gets or sets the operating system.
        /// </summary>
        public string? OperatingSystem { get; set; }

        /// <summary>
        /// Gets or sets the ID.
        /// </summary>
        public string? Id { get; set; }
    }

    /// <summary>
    /// User DTO.
    /// </summary>
    public class UserDto
    {
        /// <summary>
        /// Gets or sets the user ID.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets the user name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets whether the user has password.
        /// </summary>
        public bool HasPassword { get; set; }

        /// <summary>
        /// Gets or sets whether the user has configured password.
        /// </summary>
        public bool HasConfiguredPassword { get; set; }
    }
}
