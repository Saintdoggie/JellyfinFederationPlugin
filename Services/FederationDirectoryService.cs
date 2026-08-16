using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Client side of the friend-directory feature: registers this server's
    /// username with whichever directory it's pointed at
    /// (<see cref="Configuration.PluginConfiguration.DirectoryServerUrl"/>),
    /// searches it, and redeems invite codes. A directory is just a small,
    /// optional, self-hostable lookup service (username -&gt; server address) -
    /// this class works the same way whether that directory happens to be this
    /// server itself (<see cref="FederationDirectoryStore"/>, when
    /// <see cref="Configuration.PluginConfiguration.HostDirectory"/> is on) or
    /// someone else's. No trust is granted by any of this: redeeming an invite or
    /// a search result only ever starts an ordinary friend request through
    /// <see cref="FederationFriendService"/> - the admin on the other end still
    /// has to click Accept, exactly as if the address had been typed in by hand.
    /// </summary>
    public class FederationDirectoryService
    {
        private static readonly HttpClient DefaultHttpClient = new(new System.Net.Http.SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>
        /// Test-only seam: when set, used instead of <see cref="DefaultHttpClient"/>.
        /// Tests must reset this to null afterwards.
        /// </summary>
        internal static HttpClient? HttpClientOverride { get; set; }

        private static HttpClient SharedHttpClient => HttpClientOverride ?? DefaultHttpClient;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<FederationDirectoryService> _logger;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDirectoryService"/> class.
        /// </summary>
        /// <remarks>
        /// Resolves <see cref="FederationFriendService"/> through a short-lived DI
        /// scope per call rather than a direct constructor dependency, since it is
        /// registered scoped (needs <c>IAuthenticationManager</c>) while this class
        /// is a singleton - the same pattern <see cref="FederationSyncService"/>
        /// already uses for the same reason.
        /// </remarks>
        public FederationDirectoryService(ILogger<FederationDirectoryService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        private string LocalFederationId()
        {
            using var scope = _serviceProvider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<FederationFriendService>().GetOrCreateLocalFederationId();
        }

        private async Task<(bool Success, string Message)> SendFriendRequestAsync(string url, CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var friends = scope.ServiceProvider.GetRequiredService<FederationFriendService>();
            return await friends.SendFriendRequestAsync(url, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Registers/refreshes this server's entry with its configured directory.
        /// A no-op (not an error) when either <c>DirectoryServerUrl</c> or
        /// <c>LocalUsername</c> isn't set - the feature is entirely opt-in.
        /// </summary>
        public async Task<(bool Success, string Message)> RegisterAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            if (string.IsNullOrEmpty(config.DirectoryServerUrl))
            {
                return (false, "No directory server configured.");
            }

            if (string.IsNullOrEmpty(config.LocalUsername))
            {
                return (false, "Set a username first.");
            }

            var localUrl = config.ServerUrl.TrimEnd('/');
            if (string.IsNullOrEmpty(localUrl))
            {
                return (false, "Set this server's own public address under Advanced settings first - the directory needs somewhere to point people back to.");
            }

            var payload = new DirectoryRegisterPayload
            {
                Username = config.LocalUsername,
                FederationId = LocalFederationId(),
                ServerUrl = localUrl
            };

            try
            {
                using var response = await SharedHttpClient.PostAsJsonAsync(
                    $"{config.DirectoryServerUrl.TrimEnd('/')}/Plugins/Federation/Directory/Register",
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Directory rejected the registration (HTTP {(int)response.StatusCode}).");
                }

                return (true, "Registered.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not reach directory {Url} to register", config.DirectoryServerUrl);
                return (false, $"Could not reach the directory: {ex.Message}");
            }
        }

        /// <summary>
        /// Searches the configured directory by username substring.
        /// </summary>
        public async Task<(bool Success, string Message, IReadOnlyList<DirectorySearchResult> Results)> SearchAsync(string usernameQuery, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            if (string.IsNullOrEmpty(config.DirectoryServerUrl))
            {
                return (false, "No directory server configured.", Array.Empty<DirectorySearchResult>());
            }

            try
            {
                var url = $"{config.DirectoryServerUrl.TrimEnd('/')}/Plugins/Federation/Directory/Search?username={Uri.EscapeDataString(usernameQuery)}";
                using var response = await SharedHttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Directory search failed (HTTP {(int)response.StatusCode}).", Array.Empty<DirectorySearchResult>());
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var results = JsonSerializer.Deserialize<List<DirectorySearchResult>>(body, JsonOpts) ?? new List<DirectorySearchResult>();
                return (true, string.Empty, results);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not reach directory {Url} to search", config.DirectoryServerUrl);
                return (false, $"Could not reach the directory: {ex.Message}", Array.Empty<DirectorySearchResult>());
            }
        }

        /// <summary>
        /// Asks the configured directory to mint an invite code for this server,
        /// to hand out as a short, friendlier alternative to a full URL.
        /// </summary>
        public async Task<(bool Success, string Message, string? Code)> CreateInviteAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            if (string.IsNullOrEmpty(config.DirectoryServerUrl))
            {
                return (false, "No directory server configured.", null);
            }

            var localUrl = config.ServerUrl.TrimEnd('/');
            if (string.IsNullOrEmpty(localUrl))
            {
                return (false, "Set this server's own public address under Advanced settings first.", null);
            }

            var payload = new DirectoryInviteCreatePayload
            {
                FederationId = LocalFederationId(),
                ServerUrl = localUrl
            };

            try
            {
                using var response = await SharedHttpClient.PostAsJsonAsync(
                    $"{config.DirectoryServerUrl.TrimEnd('/')}/Plugins/Federation/Directory/Invite/Create",
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Directory refused to create an invite (HTTP {(int)response.StatusCode}).", null);
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<DirectoryInviteCreateResult>(body, JsonOpts);
                return (true, "Invite created.", result?.Code);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not reach directory {Url} to create an invite", config.DirectoryServerUrl);
                return (false, $"Could not reach the directory: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Resolves an invite code through the configured directory, then sends an
        /// ordinary friend request to the server it points at - the same
        /// handshake as pasting a URL by hand, just with the address looked up
        /// for you.
        /// </summary>
        public async Task<(bool Success, string Message)> RedeemInviteAsync(string code, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance!.Configuration;
            if (string.IsNullOrEmpty(config.DirectoryServerUrl))
            {
                return (false, "No directory server configured.");
            }

            try
            {
                var url = $"{config.DirectoryServerUrl.TrimEnd('/')}/Plugins/Federation/Directory/Invite/{Uri.EscapeDataString(code)}";
                using var response = await SharedHttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, "That invite code wasn't found (it may have expired).");
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var resolved = JsonSerializer.Deserialize<DirectorySearchResult>(body, JsonOpts);
                if (resolved == null || string.IsNullOrEmpty(resolved.ServerUrl))
                {
                    return (false, "That invite code wasn't found (it may have expired).");
                }

                return await SendFriendRequestAsync(resolved.ServerUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not redeem invite code against directory {Url}", config.DirectoryServerUrl);
                return (false, $"Could not reach the directory: {ex.Message}");
            }
        }

        private class DirectoryRegisterPayload
        {
            [JsonPropertyName("username")]
            public string Username { get; set; } = string.Empty;

            [JsonPropertyName("federationId")]
            public string FederationId { get; set; } = string.Empty;

            [JsonPropertyName("serverUrl")]
            public string ServerUrl { get; set; } = string.Empty;
        }

        private class DirectoryInviteCreatePayload
        {
            [JsonPropertyName("federationId")]
            public string FederationId { get; set; } = string.Empty;

            [JsonPropertyName("serverUrl")]
            public string ServerUrl { get; set; } = string.Empty;
        }

        private class DirectoryInviteCreateResult
        {
            [JsonPropertyName("code")]
            public string? Code { get; set; }
        }
    }

    /// <summary>
    /// One directory search hit, as returned over the wire (username + address
    /// only - avatars are always fetched peer-to-peer afterward, directly from
    /// <c>ServerUrl</c>, never from the directory).
    /// </summary>
    public class DirectorySearchResult
    {
        /// <summary>Gets or sets the registered username.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Gets or sets the server's persistent federation id.</summary>
        public string FederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the server's address.</summary>
        public string ServerUrl { get; set; } = string.Empty;
    }
}
