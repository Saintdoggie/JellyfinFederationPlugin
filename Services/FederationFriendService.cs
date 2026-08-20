using System;
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
        private static readonly HttpClient DefaultHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>
        /// Test-only seam: when set, used instead of <see cref="DefaultHttpClient"/> for
        /// every request. Kept static (rather than a constructor parameter) so the
        /// public DI constructor stays exactly what ASP.NET Core's container expects -
        /// adding an HttpClient constructor parameter would make it try to resolve one
        /// from the container and fail, since none is registered. Tests set this to a
        /// fake-handler-backed client and must reset it to null afterwards.
        /// </summary>
        internal static HttpClient? HttpClientOverride { get; set; }

        private static HttpClient SharedHttpClient => HttpClientOverride ?? DefaultHttpClient;

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

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationFriendService"/> class.
        /// </summary>
        public FederationFriendService(
            ILogger<FederationFriendService> logger,
            IAuthenticationManager authManager,
            IServerApplicationHost applicationHost,
            FederationLibraryManager federationManager,
            IHttpContextAccessor httpContextAccessor,
            IRemoteServerClientFactory clientFactory)
        {
            _logger = logger;
            _authManager = authManager;
            _applicationHost = applicationHost;
            _federationManager = federationManager;
            _httpContextAccessor = httpContextAccessor;
            _clientFactory = clientFactory;
        }

        // Guards check-then-create below against a race on first concurrent use
        // (two Direct-mode plays landing on FederationController.DirectStream at
        // almost the same instant, before Configuration.InternalRelayApiKey has been
        // persisted yet). This service is registered Scoped (see
        // PluginServiceRegistrator - it needs IAuthenticationManager, which Jellyfin
        // registers scoped), so a new instance is constructed per request; the lock
        // has to be static to actually serialize across those instances. In practice
        // this happens at most once per server lifetime.
        private static readonly SemaphoreSlim InternalRelayKeyLock = new(1, 1);

        /// <summary>
        /// Gets this server's own internal relay API key (see
        /// <see cref="PluginConfiguration.InternalRelayApiKey"/>), creating and
        /// persisting one on first use. Purely server-side plumbing: this key is
        /// never sent to a friend server or exposed to any client, only used
        /// locally to fetch this server's own native stream endpoint over loopback
        /// when relaying a Direct-mode playback-token request.
        /// </summary>
        public async Task<string> GetOrCreateInternalRelayApiKeyAsync()
        {
            var config = Plugin.Instance!.Configuration;
            if (!string.IsNullOrEmpty(config.InternalRelayApiKey))
            {
                return config.InternalRelayApiKey;
            }

            await InternalRelayKeyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check: another request may have already created and persisted
                // one while this one was waiting on the lock.
                config = Plugin.Instance!.Configuration;
                if (!string.IsNullOrEmpty(config.InternalRelayApiKey))
                {
                    return config.InternalRelayApiKey;
                }

                var apiKey = await CreateApiKeyAsync("Federation internal relay").ConfigureAwait(false);
                config.InternalRelayApiKey = apiKey;
                Plugin.Instance.SaveConfiguration();
                return apiKey;
            }
            finally
            {
                InternalRelayKeyLock.Release();
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
        public async Task<(bool Success, string Message)> SendFriendRequestAsync(string remoteUrl, CancellationToken cancellationToken, FederationPool? pool = null)
        {
            remoteUrl = (remoteUrl ?? string.Empty).TrimEnd('/');
            if (!ConfigValidator.IsValidServerUrl(remoteUrl))
            {
                return (false, "Enter a valid http(s) server address.");
            }

            var config = Plugin.Instance!.Configuration;
            if (AlreadyKnown(config, remoteUrl))
            {
                return (false, "Already friends (or a pending request already exists) with this server.");
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

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<FriendRequestResponse>(body, JsonOpts);

                config.OutgoingFriendRequests.Add(new FriendRequest
                {
                    Id = requestId,
                    RemoteServerUrl = remoteUrl,
                    RemoteServerName = result?.ServerName ?? remoteUrl,
                    ApiKey = apiKey,
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
        /// Friends-of-friends discovery: asks each current friend who their other
        /// friends are, and automatically sends a friend request to anyone new. A
        /// no-op unless <see cref="PluginConfiguration.AllowFriendsOfFriends"/> is on.
        /// Consent is never skipped by this: a friend only reveals their friends list
        /// if they've opted in themselves, and an auto-sent request still needs the
        /// discovered server's own admin to accept it, same as a manually sent one.
        /// Content stays scoped the same way regardless of how a friendship started -
        /// see the FederationKey check in FederationSyncService, which already
        /// refuses to pull in anything a source server only has because it was
        /// federated into *them* from somewhere else.
        /// </summary>
        public async Task<int> DiscoverFriendsOfFriendsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            if (!config.AllowFriendsOfFriends)
            {
                return 0;
            }

            var localUrl = ResolveLocalUrl();
            var sent = 0;

            foreach (var friend in config.RemoteServers.Where(s => s.Enabled).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var client = _clientFactory.GetClient(friend);
                List<FriendListEntry>? friendsOfFriend;
                try
                {
                    friendsOfFriend = await client.GetFriendsListAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Friends-of-friends lookup failed for {Server} (non-fatal)", friend.Name);
                    continue;
                }

                if (friendsOfFriend == null)
                {
                    continue;
                }

                foreach (var fof in friendsOfFriend)
                {
                    if (string.IsNullOrEmpty(fof.Url) || !ConfigValidator.IsValidServerUrl(fof.Url))
                    {
                        continue;
                    }

                    var candidateUrl = fof.Url.TrimEnd('/');
                    if (!string.IsNullOrEmpty(localUrl) && string.Equals(candidateUrl, localUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        // That's us - friend.Name is friends with us already, no need
                        // to introduce us to ourselves.
                        continue;
                    }

                    if (AlreadyKnown(config, candidateUrl))
                    {
                        continue;
                    }

                    var (success, message) = await SendFriendRequestAsync(candidateUrl, cancellationToken).ConfigureAwait(false);
                    if (success)
                    {
                        sent++;
                        _logger.LogInformation("[Federation] Discovered {Url} through friend {Friend} and sent a friend request", candidateUrl, friend.Name);
                    }
                    else
                    {
                        _logger.LogDebug("[Federation] Discovered {Url} through friend {Friend} but could not send a request: {Message}", candidateUrl, friend.Name, message);
                    }
                }
            }

            return sent;
        }

        /// <summary>
        /// Handles an inbound friend request. Anonymous by design - the sender has no
        /// key for us yet, since issuing one is the whole point of this handshake.
        /// </summary>
        public async Task<FriendRequestResponse> ReceiveFriendRequestAsync(FriendRequestPayload payload, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;

            if (string.IsNullOrEmpty(payload.RequestId)
                || !ConfigValidator.IsValidServerUrl(payload.FromServerUrl)
                || string.IsNullOrEmpty(payload.ApiKeyForYou))
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

            // Idempotent: a retry of the same request (e.g. the sender's original
            // response was lost) should not create a duplicate pending entry.
            var existing = config.IncomingFriendRequests.FirstOrDefault(r => r.Id == payload.RequestId);
            if (existing == null)
            {
                if (AlreadyKnown(config, fromUrl))
                {
                    return new FriendRequestResponse { Success = false, Message = "Already friends (or a pending request already exists) with this server." };
                }

                existing = new FriendRequest
                {
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
                    PoolRoster = payload.PoolRoster
                };
                config.IncomingFriendRequests.Add(existing);
            }

            // Best-effort authenticity check: confirms the sender itself really has a
            // matching outgoing request, so a third party can't plant a fake request
            // that merely *claims* to be from some other admin's server. Never blocks
            // the request from being stored - it only informs the admin's decision.
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
        public async Task<(bool Success, string Message)> AcceptFriendRequestAsync(string requestId, CancellationToken cancellationToken)
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

            try
            {
                using var content = JsonContent(payload);
                using var response = await SharedHttpClient.PostAsync($"{entry.RemoteServerUrl}/Plugins/Federation/Friends/Accept", content, cancellationToken).ConfigureAwait(false);
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

            config.RemoteServers.Add(new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Name = entry.RemoteServerName,
                Url = entry.RemoteServerUrl,
                ApiKey = entry.ApiKey,
                IssuedApiKey = apiKey,
                FederationId = entry.RemoteServerId,
                Enabled = true,
                StreamingMode = StreamingMode.Direct
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
        /// Adds an already-known friend to a pool without repeating the friend
        /// handshake, and tells them so their own copy of the pool (and their own
        /// fan-out to whichever members they don't already know) stays in sync.
        /// </summary>
        private async Task<(bool Success, string Message)> AddExistingFriendToPoolAsync(FederationPool pool, RemoteServer friend, CancellationToken cancellationToken)
        {
            var normalized = friend.Url.TrimEnd('/');
            if (!pool.Members.Any(m => string.Equals(m.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                pool.Members.Add(new PoolMember { FederationId = friend.FederationId, Name = friend.Name, Url = friend.Url });
                Plugin.Instance!.SaveConfiguration();
            }

            if (string.IsNullOrEmpty(friend.Url) || string.IsNullOrEmpty(friend.ApiKey))
            {
                return (true, $"{friend.Name} added to the pool locally, but has no address/key on file to notify.");
            }

            try
            {
                var payload = new PoolNoticePayload
                {
                    FromFederationId = GetOrCreateLocalFederationId(),
                    PoolId = pool.Id,
                    PoolName = pool.Name,
                    OwnerFederationId = pool.OwnerFederationId,
                    OwnerName = pool.OwnerName,
                    Roster = pool.Members.ToList()
                };
                using var response = await PostAuthenticatedAsync(
                    $"{friend.Url.TrimEnd('/')}/Plugins/Federation/Pools/Notice",
                    payload,
                    friend.ApiKey,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (true, $"{friend.Name} added to the pool locally, but could not notify them (HTTP {(int)response.StatusCode}) - they'll pick up the pool the next time they see it another way.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not notify {Name} about being added to pool {Pool} (non-fatal)", friend.Name, pool.Name);
                return (true, $"{friend.Name} added to the pool locally, but could not be reached to notify.");
            }

            return (true, $"{friend.Name} added to the pool.");
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
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var pool = config.Pools.FirstOrDefault(p => p.Id == poolId);
            if (pool == null)
            {
                pool = new FederationPool
                {
                    Id = poolId,
                    Name = poolName ?? poolId,
                    IsOwner = false,
                    OwnerFederationId = ownerFederationId ?? string.Empty,
                    OwnerName = ownerName ?? reachedViaName ?? string.Empty
                };
                config.Pools.Add(pool);
            }

            var selfId = GetOrCreateLocalFederationId();
            var selfName = _applicationHost.FriendlyName;
            var selfUrl = ResolveLocalUrl();

            void AddMember(string? federationId, string? name, string? url)
            {
                if (string.IsNullOrEmpty(url))
                {
                    return;
                }

                var normalized = url.TrimEnd('/');
                if (pool.Members.Any(m => string.Equals(m.Url.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                pool.Members.Add(new PoolMember { FederationId = federationId ?? string.Empty, Name = name ?? url, Url = url });
            }

            AddMember(selfId, selfName, selfUrl);
            AddMember(reachedViaFederationId, reachedViaName, reachedViaUrl);
            foreach (var member in roster ?? new List<PoolMember>())
            {
                AddMember(member.FederationId, member.Name, member.Url);
            }

            Plugin.Instance.SaveConfiguration();

            var selfUrlNormalized = (selfUrl ?? string.Empty).TrimEnd('/');
            var reachedViaUrlNormalized = reachedViaUrl.TrimEnd('/');
            var toIntroduce = pool.Members
                .Where(m => !string.Equals(m.Url.TrimEnd('/'), selfUrlNormalized, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(m.Url.TrimEnd('/'), reachedViaUrlNormalized, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var member in toIntroduce)
            {
                try
                {
                    var (success, message) = await SendPoolInviteAsync(pool.Id, member.Url, cancellationToken).ConfigureAwait(false);
                    if (!success)
                    {
                        _logger.LogDebug("[Federation] Pool mesh introduction to {Url} did not send: {Message}", member.Url, message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Pool mesh introduction to {Url} failed (non-fatal - will retry on a future pool sync)", member.Url);
                }
            }
        }

        /// <summary>
        /// Server-to-server: an existing friend added us to a pool we weren't
        /// already in, or has an updated roster for one we're already in. No accept
        /// step needed - the two servers are already connected, so this is purely
        /// informational, same trust boundary as the rest of the friend system.
        /// </summary>
        public Task ReceivePoolNotice(PoolNoticePayload payload, CancellationToken cancellationToken)
        {
            if (payload == null || string.IsNullOrEmpty(payload.PoolId) || string.IsNullOrEmpty(payload.FromFederationId))
            {
                return Task.CompletedTask;
            }

            var config = Plugin.Instance!.Configuration;
            var sender = config.RemoteServers.FirstOrDefault(s => s.FederationId == payload.FromFederationId);
            if (sender == null)
            {
                _logger.LogWarning("[Federation] Received a pool notice from an unrecognized federation id {FederationId}", payload.FromFederationId);
                return Task.CompletedTask;
            }

            return AdoptPoolRosterAndFanOutAsync(
                payload.PoolId,
                payload.PoolName,
                payload.OwnerFederationId,
                payload.OwnerName,
                sender.Url,
                sender.FederationId,
                sender.Name,
                payload.Roster,
                cancellationToken);
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
                using var content = JsonContent(new FriendRejectPayload { RequestId = entry.Id });
                await SharedHttpClient.PostAsync($"{entry.RemoteServerUrl}/Plugins/Federation/Friends/Reject", content, cancellationToken).ConfigureAwait(false);
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
        public bool HandleAcceptCallback(FriendRequestPayload payload)
        {
            if (!payload.SupportsFederationToken)
            {
                _logger.LogWarning(
                    "[Federation] Rejected an accept callback for request {RequestId}: the other side did not confirm scoped federation-token support (likely an incompatible/old plugin version)",
                    payload.RequestId);
                return false;
            }

            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == payload.RequestId);
            if (entry == null)
            {
                _logger.LogWarning("[Federation] Received an accept callback for an unknown/expired request {RequestId}", payload.RequestId);
                return false;
            }

            var memberName = string.IsNullOrEmpty(payload.FromServerName) ? entry.RemoteServerName : payload.FromServerName;
            var memberUrl = string.IsNullOrEmpty(payload.FromServerUrl) ? entry.RemoteServerUrl : payload.FromServerUrl.TrimEnd('/');

            config.RemoteServers.Add(new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Name = memberName,
                Url = memberUrl,
                ApiKey = payload.ApiKeyForYou,
                IssuedApiKey = entry.ApiKey,
                FederationId = payload.FromServerId ?? string.Empty,
                Enabled = true,
                StreamingMode = StreamingMode.Direct
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
        public void HandleRejectCallbackAsync(string requestId)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == requestId);
            if (entry == null)
            {
                return;
            }

            config.OutgoingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();
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

            server.RemoteUserAccessRules.RemoveAll(r => string.Equals(r.RemoteUserId, rule.RemoteUserId, StringComparison.OrdinalIgnoreCase));
            if (rule.Mode != RemoteUserAccessMode.AllLibraries)
            {
                server.RemoteUserAccessRules.Add(rule);
            }

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
        /// Matched by federation id, same as <see cref="ReceivePoolNotice"/>.
        /// </summary>
        public void ReceiveRemoteUserAccessRules(RemoteUserAccessRulesPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.FromFederationId))
            {
                return;
            }

            var config = Plugin.Instance!.Configuration;
            var server = config.RemoteServers.FirstOrDefault(s => s.FederationId == payload.FromFederationId);
            if (server == null)
            {
                _logger.LogWarning("[Federation] Received remote-user access rules from an unrecognized federation id {FederationId}", payload.FromFederationId);
                return;
            }

            server.FriendUserAccessRules = payload.Rules ?? new List<RemoteUserAccessRule>();
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("[Federation] {Name} updated their per-user access rules for us ({Count} rule(s))", server.Name, server.FriendUserAccessRules.Count);
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
            try
            {
                using var response = await SharedHttpClient.GetAsync(
                    $"{remoteUrl}/Plugins/Federation/Friends/Outgoing/{Uri.EscapeDataString(requestId)}",
                    cancellationToken).ConfigureAwait(false);
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
        private string ResolveLocalUrl()
        {
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

            var scheme = request.Scheme;
            var forwardedProto = request.Headers["X-Forwarded-Proto"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedProto)
                && (forwardedProto == Uri.UriSchemeHttp || forwardedProto == Uri.UriSchemeHttps))
            {
                scheme = forwardedProto;
            }

            var derived = $"{scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

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

        private static StringContent JsonContent(object payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        /// <summary>
        /// POSTs to an endpoint on an already-known friend's server, authenticated
        /// with the scoped federation token they issued this server (see
        /// <see cref="FederationTokenAuth"/>) - unlike the handshake endpoints
        /// (Friends/Request, Friends/Accept, Friends/Reject), which are anonymous
        /// by necessity since no token exists yet at that point. Unlike the real
        /// Jellyfin API key this used to send, this token satisfies nothing on
        /// Jellyfin's own native auth - it only means anything to the friend's own
        /// <see cref="FederationTokenAuth.ResolveCaller"/> check.
        /// </summary>
        private static async Task<HttpResponseMessage> PostAuthenticatedAsync(string url, object payload, string apiKey, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(payload) };
            request.Headers.TryAddWithoutValidation(FederationTokenAuth.Header, apiKey);
            return await SharedHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
    }
}
