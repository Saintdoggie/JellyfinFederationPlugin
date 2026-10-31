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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Plex Companion integration supplied by the optional Plex add-in.</summary>
    public sealed class PlexCompanionService : IPlexCompanionIntegration
    {
        private static readonly HttpClient DefaultHttpClient = new HttpClient(FederationHttp.Handler()) { Timeout = TimeSpan.FromSeconds(20) };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<PlexCompanionService> _logger;
        private readonly IServerApplicationHost _applicationHost;
        private readonly FederationLibraryManager _federationManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IRemoteServerClientFactory _clientFactory;

        public PlexCompanionService(
            ILogger<PlexCompanionService> logger,
            IServerApplicationHost applicationHost,
            FederationLibraryManager federationManager,
            IHttpContextAccessor httpContextAccessor,
            IRemoteServerClientFactory clientFactory)
        {
            _logger = logger;
            _applicationHost = applicationHost;
            _federationManager = federationManager;
            _httpContextAccessor = httpContextAccessor;
            _clientFactory = clientFactory;
        }

        internal static HttpClient? HttpClientOverride { get; set; }

        private static HttpClient SharedHttpClient => HttpClientOverride ?? DefaultHttpClient;

        /// <summary>
        /// Claims a Plex-to-Jellyfin Companion connect code. The copied code is
        /// either a one-time claim against Companion's public (Funnel) URL, or a
        /// direct Plex remote/relay address. Either way the friend does not need
        /// to be on the Plex owner's Tailscale; LAN and 100.x tailnet addresses
        /// are rejected so a "connected" server is actually reachable.
        /// </summary>
        public async Task<(bool Success, string Message, RemoteServer? Server)> ConnectAsync(string? code, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return (false, "Paste a valid Companion connect code.", null);
            }

            CompanionConnectCodePayload? decoded;
            try
            {
                decoded = JsonSerializer.Deserialize<CompanionConnectCodePayload>(
                    Convert.FromBase64String(code.Trim()),
                    JsonOpts);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return (false, "That connect code could not be read - check it was copied in full.", null);
            }

            if (decoded == null || string.IsNullOrWhiteSpace(decoded.Url) || string.IsNullOrWhiteSpace(decoded.Token))
            {
                return (false, "That connect code is missing an address or token.", null);
            }

            if (!ConfigValidator.IsValidServerUrl(decoded.Url))
            {
                return (false, "That connect code has an invalid server address.", null);
            }

            string plexUrl;
            string plexToken;
            string serverName;
            IReadOnlyList<CompanionSharedLibrary> libraries;

            // Historical codes omitted <c>claim</c> and put Companion's public
            // URL + a one-time token in {url, token}. The settings page used to
            // save those as Plex credentials, which only worked on the same
            // tailnet. Default is to claim.
            if (decoded.Claim != false)
            {
                var claimed = await ClaimCompanionLinkAsync(decoded, cancellationToken).ConfigureAwait(false);
                if (claimed.Success)
                {
                    plexUrl = claimed.PlexUrl!;
                    plexToken = claimed.PlexToken!;
                    serverName = claimed.ServerName!;
                    libraries = claimed.Libraries;
                }
                else if (!string.IsNullOrWhiteSpace(decoded.FallbackUrl)
                    && !string.IsNullOrWhiteSpace(decoded.FallbackToken))
                {
                    // Funnel TLS is often advertised while dead. The same code
                    // can carry Plex Remote Access/Relay as a backup so the
                    // Starlink friend still connects.
                    plexUrl = decoded.FallbackUrl.Trim().TrimEnd('/');
                    plexToken = decoded.FallbackToken;
                    serverName = string.IsNullOrWhiteSpace(decoded.Name) ? plexUrl : decoded.Name.Trim();
                    libraries = decoded.Libraries ?? new List<CompanionSharedLibrary>();
                }
                else
                {
                    return (false, claimed.Message, null);
                }
            }
            else
            {
                plexUrl = decoded.Url.Trim().TrimEnd('/');
                plexToken = decoded.Token;
                serverName = string.IsNullOrWhiteSpace(decoded.Name) ? plexUrl : decoded.Name.Trim();
                libraries = decoded.Libraries ?? new List<CompanionSharedLibrary>();
            }

            if (ConfigValidator.IsPrivateOrLoopbackHost(plexUrl))
            {
                return (false, "That Plex server only published a local or Tailscale address. Ask your friend to enable Plex Remote Access or Plex Relay so you can connect without being on their Tailscale.", null);
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return (false, "Plugin not initialized.", null);
            }

            config.RemoteServers ??= new List<RemoteServer>();
            if (config.RemoteServers.Any(s =>
                    !string.IsNullOrWhiteSpace(s.Url)
                    && string.Equals(s.Url.TrimEnd('/'), plexUrl, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "A server with this address is already configured - edit it in the list instead of adding it again.", null);
            }

            var server = new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Kind = ServerKind.Plex,
                Name = serverName,
                Url = plexUrl,
                ApiKey = plexToken,
                CompanionUrl = decoded.Claim != false
                    && decoded.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    && !ConfigValidator.IsPrivateOrLoopbackHost(decoded.Url)
                    ? decoded.Url.Trim().TrimEnd('/')
                    : string.Empty,
                FederationId = Guid.NewGuid().ToString(),
                IssuedApiKey = FederationTokenAuth.GenerateToken(),
                StreamingMode = StreamingMode.Proxy,
                Enabled = true
            };

            var shared = libraries
                .Where(l => !string.IsNullOrWhiteSpace(l.SectionKey))
                .Select(l => new CompanionSharedLibrary
                {
                    SectionKey = l.SectionKey,
                    Title = l.Title,
                    Type = l.Type
                })
                .ToList();

            if (shared.Count > 0)
            {
                server.AllowedExternalLibraryIds = shared.Select(l => l.SectionKey!).ToList();
            }

            config.RemoteServers.Add(server);
            AttachPlexLibraryMappings(config, server, shared);
            Plugin.Instance!.SaveConfiguration();
            _clientFactory.InvalidateAll();

            var sharedNote = shared.Count == 0
                ? " They have not marked any libraries shared yet, so nothing will sync until they do."
                : $" {shared.Count} shared library(ies) are mapped and will sync.";
            return (true, $"Connected to {server.Name}.{sharedNote}", server);
        }

        /// <summary>
        /// Stores a Plex Companion share that was pushed by typing this
        /// server's address. The admin still has to Accept; the code is not
        /// applied until then.
        /// </summary>
        public (bool Success, string Message) ReceiveOffer(string? code)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return (false, "Plugin not initialized.");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return (false, "That share request is empty.");
            }

            CompanionConnectCodePayload? decoded;
            try
            {
                decoded = JsonSerializer.Deserialize<CompanionConnectCodePayload>(
                    Convert.FromBase64String(code.Trim()),
                    JsonOpts);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return (false, "That share request could not be read.");
            }

            if (decoded == null || string.IsNullOrWhiteSpace(decoded.Url) || string.IsNullOrWhiteSpace(decoded.Token))
            {
                return (false, "That share request is missing an address or token.");
            }

            config.IncomingPlexOffers ??= new List<IncomingPlexOffer>();
            config.IncomingPlexOffers.RemoveAll(o => o.CreatedUtc < DateTime.UtcNow.AddHours(-24));
            if (config.IncomingPlexOffers.Count >= 25)
            {
                return (false, "This server already has too many pending Plex share requests. Ask the admin to accept or reject the existing ones.");
            }

            if (config.IncomingPlexOffers.Any(o => string.Equals(o.Code, code.Trim(), StringComparison.Ordinal)))
            {
                return (true, "That share request is already waiting for the admin to accept.");
            }

            config.IncomingPlexOffers.Add(new IncomingPlexOffer
            {
                Code = code.Trim(),
                ServerName = string.IsNullOrWhiteSpace(decoded.Name) ? decoded.Url : decoded.Name.Trim(),
                CompanionUrl = decoded.Url.Trim().TrimEnd('/'),
                CreatedUtc = DateTime.UtcNow
            });
            Plugin.Instance!.SaveConfiguration();
            return (true, "Share request received. The Jellyfin admin can Accept it under Federation → Companion.");
        }

        public IReadOnlyList<IncomingPlexOffer> ListOffers()
        {
            var config = Plugin.Instance?.Configuration;
            return (config?.IncomingPlexOffers ?? new List<IncomingPlexOffer>())
                .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddHours(-24))
                .ToList();
        }

        public async Task<(bool Success, string Message, RemoteServer? Server)> AcceptOfferAsync(string offerId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            var offer = config?.IncomingPlexOffers?.FirstOrDefault(o => o.Id == offerId);
            if (offer == null)
            {
                return (false, "That Plex share request was not found. It may have been accepted or rejected already.", null);
            }

            var (success, message, server) = await ConnectAsync(offer.Code, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                config!.IncomingPlexOffers.RemoveAll(o => o.Id == offerId);
                Plugin.Instance!.SaveConfiguration();
            }

            return (success, message, server);
        }

        public (bool Success, string Message) RejectOffer(string offerId)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.IncomingPlexOffers == null)
            {
                return (false, "That Plex share request was not found.");
            }

            var removed = config.IncomingPlexOffers.RemoveAll(o => o.Id == offerId) > 0;
            if (!removed)
            {
                return (false, "That Plex share request was not found.");
            }

            Plugin.Instance!.SaveConfiguration();
            return (true, "Plex share request rejected.");
        }

        private async Task<(bool Success, string Message, string? PlexUrl, string? PlexToken, string? ServerName, List<CompanionSharedLibrary> Libraries)> ClaimCompanionLinkAsync(
            CompanionConnectCodePayload decoded,
            CancellationToken cancellationToken)
        {
            var claimUrl = decoded.Url!.Trim().TrimEnd('/') + "/api/link/complete";
            try
            {
                using var content = JsonContent(new
                {
                    token = decoded.Token,
                    requesterName = _applicationHost.FriendlyName
                });
                using var response = await SharedHttpClient.PostAsync(claimUrl, content, cancellationToken).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var remoteError = TryReadError(body);
                    if (LooksLikePlexInsteadOfCompanion(body))
                    {
                        return (false, "That public address is Plex, not Companion. Ask your friend to Update Companion and send the share request again — it will use Plex's own public path so you can Accept without calling Companion through Funnel.", null, null, null, new List<CompanionSharedLibrary>());
                    }

                    return (false, remoteError ?? "Could not reach the Companion app to finish the connect code. Ask your friend to enable Plex Remote Access / Plex Relay and generate a new code — they do not need port forwarding or a working Funnel for that.", null, null, null, new List<CompanionSharedLibrary>());
                }

                if (LooksLikePlexInsteadOfCompanion(body))
                {
                    return (false, "That public address is Plex, not Companion. Ask your friend to Update Companion and send the share request again — it will use Plex's own public path so you can Accept without calling Companion through Funnel.", null, null, null, new List<CompanionSharedLibrary>());
                }

                var claimed = JsonSerializer.Deserialize<CompanionLinkCompleteResponse>(body, JsonOpts);
                if (claimed == null || string.IsNullOrWhiteSpace(claimed.PlexUrl) || string.IsNullOrWhiteSpace(claimed.PlexToken))
                {
                    return (false, "The Companion app did not return a usable Plex address.", null, null, null, new List<CompanionSharedLibrary>());
                }

                return (
                    true,
                    "ok",
                    claimed.PlexUrl.TrimEnd('/'),
                    claimed.PlexToken,
                    string.IsNullOrWhiteSpace(claimed.ServerName) ? (decoded.Name ?? claimed.PlexUrl) : claimed.ServerName.Trim(),
                    claimed.Libraries ?? new List<CompanionSharedLibrary>());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                _logger.LogWarning(ex, "[Federation] Companion connect-code claim failed for {Url}", decoded.Url);
                return (false, DescribeClaimTransportFailure(decoded.Url, ex), null, null, null, new List<CompanionSharedLibrary>());
            }
        }

        internal static string DescribeClaimTransportFailure(string? companionUrl, Exception ex)
        {
            var host = Uri.TryCreate(companionUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
                ? uri.Host
                : companionUrl ?? "their Companion app";

            if (IsTlsHandshakeFailure(ex))
            {
                return $"Could not complete HTTPS to {host}. Their Tailscale Funnel is advertised but TLS is not serving — DNS and the HTTP redirect work, the certificate handshake does not. This particular code cannot be used until Funnel HTTPS works. Ask them to enable Plex Remote Access (Relay works on Starlink with no port forwarding) and generate a new connect code. Companion will use Plex's own public address instead of Funnel.";
            }

            if (ex is TaskCanceledException)
            {
                return $"Timed out reaching {host}. Ask them to generate a new code after Plex Remote Access / Plex Relay is on — they do not need port forwarding or a working Funnel for that.";
            }

            return $"Could not reach {host} over the internet. They do not need to join your Tailscale. Ask them to enable Plex Remote Access / Plex Relay and generate a new connect code.";
        }

        internal static bool LooksLikePlexInsteadOfCompanion(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            return body.Contains("MediaContainer", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Plex Media Server", StringComparison.OrdinalIgnoreCase)
                || body.Contains("<html", StringComparison.OrdinalIgnoreCase) && body.Contains("plex", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsTlsHandshakeFailure(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.Authentication.AuthenticationException)
                {
                    return true;
                }

                var message = current.Message ?? string.Empty;
                if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("trust relationship", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? TryReadError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }
            catch (JsonException)
            {
                // Body is not JSON - keep the generic caller-side message.
            }

            return null;
        }

        private void AttachPlexLibraryMappings(PluginConfiguration config, RemoteServer server, IReadOnlyList<CompanionSharedLibrary> libraries)
        {
            config.LibraryMappings ??= new List<LibraryMapping>();
            foreach (var library in libraries)
            {
                var mediaType = string.Equals(library.Type, "show", StringComparison.OrdinalIgnoreCase)
                    ? "Series"
                    : "Movie";
                var mappingName = _federationManager.LocalLibraryNameFor(mediaType);
                var mapping = config.LibraryMappings.FirstOrDefault(m =>
                    string.Equals(m.LocalLibraryName, mappingName, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                {
                    mapping = new LibraryMapping
                    {
                        LocalLibraryName = mappingName,
                        MediaType = mediaType,
                        Enabled = true,
                        AutoProvision = true,
                        AutoManaged = true
                    };
                    config.LibraryMappings.Add(mapping);
                }

                if (!mapping.RemoteServerIds.Contains(server.Id))
                {
                    mapping.RemoteServerIds.Add(server.Id);
                }

                if (!mapping.RemoteLibrarySources.Any(s =>
                        s.ServerId == server.Id
                        && string.Equals(s.RemoteLibraryId, library.SectionKey, StringComparison.Ordinal)))
                {
                    mapping.RemoteLibrarySources.Add(new RemoteLibrarySource
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        RemoteLibraryId = library.SectionKey ?? string.Empty,
                        RemoteLibraryName = string.IsNullOrWhiteSpace(library.Title) ? mappingName : library.Title!
                    });
                }
            }
        }

        /// <summary>
        /// Admin-triggered: registers a non-Jellyfin consumer of our own catalog
        /// (<see cref="ServerKind.Companion"/>) - today, a Federation Companion
        /// instance a Plex-owning friend runs to import our content as
        /// <c>.strm</c> files. There is no reciprocal server to hand this token
        /// to automatically, so this mints the token and returns a connect code.
        /// </summary>
        public (bool Success, string Message, string? ConnectCode) CreateReceiver(string name, bool shareAllLibraries, List<string>? sharedLibraryFolderIds)
        {
            var localUrl = ResolveLocalUrl();
            if (string.IsNullOrEmpty(localUrl))
            {
                return (false, "Could not determine this server's own public URL. Set it under Advanced settings first, since your friend's Companion app needs it to reach back.", null);
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return (false, "Plugin not initialized.", null);
            }

            var token = FederationTokenAuth.GenerateToken();
            var friendlyName = string.IsNullOrWhiteSpace(name) ? "Plex friend" : name.Trim();
            var server = new RemoteServer
            {
                Id = Guid.NewGuid().ToString(),
                Kind = ServerKind.Companion,
                Name = friendlyName,
                IssuedApiKey = token,
                // Playback/session tokens minted for this peer are bound to its
                // FederationId at mint time and re-resolved through
                // FindByFederationId at stream time (see
                // FederationController.IsStreamTokenAuthorized). Leaving this
                // empty - as entries created by 0.0.116 were - made every
                // DirectStream request from the Companion's .strm files 403,
                // because an empty id can never be re-resolved to a friend.
                FederationId = Guid.NewGuid().ToString(),
                Enabled = true,
                ShareAllLibraries = shareAllLibraries,
                SharedLibraryFolderIds = sharedLibraryFolderIds ?? new List<string>()
            };

            config.RemoteServers ??= new List<RemoteServer>();
            config.RemoteServers.Add(server);
            Plugin.Instance!.SaveConfiguration();

            return (true, $"Connect code generated for {friendlyName}.", BuildConnectCode(localUrl, token, friendlyName));
        }

        /// <summary>
        /// Reconstructs the same connect code <see cref="CreateReceiver"/>
        /// returns, from an already-stored <see cref="RemoteServer.IssuedApiKey"/>
        /// without rotating the token.
        /// </summary>
        public (bool Success, string Message, string? ConnectCode) GetConnectCode(RemoteServer server)
        {
            var localUrl = ResolveLocalUrl();
            if (string.IsNullOrEmpty(localUrl))
            {
                return (false, "Could not determine this server's own public URL. Set it under Advanced settings first.", null);
            }

            if (string.IsNullOrEmpty(server.IssuedApiKey))
            {
                return (false, "This friend has no issued token yet.", null);
            }

            return (true, "Connect code ready.", BuildConnectCode(localUrl, server.IssuedApiKey, server.Name));
        }

        public Task<string?> TryGetVersionAsync(RemoteServer friend, CancellationToken cancellationToken)
            => PlexCompanionEndpoint.TryGetVersionAsync(friend, cancellationToken);

        public string? GetPoolInviteUrl(RemoteServer friend)
        {
            var baseUrl = PlexCompanionEndpoint.TryGetBaseUrl(friend);
            return baseUrl == null ? null : PlexCompanionEndpoint.PoolInviteUrl(baseUrl);
        }

        private static string BuildConnectCode(string url, string token, string name)
        {
            var payload = JsonSerializer.Serialize(new { url, token, name });
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        }

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

        private static StringContent JsonContent(object payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
    }

    internal sealed class CompanionConnectCodePayload
    {
        public string? Url { get; set; }

        public string? Token { get; set; }

        public string? Name { get; set; }

        public bool? Claim { get; set; }

        public string? FallbackUrl { get; set; }

        public string? FallbackToken { get; set; }

        public List<CompanionSharedLibrary>? Libraries { get; set; }
    }

    internal sealed class CompanionLinkCompleteResponse
    {
        public string? PlexUrl { get; set; }

        public string? PlexToken { get; set; }

        public string? ServerName { get; set; }

        public List<CompanionSharedLibrary>? Libraries { get; set; }
    }

    internal sealed class CompanionSharedLibrary
    {
        public string? SectionKey { get; set; }

        public string? Title { get; set; }

        public string? Type { get; set; }
    }
}
