using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Implements the friend-request handshake between two Federation-enabled
    /// servers: instead of admins manually creating and copy-pasting API keys, each
    /// side mints a key for the other automatically as part of accepting a request.
    /// Trust still lives entirely with the human clicking Accept - this only removes
    /// the manual key-copying step, not identity verification, which is inherently
    /// limited to "does this URL respond the way it claims to" (see
    /// <see cref="VerifyOutgoingRequestExistsAsync"/>).
    /// </summary>
    public class FederationFriendService
    {
        private static readonly HttpClient DefaultHttpClient = new HttpClient(FederationHttp.Handler())
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = HttpContentReader.ControlJsonLimit
        };

        // Inbound origin-verify only: redirects are never followed, including to
        // loopback/RFC1918/metadata or another host that could receive credentials.
        internal static readonly SocketsHttpHandler DefaultVerifyHandler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = FederationHttp.Guard(PublicCallbackConnection.ConnectAsync)
        };

        private static readonly HttpClient DefaultVerifyHttpClient = new HttpClient(FederationHttp.Stamp(DefaultVerifyHandler), disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        /// <summary>
        /// Test-only seam: when set, used instead of <see cref="DefaultHttpClient"/> for
        /// every request. Kept static (rather than a constructor parameter) so the
        /// public DI constructor stays exactly what ASP.NET Core's container expects -
        /// adding an HttpClient constructor parameter would make it try to resolve one
        /// from the container and fail, since none is registered. Tests set this to a
        /// fake-handler-backed client and must reset it to null afterwards.
        /// </summary>
        internal static HttpClient? HttpClientOverride { get; set; }

        // Carries X-Federation-Token, so it must never follow a redirect by itself:
        // a friend (or anything in front of it) answering 3xx to another host would
        // otherwise be handed the token. See TokenRedirect for the same-host hop.
        internal static readonly SocketsHttpHandler DefaultTokenHandler = FederationHttp.Sockets(allowAutoRedirect: false);

        private static readonly HttpClient DefaultTokenHttpClient = new HttpClient(FederationHttp.Stamp(DefaultTokenHandler), disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private static HttpClient SharedHttpClient => HttpClientOverride ?? DefaultHttpClient;

        private static HttpClient TokenHttpClient => HttpClientOverride ?? DefaultTokenHttpClient;

        private static HttpClient VerifyHttpClient => HttpClientOverride ?? DefaultVerifyHttpClient;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<FederationFriendService> _logger;
        private readonly IAuthenticationManager _authManager;
        private readonly IServerApplicationHost _applicationHost;
        private readonly FederationLibraryManager _federationManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly IPlexCompanionIntegration _plexCompanion;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationFriendService"/> class.
        /// </summary>
        public FederationFriendService(
            ILogger<FederationFriendService> logger,
            IAuthenticationManager authManager,
            IServerApplicationHost applicationHost,
            FederationLibraryManager federationManager,
            IHttpContextAccessor httpContextAccessor,
            IRemoteServerClientFactory clientFactory,
            IPlexCompanionIntegration? plexCompanion = null)
        {
            _logger = logger;
            _authManager = authManager;
            _applicationHost = applicationHost;
            _federationManager = federationManager;
            _httpContextAccessor = httpContextAccessor;
            _clientFactory = clientFactory;
            _plexCompanion = plexCompanion ?? new UnavailablePlexCompanionIntegration();
        }

        // Name under which this plugin's single internal relay API key is
        // registered in Jellyfin's key store - shared by creation, the
        // once-per-process orphan sweep, and uninstall-time revocation.
        internal const string InternalRelayKeyName = "Federation internal relay";

        // Same order of magnitude as pending Plex offers. Anonymous inbound
        // friend requests would otherwise grow IncomingFriendRequests without bound.
        internal const int MaxPendingIncomingFriendRequests = 25;

        private static readonly TimeSpan IncomingFriendRequestMaxAge = TimeSpan.FromDays(1);
        private static readonly ConcurrentDictionary<string, Queue<DateTime>> FriendRequestAttempts = new(StringComparer.Ordinal);

        // Local SetPoolIconAsync already rejects icons over this length; inbound
        // peer notices must use the same bound.
        internal const int MaxPoolIconBase64Length = 150_000;

        // Guards check-then-create below against a race on first concurrent use
        // (two Direct-mode plays landing on FederationController.DirectStream at
        // almost the same instant, before Configuration.InternalRelayApiKey has been
        // persisted yet). This service is registered Scoped (see
        // PluginServiceRegistrator - it needs IAuthenticationManager, which Jellyfin
        // registers scoped), so a new instance is constructed per request; the lock
        // has to be static to actually serialize across those instances. In practice
        // this happens at most once per server lifetime.
        private static readonly SemaphoreSlim InternalRelayKeyLock = new(1, 1);
        private static string _internalRelayApiKey = string.Empty;
        private static int _orphanKeySweepDone;
        private static DateTime _relayKeyCheckedUtc = DateTime.MinValue;

        internal static TimeSpan RelayKeyCheckInterval { get; set; } = TimeSpan.FromMinutes(10);

        internal static void ResetRelayKeyCheckForTests()
        {
            _internalRelayApiKey = string.Empty;
            _relayKeyCheckedUtc = DateTime.MinValue;
            _orphanKeySweepDone = 0;
        }

        /// <summary>
        /// Gets this server's process-lifetime internal relay API key, creating one
        /// on first use. The native admin-equivalent credential is deliberately never
        /// written to plugin configuration.
        /// </summary>
        public async Task<string> GetOrCreateInternalRelayApiKeyAsync(bool forceCheck = false)
        {
            if (!string.IsNullOrEmpty(_internalRelayApiKey)
                && !forceCheck
                && DateTime.UtcNow - _relayKeyCheckedUtc < RelayKeyCheckInterval)
            {
                await SweepOrphanedRelayKeysOnceAsync().ConfigureAwait(false);
                return _internalRelayApiKey;
            }

            await InternalRelayKeyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var current = _internalRelayApiKey;
                if (!string.IsNullOrEmpty(current))
                {
                    if (!forceCheck && DateTime.UtcNow - _relayKeyCheckedUtc < RelayKeyCheckInterval)
                    {
                        return current;
                    }

                    bool exists;
                    try
                    {
                        var keys = await _authManager.GetApiKeys().ConfigureAwait(false);
                        exists = keys.Any(k => string.Equals(k.AccessToken, current, StringComparison.Ordinal));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[Federation] Could not list API keys to check the internal relay key; keeping it");
                        exists = true;
                    }

                    if (exists)
                    {
                        _relayKeyCheckedUtc = DateTime.UtcNow;
                        await SweepOrphanedRelayKeysOnceAsync().ConfigureAwait(false);
                        return current;
                    }
                }

                _internalRelayApiKey = await CreateApiKeyAsync(InternalRelayKeyName).ConfigureAwait(false);
                _relayKeyCheckedUtc = DateTime.UtcNow;
                await SweepOrphanedRelayKeysOnceAsync().ConfigureAwait(false);
                return _internalRelayApiKey;
            }
            finally
            {
                InternalRelayKeyLock.Release();
            }
        }

        private async Task SweepOrphanedRelayKeysOnceAsync()
        {
            if (Interlocked.Exchange(ref _orphanKeySweepDone, 1) != 0)
            {
                return;
            }

            try
            {
                var current = _internalRelayApiKey;
                var keys = await _authManager.GetApiKeys().ConfigureAwait(false);
                foreach (var key in keys.Where(k => string.Equals(k.AppName, InternalRelayKeyName, StringComparison.Ordinal)
                                                    && !string.Equals(k.AccessToken, current, StringComparison.Ordinal)).ToList())
                {
                    await _authManager.DeleteApiKey(key.AccessToken).ConfigureAwait(false);
                    _logger.LogInformation("[Federation] Deleted an orphaned '{KeyName}' API key left behind by an older plugin version", InternalRelayKeyName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not sweep orphaned internal relay API keys (non-fatal)");
            }
        }

        /// <summary>
        /// Deletes every "Federation internal relay" API key from Jellyfin's key
        /// store. Called on plugin uninstall: the key is a real, admin-equivalent
        /// Jellyfin credential (the only one this plugin ever creates), and it
        /// lives in Jellyfin's own key store rather than this plugin's config, so
        /// without this it would survive the uninstall as a standing admin key
        /// nobody remembers minting. Static because the plugin instance itself is
        /// a singleton and must resolve the DI-scoped
        /// <see cref="MediaBrowser.Controller.Security.IAuthenticationManager"/>
        /// through a short-lived scope at uninstall time.
        /// </summary>
        internal static async Task RevokeInternalRelayApiKeysAsync(IAuthenticationManager authManager)
        {
            var keys = await authManager.GetApiKeys().ConfigureAwait(false);
            foreach (var key in keys.Where(k => string.Equals(k.AppName, InternalRelayKeyName, StringComparison.Ordinal)).ToList())
            {
                await authManager.DeleteApiKey(key.AccessToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Gets this server's persistent federation identity, generating and saving
        /// one on first use. Not part of the config's default property initializer:
        /// that would mint a new one every time a fresh config object is constructed
        /// before ever being persisted, which is exactly the "identity changes on
        /// every restart" bug this method exists to avoid.
        /// </summary>
        public string GetOrCreateLocalFederationId()
        {
            var config = Plugin.Instance!.Configuration;
            if (string.IsNullOrEmpty(config.LocalFederationId))
            {
                config.LocalFederationId = Guid.NewGuid().ToString();
                Plugin.Instance.SaveConfiguration();
            }

            return config.LocalFederationId;
        }

        /// <summary>
        /// Sends a friend request to a remote Federation server: mints a fresh API
        /// key on this server for the remote to use, and asks them to accept.
        /// </summary>
        /// <param name="remoteUrl">The remote server's address.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="pool">
        /// When set, this request also introduces the recipient to the named pool -
        /// see <see cref="SendPoolInviteAsync"/>. Null for an ordinary direct friend
        /// request.
        /// </param>
        /// <summary>
        /// Asks an existing friend to renew this friendship's keys, for example
        /// after the friend's server was reinstalled or one side lost its keys.
        /// The friend's admin has to accept, exactly as for a new friendship; the
        /// request goes only to the friend's stored address, and accepting keeps
        /// this entry with all its libraries, sharing rules and synced titles.
        /// </summary>
        public async Task<(bool Success, string Message)> ReconnectFriendAsync(string serverId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var friend = config.RemoteServers.FirstOrDefault(s => s.Id == serverId && s.Kind == ServerKind.Jellyfin);
            if (friend == null)
            {
                return (false, "Friend not found.");
            }

            var url = friend.Url.TrimEnd('/');
            if (config.OutgoingFriendRequests.Any(r => string.Equals(r.RemoteServerUrl.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"A request to {friend.Name} is already waiting for them to accept.");
            }

            var sent = await SendFriendRequestCoreAsync(url, null, friend.Id, cancellationToken).ConfigureAwait(false);
            return sent.Success
                ? (true, $"Reconnect request sent to {friend.Name}. Their admin has to accept it; your libraries, sharing settings and synced titles stay as they are.")
                : sent;
        }

        public async Task<(bool Success, string Message)> SendFriendRequestAsync(string remoteUrl, CancellationToken cancellationToken, FederationPool? pool = null, FriendSharing? sharing = null)
        {
            remoteUrl = (remoteUrl ?? string.Empty).TrimEnd('/');
            if (!ConfigValidator.IsValidServerUrl(remoteUrl))
            {
                return (false, "Enter a valid http(s) server address.");
            }

            var config = Plugin.Instance!.Configuration;
            if (AlreadyKnown(config, remoteUrl))
            {
                return (false, "Already friends (or a pending request already exists) with this server. If the connection broke, use Reconnect on that friend instead.");
            }

            return await SendFriendRequestCoreAsync(remoteUrl, pool, null, cancellationToken, sharing).ConfigureAwait(false);
        }

        private async Task<(bool Success, string Message)> SendFriendRequestCoreAsync(string remoteUrl, FederationPool? pool, string? replacesServerId, CancellationToken cancellationToken, FriendSharing? sharing = null)
        {
            var config = Plugin.Instance!.Configuration;
            if (config.OutgoingFriendRequests.Count >= MaxOutgoingFriendRequests)
            {
                return (false, "Too many outgoing friend requests are still pending. Cancel or accept some before sending more.");
            }

            var localUrl = ResolveLocalUrl();
            if (string.IsNullOrEmpty(localUrl))
            {
                return (false, "Could not determine this server's own public URL. Set it under Advanced settings first, since your friend's server needs it to reach back.");
            }

            var requestId = Guid.NewGuid().ToString();
            var apiKey = FederationTokenAuth.GenerateToken();

            var payload = new FriendRequestPayload
            {
                RequestId = requestId,
                FromServerUrl = localUrl,
                FromServerName = _applicationHost.FriendlyName,
                FromServerId = GetOrCreateLocalFederationId(),
                ApiKeyForYou = apiKey,
                SupportsFederationToken = true
            };

            if (pool != null)
            {
                payload.PoolId = pool.Id;
                payload.PoolName = pool.Name;
                payload.PoolOwnerFederationId = pool.OwnerFederationId;
                payload.PoolOwnerName = pool.OwnerName;
                payload.PoolRoster = pool.Members.ToList();
            }

            try
            {
                using var content = JsonContent(payload);
                using var response = await SharedHttpClient.PostAsync($"{remoteUrl}/Plugins/Federation/Friends/Request", content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Friend request rejected by the remote server (HTTP {(int)response.StatusCode}). Check the address - is Federation installed there too, and running a compatible version?");
                }

                var body = await HttpContentReader.ReadTextAsync(response.Content, HttpContentReader.ControlJsonLimit, cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<FriendRequestResponse>(body, JsonOpts);

                config.OutgoingFriendRequests.Add(new FriendRequest
                {
                    Id = requestId,
                    RemoteServerUrl = remoteUrl,
                    RemoteServerName = result?.ServerName ?? remoteUrl,
                    ApiKey = apiKey,
                    ReplacesServerId = replacesServerId,
                    ShareAllLibraries = sharing?.ShareAll ?? (pool != null ? false : null),
                    SharedLibraryFolderIds = sharing?.FolderIds?.ToList(),
                    CreatedUtc = DateTime.UtcNow,
                    PoolId = pool?.Id,
                    PoolName = pool?.Name,
                    PoolOwnerFederationId = pool?.OwnerFederationId,
                    PoolOwnerName = pool?.OwnerName,
                    PoolRoster = pool?.Members.ToList()
                });
                Plugin.Instance.SaveConfiguration();

                return (true, $"Friend request sent to {(result?.ServerName ?? remoteUrl)}. Waiting for them to accept.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to send friend request to {Url}", remoteUrl);
                return (false, $"Could not reach {remoteUrl}: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes reviewable friends-of-friends results without contacting any
        /// discovered candidate. An administrator must explicitly choose a candidate
        /// and its sharing scope before a friend request is sent.
        /// </summary>
        public Task<int> DiscoverFriendsOfFriendsAsync(CancellationToken cancellationToken)
            => Plugin.Instance!.Configuration.AllowFriendsOfFriends
                ? RefreshDiscoveredServersAsync(cancellationToken)
                : Task.FromResult(0);

        internal static readonly TimeSpan DeclinedCandidateTtl = TimeSpan.FromDays(30);

        // In-memory only, static so it survives across this Scoped service's
        // per-request instances (same reasoning as InternalRelayKeyLock above).
        // Deliberately never persisted to PluginConfiguration: it's a derived,
        // best-effort cache the admin can always rebuild with a refresh, not
        // something that needs to survive a restart, and keeping it out of the
        // saved config means a discovered-but-unfriended server's address never
        // ends up written to disk.
        private static readonly ConcurrentDictionary<string, DiscoveredServer> DiscoveredServersCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Admin-triggered: asks every current friend who their other friends are,
        /// using the same primitive as <see cref="DiscoverFriendsOfFriendsAsync"/>,
        /// to populate the Discovery dashboard's search results. Never adds anyone
        /// as a friend by itself; the admin picks who to send a request to. Requires
        /// the asked friend to have <see cref="PluginConfiguration.AllowFriendsOfFriends"/>
        /// enabled on their side.
        /// </summary>
        public async Task<int> RefreshDiscoveredServersAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var localUrl = (ResolveLocalUrl() ?? string.Empty).TrimEnd('/');
            var found = 0;

            // Discovery is a Jellyfin Federation protocol call. Plex sources do
            // not implement Peer/Friends, and Companion entries are receivers with
            // no address at all. Trying to construct a client for the latter threw
            // UriFormatException before the per-friend try/catch and surfaced to
            // the browser as the plain text "Error processing request" response.
            foreach (var friend in config.RemoteServers.Where(s =>
                         s.Enabled
                         && s.Kind == ServerKind.Jellyfin
                         && ConfigValidator.IsValidServerUrl(s.Url)).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<FriendListEntry>? entries;
                try
                {
                    var client = _clientFactory.GetClient(friend);
                    entries = await client.GetFriendsListAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Discovery lookup failed for {Server} (non-fatal)", friend.Name);
                    continue;
                }

                if (entries == null)
                {
                    continue;
                }

                // This friend answered: what it no longer lists (unshared, or
                // friends-of-friends turned off) disappears here too.
                foreach (var stale in DiscoveredServersCache.Where(kv => kv.Value.DiscoveredViaFriendId == friend.Id).Select(kv => kv.Key).ToList())
                {
                    DiscoveredServersCache.TryRemove(stale, out _);
                }

                foreach (var entry in entries)
                {
                    if (found >= 500 || string.IsNullOrEmpty(entry.Url) || !ConfigValidator.IsValidServerUrl(entry.Url))
                    {
                        continue;
                    }

                    var normalizedUrl = entry.Url.TrimEnd('/');
                    if ((!string.IsNullOrEmpty(localUrl) && string.Equals(normalizedUrl, localUrl, StringComparison.OrdinalIgnoreCase))
                        || AlreadyKnown(config, normalizedUrl)
                        || !IsAutoIntroducibleUrl(normalizedUrl))
                    {
                        continue;
                    }

                    DiscoveredServersCache[normalizedUrl] = new DiscoveredServer
                    {
                        Name = TrimTo(entry.Name, 200) ?? normalizedUrl,
                        Url = normalizedUrl,
                        DiscoveredViaFriendName = friend.Name,
                        DiscoveredViaFriendId = friend.Id,
                        LastSeenUtc = DateTime.UtcNow
                    };
                    found++;
                }
            }

            // Servers found through a friend that was removed or switched off.
            var askable = config.RemoteServers.Where(s => s.Enabled && s.Kind == ServerKind.Jellyfin).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var orphan in DiscoveredServersCache.Where(kv => !askable.Contains(kv.Value.DiscoveredViaFriendId)).Select(kv => kv.Key).ToList())
            {
                DiscoveredServersCache.TryRemove(orphan, out _);
            }

            return found;
        }

        /// <summary>
        /// Searches the cache <see cref="RefreshDiscoveredServersAsync"/> builds, by
        /// substring match on name or address.
        /// </summary>
        public IReadOnlyList<DiscoveredServer> SearchDiscoveredServers(string? query)
        {
            IEnumerable<DiscoveredServer> results = DiscoveredServersCache.Values;
            if (!string.IsNullOrWhiteSpace(query))
            {
                results = results.Where(s =>
                    s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || s.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            return results.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Handles an inbound friend request. Anonymous by design - the sender has no
        /// key for us yet, since issuing one is the whole point of this handshake.
        /// </summary>
        public async Task<FriendRequestResponse> ReceiveFriendRequestAsync(FriendRequestPayload payload, CancellationToken cancellationToken, string? sourceAddress = null)
        {
            var config = Plugin.Instance!.Configuration;

            if (payload == null
                || string.IsNullOrEmpty(payload.RequestId)
                || payload.RequestId.Length > 128
                || !ConfigValidator.IsValidServerUrl(payload.FromServerUrl)
                || payload.FromServerUrl.Length > 2048
                || string.IsNullOrEmpty(payload.ApiKeyForYou)
                || payload.ApiKeyForYou.Length > 128
                || payload.FromServerName?.Length > 200
                || payload.FromServerId?.Length > 128
                || payload.PoolId?.Length > 128
                || payload.PoolName?.Length > MaxPoolNameLength
                || payload.PoolOwnerFederationId?.Length > 128
                || payload.PoolOwnerName?.Length > 200)
            {
                return new FriendRequestResponse { Success = false, Message = "Malformed friend request." };
            }

            // This server only ever exchanges scoped federation tokens now, never
            // a real Jellyfin API key - a sender that doesn't declare support for
            // that (an old plugin version) would otherwise have ApiKeyForYou
            // treated as a federation token when it is actually a real,
            // full-admin-equivalent key, or vice versa on the other side. Neither
            // mixup is safe, so both sides simply need a compatible version -
            // rejected here, loudly, rather than silently mishandled.
            if (!payload.SupportsFederationToken)
            {
                return new FriendRequestResponse
                {
                    Success = false,
                    Message = "This server requires a compatible Federation plugin version on both sides (scoped federation tokens). Please upgrade the Federation plugin and try again."
                };
            }

            var fromUrl = payload.FromServerUrl.TrimEnd('/');

            var staleCutoff = DateTime.UtcNow - IncomingFriendRequestMaxAge;
            var swept = config.IncomingFriendRequests.RemoveAll(r => r.CreatedUtc < staleCutoff);

            // Idempotent: a retry of the same request (e.g. the sender's original
            // response was lost) should not create a duplicate pending entry, and
            // must not consume a slot against the pending cap.
            var existing = config.IncomingFriendRequests.FirstOrDefault(r => r.Id == payload.RequestId);
            if (existing == null)
            {
                var knownFriend = config.RemoteServers.FirstOrDefault(s =>
                    s.Kind == ServerKind.Jellyfin
                    && string.Equals(s.Url.TrimEnd('/'), fromUrl, StringComparison.OrdinalIgnoreCase));
                var pendingLimit = knownFriend == null ? MaxPendingIncomingFriendRequests - 5 : MaxPendingIncomingFriendRequests;
                var sourceFull = config.IncomingFriendRequests.Count(r =>
                    string.Equals(r.RemoteServerUrl.TrimEnd('/'), fromUrl, StringComparison.OrdinalIgnoreCase)) >= MaxIncomingRequestsPerSource;
                if (!AllowFriendRequestAttempt(sourceAddress)
                    || sourceFull
                    || config.IncomingFriendRequests.Count >= pendingLimit)
                {
                    if (swept > 0)
                    {
                        Plugin.Instance.SaveConfiguration();
                    }

                    return new FriendRequestResponse
                    {
                        Success = false,
                        Message = "Too many pending friend requests or attempts from this source. Try again later."
                    };
                }

                // Both admins may press Reconnect at the same time; either request
                // renews the keys on both sides, so only a duplicate incoming one is
                // refused.
                var pendingForUrl = config.IncomingFriendRequests.Any(r => string.Equals(r.RemoteServerUrl.TrimEnd('/'), fromUrl, StringComparison.OrdinalIgnoreCase));
                if ((knownFriend == null && AlreadyKnown(config, fromUrl)) || (knownFriend != null && pendingForUrl))
                {
                    if (swept > 0)
                    {
                        Plugin.Instance.SaveConfiguration();
                    }

                    return new FriendRequestResponse { Success = false, Message = "Already friends (or a pending request already exists) with this server." };
                }

                existing = new FriendRequest
                {
                    ReplacesServerId = knownFriend?.Id,
                    IdentityChanged = knownFriend != null
                        && !string.IsNullOrEmpty(knownFriend.FederationId)
                        && !string.Equals(knownFriend.FederationId, payload.FromServerId, StringComparison.Ordinal),
                    Id = payload.RequestId,
                    RemoteServerUrl = fromUrl,
                    RemoteServerName = string.IsNullOrEmpty(payload.FromServerName) ? fromUrl : payload.FromServerName,
                    RemoteServerId = payload.FromServerId ?? string.Empty,
                    ApiKey = payload.ApiKeyForYou,
                    CreatedUtc = DateTime.UtcNow,
                    PoolId = payload.PoolId,
                    PoolName = payload.PoolName,
                    PoolOwnerFederationId = payload.PoolOwnerFederationId,
                    PoolOwnerName = payload.PoolOwnerName,
                    PoolRoster = BoundPoolRoster(payload.PoolRoster)
                };
                config.IncomingFriendRequests.Add(existing);
            }

            // Best-effort authenticity check: confirms the sender itself really has a
            // matching outgoing request, so a third party can't plant a fake request
            // that merely *claims* to be from some other admin's server. Never blocks
            // the request from being stored - it only informs the admin's decision.
            // Private/loopback/metadata callback URLs stay pending and unverified
            // without a GET (this endpoint is anonymous; fetching them is SSRF).
            existing.Verified = await VerifyOutgoingRequestExistsAsync(fromUrl, payload.RequestId, cancellationToken).ConfigureAwait(false);

            Plugin.Instance.SaveConfiguration();

            return new FriendRequestResponse { Success = true, ServerName = _applicationHost.FriendlyName };
        }

        /// <summary>
        /// Serves this server's side of the verification check described on
        /// <see cref="ReceiveFriendRequestAsync"/>: confirms we really do have a
        /// pending outgoing request under this id.
        /// </summary>
        public bool HasOutgoingRequest(string requestId)
        {
            var config = Plugin.Instance!.Configuration;
            return config.OutgoingFriendRequests.Any(r => r.Id == requestId);
        }

        /// <summary>
        /// Admin-triggered: accepts an incoming friend request. Mints a fresh API key
        /// for the sender to use, adds them as a friend (RemoteServer) locally using
        /// the key they gave us, and confirms back to their server so they add us
        /// too. Confirmation is required to succeed, not best-effort: without it we
        /// can't tell whether the sender's original request is even still valid (they
        /// may have cancelled it), so adding them locally first and hoping would risk
        /// a friend entry with a key that's already been revoked on their end.
        /// </summary>
        public async Task<(bool Success, string Message)> AcceptFriendRequestAsync(string requestId, CancellationToken cancellationToken, FriendSharing? sharing = null)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.IncomingFriendRequests.FirstOrDefault(r => r.Id == requestId);
            if (entry == null)
            {
                return (false, "Friend request not found.");
            }

            var localUrl = ResolveLocalUrl();
            if (string.IsNullOrEmpty(localUrl))
            {
                return (false, "Could not determine this server's own public URL. Set it under Advanced settings first.");
            }

            var apiKey = FederationTokenAuth.GenerateToken();

            var payload = new FriendRequestPayload
            {
                RequestId = entry.Id,
                FromServerUrl = localUrl,
                FromServerName = _applicationHost.FriendlyName,
                FromServerId = GetOrCreateLocalFederationId(),
                ApiKeyForYou = apiKey,
                SupportsFederationToken = true
            };

            var replaced = string.IsNullOrEmpty(entry.ReplacesServerId)
                ? null
                : config.RemoteServers.FirstOrDefault(s => s.Id == entry.ReplacesServerId);
            var acceptUrl = (replaced?.Url ?? entry.RemoteServerUrl).TrimEnd('/');

            try
            {
                using var response = await PostAuthenticatedAsync(
                    $"{acceptUrl}/Plugins/Federation/Friends/Accept",
                    payload,
                    entry.ApiKey,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Could not confirm with {entry.RemoteServerName} (HTTP {(int)response.StatusCode}). They may have cancelled the request, are unreachable right now, or are running an incompatible Federation plugin version - try again shortly, or ask them to upgrade.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to confirm friend acceptance with {Url}", entry.RemoteServerUrl);
                return (false, $"Could not reach {entry.RemoteServerName}: {ex.Message}");
            }

            if (replaced != null)
            {
                // Only the keys and the reported identity change; the old keys stop
                // working at once. Sharing rules, libraries and synced titles stay.
                replaced.ApiKey = entry.ApiKey;
                replaced.IssuedApiKey = apiKey;
                if (!string.IsNullOrEmpty(entry.RemoteServerId))
                {
                    replaced.FederationId = entry.RemoteServerId;
                }

                config.IncomingFriendRequests.Remove(entry);
                Plugin.Instance.SaveConfiguration();
                _clientFactory.InvalidateAll();
                _logger.LogInformation("[Federation] Renewed the keys of friend {Name} after its reconnect request{Identity}", replaced.Name, entry.IdentityChanged ? " (its server identity changed)" : string.Empty);
                return (true, $"Reconnected with {replaced.Name}. Libraries, sharing settings and synced titles were kept.");
            }

            config.RemoteServers.Add(new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Name = entry.RemoteServerName,
                Url = entry.RemoteServerUrl,
                ApiKey = entry.ApiKey,
                IssuedApiKey = apiKey,
                FederationId = entry.RemoteServerId,
                Enabled = true,
                StreamingMode = StreamingMode.Direct,
                ShareAllLibraries = sharing?.ShareAll ?? true,
                SharedLibraryFolderIds = sharing?.FolderIds?.ToList() ?? new List<string>()
            });
            config.IncomingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();
            _clientFactory.InvalidateAll();

            if (!string.IsNullOrEmpty(entry.PoolId))
            {
                await AdoptPoolAndFanOutAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            return (true, $"You and {entry.RemoteServerName} are now federated.");
        }

        /// <summary>
        /// Claims a Plex-to-Jellyfin Companion connect code. The copied code is
        /// either a one-time claim against Companion's public (Funnel) URL, or a
        /// direct Plex remote/relay address. Either way the friend does not need
        /// to be on the Plex owner's Tailscale; LAN and 100.x tailnet addresses
        /// are rejected so a "connected" server is actually reachable.
        /// </summary>
        public Task<(bool Success, string Message, RemoteServer? Server)> ConnectPlexCompanionAsync(string? code, CancellationToken cancellationToken)
            => _plexCompanion.ConnectAsync(code, cancellationToken);

        /// <summary>
        /// Stores a Plex Companion share that was pushed by typing this
        /// server's address. The admin still has to Accept; the code is not
        /// applied until then.
        /// </summary>
        public (bool Success, string Message) ReceivePlexOffer(string? code)
            => _plexCompanion.ReceiveOffer(code);

        public Task<(bool Success, string Message, RemoteServer? Server)> AcceptPlexOfferAsync(string offerId, CancellationToken cancellationToken)
            => _plexCompanion.AcceptOfferAsync(offerId, cancellationToken);

        public (bool Success, string Message) RejectPlexOffer(string offerId)
            => _plexCompanion.RejectOffer(offerId);

        /// <summary>
        /// Admin-triggered: registers a non-Jellyfin consumer of our own catalog
        /// (<see cref="ServerKind.Companion"/>) - today, a Federation Companion
        /// instance a Plex-owning friend runs to import our content as
        /// <c>.strm</c> files. Unlike <see cref="AcceptFriendRequestAsync"/>
        /// there is no reciprocal server to hand this token to automatically
        /// (a Companion instance has no Federation plugin of its own to receive
        /// a handshake call), so this mints the token and hands back a
        /// connect code for the admin to copy to their friend directly - the
        /// same <c>{ url, token, name }</c> base64 shape Companion's own
        /// <c>/api/connect/generate</c> already produces, for symmetry.
        /// </summary>
        public (bool Success, string Message, string? ConnectCode) CreateCompanionFriend(string name, bool shareAllLibraries, List<string>? sharedLibraryFolderIds)
            => _plexCompanion.CreateReceiver(name, shareAllLibraries, sharedLibraryFolderIds);

        public (bool Success, string Message, string? ConnectCode) GetCompanionConnectCode(RemoteServer server)
            => _plexCompanion.GetConnectCode(server);

        public Task<string?> TryGetPlexCompanionVersionAsync(RemoteServer server, CancellationToken cancellationToken)
            => _plexCompanion.TryGetVersionAsync(server, cancellationToken);

        /// <summary>
        /// Backfills a missing <see cref="RemoteServer.FederationId"/> on an
        /// existing friend entry, generating and persisting one when empty.
        /// Streaming tokens are minted bound to the caller's FederationId and
        /// re-resolved through it at stream time (see
        /// <see cref="FederationController.IsStreamTokenAuthorized"/>) - an
        /// entry created before that binding existed (older friendships,
        /// Companion friends before 0.0.117) holds an empty id, which makes
        /// every token it ever mints unresolvable at stream time and therefore
        /// unplayable, while browsing and sync (which only need the
        /// IssuedApiKey) keep working. Called at token-mint time, so the entry
        /// heals itself on the friend's very next playback without any admin
        /// action; a no-op once the id is present.
        /// </summary>
        public void EnsureFederationId(RemoteServer server)
        {
            if (!string.IsNullOrEmpty(server.FederationId))
            {
                return;
            }

            server.FederationId = Guid.NewGuid().ToString();
            Plugin.Instance?.SaveConfiguration();
        }

        /// <summary>
        /// Admin-triggered: creates a new pool owned by this server, with this server
        /// as its sole initial member.
        /// </summary>
        public FederationPool CreatePool(string name)
        {
            var config = Plugin.Instance!.Configuration;
            var selfId = GetOrCreateLocalFederationId();
            var selfName = _applicationHost.FriendlyName;
            var selfUrl = ResolveLocalUrl();

            var pool = new FederationPool
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Pool" : name.Trim(),
                IsOwner = true,
                OwnerFederationId = selfId,
                OwnerName = selfName
            };
            pool.Members.Add(new PoolMember { FederationId = selfId, Name = selfName, Url = selfUrl });

            config.Pools.Add(pool);
            Plugin.Instance.SaveConfiguration();
            return pool;
        }

        /// <summary>
        /// Admin-triggered: adds a server into a pool this server already belongs
        /// to. The whole point of a pool is not re-doing the friend handshake for
        /// people you've already connected with one at a time - so if the target is
        /// already a friend, this just adds them to the pool roster locally and
        /// sends them a pool notice (no accept step, they're already trusted); only
        /// a genuinely new contact goes through the full friend-request handshake,
        /// carrying the pool's identity and current roster so accepting it also
        /// triggers them to connect to every other member - see
        /// <see cref="SendFriendRequestAsync"/> and <see cref="AdoptPoolAndFanOutAsync"/>.
        /// </summary>
        public async Task<(bool Success, string Message)> SendPoolInviteAsync(string poolId, string remoteUrl, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var pool = config.Pools.FirstOrDefault(p => p.Id == poolId);
            if (pool == null)
            {
                return (false, "Pool not found.");
            }

            var normalizedUrl = remoteUrl.TrimEnd('/');
            var existingFriend = config.RemoteServers.FirstOrDefault(s => string.Equals(s.Url.TrimEnd('/'), normalizedUrl, StringComparison.OrdinalIgnoreCase));
            if (existingFriend != null)
            {
                return await AddExistingFriendToPoolAsync(pool, existingFriend, cancellationToken).ConfigureAwait(false);
            }

            return await SendFriendRequestAsync(remoteUrl, cancellationToken, pool).ConfigureAwait(false);
        }

        /// <summary>
        /// Invites an already-known friend into a pool without repeating the full
        /// friend handshake - but still requires their admin to explicitly accept,
        /// same as any other pool introduction. Previously this added the friend to
        /// the pool immediately and only *notified* them afterward; that let one
        /// admin unilaterally enroll another server in a pool with zero consent on
        /// their side. Now it stages a <see cref="PoolInvite"/> on their end (see
        /// <see cref="ReceivePoolInviteNotice"/>) and only actually joins them to
        /// the pool once <see cref="AcceptPoolInviteAsync"/> confirms back.
        /// </summary>
        private async Task<(bool Success, string Message)> AddExistingFriendToPoolAsync(FederationPool pool, RemoteServer friend, CancellationToken cancellationToken)
        {
            var normalized = friend.Url.TrimEnd('/');
            if (pool.Members.Any(m => string.Equals(m.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"{friend.Name} is already in this pool.");
            }

            if (string.IsNullOrEmpty(friend.Url) || string.IsNullOrEmpty(friend.ApiKey))
            {
                return (false, $"No address/key on file for {friend.Name} yet.");
            }

            var companionInviteUrl = friend.Kind == ServerKind.Jellyfin ? null : _plexCompanion.GetPoolInviteUrl(friend);
            if (friend.Kind != ServerKind.Jellyfin && companionInviteUrl == null)
            {
                return (false, $"{friend.Name} is a Plex friend without an available Companion address, so a pool invite cannot be delivered. Install the Plex add-in, or ask them to enable Companion with Tailscale Funnel pointing to Companion rather than Plex, then reconnect.");
            }

            var config = Plugin.Instance!.Configuration;
            if (pool.Members.Count >= MaxPoolMembers)
            {
                return (false, $"This pool already has the maximum of {MaxPoolMembers} members.");
            }

            if (config.OutgoingPoolInvites.Count >= MaxOutgoingPoolInvites)
            {
                return (false, "Too many pool invitations are still pending.");
            }

            if (config.OutgoingPoolInvites.Any(i => i.PoolId == pool.Id && string.Equals(i.RemoteServerUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"{friend.Name} already has a pending invite to this pool.");
            }

            if (string.IsNullOrEmpty(friend.FederationId))
            {
                friend.FederationId = friend.Id;
            }

            if (string.IsNullOrEmpty(friend.IssuedApiKey))
            {
                friend.IssuedApiKey = FederationTokenAuth.GenerateToken();
            }

            var inviteId = Guid.NewGuid().ToString();
            var localUrl = ResolveLocalUrl();
            var pluginVersion = typeof(Plugin).Assembly.GetName().Version is { } ver
                ? $"{ver.Major}.{ver.Minor}.{ver.Build}"
                : string.Empty;
            try
            {
                var payload = new PoolInviteNoticePayload
                {
                    InviteId = inviteId,
                    FromFederationId = GetOrCreateLocalFederationId(),
                    PoolId = pool.Id,
                    PoolName = pool.Name,
                    OwnerFederationId = pool.OwnerFederationId,
                    OwnerName = pool.OwnerName,
                    Roster = pool.Members.ToList(),
                    IconBase64 = pool.IconBase64,
                    CallbackUrl = localUrl,
                    CallbackToken = friend.IssuedApiKey,
                    FederationPluginVersion = pluginVersion
                };

                var inviteUrl = friend.Kind == ServerKind.Jellyfin
                    ? $"{friend.Url.TrimEnd('/')}/Plugins/Federation/Pools/InviteNotice"
                    : companionInviteUrl!;
                var authToken = friend.ApiKey;

                using var response = await PostAuthenticatedAsync(
                    inviteUrl,
                    payload,
                    authToken,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var kind = friend.Kind == ServerKind.Jellyfin ? "Federation plugin" : "Plex Companion";
                    return (false, $"Could not invite {friend.Name} to the pool (HTTP {(int)response.StatusCode}). Check they're reachable and running a compatible {kind} version.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not invite {Name} to pool {Pool} (non-fatal)", friend.Name, pool.Name);
                return (false, $"Could not reach {friend.Name}: {ex.Message}");
            }

            config.OutgoingPoolInvites.Add(new PoolInvite
            {
                Id = inviteId,
                PoolId = pool.Id,
                PoolName = pool.Name,
                OwnerFederationId = pool.OwnerFederationId,
                OwnerName = pool.OwnerName,
                RemoteServerUrl = friend.Url,
                RemoteServerName = friend.Name,
                RemoteServerId = friend.FederationId
            });
            Plugin.Instance.SaveConfiguration();

            return (true, $"Invited {friend.Name} to the pool. Waiting for them to accept.");
        }

        /// <summary>
        /// Server-to-server: an already-known friend is inviting us into a pool. If
        /// we're already a member (they're just re-sending an up-to-date roster/icon
        /// for a pool we both already belong to), this is treated as an ordinary
        /// sync - no new consent needed, same trust boundary as
        /// <see cref="ReceivePoolNotice"/>. Only a pool genuinely new to us is
        /// staged as a <see cref="PoolInvite"/> for the admin to accept or reject.
        /// Sender identity is <paramref name="caller"/> (the token), not
        /// <c>payload.FromFederationId</c>.
        /// </summary>
        public Task ReceivePoolInviteNotice(RemoteServer caller, PoolInviteNoticePayload payload, CancellationToken cancellationToken)
        {
            if (caller == null || payload == null || string.IsNullOrEmpty(payload.InviteId) || string.IsNullOrEmpty(payload.PoolId))
            {
                return Task.CompletedTask;
            }

            if (!ClaimedFederationIdMatchesCaller(caller, payload.FromFederationId, "pool invite"))
            {
                return Task.CompletedTask;
            }

            var config = Plugin.Instance!.Configuration;
            var sender = caller;

            var existingPool = config.Pools.FirstOrDefault(p => p.Id == payload.PoolId);
            if (existingPool != null)
            {
                var senderUrl = sender.Url.TrimEnd('/');
                if (!existingPool.Members.Any(m =>
                        (!string.IsNullOrEmpty(sender.FederationId) && string.Equals(m.FederationId, sender.FederationId, StringComparison.OrdinalIgnoreCase))
                        || string.Equals(m.Url.TrimEnd('/'), senderUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("[Federation] Ignored a pool invite/sync from {Friend}: they are not a member of pool {PoolId}", sender.Name, payload.PoolId);
                    return Task.CompletedTask;
                }

                ApplyInboundPoolIcon(existingPool, payload.IconBase64);

                return AdoptPoolRosterAndFanOutAsync(
                    payload.PoolId,
                    payload.PoolName,
                    payload.OwnerFederationId,
                    payload.OwnerName,
                    sender.Url,
                    sender.FederationId,
                    sender.Name,
                    payload.Roster,
                    cancellationToken,
                    inbound: true);
            }

            if (config.IncomingPoolInvites.Any(i => i.Id == payload.InviteId))
            {
                return Task.CompletedTask;
            }

            // One friend can't fill the pending list (it is stored in the plugin config).
            if (config.IncomingPoolInvites.Count >= MaxPendingPoolInvites
                || config.IncomingPoolInvites.Count(i => string.Equals(i.RemoteServerId, sender.FederationId, StringComparison.Ordinal)) >= MaxPendingPoolInvitesPerFriend)
            {
                return Task.CompletedTask;
            }

            config.IncomingPoolInvites.Add(new PoolInvite
            {
                Id = payload.InviteId,
                PoolId = payload.PoolId,
                PoolName = TrimTo(payload.PoolName, MaxPoolNameLength) ?? payload.PoolId,
                OwnerFederationId = payload.OwnerFederationId ?? string.Empty,
                OwnerName = payload.OwnerName ?? sender.Name,
                RemoteServerUrl = sender.Url,
                RemoteServerName = sender.Name,
                RemoteServerId = sender.FederationId,
                Roster = BoundPoolRoster(payload.Roster),
                IconBase64 = NormalizePoolIcon(payload.IconBase64)
            });
            Plugin.Instance.SaveConfiguration();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Admin-triggered: accepts a pending pool invite from an already-known
        /// friend. Confirmation to the inviter is required to succeed, not
        /// best-effort - mirrors <see cref="AcceptFriendRequestAsync"/>'s reasoning:
        /// without it, the inviter never adds us to their own copy of the roster.
        /// </summary>
        public async Task<(bool Success, string Message)> AcceptPoolInviteAsync(string inviteId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.IncomingPoolInvites.FirstOrDefault(i => i.Id == inviteId);
            if (entry == null)
            {
                return (false, "Pool invite not found.");
            }

            if (config.Pools.Count >= MaxPools && config.Pools.All(p => p.Id != entry.PoolId))
            {
                return (false, $"This server already has the maximum of {MaxPools} pools.");
            }

            var existingPool = config.Pools.FirstOrDefault(p => p.Id == entry.PoolId);
            var normalizedTarget = entry.RemoteServerUrl.TrimEnd('/');
            if (existingPool != null
                && existingPool.Members.Count >= MaxPoolMembers
                && existingPool.Members.All(m => !string.Equals(m.Url.TrimEnd('/'), normalizedTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"This pool already has the maximum of {MaxPoolMembers} members.");
            }

            var friend = config.RemoteServers.FirstOrDefault(s => s.FederationId == entry.RemoteServerId);
            if (friend == null || string.IsNullOrEmpty(friend.ApiKey))
            {
                config.IncomingPoolInvites.Remove(entry);
                Plugin.Instance.SaveConfiguration();
                return (false, $"{entry.RemoteServerName} is no longer a connected friend.");
            }

            try
            {
                var payload = new PoolInviteResponsePayload { InviteId = entry.Id, FromFederationId = GetOrCreateLocalFederationId() };
                using var response = await PostAuthenticatedAsync(
                    $"{friend.Url.TrimEnd('/')}/Plugins/Federation/Pools/AcceptNotice",
                    payload,
                    friend.ApiKey,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Could not confirm with {entry.RemoteServerName} (HTTP {(int)response.StatusCode}). Try again shortly.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to confirm pool acceptance with {Url}", friend.Url);
                return (false, $"Could not reach {entry.RemoteServerName}: {ex.Message}");
            }

            config.IncomingPoolInvites.Remove(entry);
            Plugin.Instance.SaveConfiguration();

            await AdoptPoolRosterAndFanOutAsync(
                entry.PoolId,
                entry.PoolName,
                entry.OwnerFederationId,
                entry.OwnerName,
                entry.RemoteServerUrl,
                entry.RemoteServerId,
                entry.RemoteServerName,
                entry.Roster,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(entry.IconBase64))
            {
                var pool = config.Pools.FirstOrDefault(p => p.Id == entry.PoolId);
                if (pool != null)
                {
                    pool.IconBase64 = entry.IconBase64;
                    Plugin.Instance.SaveConfiguration();
                }
            }

            return (true, $"You joined {entry.PoolName}.");
        }

        /// <summary>
        /// Admin-triggered: rejects a pending pool invite from an already-known
        /// friend, and best-effort tells them so their outgoing list clears too.
        /// </summary>
        public async Task<(bool Success, string Message)> RejectPoolInviteAsync(string inviteId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.IncomingPoolInvites.FirstOrDefault(i => i.Id == inviteId);
            if (entry == null)
            {
                return (false, "Pool invite not found.");
            }

            config.IncomingPoolInvites.Remove(entry);
            Plugin.Instance.SaveConfiguration();

            var friend = config.RemoteServers.FirstOrDefault(s => s.FederationId == entry.RemoteServerId);
            if (friend != null && !string.IsNullOrEmpty(friend.ApiKey))
            {
                try
                {
                    var payload = new PoolInviteResponsePayload { InviteId = entry.Id, FromFederationId = GetOrCreateLocalFederationId() };
                    using var response = await PostAuthenticatedAsync(
                        $"{friend.Url.TrimEnd('/')}/Plugins/Federation/Pools/RejectNotice",
                        payload,
                        friend.ApiKey,
                        cancellationToken).ConfigureAwait(false);
                    _ = response;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not notify {Url} of a rejected pool invite (non-fatal)", friend.Url);
                }
            }

            return (true, "Pool invite rejected.");
        }

        /// <summary>
        /// Admin-triggered: cancels a pool invite this server sent before the other
        /// side responded.
        /// </summary>
        public Task<(bool Success, string Message)> CancelOutgoingPoolInviteAsync(string inviteId)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingPoolInvites.FirstOrDefault(i => i.Id == inviteId);
            if (entry == null)
            {
                return Task.FromResult((false, "Pool invite not found."));
            }

            config.OutgoingPoolInvites.Remove(entry);
            Plugin.Instance.SaveConfiguration();

            return Task.FromResult((true, "Pool invite cancelled."));
        }

        /// <summary>
        /// Server-to-server: the friend we invited into a pool has accepted -
        /// finally adds them to our own copy of the roster, and best-effort tells
        /// every other current member so the mesh learns about the newcomer without
        /// each of them having to sync independently.
        /// </summary>
        public bool HandlePoolAcceptNotice(RemoteServer caller, PoolInviteResponsePayload payload)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingPoolInvites.FirstOrDefault(i => i.Id == payload?.InviteId);
            if (entry == null || !MatchesPoolInviteTarget(caller, entry))
            {
                _logger.LogWarning("[Federation] Rejected a pool-accept callback from someone other than the invited friend");
                return false;
            }

            var pool = config.Pools.FirstOrDefault(p => p.Id == entry.PoolId);
            if (pool == null)
            {
                config.OutgoingPoolInvites.Remove(entry);
                Plugin.Instance.SaveConfiguration();
                return false;
            }

            var normalized = entry.RemoteServerUrl.TrimEnd('/');
            if (!pool.Members.Any(m => string.Equals(m.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                if (pool.Members.Count >= MaxPoolMembers)
                {
                    return false;
                }

                pool.Members.Add(new PoolMember { FederationId = entry.RemoteServerId, Name = entry.RemoteServerName, Url = entry.RemoteServerUrl });
            }

            config.OutgoingPoolInvites.Remove(entry);
            Plugin.Instance.SaveConfiguration();

            _ = BroadcastPoolNoticeAsync(pool, entry.RemoteServerId, CancellationToken.None);

            return true;
        }

        /// <summary>
        /// Server-to-server: the invited friend declined this specific pool invite.
        /// </summary>
        public bool HandlePoolRejectNotice(RemoteServer caller, string inviteId)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingPoolInvites.FirstOrDefault(i => i.Id == inviteId);
            if (entry == null || !MatchesPoolInviteTarget(caller, entry))
            {
                return false;
            }

            config.OutgoingPoolInvites.Remove(entry);
            Plugin.Instance.SaveConfiguration();
            return true;
        }

        private static bool MatchesPoolInviteTarget(RemoteServer caller, PoolInvite invite)
            => (!string.IsNullOrEmpty(invite.RemoteServerId)
                && string.Equals(caller.FederationId, invite.RemoteServerId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(caller.Url.TrimEnd('/'), invite.RemoteServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Admin-triggered: sets or clears a pool's icon and best-effort spreads it
        /// to every other current member via the same roster-sync notice used for
        /// membership changes - no central image host, purely peer-to-peer.
        /// </summary>
        public async Task<(bool Success, string Message)> SetPoolIconAsync(string poolId, string? iconBase64, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var pool = config.Pools.FirstOrDefault(p => p.Id == poolId);
            if (pool == null)
            {
                return (false, "Pool not found.");
            }

            var normalizedIcon = NormalizePoolIcon(iconBase64);
            if (!string.IsNullOrEmpty(iconBase64) && normalizedIcon == null)
            {
                return (false, "Icon must be a valid PNG image no larger than 150,000 base64 characters.");
            }

            pool.IconBase64 = normalizedIcon;
            Plugin.Instance.SaveConfiguration();

            await BroadcastPoolNoticeAsync(pool, excludeFederationId: null, cancellationToken).ConfigureAwait(false);

            return (true, "Pool icon updated.");
        }

        /// <summary>
        /// Best-effort tells every current member of <paramref name="pool"/> (other
        /// than <paramref name="excludeFederationId"/>, if given) the pool's current
        /// roster and icon, via the same already-trusted <c>Pools/Notice</c> channel
        /// used for any other in-pool sync. Failures are logged and skipped per
        /// member - a full picture converges over the next sync rather than
        /// depending on every member being reachable right now.
        /// </summary>
        private async Task BroadcastPoolNoticeAsync(FederationPool pool, string? excludeFederationId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var selfId = GetOrCreateLocalFederationId();

            foreach (var member in pool.Members.Where(m => m.FederationId != selfId && m.FederationId != excludeFederationId))
            {
                var friend = config.RemoteServers.FirstOrDefault(s => s.FederationId == member.FederationId);
                if (friend == null || string.IsNullOrEmpty(friend.ApiKey))
                {
                    continue;
                }

                try
                {
                    var payload = new PoolNoticePayload
                    {
                        FromFederationId = selfId,
                        PoolId = pool.Id,
                        PoolName = pool.Name,
                        OwnerFederationId = pool.OwnerFederationId,
                        OwnerName = pool.OwnerName,
                        Roster = pool.Members.ToList(),
                        IconBase64 = pool.IconBase64
                    };
                    using var response = await PostAuthenticatedAsync(
                        $"{friend.Url.TrimEnd('/')}/Plugins/Federation/Pools/Notice",
                        payload,
                        friend.ApiKey,
                        cancellationToken).ConfigureAwait(false);
                    _ = response;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Pool sync to {Name} failed (non-fatal - will converge on a future sync)", friend.Name);
                }
            }
        }

        /// <summary>
        /// Admin-triggered: adds an already-connected friend (picked from this
        /// server's own friend list, by id, rather than typed in as a URL again) to
        /// a pool. Thin wrapper around <see cref="AddExistingFriendToPoolAsync"/> so
        /// the UI can offer "add someone I already know" without re-collecting a URL.
        /// </summary>
        public async Task<(bool Success, string Message)> AddFriendToPoolAsync(string poolId, string remoteServerId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var pool = config.Pools.FirstOrDefault(p => p.Id == poolId);
            if (pool == null)
            {
                return (false, "Pool not found.");
            }

            var friend = config.RemoteServers.FirstOrDefault(s => s.Id == remoteServerId);
            if (friend == null)
            {
                return (false, "Friend not found.");
            }

            return await AddExistingFriendToPoolAsync(pool, friend, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Admin-triggered: removes this server's own membership record for a pool.
        /// Existing friendships formed through it are left alone - leaving a pool
        /// stops it introducing you to *new* members, it does not unfriend anyone.
        /// </summary>
        public bool LeavePool(string poolId)
        {
            var config = Plugin.Instance!.Configuration;
            var pool = config.Pools.FirstOrDefault(p => p.Id == poolId);
            if (pool == null)
            {
                return false;
            }

            config.Pools.Remove(pool);
            Plugin.Instance.SaveConfiguration();
            return true;
        }

        /// <summary>
        /// Called after accepting a pool-tagged friend request: records/updates this
        /// server's local copy of the pool and fans out to the rest of the roster -
        /// see <see cref="AdoptPoolRosterAndFanOutAsync"/>.
        /// </summary>
        private Task AdoptPoolAndFanOutAsync(FriendRequest entry, CancellationToken cancellationToken)
        {
            return AdoptPoolRosterAndFanOutAsync(
                entry.PoolId!,
                entry.PoolName,
                entry.PoolOwnerFederationId,
                entry.PoolOwnerName,
                entry.RemoteServerUrl,
                entry.RemoteServerId,
                entry.RemoteServerName,
                entry.PoolRoster,
                cancellationToken);
        }

        /// <summary>
        /// Records/updates this server's local copy of a pool (creating it on first
        /// contact) and, for every other member the roster told us about that we're
        /// not already connected to, sends a pool invite of our own so the mesh
        /// keeps forming. Shared by two entry points that both amount to "I just
        /// learned about this pool's roster from someone I'm already connected to":
        /// accepting a pool-tagged friend request (<see cref="AdoptPoolAndFanOutAsync"/>)
        /// and a pool notice from an existing friend who added us to a pool we
        /// weren't already in (<see cref="ReceivePoolNotice"/>) - the latter needs
        /// no new friend request of its own, since the two servers are already
        /// connected. Each introduction sent here is still an ordinary friend
        /// request or pool notice - the recipient's admin still has to click Accept
        /// for anyone genuinely new, same trust boundary as any direct friendship.
        /// A failure introducing any one member is logged and skipped rather than
        /// aborting the rest; a full picture converges over the next few pool syncs
        /// rather than depending on every hop succeeding in one pass.
        /// </summary>
        private async Task AdoptPoolRosterAndFanOutAsync(
            string poolId,
            string? poolName,
            string? ownerFederationId,
            string? ownerName,
            string reachedViaUrl,
            string? reachedViaFederationId,
            string? reachedViaName,
            List<PoolMember>? roster,
            CancellationToken cancellationToken,
            bool inbound = false)
        {
            var config = Plugin.Instance!.Configuration;
            var pool = config.Pools.FirstOrDefault(p => p.Id == poolId);
            if (pool == null)
            {
                if (config.Pools.Count >= MaxPools)
                {
                    _logger.LogWarning("[Federation] Ignored a new pool while the {Limit}-pool limit is reached", MaxPools);
                    return;
                }

                pool = new FederationPool
                {
                    Id = poolId,
                    Name = TrimTo(poolName, MaxPoolNameLength) ?? poolId,
                    IsOwner = false,
                    OwnerFederationId = ownerFederationId ?? string.Empty,
                    OwnerName = ownerName ?? reachedViaName ?? string.Empty
                };
                config.Pools.Add(pool);
            }

            var selfId = GetOrCreateLocalFederationId();
            var selfName = _applicationHost.FriendlyName;
            var selfUrl = ResolveLocalUrl();

            void AddMember(string? federationId, string? name, string? url, bool fromRoster = false)
            {
                if (string.IsNullOrEmpty(url))
                {
                    return;
                }

                // A roster comes from another server: only well-formed public
                // addresses are stored, and only already-known friends are contacted.
                if (fromRoster && !IsAutoIntroducibleUrl(url))
                {
                    _logger.LogDebug("[Federation] Ignored pool roster entry {Url}: not a valid public server address", url);
                    return;
                }

                var normalized = url.TrimEnd('/');
                if (pool.Members.Count >= MaxPoolMembers
                    || pool.Members.Any(m => string.Equals(m.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                pool.Members.Add(new PoolMember
                {
                    FederationId = TrimTo(federationId, 128) ?? string.Empty,
                    Name = TrimTo(name, 200) ?? normalized,
                    Url = normalized
                });
            }

            AddMember(selfId, selfName, selfUrl);
            AddMember(reachedViaFederationId, reachedViaName, reachedViaUrl);
            foreach (var member in (roster ?? new List<PoolMember>()).Take(MaxPoolRosterEntries))
            {
                AddMember(member?.FederationId, member?.Name, member?.Url, fromRoster: true);
            }

            Plugin.Instance.SaveConfiguration();

            var selfUrlNormalized = (selfUrl ?? string.Empty).TrimEnd('/');
            var reachedViaUrlNormalized = reachedViaUrl.TrimEnd('/');
            var toIntroduce = pool.Members
                .Where(m => !string.Equals(m.Url.TrimEnd('/'), selfUrlNormalized, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(m.Url.TrimEnd('/'), reachedViaUrlNormalized, StringComparison.OrdinalIgnoreCase)
                    && IsAutoIntroducibleUrl(m.Url)
                    && config.RemoteServers.Any(s => s.Enabled
                        && string.Equals(s.Url.TrimEnd('/'), m.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                .Take(MaxPoolRosterEntries)
                .ToList();

            if (!inbound)
            {
                await FanOutPoolIntroductionsAsync(pool.Id, toIntroduce, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Inbound notice: answer the friend now and introduce the rest in the
            // background, so a slow or hostile roster entry can't hold the request
            // open. The request's own address is pinned first because the
            // HttpContext is gone once this request ends.
            //
            // This instance is Scoped but outlives its request scope here on purpose:
            // every constructor dependency (IAuthenticationManager, IServerApplicationHost,
            // FederationLibraryManager, IHttpContextAccessor, IRemoteServerClientFactory) is
            // a singleton and nothing request-bound is touched after the pin, so no fresh
            // scope is needed. If a scoped dependency is ever added, resolve one in here.
            _pinnedLocalUrl = selfUrl;
            PendingFanOut = Task.Run(async () =>
            {
                try
                {
                    await FanOutPoolIntroductionsAsync(pool.Id, toIntroduce, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Background pool mesh introductions for pool {PoolId} failed (non-fatal - will retry on a future pool sync)", pool.Id);
                }
            });
        }

        /// <summary>Gets the most recent background pool fan-out (tests await it).</summary>
        internal Task PendingFanOut { get; private set; } = Task.CompletedTask;

        private async Task FanOutPoolIntroductionsAsync(string poolId, List<PoolMember> toIntroduce, CancellationToken cancellationToken)
        {
            foreach (var member in toIntroduce)
            {
                try
                {
                    var (success, message) = await SendPoolInviteAsync(poolId, member.Url, cancellationToken).ConfigureAwait(false);
                    if (!success)
                    {
                        _logger.LogWarning("[Federation] Pool mesh introduction to {Url} did not send: {Message}", member.Url, message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Pool mesh introduction to {Url} failed (non-fatal - will retry on a future pool sync)", member.Url);
                }
            }
        }

        /// <summary>
        /// Server-to-server: an existing member of a pool we're already in is
        /// syncing its current roster and/or icon (e.g. a new member just accepted,
        /// or the icon changed). No accept step needed - the two servers are already
        /// connected *and* already share this specific pool, so this is purely
        /// informational, same trust boundary as the rest of the friend system.
        /// A pool we don't already belong to is ignored here: introducing a
        /// genuinely new pool goes through <see cref="ReceivePoolInviteNotice"/>
        /// instead, which does require the admin to accept. Sender identity is
        /// <paramref name="caller"/> (the token), not <c>payload.FromFederationId</c>.
        /// </summary>
        public Task ReceivePoolNotice(RemoteServer caller, PoolNoticePayload payload, CancellationToken cancellationToken)
        {
            if (caller == null || payload == null || string.IsNullOrEmpty(payload.PoolId))
            {
                return Task.CompletedTask;
            }

            if (!ClaimedFederationIdMatchesCaller(caller, payload.FromFederationId, "pool notice"))
            {
                return Task.CompletedTask;
            }

            var config = Plugin.Instance!.Configuration;
            var sender = caller;

            var existingPool = config.Pools.FirstOrDefault(p => p.Id == payload.PoolId);
            if (existingPool == null)
            {
                _logger.LogDebug("[Federation] Ignored a pool sync notice for pool {PoolId} we're not a member of - introductions go through Pools/InviteNotice", payload.PoolId);
                return Task.CompletedTask;
            }

            var senderUrl = sender.Url.TrimEnd('/');
            if (!existingPool.Members.Any(m =>
                    (!string.IsNullOrEmpty(sender.FederationId) && string.Equals(m.FederationId, sender.FederationId, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(m.Url.TrimEnd('/'), senderUrl, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("[Federation] Ignored a pool sync notice from {Friend}: they are not a member of pool {PoolId}", sender.Name, payload.PoolId);
                return Task.CompletedTask;
            }

            ApplyInboundPoolIcon(existingPool, payload.IconBase64);

            return AdoptPoolRosterAndFanOutAsync(
                payload.PoolId,
                payload.PoolName,
                payload.OwnerFederationId,
                payload.OwnerName,
                sender.Url,
                sender.FederationId,
                sender.Name,
                payload.Roster,
                cancellationToken,
                inbound: true);
        }

        /// <summary>
        /// Admin-triggered: rejects an incoming friend request and best-effort tells
        /// the sender, so they can revoke the key they minted for us instead of
        /// leaving it dangling.
        /// </summary>
        public async Task<(bool Success, string Message)> RejectFriendRequestAsync(string requestId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.IncomingFriendRequests.FirstOrDefault(r => r.Id == requestId);
            if (entry == null)
            {
                return (false, "Friend request not found.");
            }

            config.IncomingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();

            try
            {
                using var response = await PostAuthenticatedAsync(
                    $"{entry.RemoteServerUrl.TrimEnd('/')}/Plugins/Federation/Friends/Reject",
                    new FriendRejectPayload { RequestId = entry.Id },
                    entry.ApiKey,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not notify {Url} of a rejected friend request (non-fatal)", entry.RemoteServerUrl);
            }

            return (true, "Friend request rejected.");
        }

        /// <summary>
        /// Admin-triggered: cancels a friend request this server sent before the
        /// other side responded. The token minted for it needs no separate
        /// revocation - see <see cref="Configuration.RemoteServer.IssuedApiKey"/>.
        /// </summary>
        public Task<(bool Success, string Message)> CancelOutgoingFriendRequestAsync(string requestId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == requestId);
            if (entry == null)
            {
                return Task.FromResult((false, "Friend request not found."));
            }

            config.OutgoingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();

            return Task.FromResult((true, "Friend request cancelled."));
        }

        /// <summary>
        /// Handles the accept callback: the other server has accepted our earlier
        /// outgoing request and is giving us a token to use pulling from them.
        /// Returns false when this should be rejected (see
        /// <see cref="FriendRequestPayload.SupportsFederationToken"/>'s doc
        /// comment) so <c>FederationController.ReceiveFriendAccept</c> can answer
        /// with a non-2xx status - the accepting side's own
        /// <see cref="AcceptFriendRequestAsync"/> already surfaces a clear error
        /// to its admin on anything but success, so this is what actually stops
        /// an incompatible acceptance from creating a broken friend entry here.
        /// </summary>
        public bool HandleAcceptCallback(FriendRequestPayload payload, string? presentedCredential = null)
        {
            if (payload == null || !payload.SupportsFederationToken || string.IsNullOrEmpty(payload.ApiKeyForYou))
            {
                _logger.LogWarning("[Federation] Rejected a malformed or incompatible friend-accept callback");
                return false;
            }

            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == payload.RequestId);
            if (entry == null
                || string.IsNullOrEmpty(entry.ApiKey)
                || presentedCredential == null
                || !FederationTokenAuth.FixedTimeEquals(entry.ApiKey, presentedCredential))
            {
                _logger.LogWarning("[Federation] Rejected a friend-accept callback without the pending request credential");
                return false;
            }

            var reconnected = string.IsNullOrEmpty(entry.ReplacesServerId)
                ? null
                : config.RemoteServers.FirstOrDefault(s => s.Id == entry.ReplacesServerId);
            if (reconnected != null)
            {
                // Our reconnect request was accepted: renew the keys of the existing
                // entry. Its stored address is kept; an address in the callback is
                // never trusted for an existing friend.
                reconnected.ApiKey = payload.ApiKeyForYou;
                reconnected.IssuedApiKey = entry.ApiKey;
                if (!string.IsNullOrEmpty(payload.FromServerId))
                {
                    reconnected.FederationId = payload.FromServerId;
                }

                config.OutgoingFriendRequests.Remove(entry);
                Plugin.Instance.SaveConfiguration();
                _clientFactory.InvalidateAll();
                _logger.LogInformation("[Federation] {Name} accepted the reconnect request; the friendship's keys were renewed", reconnected.Name);
                return true;
            }

            var memberName = string.IsNullOrEmpty(payload.FromServerName) ? entry.RemoteServerName : payload.FromServerName;

            // The address we sent the request to is the one we trust; the callback
            // may not point the new friend (and its key) somewhere else.
            var memberUrl = entry.RemoteServerUrl.TrimEnd('/');
            if (!string.IsNullOrEmpty(payload.FromServerUrl)
                && !string.Equals(payload.FromServerUrl.TrimEnd('/'), memberUrl, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[Federation] Accept callback for request {RequestId} claimed address {Claimed}; using the address the request was sent to ({Url})",
                    payload.RequestId,
                    payload.FromServerUrl,
                    memberUrl);
            }

            var targetPool = string.IsNullOrEmpty(entry.PoolId) ? null : config.Pools.FirstOrDefault(p => p.Id == entry.PoolId);
            if (targetPool != null
                && targetPool.Members.Count >= MaxPoolMembers
                && targetPool.Members.All(m => !string.Equals(m.Url.TrimEnd('/'), memberUrl, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            config.RemoteServers.Add(new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Name = memberName,
                Url = memberUrl,
                ApiKey = payload.ApiKeyForYou,
                IssuedApiKey = entry.ApiKey,
                FederationId = payload.FromServerId ?? string.Empty,
                Enabled = true,
                StreamingMode = StreamingMode.Direct,
                // Chosen in the friend request dialog before sending; the friend
                // could not see anything before accepting.
                ShareAllLibraries = entry.ShareAllLibraries ?? string.IsNullOrEmpty(entry.PoolId),
                SharedLibraryFolderIds = entry.SharedLibraryFolderIds?.ToList() ?? new List<string>()
            });

            if (!string.IsNullOrEmpty(entry.PoolId))
            {
                var pool = config.Pools.FirstOrDefault(p => p.Id == entry.PoolId);
                var normalized = memberUrl.TrimEnd('/');
                if (pool != null && !pool.Members.Any(m => string.Equals(m.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    pool.Members.Add(new PoolMember { FederationId = payload.FromServerId ?? string.Empty, Name = memberName, Url = memberUrl });
                }
            }

            config.OutgoingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();
            _clientFactory.InvalidateAll();
            return true;
        }

        /// <summary>
        /// Handles the reject callback: the other server declined our request.
        /// The token we minted for them needs no separate revocation - see
        /// <see cref="Configuration.RemoteServer.IssuedApiKey"/> - dropping the
        /// now-unused <see cref="FriendRequest"/> entry is enough.
        /// </summary>
        public bool HandleRejectCallbackAsync(string requestId, string? presentedCredential = null)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == requestId);
            if (entry == null
                || string.IsNullOrEmpty(entry.ApiKey)
                || presentedCredential == null
                || !FederationTokenAuth.FixedTimeEquals(entry.ApiKey, presentedCredential))
            {
                return false;
            }

            config.OutgoingFriendRequests.Remove(entry);

            var declinedUrl = entry.RemoteServerUrl.TrimEnd('/');
            config.DeclinedDiscoveryCandidates.RemoveAll(d => string.Equals(d.Url, declinedUrl, StringComparison.OrdinalIgnoreCase));
            config.DeclinedDiscoveryCandidates.Add(new DeclinedDiscoveryCandidate { Url = declinedUrl, UntilUtc = DateTime.UtcNow + DeclinedCandidateTtl });
            Plugin.Instance.SaveConfiguration();
            return true;
        }

        /// <summary>
        /// Admin-triggered: sets which of this server's own local libraries a
        /// specific friend can see, plus a per-friend list of specific items that
        /// are never shared with them regardless of that scope. Purely local
        /// state under the federation-token model - <see cref="FederationPeerAccessService"/>
        /// enforces <see cref="RemoteServer.ShareAllLibraries"/>/
        /// <see cref="RemoteServer.SharedLibraryFolderIds"/>/<see cref="RemoteServer.ExcludedItemIds"/>
        /// itself, server-side, on every <c>Peer/*</c> request, so there is
        /// nothing to notify the friend of and nothing that requires a Jellyfin
        /// user account to enforce. (Previously this required picking a
        /// dedicated local Jellyfin user and pushing a "query as this user now"
        /// notice to the friend - that only made sense when a friend queried
        /// Jellyfin's own native per-user REST API directly; friends no longer do
        /// that at all. See the deleted <c>ApplySharePolicyAsync</c>/
        /// <c>ReceiveSharedUserUpdate</c> for that history, in git blame.)
        /// </summary>
        public Task<(bool Success, string Message)> UpdateFriendSharingAsync(
            string remoteServerId,
            bool shareAll,
            List<string> folderIds,
            List<string>? excludedItemIds = null)
        {
            var config = Plugin.Instance!.Configuration;
            var server = config.RemoteServers.FirstOrDefault(s => s.Id == remoteServerId);
            if (server == null)
            {
                return Task.FromResult((false, "Friend not found."));
            }

            server.ShareAllLibraries = shareAll;
            server.SharedLibraryFolderIds = folderIds ?? new List<string>();
            server.ExcludedItemIds = excludedItemIds ?? new List<string>();
            Plugin.Instance.SaveConfiguration();

            return Task.FromResult((true, "Sharing updated."));
        }

        /// <summary>
        /// Admin-triggered: sets (or clears, when <paramref name="rule"/> is null) a
        /// per-remote-user override on <paramref name="remoteServerId"/> for one of
        /// that friend's own local users, and pushes the friend's complete
        /// <see cref="RemoteServer.RemoteUserAccessRules"/> list to them so their
        /// plugin can enforce it locally against their own users (this server has no
        /// visibility into which of a friend's users is browsing/streaming at any
        /// given moment - see <see cref="RemoteAccessControlService"/>). Mirrors the
        /// shape of <see cref="UpdateFriendSharingAsync"/>: local state is saved
        /// first regardless of whether the friend can be reached, so the admin's
        /// change always sticks even if the push fails.
        /// </summary>
        public async Task<(bool Success, string Message)> SetRemoteUserAccessRuleAsync(
            string remoteServerId,
            RemoteUserAccessRule? rule,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var server = config.RemoteServers.FirstOrDefault(s => s.Id == remoteServerId);
            if (server == null)
            {
                return (false, "Friend not found.");
            }

            if (rule == null || string.IsNullOrWhiteSpace(rule.RemoteUserId))
            {
                return (false, "A remote user id is required.");
            }

            var updated = server.RemoteUserAccessRules
                .Where(r => !string.Equals(r.RemoteUserId, rule.RemoteUserId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var hasExtraRestrictions = !string.IsNullOrWhiteSpace(rule.MaxAllowedRating) || !rule.AllowDownload || (rule.BlockedItemIds != null && rule.BlockedItemIds.Count > 0);
            if (rule.Mode != RemoteUserAccessMode.AllLibraries || hasExtraRestrictions)
            {
                updated.Add(rule);
            }

            if (!ValidRemoteUserRules(updated))
            {
                return (false, "The rule set is too large or contains invalid values.");
            }

            server.RemoteUserAccessRules = updated;
            Plugin.Instance.SaveConfiguration();

            if (string.IsNullOrEmpty(server.Url) || string.IsNullOrEmpty(server.ApiKey))
            {
                return (true, "Saved locally, but this friend has no address/key on file to notify.");
            }

            try
            {
                var payload = new RemoteUserAccessRulesPayload
                {
                    FromFederationId = GetOrCreateLocalFederationId(),
                    Rules = server.RemoteUserAccessRules
                };
                using var response = await PostAuthenticatedAsync(
                    $"{server.Url.TrimEnd('/')}/Plugins/Federation/Friends/RemoteUserRules",
                    payload,
                    server.ApiKey,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (true, $"Saved locally, but could not notify {server.Name} (HTTP {(int)response.StatusCode}) - they will keep enforcing their old copy until they resync.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not notify {Name} of an updated remote-user access rule (non-fatal)", server.Name);
                return (true, $"Saved locally, but could not reach {server.Name} - they will keep enforcing their old copy until they resync.");
            }

            return (true, "Saved.");
        }

        /// <summary>
        /// Server-to-server: a friend we already share content with is telling us
        /// the complete, current list of per-remote-user overrides they have
        /// configured for our own local users - the counterpart to
        /// <see cref="SetRemoteUserAccessRuleAsync"/> on their side. Replaces (not
        /// merges) our stored copy, since the sender always pushes its full list.
        /// Applied to <paramref name="caller"/> (the token), not whoever
        /// <c>payload.FromFederationId</c> names.
        /// </summary>
        public RemoteUserRulesUpdateResult ReceiveRemoteUserAccessRules(RemoteServer caller, RemoteUserAccessRulesPayload payload)
        {
            if (caller == null || payload == null
                || !ClaimedFederationIdMatchesCaller(caller, payload.FromFederationId, "remote-user access rules"))
            {
                return RemoteUserRulesUpdateResult.Invalid;
            }

            var rules = payload.Rules ?? new List<RemoteUserAccessRule>();
            if (!ValidRemoteUserRules(rules))
            {
                return RemoteUserRulesUpdateResult.TooLarge;
            }

            if (JsonSerializer.Serialize(rules) == JsonSerializer.Serialize(caller.FriendUserAccessRules ?? new List<RemoteUserAccessRule>()))
            {
                return RemoteUserRulesUpdateResult.Unchanged;
            }

            caller.FriendUserAccessRules = rules;
            Plugin.Instance!.SaveConfiguration();
            _logger.LogInformation("[Federation] {Name} updated their per-user access rules for us ({Count} rule(s))", caller.Name, caller.FriendUserAccessRules.Count);
            return RemoteUserRulesUpdateResult.Changed;
        }

        internal static bool ValidRemoteUserRules(List<RemoteUserAccessRule> rules)
        {
            if (rules.Count > 100
                || rules.Any(r => r == null)
                || rules.Sum(r => (r.ItemIds?.Count ?? 0) + (r.LibraryFolderIds?.Count ?? 0) + (r.BlockedItemIds?.Count ?? 0)) > 5000)
            {
                return false;
            }

            return rules.All(r => !string.IsNullOrWhiteSpace(r.RemoteUserId)
                && r.RemoteUserId.Length <= 128
                && (r.RemoteUserName == null || r.RemoteUserName.Length <= 200)
                && (r.MaxAllowedRating == null || r.MaxAllowedRating.Length <= 50)
                && Enum.IsDefined(r.Mode)
                && ValidRuleIds(r.ItemIds)
                && ValidRuleIds(r.LibraryFolderIds)
                && ValidRuleIds(r.BlockedItemIds));
        }

        private static bool ValidRuleIds(List<string>? ids)
            => ids == null || (ids.Count <= 1000 && ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128));

        /// <summary>
        /// True when the body federation id is absent or matches the authenticated
        /// caller's stored id. A mismatch is ignored: a valid token for one friend
        /// must not act as another friend.
        /// </summary>
        private bool ClaimedFederationIdMatchesCaller(RemoteServer caller, string? claimedFederationId, string action)
        {
            if (string.IsNullOrEmpty(claimedFederationId) || string.IsNullOrEmpty(caller.FederationId))
            {
                return true;
            }

            if (string.Equals(claimedFederationId, caller.FederationId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogWarning("[Federation] Ignored {Action} whose FromFederationId {ClaimedId} did not match authenticated caller {CallerId}", action, claimedFederationId, caller.FederationId);
            return false;
        }

        private static string? NormalizePoolIcon(string? iconBase64)
        {
            if (string.IsNullOrEmpty(iconBase64))
            {
                return null;
            }

            if (iconBase64.Length > MaxPoolIconBase64Length)
            {
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(iconBase64);
                ReadOnlySpan<byte> pngSignature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
                return bytes.Length > pngSignature.Length && bytes.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature)
                    ? Convert.ToBase64String(bytes)
                    : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static void ApplyInboundPoolIcon(FederationPool pool, string? iconBase64)
        {
            var normalized = NormalizePoolIcon(iconBase64);
            if (normalized != null)
            {
                pool.IconBase64 = normalized;
            }
        }

        private static bool AlreadyKnown(PluginConfiguration config, string url)
        {
            var normalized = url.TrimEnd('/');
            return config.RemoteServers.Any(s => string.Equals(s.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
                || config.OutgoingFriendRequests.Any(r => string.Equals(r.RemoteServerUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
                || config.IncomingFriendRequests.Any(r => string.Equals(r.RemoteServerUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> VerifyOutgoingRequestExistsAsync(string remoteUrl, string requestId, CancellationToken cancellationToken)
        {
            // Unauthenticated inbound path only. Admin SendFriendRequestAsync still
            // POSTs to LAN/Tailscale friends via SharedHttpClient.
            if (ConfigValidator.IsPrivateOrLoopbackHost(remoteUrl))
            {
                _logger.LogDebug("[Federation] Skipping friend-request origin verify for private/loopback/metadata host {Url}", remoteUrl);
                return false;
            }

            try
            {
                using var response = await VerifyHttpClient.GetAsync(
                    $"{remoteUrl}/Plugins/Federation/Friends/Outgoing/{Uri.EscapeDataString(requestId)}",
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not verify friend request origin at {Url} (non-fatal - request stays pending, just unverified)", remoteUrl);
                return false;
            }
        }

        private async Task<string> CreateApiKeyAsync(string name)
        {
            await _authManager.CreateApiKey(name).ConfigureAwait(false);
            var keys = await _authManager.GetApiKeys().ConfigureAwait(false);
            var match = keys
                .Where(k => k.AppName == name)
                .OrderByDescending(k => k.DateCreated)
                .FirstOrDefault();

            if (match == null)
            {
                throw new InvalidOperationException($"API key '{name}' was created but could not be read back.");
            }

            return match.AccessToken;
        }

        /// <summary>
        /// Best-effort tells <paramref name="server"/> that this friendship is
        /// being removed, so their side auto-removes it too instead of silently
        /// continuing to pull this server's content until an admin notices and
        /// manually unfriends back - previously removing a friend was entirely
        /// one-sided: nothing here ever reached the other side at all, so a real
        /// unfriend needed both admins to separately click remove.
        /// <see cref="RemoteServer.IssuedApiKey"/> (the token this server minted
        /// for them) needs no separate revocation call - the caller is about to
        /// delete this <see cref="RemoteServer"/> entry entirely, and
        /// <see cref="FederationTokenAuth.ResolveCaller"/> only ever matches a
        /// token against a currently-configured friend, so their access is cut
        /// immediately the moment the entry is gone, even if they're offline or
        /// on a plugin version too old to understand this notification.
        /// </summary>
        public async Task NotifyAndRevokeOnUnfriendAsync(RemoteServer server, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(server.Url) || string.IsNullOrEmpty(server.ApiKey))
            {
                return;
            }

            try
            {
                var payload = new UnfriendPayload { FromFederationId = GetOrCreateLocalFederationId() };
                using var response = await PostAuthenticatedAsync(
                    $"{server.Url.TrimEnd('/')}/Plugins/Federation/Friends/Unfriend",
                    payload,
                    server.ApiKey,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[Federation] Could not notify {Name} that this friendship was removed (HTTP {StatusCode}) - they will keep this server listed until they notice and remove it themselves",
                        server.Name,
                        (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not notify {Name} that this friendship was removed (non-fatal)", server.Name);
            }
        }

        /// <summary>
        /// Server-to-server: a friend is telling us they removed this friendship
        /// on their side, so we should remove it here too rather than keep pulling
        /// their content from a relationship they've already ended. Matched by
        /// federation id, same as the other server-to-server notifications.
        /// Returns the matching local server entry (for the caller to run the
        /// same reconciliation/cache cleanup <c>DeleteServer</c> does), or null if
        /// no matching friend was found.
        /// </summary>
        public RemoteServer? FindByFederationId(string? federationId)
        {
            if (string.IsNullOrEmpty(federationId))
            {
                return null;
            }

            var config = Plugin.Instance!.Configuration;
            return config.RemoteServers.FirstOrDefault(s => string.Equals(s.FederationId, federationId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves this server's own public URL the same way
        /// <c>FederationMediaSourceProvider</c> does for Proxy streaming: an explicit
        /// config override when set, otherwise derived from the current incoming
        /// request. Friend requests need this so the other side has an address to
        /// call back on.
        /// </summary>
        private string? _pinnedLocalUrl;

        private string ResolveLocalUrl()
        {
            if (!string.IsNullOrEmpty(_pinnedLocalUrl))
            {
                return _pinnedLocalUrl;
            }

            var configured = _federationManager.GetLocalServerUrl();
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            var request = _httpContextAccessor.HttpContext?.Request;
            if (request == null)
            {
                return string.Empty;
            }

            var derived = $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

            // An admin managing Jellyfin from their own LAN (very common - even a
            // publicly-tunnelled server's admin dashboard is usually opened over the
            // LAN, not the public URL) means request.Host here is a private address.
            // Silently sending that to a friend as "here's how to reach me" is what
            // actually broke a real friend connection: the friend could never sync
            // (or stream in Direct mode) from an address only reachable on this
            // server's own network. Treated as "unresolvable" here so the existing
            // caller-side failure path ("Could not determine this server's own public
            // URL...") fires instead of silently poisoning the friend's config -
            // exactly like the no-request-context case just above.
            if (ConfigValidator.IsPrivateOrLoopbackHost(derived))
            {
                _logger.LogWarning(
                    "[Federation] Auto-detected server URL {Url} is a private/loopback address - refusing to send it to a friend. Set 'This server's public URL' under Advanced settings.",
                    derived);
                return string.Empty;
            }

            return derived;
        }

        internal const int MaxPoolRosterEntries = 50;
        internal const int MaxPoolMembers = 250;
        internal const int MaxPools = 100;
        internal const int MaxPendingPoolInvites = 100;
        internal const int MaxPendingPoolInvitesPerFriend = 10;
        internal const int MaxOutgoingPoolInvites = 100;
        internal const int MaxOutgoingFriendRequests = 100;
        internal const int MaxIncomingRequestsPerSource = 2;
        internal const int MaxPoolNameLength = 100;

        private static string? TrimTo(string? value, int max)
            => value != null && value.Length > max ? value[..max] : value;

        private static List<PoolMember> BoundPoolRoster(List<PoolMember>? roster)
            => (roster ?? new List<PoolMember>()).Take(MaxPoolRosterEntries).Select(member => new PoolMember
            {
                FederationId = TrimTo(member?.FederationId, 128) ?? string.Empty,
                Name = TrimTo(member?.Name, 200) ?? string.Empty,
                Url = TrimTo(member?.Url, 2048) ?? string.Empty
            }).ToList();

        private static bool AllowFriendRequestAttempt(string? sourceAddress)
        {
            if (string.IsNullOrEmpty(sourceAddress))
            {
                return true;
            }

            var now = DateTime.UtcNow;
            var attempts = FriendRequestAttempts.GetOrAdd(sourceAddress, _ => new Queue<DateTime>());
            lock (attempts)
            {
                while (attempts.Count > 0 && now - attempts.Peek() > TimeSpan.FromMinutes(1))
                {
                    attempts.Dequeue();
                }

                if (attempts.Count >= 5)
                {
                    return false;
                }

                attempts.Enqueue(now);
            }

            if (FriendRequestAttempts.Count > 1024)
            {
                foreach (var pair in FriendRequestAttempts)
                {
                    lock (pair.Value)
                    {
                        if (pair.Value.Count == 0 || now - pair.Value.Last() > TimeSpan.FromMinutes(1))
                        {
                            FriendRequestAttempts.TryRemove(pair.Key, out _);
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Addresses another server handed us are only contacted automatically when
        /// they are well-formed and not loopback/LAN/link-local/metadata (Tailscale
        /// addresses are fine, friends often run over a tailnet), so a
        /// roster or friends list can't aim this server at its own network.
        /// </summary>
        internal static bool IsAutoIntroducibleUrl(string? url)
            => ConfigValidator.IsValidServerUrl(url) && !ConfigValidator.IsBlockedForAutoContact(url);

        private static StringContent JsonContent(object payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        /// <summary>
        /// POSTs with a scoped federation token (see <see cref="FederationTokenAuth"/>).
        /// Established friends use the token they issued this server; accept/reject
        /// callbacks use the credential issued for that pending handshake. The token
        /// satisfies nothing on Jellyfin's own native auth.
        /// </summary>
        private static async Task<HttpResponseMessage> PostAuthenticatedAsync(string url, object payload, string apiKey, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(payload);
            return await TokenRedirect.SendAsync(
                TokenHttpClient,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    request.Headers.TryAddWithoutValidation(FederationTokenAuth.Header, apiKey);
                    return request;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wire payload for sending/accepting a friend request.
    /// </summary>
    public class FriendRequestPayload
    {
        /// <summary>Gets or sets the id shared by both sides of the request.</summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>Gets or sets the sender's own public URL.</summary>
        public string FromServerUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets the sender's display name.</summary>
        public string FromServerName { get; set; } = string.Empty;

        /// <summary>Gets or sets the sender's persistent federation id.</summary>
        public string FromServerId { get; set; } = string.Empty;

        /// <summary>Gets or sets the federation token the sender minted for the recipient to use.</summary>
        public string ApiKeyForYou { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the sender's plugin understands
        /// the scoped federation-token model (see <see cref="Configuration.RemoteServer.ApiKey"/>)
        /// rather than the old real-Jellyfin-API-key exchange. Defaults false, so
        /// an old plugin version's payload (which never sets this) is
        /// unambiguously distinguishable from a new one's. A new-version server
        /// receiving a request/accept without this set rejects it outright rather
        /// than silently treating a real API key as a scoped token or vice versa -
        /// see <see cref="FederationFriendService.ReceiveFriendRequestAsync"/> and
        /// <see cref="FederationFriendService.HandleAcceptCallback"/>. Both sides
        /// need a compatible plugin version; there is no dual-protocol fallback.
        /// </summary>
        public bool SupportsFederationToken { get; set; }

        /// <summary>
        /// Gets or sets the pool this request is introducing the recipient to, or
        /// null for an ordinary direct friend request. See
        /// <see cref="FederationFriendService.SendPoolInviteAsync"/>.
        /// </summary>
        public string? PoolId { get; set; }

        /// <summary>Gets or sets the pool's display name.</summary>
        public string? PoolName { get; set; }

        /// <summary>Gets or sets the persistent federation id of the pool's owner.</summary>
        public string? PoolOwnerFederationId { get; set; }

        /// <summary>Gets or sets the pool owner's display name.</summary>
        public string? PoolOwnerName { get; set; }

        /// <summary>Gets or sets the pool's membership as known by the sender when this request was sent.</summary>
        public List<PoolMember>? PoolRoster { get; set; }
    }

    /// <summary>
    /// Response to a <see cref="FriendRequestPayload"/> POST.
    /// </summary>
    public class FriendRequestResponse
    {
        /// <summary>Gets or sets a value indicating whether the request was accepted for processing.</summary>
        public bool Success { get; set; }

        /// <summary>Gets or sets a human-readable message (used on failure).</summary>
        public string? Message { get; set; }

        /// <summary>Gets or sets the responding server's display name.</summary>
        public string? ServerName { get; set; }
    }

    /// <summary>
    /// Wire payload for rejecting a friend request.
    /// </summary>
    public class FriendRejectPayload
    {
        /// <summary>Gets or sets the id of the request being rejected.</summary>
        public string RequestId { get; set; } = string.Empty;
    }


    /// <summary>
    /// Wire payload telling an already-known friend this friendship has been
    /// removed on the sender's side.
    /// </summary>
    public class UnfriendPayload
    {
        /// <summary>Gets or sets the sender's persistent federation id.</summary>
        public string FromFederationId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Wire payload telling an already-known friend the complete, current list of
    /// per-remote-user overrides configured for their own local users - see
    /// <see cref="FederationFriendService.SetRemoteUserAccessRuleAsync"/>.
    /// </summary>
    public class RemoteUserAccessRulesPayload
    {
        /// <summary>Gets or sets the sender's persistent federation id.</summary>
        public string FromFederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the sender's complete, current rule list.</summary>
        public List<RemoteUserAccessRule>? Rules { get; set; }
    }

    /// <summary>
    /// Wire payload telling an already-known friend about a pool - either that
    /// they've been added to one, or an updated roster for one they're already in.
    /// See <see cref="FederationFriendService.ReceivePoolNotice"/>.
    /// </summary>
    public class PoolNoticePayload
    {
        /// <summary>Gets or sets the sender's persistent federation id.</summary>
        public string FromFederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the pool's id.</summary>
        public string PoolId { get; set; } = string.Empty;

        /// <summary>Gets or sets the pool's display name.</summary>
        public string? PoolName { get; set; }

        /// <summary>Gets or sets the persistent federation id of the pool's owner.</summary>
        public string? OwnerFederationId { get; set; }

        /// <summary>Gets or sets the pool owner's display name.</summary>
        public string? OwnerName { get; set; }

        /// <summary>Gets or sets the pool's membership as known by the sender.</summary>
        public List<PoolMember>? Roster { get; set; }

        /// <summary>Gets or sets the pool's current icon, if any.</summary>
        public string? IconBase64 { get; set; }
    }

    /// <summary>
    /// Wire payload inviting an already-known friend into a pool - see
    /// <see cref="FederationFriendService.AddExistingFriendToPoolAsync"/> and
    /// <see cref="FederationFriendService.ReceivePoolInviteNotice"/>.
    /// </summary>
    public class PoolInviteNoticePayload
    {
        /// <summary>Gets or sets the id shared by both sides of the invite.</summary>
        public string InviteId { get; set; } = string.Empty;

        /// <summary>Gets or sets the sender's persistent federation id.</summary>
        public string FromFederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the pool's id.</summary>
        public string PoolId { get; set; } = string.Empty;

        /// <summary>Gets or sets the pool's display name.</summary>
        public string? PoolName { get; set; }

        /// <summary>Gets or sets the persistent federation id of the pool's owner.</summary>
        public string? OwnerFederationId { get; set; }

        /// <summary>Gets or sets the pool owner's display name.</summary>
        public string? OwnerName { get; set; }

        /// <summary>Gets or sets the pool's membership as known by the sender.</summary>
        public List<PoolMember>? Roster { get; set; }

        /// <summary>Gets or sets the pool's current icon, if any.</summary>
        public string? IconBase64 { get; set; }

        /// <summary>
        /// Our public Jellyfin URL, so a Plex Companion can POST AcceptNotice
        /// back. Unused for Jellyfin-to-Jellyfin invites.
        /// </summary>
        public string? CallbackUrl { get; set; }

        /// <summary>
        /// Federation token we minted for this Plex friend to call us back.
        /// </summary>
        public string? CallbackToken { get; set; }

        /// <summary>This plugin's version, advertised to Companion.</summary>
        public string? FederationPluginVersion { get; set; }
    }

    /// <summary>
    /// Wire payload accepting or rejecting a pool invite - see
    /// <see cref="FederationFriendService.HandlePoolAcceptNotice"/> and
    /// <see cref="FederationFriendService.HandlePoolRejectNotice"/>.
    /// </summary>
    public class PoolInviteResponsePayload
    {
        /// <summary>Gets or sets the id of the invite being responded to.</summary>
        public string InviteId { get; set; } = string.Empty;

        /// <summary>Gets or sets the responder's persistent federation id.</summary>
        public string FromFederationId { get; set; } = string.Empty;
    }

    /// <summary>
    /// A server found through friends-of-friends discovery, cached in memory for
    /// the settings page's Discovery dashboard - see
    /// <see cref="FederationFriendService.RefreshDiscoveredServersAsync"/>.
    /// </summary>
    public class DiscoveredServer
    {
        /// <summary>Gets or sets the server's display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the server's address.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Gets or sets the name of the friend this server was discovered through.</summary>
        public string? DiscoveredViaFriendName { get; set; }

        /// <summary>Gets or sets when this server was last seen in a discovery refresh.</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>Gets or sets the id of the friend that listed this server (not sent to the browser).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DiscoveredViaFriendId { get; set; } = string.Empty;
    }

    public enum RemoteUserRulesUpdateResult
    {
        Invalid,
        TooLarge,
        Unchanged,
        Changed
    }

    /// <summary>What a new friend may see of this server, chosen before the friendship exists.</summary>
    public sealed record FriendSharing(bool ShareAll, IReadOnlyList<string> FolderIds);
}
