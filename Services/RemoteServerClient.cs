using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        // A new RemoteServerClient is constructed per call (see
        // RemoteServerClientFactory), so this cache is static/shared rather than an
        // instance field - otherwise it would never survive between calls. Every
        // play of a federated item calls GetPlaybackInfoAsync, which is a live HTTP
        // round trip to the remote; a short TTL absorbs the common case of a client
        // re-requesting playback info seconds apart (multiple sources for the same
        // item, a player re-checking on resume) without going stale for an actual
        // viewing session.
        private static readonly ConcurrentDictionary<string, (DateTime Expires, PlaybackInfoResponse Response)> PlaybackInfoCache = new();
        private static readonly TimeSpan PlaybackInfoCacheTtl = TimeSpan.FromSeconds(15);

        // Same rationale as PlaybackInfoCache above (a client is constructed per
        // call, so this has to be static to survive between them). Keeps one sync
        // cycle from probing the same server once per mapping/source.
        private static readonly ConcurrentDictionary<string, (DateTime Expires, FederationPeerStatus Status)> PeerStatusCache = new();
        private static readonly TimeSpan PeerStatusCacheTtl = TimeSpan.FromSeconds(30);

        // In-memory only, deliberately never written to _server.UserId: that field
        // lives on the same RemoteServer instance Plugin.Instance.Configuration
        // holds, so mutating it would get silently persisted to disk the next time
        // anything calls SaveConfiguration() (adding a server, accepting a friend
        // request, ...) - indistinguishable from an admin having configured it
        // themselves, and immune to an admin's later attempt to clear or change it.
        // Keyed by server id so this still skips the GetUsersAsync round trip on
        // every subsequent play for the rest of this process's lifetime, without
        // that persistence risk.
        private static readonly ConcurrentDictionary<string, string> ResolvedPlaybackUserIdCache = new(StringComparer.OrdinalIgnoreCase);

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
                bool fallbackToFirstUser = false;
                if (string.IsNullOrEmpty(userIdToUse) && userId == null
                    && ResolvedPlaybackUserIdCache.TryGetValue(_server.Id, out var previouslyResolved))
                {
                    userIdToUse = previouslyResolved;
                    fallbackToFirstUser = true;
                }

                if (string.IsNullOrEmpty(userIdToUse))
                {
                    // PlaybackInfo is a per-user endpoint. When no user is stored on
                    // the server config, fall back to a remote user so we can still read
                    // stream details instead of failing playback outright. Prefer an
                    // administrator: a non-admin user can be restricted to specific
                    // libraries/folders (UserPolicy.EnabledFolders) or have
                    // EnableMediaPlayback turned off entirely, and either one makes
                    // PlaybackInfo come back empty for an otherwise-visible item -
                    // "shows up but can't stream". An arbitrary first user risks hitting
                    // exactly that; an admin has no such restriction by default.
                    _logger.LogWarning(
                        "[Federation] No UserId configured for remote server {ServerName}; resolving playback user automatically for item {ItemId}",
                        _server.Name,
                        itemId);

                    var users = await GetUsersAsync(cancellationToken).ConfigureAwait(false);
                    var chosen = users?.FirstOrDefault(u => u.IsAdministrator) ?? users?.FirstOrDefault();
                    userIdToUse = chosen?.Id;
                    fallbackToFirstUser = userIdToUse != null;

                    if (userId == null && userIdToUse != null)
                    {
                        // Cached in-memory only (see ResolvedPlaybackUserIdCache) so
                        // every future play of any item on this server skips this
                        // GetUsersAsync round trip for the rest of this process's
                        // lifetime, without ever touching the persisted config - an
                        // admin who configures (or clears) UserId always wins,
                        // immediately, not just after a restart.
                        ResolvedPlaybackUserIdCache[_server.Id] = userIdToUse;
                    }

                    if (chosen != null && !chosen.IsAdministrator)
                    {
                        _logger.LogWarning(
                            "[Federation] No administrator account found on server {ServerName}; falling back to non-admin user {UserName} ({UserId}), which may be restricted from playing some items",
                            _server.Name,
                            chosen.Name,
                            chosen.Id);
                    }

                    if (chosen != null && chosen.Policy != null && !chosen.Policy.EnableMediaPlayback)
                    {
                        _logger.LogWarning(
                            "[Federation] User {UserName} ({UserId}) on server {ServerName} has media playback disabled in their policy; PlaybackInfo for item {ItemId} will likely come back empty",
                            chosen.Name,
                            chosen.Id,
                            _server.Name,
                            itemId);
                    }
                }

                if (string.IsNullOrEmpty(userIdToUse))
                {
                    _logger.LogWarning(
                        "[Federation] Could not resolve any user on remote server {ServerName}, so stream info for item {ItemId} cannot be read and playback will fail",
                        _server.Name,
                        itemId);
                    return null;
                }

                if (fallbackToFirstUser)
                {
                    _logger.LogInformation(
                        "[Federation] Resolved playback user {UserId} on server {ServerName} for item {ItemId} (no configured UserId)",
                        userIdToUse,
                        _server.Name,
                        itemId);
                }

                var cacheKey = $"{_server.Id}:{itemId}:{userIdToUse}";
                if (PlaybackInfoCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
                {
                    _logger.LogDebug("[Federation] Using cached playback info for item {ItemId} from {ServerName}", itemId, _server.Name);
                    return cached.Response;
                }

                var url = $"/Items/{itemId}/PlaybackInfo?UserId={userIdToUse}";
                _logger.LogDebug("[Federation] Getting playback info for item {ItemId} from {ServerName} as user {UserId}", itemId, _server.Name, userIdToUse);

                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var playbackInfo = JsonSerializer.Deserialize<PlaybackInfoResponse>(content, JsonOpts);
                _logger.LogInformation(
                    "[Federation] Playback info for item {ItemId} on {ServerName}: {SourceCount} source(s)",
                    itemId,
                    _server.Name,
                    playbackInfo?.MediaSources?.Count ?? 0);

                if (playbackInfo != null && (playbackInfo.MediaSources?.Count ?? 0) > 0)
                {
                    PlaybackInfoCache[cacheKey] = (DateTime.UtcNow + PlaybackInfoCacheTtl, playbackInfo);
                }

                if ((playbackInfo?.MediaSources?.Count ?? 0) == 0)
                {
                    // The remote accepted the request but had nothing to offer this
                    // user for this item - usually a permissions problem (wrong
                    // library access, media playback disabled) rather than a network
                    // or plugin failure. ErrorCode, when present, says why.
                    _logger.LogWarning(
                        "[Federation] Server {ServerName} returned zero media sources for item {ItemId} as user {UserId}; ErrorCode={ErrorCode}",
                        _server.Name,
                        itemId,
                        userIdToUse,
                        playbackInfo?.ErrorCode ?? "(none)");
                }

                return playbackInfo;
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
            var (info, _) = await GetSystemInfoDetailedAsync(cancellationToken).ConfigureAwait(false);
            return info;
        }

        /// <summary>
        /// Same request as <see cref="GetSystemInfoAsync"/>, but also returns a
        /// human-readable reason on failure. <c>/System/Info</c> (unlike
        /// <c>/System/Info/Public</c>, which <see cref="TestConnectionAsync"/> uses)
        /// requires a valid, sufficiently-privileged API key - so "connected fine,
        /// then this fails" almost always means a bad/missing/insufficiently-
        /// privileged key, not a dead server. Only <see cref="Api.FederationController.TestServer"/>
        /// needs that distinction surfaced to the person setting up the connection;
        /// every other caller of <see cref="GetSystemInfoAsync"/> only needs success/failure.
        /// </summary>
        public async Task<(SystemInfo? Info, string? Error)> GetSystemInfoDetailedAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("/System/Info", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var reason = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized => "the API key is invalid or missing",
                        System.Net.HttpStatusCode.Forbidden => "the API key does not have permission to view system info (use an administrator account's key)",
                        _ => $"the server returned {(int)response.StatusCode} {response.StatusCode}"
                    };
                    _logger.LogError("Error getting system info from remote server {ServerName}: {Reason}", _server.Name, reason);
                    return (null, reason);
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return (JsonSerializer.Deserialize<SystemInfo>(content, JsonOpts), null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting system info from remote server {ServerName}", _server.Name);
                return (null, ex.Message);
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

                    // Prefer an administrator: a restricted user's Views may omit
                    // libraries they don't have access to, silently shrinking what
                    // gets federated. See GetPlaybackInfoAsync for the same reasoning.
                    userIdToUse = (users.FirstOrDefault(u => u.IsAdministrator) ?? users[0]).Id;
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
        /// Gets this server's friends-of-friends list, if it has that feature enabled.
        /// Returns null (not an empty list) when the remote is unreachable, doesn't
        /// run Federation, or has the feature disabled - callers should treat that as
        /// "nothing to discover here," not as an error worth surfacing.
        /// </summary>
        public async Task<List<FriendListEntry>?> GetFriendsListAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("/Plugins/Federation/Friends/List", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<FriendsListResponse>(content, JsonOpts);
                return result?.AllowsIntroductions == true ? result.Friends : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not get friends-of-friends list from {ServerName}", _server.Name);
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

        /// <summary>
        /// Determines whether the remote server is itself running the Federation
        /// plugin, by requesting its <c>Plugins/Federation/Config</c> route -
        /// registered only by this plugin's own controller, served
        /// <see cref="Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute"/> so
        /// no API key is needed to probe it. A remote that isn't running Federation
        /// 404s here even though ordinary item/library endpoints (which this plugin
        /// also depends on) work fine against any stock Jellyfin server - this is what
        /// actually distinguishes "a Jellyfin server" from "a federation peer."
        /// <para>
        /// Deliberately tri-state rather than a bool. The caller deletes a server's
        /// entire federated library when told the plugin is gone, so "I could not
        /// reach the remote" must never be reported as "the plugin is not installed" -
        /// that is the difference between riding out a transient outage and wiping a
        /// library because a tunnel returned 502 for a minute. Only a 404 from a
        /// server that is provably alive counts as absence; everything else
        /// (timeouts, refused connections, 5xx from a proxy, an auth layer answering
        /// 401/403) is <see cref="FederationPeerStatus.Unknown"/>.
        /// </para>
        /// </summary>
        public async Task<FederationPeerStatus> GetFederationPeerStatusAsync(CancellationToken cancellationToken = default)
        {
            var cacheKey = _server.Id ?? _server.Url;
            if (PeerStatusCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                return cached.Status;
            }

            var status = await ProbeFederationPeerStatusAsync(cancellationToken).ConfigureAwait(false);

            // Cached briefly so a sync covering several mappings/sources on the same
            // server does not re-probe once per source. Short enough that a plugin
            // installed or removed mid-session is still noticed on the next cycle.
            PeerStatusCache[cacheKey] = (DateTime.UtcNow.Add(PeerStatusCacheTtl), status);
            return status;
        }

        private async Task<FederationPeerStatus> ProbeFederationPeerStatusAsync(CancellationToken cancellationToken)
        {
            System.Net.HttpStatusCode statusCode;
            try
            {
                using var response = await _httpClient.GetAsync("/Plugins/Federation/Config", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return FederationPeerStatus.Installed;
                }

                statusCode = response.StatusCode;
            }
            catch (Exception ex)
            {
                // Timeout, DNS failure, connection refused, TLS error, ... - says
                // nothing about whether the plugin is installed.
                _logger.LogDebug(ex, "[Federation] Could not reach {ServerName} to check whether it runs Federation; treating as unknown", _server.Name);
                return FederationPeerStatus.Unknown;
            }

            if (statusCode != System.Net.HttpStatusCode.NotFound)
            {
                // 502/503 from a tunnel or reverse proxy, 500 from a struggling
                // remote, 401/403 from an access layer in front of it. None of these
                // mean "no Federation here".
                _logger.LogDebug(
                    "[Federation] {ServerName} answered {StatusCode} for the Federation plugin probe; treating as unknown rather than uninstalled",
                    _server.Name,
                    (int)statusCode);
                return FederationPeerStatus.Unknown;
            }

            // A 404 alone is not proof either: a misrouted tunnel or a reverse proxy
            // pointed at the wrong origin will 404 every path, including this one.
            // Confirm the address really is serving a live Jellyfin before reporting
            // an absence the caller will delete content over.
            try
            {
                using var alive = await _httpClient.GetAsync("/System/Info/Public", cancellationToken).ConfigureAwait(false);
                if (alive.IsSuccessStatusCode)
                {
                    return FederationPeerStatus.NotInstalled;
                }

                _logger.LogDebug(
                    "[Federation] {ServerName} 404s the Federation probe and is not serving Jellyfin either ({StatusCode}); treating as unknown",
                    _server.Name,
                    (int)alive.StatusCode);
                return FederationPeerStatus.Unknown;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not confirm {ServerName} is a live Jellyfin server; treating as unknown", _server.Name);
                return FederationPeerStatus.Unknown;
            }
        }

        /// <summary>
        /// Measures approximate download throughput from this remote server by timing
        /// a fetch against its own <c>/Playback/BitrateTest</c> endpoint - the same
        /// mechanism jellyfin-web itself uses client-side to auto-pick a streaming
        /// quality before playback starts. Used by <see cref="WanBandwidthMonitor"/>
        /// to size a WAN bitrate cap from a real measurement instead of a guessed
        /// fixed number. Returns null on any failure; callers must keep using their
        /// last known-good measurement rather than treat null as "no bandwidth".
        /// </summary>
        public async Task<double?> MeasureBandwidthMbpsAsync(CancellationToken cancellationToken = default)
        {
            // 5MB: large enough that TCP slow-start (the connection ramping up to its
            // real throughput over the first several round-trips) doesn't skew a
            // short sample toward an artificially low reading - a real concern on
            // faster links, where slow-start accounts for a bigger fraction of a
            // small sample's total time. Still finishes in a few seconds on a
            // fairly slow link.
            const int sampleBytes = 5_000_000;
            try
            {
                // The shared HttpClient's own timeout (5 minutes - reasonable for a
                // real library sync, which can legitimately take a while) is far too
                // long for what is supposed to be a quick background health check: an
                // unreachable or badly congested remote would otherwise stall this
                // probe for up to 5 minutes, and since WanBandwidthMoniton.
                // RefreshIfDueAsync is awaited at the very start of every sync cycle,
                // that stalls reconciliation for every other server and mapping too,
                // not just the slow one. 20s is generous enough to still get an
                // accurate reading down to roughly 2 Mbps (5MB / 2Mbps ≈ 20s) without
                // risking that blast radius.
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync($"/Playback/BitrateTest?Size={sampleBytes}", linkedCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token).ConfigureAwait(false);
                stopwatch.Stop();

                if (bytes.Length == 0 || stopwatch.Elapsed.TotalSeconds <= 0)
                {
                    return null;
                }

                return bytes.Length * 8.0 / stopwatch.Elapsed.TotalSeconds / 1_000_000.0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bandwidth probe failed for remote server {ServerName}", _server.Name);
                return null;
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

            // Needed so the materialized item can advertise its container without a
            // probe round-trip; already present in the same response, since the sync
            // query asks for the MediaSources field.
            if (itemElement.TryGetProperty("Container", out var containerProp) && containerProp.ValueKind == JsonValueKind.String)
            {
                item.Container = containerProp.GetString();
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

            if (itemElement.TryGetProperty("SeriesId", out var seriesIdProp) && seriesIdProp.ValueKind != JsonValueKind.Null
                && Guid.TryParse(seriesIdProp.GetString(), out var seriesGuid))
            {
                item.SeriesId = seriesGuid;
            }

            if (itemElement.TryGetProperty("SeasonId", out var seasonIdProp) && seasonIdProp.ValueKind != JsonValueKind.Null
                && Guid.TryParse(seasonIdProp.GetString(), out var seasonGuid))
            {
                item.SeasonId = seasonGuid;
            }

            if (itemElement.TryGetProperty("SeasonName", out var seasonNameProp) && seasonNameProp.ValueKind != JsonValueKind.Null)
            {
                item.SeasonName = seasonNameProp.GetString();
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

        // Without a string-enum converter, System.Text.Json's default enum handling
        // expects the underlying numeric value, not a name - but every modern
        // Jellyfin server (including every remote this plugin talks to) serializes
        // enums as their string name (e.g. "Protocol": "File"). That mismatch meant
        // deserializing *any* PlaybackInfo response threw on its very first enum
        // field (Protocol) - not an occasional or item-specific failure, every
        // single call, for every item, on every server, always fell back to the
        // catch block in GetPlaybackInfoAsync and returned null. Confirmed live:
        // querying a remote directly showed a perfectly ordinary "Protocol":"File",
        // yet the plugin's own deserialization of that exact response threw
        // JsonException every time. This is very likely the single biggest
        // contributor to "sometimes takes a long time to load" - every play
        // wasted a full remote round trip that could never succeed, then fell back
        // to local ffprobe discovery instead of the fast, accurate codec info the
        // remote had been correctly sending back the whole time.
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Whether a remote server is a Federation peer, as far as this server can
    /// tell. See <see cref="RemoteServerClient.GetFederationPeerStatusAsync"/> for
    /// why an unreachable remote must be reported as <see cref="Unknown"/> rather
    /// than <see cref="NotInstalled"/>.
    /// </summary>
    public enum FederationPeerStatus
    {
        /// <summary>
        /// Could not be determined: the remote was unreachable, answered from a
        /// proxy/error page, or otherwise gave no trustworthy answer. Callers must
        /// treat this as a transient failure and leave cached content alone.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// The remote answered this plugin's own route: it is running Federation.
        /// </summary>
        Installed = 1,

        /// <summary>
        /// The remote is demonstrably a live Jellyfin server that does not have
        /// Federation installed. Only this value justifies removing content.
        /// </summary>
        NotInstalled = 2
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
    /// Response to a <c>GET /Plugins/Federation/Friends/List</c> request.
    /// </summary>
    public class FriendsListResponse
    {
        /// <summary>Gets or sets a value indicating whether the responding server allows friends-of-friends discovery.</summary>
        public bool AllowsIntroductions { get; set; }

        /// <summary>Gets or sets the responding server's own friends.</summary>
        public List<FriendListEntry> Friends { get; set; } = new();
    }

    /// <summary>
    /// One entry in a <see cref="FriendsListResponse"/>.
    /// </summary>
    public class FriendListEntry
    {
        /// <summary>Gets or sets the friend's display name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the friend's server address.</summary>
        public string? Url { get; set; }
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

        /// <summary>
        /// Gets or sets the user's access policy (library/playback restrictions).
        /// </summary>
        public UserPolicyDto? Policy { get; set; }

        /// <summary>
        /// Gets whether this user is an administrator on the remote server.
        /// </summary>
        public bool IsAdministrator => Policy?.IsAdministrator ?? false;
    }

    /// <summary>
    /// The subset of a remote user's policy federation cares about.
    /// </summary>
    public class UserPolicyDto
    {
        /// <summary>
        /// Gets or sets whether the user is an administrator.
        /// </summary>
        public bool IsAdministrator { get; set; }

        /// <summary>
        /// Gets or sets whether the user is allowed to play media at all. A user can
        /// have full browse access (so items sync fine) yet have this set to false,
        /// which fails only at playback time - "shows up but can't stream".
        /// </summary>
        public bool EnableMediaPlayback { get; set; } = true;
    }
}
