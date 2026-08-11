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
        public async Task<(bool Success, string Message)> SendFriendRequestAsync(string remoteUrl, CancellationToken cancellationToken)
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
            string apiKey;
            try
            {
                apiKey = await CreateApiKeyAsync($"Federation friend: {remoteUrl}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to mint an API key for friend request to {Url}", remoteUrl);
                return (false, "Could not create an API key on this server for your friend to use.");
            }

            var payload = new FriendRequestPayload
            {
                RequestId = requestId,
                FromServerUrl = localUrl,
                FromServerName = _applicationHost.FriendlyName,
                FromServerId = GetOrCreateLocalFederationId(),
                ApiKeyForYou = apiKey
            };

            try
            {
                using var content = JsonContent(payload);
                using var response = await SharedHttpClient.PostAsync($"{remoteUrl}/Plugins/Federation/Friends/Request", content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await RevokeApiKeyAsync(apiKey).ConfigureAwait(false);
                    return (false, $"Friend request rejected by the remote server (HTTP {(int)response.StatusCode}). Check the address - is Federation installed there too?");
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<FriendRequestResponse>(body, JsonOpts);

                config.OutgoingFriendRequests.Add(new FriendRequest
                {
                    Id = requestId,
                    RemoteServerUrl = remoteUrl,
                    RemoteServerName = result?.ServerName ?? remoteUrl,
                    ApiKey = apiKey,
                    CreatedUtc = DateTime.UtcNow
                });
                Plugin.Instance.SaveConfiguration();

                return (true, $"Friend request sent to {(result?.ServerName ?? remoteUrl)}. Waiting for them to accept.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to send friend request to {Url}", remoteUrl);
                await RevokeApiKeyAsync(apiKey).ConfigureAwait(false);
                return (false, $"Could not reach {remoteUrl}: {ex.Message}");
            }
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
                    CreatedUtc = DateTime.UtcNow
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

            string apiKey;
            try
            {
                apiKey = await CreateApiKeyAsync($"Federation friend: {entry.RemoteServerName}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to mint an API key accepting friend request from {Url}", entry.RemoteServerUrl);
                return (false, "Could not create an API key on this server for your friend to use.");
            }

            var payload = new FriendRequestPayload
            {
                RequestId = entry.Id,
                FromServerUrl = localUrl,
                FromServerName = _applicationHost.FriendlyName,
                FromServerId = GetOrCreateLocalFederationId(),
                ApiKeyForYou = apiKey
            };

            try
            {
                using var content = JsonContent(payload);
                using var response = await SharedHttpClient.PostAsync($"{entry.RemoteServerUrl}/Plugins/Federation/Friends/Accept", content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await RevokeApiKeyAsync(apiKey).ConfigureAwait(false);
                    return (false, $"Could not confirm with {entry.RemoteServerName} (HTTP {(int)response.StatusCode}). They may have cancelled the request, or are unreachable right now - try again shortly.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Failed to confirm friend acceptance with {Url}", entry.RemoteServerUrl);
                await RevokeApiKeyAsync(apiKey).ConfigureAwait(false);
                return (false, $"Could not reach {entry.RemoteServerName}: {ex.Message}");
            }

            config.RemoteServers.Add(new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Name = entry.RemoteServerName,
                Url = entry.RemoteServerUrl,
                ApiKey = entry.ApiKey,
                Enabled = true,
                StreamingMode = StreamingMode.Direct
            });
            config.IncomingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();
            _clientFactory.InvalidateAll();

            return (true, $"You and {entry.RemoteServerName} are now federated.");
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
        /// other side responded, and revokes the key minted for it.
        /// </summary>
        public async Task<(bool Success, string Message)> CancelOutgoingFriendRequestAsync(string requestId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == requestId);
            if (entry == null)
            {
                return (false, "Friend request not found.");
            }

            config.OutgoingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();
            await RevokeApiKeyAsync(entry.ApiKey).ConfigureAwait(false);

            return (true, "Friend request cancelled.");
        }

        /// <summary>
        /// Handles the accept callback: the other server has accepted our earlier
        /// outgoing request and is giving us a key to use pulling from them.
        /// </summary>
        public void HandleAcceptCallback(FriendRequestPayload payload)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == payload.RequestId);
            if (entry == null)
            {
                _logger.LogWarning("[Federation] Received an accept callback for an unknown/expired request {RequestId}", payload.RequestId);
                return;
            }

            config.RemoteServers.Add(new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Name = string.IsNullOrEmpty(payload.FromServerName) ? entry.RemoteServerName : payload.FromServerName,
                Url = string.IsNullOrEmpty(payload.FromServerUrl) ? entry.RemoteServerUrl : payload.FromServerUrl.TrimEnd('/'),
                ApiKey = payload.ApiKeyForYou,
                Enabled = true,
                StreamingMode = StreamingMode.Direct
            });
            config.OutgoingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();
            _clientFactory.InvalidateAll();
        }

        /// <summary>
        /// Handles the reject callback: the other server declined our request.
        /// Revokes the key we minted for them, since it's now useless.
        /// </summary>
        public async Task HandleRejectCallbackAsync(string requestId)
        {
            var config = Plugin.Instance!.Configuration;
            var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == requestId);
            if (entry == null)
            {
                return;
            }

            config.OutgoingFriendRequests.Remove(entry);
            Plugin.Instance.SaveConfiguration();
            await RevokeApiKeyAsync(entry.ApiKey).ConfigureAwait(false);
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

        private async Task RevokeApiKeyAsync(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                return;
            }

            try
            {
                await _authManager.DeleteApiKey(accessToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to revoke an unused API key (non-fatal)");
            }
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

            return $"{scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
        }

        private static StringContent JsonContent(object payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
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

        /// <summary>Gets or sets the API key the sender minted for the recipient to use.</summary>
        public string ApiKeyForYou { get; set; } = string.Empty;
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
}
