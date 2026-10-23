using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>
    /// Friends without a reachable address (docs/PAIRING.md): makes and reads pairing
    /// codes, keeps each paired friend's link up on its own UDP port, routes every
    /// request for a paired friend over that link and serves the friend's requests.
    /// </summary>
    public sealed class PairingService : IHostedService, IAsyncDisposable
    {
        /// <summary>Most pending codes this server made at once.</summary>
        public const int MaxPendingOffers = 3;

        /// <summary>Most paired friends.</summary>
        public const int MaxPairedFriends = 50;

        /// <summary>
        /// Build gate for the Beta review: while true, pairing codes cannot be turned
        /// on, no pairing port opens, and any saved <see cref="PluginConfiguration.PairingOptIn"/>
        /// is ignored. Paired-friend records are kept, so flipping this back to false
        /// restores them. Direct links are deliberately not affected.
        /// </summary>
        public const bool ReviewLocked = true;

        /// <summary>The message shown wherever the review gate blocks pairing.</summary>
        public const string ReviewLockedMessage = "Pairing codes are disabled in this build while the feature is under review.";

        internal const string CertificateFolder = "pairing";

        private static readonly TimeSpan ListenWindow = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan NeverConnectedLimit = PairingCode.Lifetime + TimeSpan.FromMinutes(5);

        private readonly ILogger<PairingService> _logger;
        private readonly INetworkManager? _network;
        private readonly FederationLibraryManager? _library;
        private readonly MediaBrowser.Controller.IServerApplicationHost? _host;
        private readonly PairedForwarder _forwarder;
        private readonly ConcurrentDictionary<string, PendingOffer> _offers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Runtime> _friends = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _configGate = new();
        private Task? _supervisor;
        private int _stopped;

        /// <summary>Initializes a new instance of the <see cref="PairingService"/> class.</summary>
        public PairingService(ILogger<PairingService> logger, FederationLibraryManager? library = null, INetworkManager? network = null, MediaBrowser.Controller.IServerApplicationHost? host = null)
        {
            _logger = logger;
            _host = host;
            _library = library;
            _network = network;
            _forwarder = new PairedForwarder(LocalBase, logger);
            Current = this;
        }

        /// <summary>Gets the running service (for the controller and the HTTP hook).</summary>
        public static PairingService? Current { get; private set; }

        /// <summary>Gets or sets the clock (tests).</summary>
        internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

        /// <summary>Gets or sets the plugin data folder (tests); defaults to the plugin's.</summary>
        internal string? DataFolder { get; set; }

        private static bool AllowPrivateForTests => Environment.GetEnvironmentVariable("FEDERATION_DIRECTLINK_TEST_ALLOW_PRIVATE") == "1";

        private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

        /// <summary>
        /// Whether pairing may run, after both the admin's opt-in and the build's
        /// review gate. Used for every route, the supervisor and the status page so
        /// a stale saved <c>PairingOptIn=true</c> cannot bypass the gate.
        /// </summary>
        /// <param name="config">The plugin configuration, or null.</param>
        /// <returns>True only when pairing is opted in and not review-locked.</returns>
        internal static bool IsEnabled(PluginConfiguration? config) => IsEnabled(config?.PairingOptIn == true, ReviewLocked);

        /// <summary>Pure form of <see cref="IsEnabled(PluginConfiguration?)"/> (tests).</summary>
        /// <param name="configuredOn">The saved admin opt-in.</param>
        /// <param name="reviewLocked">The build's review gate.</param>
        /// <returns>True only when both allow pairing.</returns>
        internal static bool IsEnabled(bool configuredOn, bool reviewLocked) => configuredOn && !reviewLocked;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            FederationHttp.OpenLinkStream = OpenLinkStreamAsync;
            _supervisor = Task.Run(() => SuperviseAsync(_shutdown.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1)
            {
                return;
            }

            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_supervisor != null)
            {
                await _supervisor.ConfigureAwait(false);
            }

            foreach (var runtime in _friends.Values)
            {
                await runtime.StopAsync().ConfigureAwait(false);
            }

            foreach (var offer in _offers.Values)
            {
                offer.Dispose();
            }

            _friends.Clear();
            _offers.Clear();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _shutdown.Dispose();
        }

        // ------------------------------------------------------------------ admin

        /// <summary>Makes a first code (the offer). Its port stays open, keeping the router's mapping, until it is used or expires.</summary>
        /// <returns>The code text and when it expires, or an error.</returns>
        public async Task<(string? Code, DateTimeOffset Expires, string? Error)> CreateOfferAsync(CancellationToken cancellationToken)
        {
            var config = Config;
            if (!IsEnabled(config))
            {
                return (null, default, ReviewLocked ? ReviewLockedMessage : "Pairing codes are switched off on the Beta tab.");
            }

            if (!QuicTransportHost.Available)
            {
                return (null, default, "Pairing isn't available on this server: " + QuicTransportHost.Reason + ".");
            }

            PruneOffers();
            if (_offers.Count >= MaxPendingOffers)
            {
                return (null, default, "This server already has " + MaxPendingOffers + " unused codes. Cancel one or wait for them to expire.");
            }

            if (config!.RemoteServers.Count(s => s.Paired) >= MaxPairedFriends)
            {
                return (null, default, "This server has the most paired friends it allows.");
            }

            var port = PairPort.BindAny(config.PairingPortFirst, config.PairingPortLast, 0, TakenPorts());
            if (port == null)
            {
                return (null, default, "No free pairing port in " + config.PairingPortFirst + "-" + config.PairingPortLast + ".");
            }

            var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var certificate = CreateCertificate();
            try
            {
                var (endpoints, stun) = await DiscoverEndpointsAsync(port, cancellationToken).ConfigureAwait(false);
                if (endpoints.Count == 0)
                {
                    throw new InvalidOperationException("This server couldn't find its own internet address (STUN didn't answer).");
                }

                var now = Now();
                var offer = new PairingCode
                {
                    Kind = PairingCodeKind.Offer,
                    Created = now,
                    Expires = now + PairingCode.Lifetime,
                    CertificateFingerprint = SHA256.HashData(certificate.GetRawCertData()),
                    PublicKey = PairingCrypto.PublicKeyBytes(ephemeral),
                    Secret = RandomNumberGenerator.GetBytes(PairingCode.SecretLength),
                    Endpoints = endpoints,
                    Name = ServerName()
                };
                var (bytes, text) = offer.Encode();
                var pending = new PendingOffer(offer, bytes, ephemeral, certificate, port, stun);
                _offers[Convert.ToHexString(PairingCode.HashOffer(bytes))] = pending;
                pending.StartKeepAlive(_shutdown.Token);
                _logger.LogInformation("[Federation] Made a pairing code on port {Port} (expires {Expires:t} UTC)", port.Port, offer.Expires);
                return (text, offer.Expires, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SocketException or CryptographicException)
            {
                port.Dispose();
                ephemeral.Dispose();
                certificate.Dispose();
                return (null, default, ex.Message);
            }
        }

        /// <summary>Cancels a code this server made.</summary>
        /// <returns>True when it existed.</returns>
        public bool CancelOffer(string offerHashHex)
        {
            if (_offers.TryRemove(offerHashHex, out var offer))
            {
                offer.Dispose();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads a friend's first code and makes the answer. Starts sending to the
        /// friend's address right away, which opens this side's router for it.
        /// </summary>
        /// <returns>The answer text and the new (not yet confirmed) friend's id, or an error.</returns>
        public async Task<(string? Answer, string? FriendId, string? Error)> AnswerOfferAsync(string offerText, CancellationToken cancellationToken)
        {
            var config = Config;
            if (!IsEnabled(config))
            {
                return (null, null, ReviewLocked ? ReviewLockedMessage : "Pairing codes are switched off on the Beta tab.");
            }

            if (!QuicTransportHost.Available)
            {
                return (null, null, "Pairing isn't available on this server: " + QuicTransportHost.Reason + ".");
            }

            var (offer, offerBytes, error) = PairingCode.TryDecode(offerText, PairingCodeKind.Offer, Now());
            if (offer == null)
            {
                return (null, null, error);
            }

            var offerHash = PairingCode.HashOffer(offerBytes);
            if (_offers.ContainsKey(Convert.ToHexString(offerHash)))
            {
                return (null, null, "That's a code this server made. Send it to your friend, and enter the answer they send back.");
            }

            var peerEndpoints = PairingEndpoints.Filter(offer.Endpoints, OwnSubnets(), AllowPrivateForTests);
            if (peerEndpoints.Count == 0)
            {
                return (null, null, "The code has no internet address this server may send to.");
            }

            if (config!.RemoteServers.Count(s => s.Paired) >= MaxPairedFriends)
            {
                return (null, null, "This server has the most paired friends it allows.");
            }

            var port = PairPort.BindAny(config.PairingPortFirst, config.PairingPortLast, 0, TakenPorts());
            if (port == null)
            {
                return (null, null, "No free pairing port in " + config.PairingPortFirst + "-" + config.PairingPortLast + ".");
            }

            using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var certificate = CreateCertificate();
            var keep = false;
            try
            {
                var (endpoints, _) = await DiscoverEndpointsAsync(port, cancellationToken).ConfigureAwait(false);
                if (endpoints.Count == 0)
                {
                    return (null, null, "This server couldn't find its own internet address (STUN didn't answer).");
                }

                var now = Now();
                var answer = new PairingCode
                {
                    Kind = PairingCodeKind.Answer,
                    Created = now,
                    Expires = offer.Expires,
                    OfferHash = offerHash,
                    CertificateFingerprint = SHA256.HashData(certificate.GetRawCertData()),
                    PublicKey = PairingCrypto.PublicKeyBytes(ephemeral),
                    Secret = RandomNumberGenerator.GetBytes(PairingCode.SecretLength),
                    Endpoints = endpoints,
                    Name = ServerName()
                };
                if (answer.Expires < now + TimeSpan.FromMinutes(1))
                {
                    answer.Expires = now + TimeSpan.FromMinutes(1);
                }

                var (_, answerText) = answer.Encode(offer.Secret);
                var key = PairingCrypto.DerivePairKey(ephemeral, offer.PublicKey, offer.Secret, answer.Secret, offerHash, offer.CertificateFingerprint, answer.CertificateFingerprint);
                var friendId = AddPairedFriend(
                    claimedName: offer.Name,
                    key,
                    certificate,
                    peerFingerprint: offer.CertificateFingerprint,
                    ownFingerprint: answer.CertificateFingerprint,
                    safety: PairingCrypto.SafetyNumber(offerHash, offer.CertificateFingerprint, answer.CertificateFingerprint, offer.PublicKey, answer.PublicKey),
                    endpoints: peerEndpoints,
                    port);
                keep = true;
                _logger.LogInformation("[Federation] Answered a pairing code from a server that calls itself {Name}; waiting for the link", Printable(offer.Name));
                return (answerText, friendId, null);
            }
            catch (Exception ex) when (ex is SocketException or CryptographicException or IOException)
            {
                return (null, null, ex.Message);
            }
            finally
            {
                if (!keep)
                {
                    port.Dispose();
                    certificate.Dispose();
                }
            }
        }

        /// <summary>Reads the friend's answer to one of this server's codes and starts the link.</summary>
        /// <returns>The new (not yet confirmed) friend's id, or an error.</returns>
        public (string? FriendId, string? Error) CompleteOffer(string answerText)
        {
            if (!IsEnabled(Config))
            {
                return (null, ReviewLocked ? ReviewLockedMessage : "Pairing codes are switched off on the Beta tab.");
            }

            var hash = PairingCode.PeekOfferHash(answerText);
            if (hash == null)
            {
                return (null, "That isn't an answer code. Check it was copied whole.");
            }

            if (!_offers.TryGetValue(Convert.ToHexString(hash), out var pending) || Now() > pending.Offer.Expires)
            {
                return (null, "This answer isn't for a code this server made, or that code was used, cancelled or expired. Make a new code.");
            }

            var (answer, _, error) = PairingCode.TryDecode(answerText, PairingCodeKind.Answer, Now(), pending.Offer.Secret);
            if (answer == null)
            {
                return (null, error);
            }

            var peerEndpoints = PairingEndpoints.Filter(answer.Endpoints, OwnSubnets(), AllowPrivateForTests);
            if (peerEndpoints.Count == 0)
            {
                return (null, "The answer has no internet address this server may send to.");
            }

            // Single use: whatever happens next, this offer is gone.
            if (!_offers.TryRemove(Convert.ToHexString(hash), out _))
            {
                return (null, "This code was just used.");
            }

            var port = pending.TakePort();
            try
            {
                var key = PairingCrypto.DerivePairKey(pending.Ephemeral, answer.PublicKey, pending.Offer.Secret, answer.Secret, hash, pending.Offer.CertificateFingerprint, answer.CertificateFingerprint);
                var friendId = AddPairedFriend(
                    claimedName: answer.Name,
                    key,
                    pending.TakeCertificate(),
                    peerFingerprint: answer.CertificateFingerprint,
                    ownFingerprint: pending.Offer.CertificateFingerprint,
                    safety: PairingCrypto.SafetyNumber(hash, pending.Offer.CertificateFingerprint, answer.CertificateFingerprint, pending.Offer.PublicKey, answer.PublicKey),
                    endpoints: peerEndpoints,
                    port);
                _logger.LogInformation("[Federation] Accepted the answer from a server that calls itself {Name}; connecting", Printable(answer.Name));
                return (friendId, null);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                port.Dispose();
                return (null, ex.Message);
            }
            finally
            {
                pending.Dispose();
            }
        }

        /// <summary>The admin compared the safety number with the friend and it matched.</summary>
        /// <returns>An error, or null.</returns>
        public async Task<string?> ConfirmAsync(string friendId, CancellationToken cancellationToken)
        {
            // The number is only worth comparing once the link it describes is up.
            if (!_friends.TryGetValue(friendId, out var runtime) || runtime.Link is not { IsClosed: false })
            {
                return "The link to this friend isn't up yet; confirm once it shows connected.";
            }

            var updated = UpdateFriend(friendId, s => s.PairSafetyConfirmed = true);
            if (updated == null)
            {
                return "No paired friend with that id.";
            }

            _logger.LogInformation("[Federation] The safety number for paired friend {Name} was confirmed here", updated.Name);
            await SendConfirmedAndHelloAsync(friendId, sendConfirmed: true, cancellationToken).ConfigureAwait(false);
            return null;
        }

        /// <summary>Status for the admin page: pending codes and paired friends (no secrets).</summary>
        /// <returns>The status.</returns>
        public PairingStatus Status()
        {
            PruneOffers();
            var config = Config;
            return new PairingStatus
            {
                Enabled = IsEnabled(config),
                Available = !ReviewLocked && QuicTransportHost.Available,
                Reason = ReviewLocked ? ReviewLockedMessage : QuicTransportHost.Reason,
                PortFirst = config?.PairingPortFirst ?? 0,
                PortLast = config?.PairingPortLast ?? 0,
                PendingCodes = _offers.Select(o => new PendingCodeStatus { Id = o.Key, Expires = o.Value.Offer.Expires, Port = o.Value.Port }).ToList(),
                Friends = (config?.RemoteServers ?? new List<RemoteServer>()).Where(s => s.Paired).Select(s =>
                {
                    _friends.TryGetValue(s.Id, out var runtime);
                    return new PairedFriendStatus
                    {
                        ServerId = s.Id,
                        Name = s.Name,
                        CallsItself = s.PairClaimedName,
                        State = runtime?.State ?? "stopped",
                        Detail = DirectLink.DirectLinkProtocol.RedactText(runtime?.Detail),
                        SafetyNumber = s.PairSafetyNumber,
                        Confirmed = s.PairSafetyConfirmed,
                        FriendConfirmed = s.PairPeerConfirmed,
                        Ready = s.PairSafetyConfirmed && s.PairPeerConfirmed && !string.IsNullOrEmpty(s.ApiKey) && !string.IsNullOrEmpty(s.IssuedApiKey),
                        Port = s.PairLocalPort,
                        ConnectedSinceUtc = runtime?.Link is { IsClosed: false } link ? link.ConnectedUtc : null,
                        LastConnectedUtc = runtime?.LastConnectedUtc,
                    };
                }).ToList()
            };
        }

        // ------------------------------------------------------------------ peer (over the link)

        /// <summary>The friend's admin confirmed the safety number on their side.</summary>
        public async Task OnPeerConfirmedAsync(string friendId, CancellationToken cancellationToken)
        {
            // Acts once: a repeat changes nothing, saves nothing and sends nothing back
            // (two servers echoing Confirmed would call each other until a limit cut in).
            var changed = false;
            UpdateFriend(friendId, s =>
            {
                changed = !s.PairPeerConfirmed;
                s.PairPeerConfirmed = true;
                return changed;
            });
            if (changed)
            {
                await SendConfirmedAndHelloAsync(friendId, sendConfirmed: false, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The friend introduces itself once both admins confirmed: its federation id and
        /// the token it made for this server. The answer carries this server's.
        /// </summary>
        /// <returns>This server's hello, or an error to refuse with.</returns>
        public (PairingHello? Answer, string? Error) OnHello(string friendId, PairingHello? hello)
        {
            if (hello == null || !IsToken(hello.Token) || !IsFederationId(hello.FederationId))
            {
                return (null, "malformed");
            }

            var config = Config;
            var friend = config?.RemoteServers.FirstOrDefault(s => s.Id == friendId && s.Paired);
            if (config == null || friend == null)
            {
                return (null, "unknown");
            }

            // Both admins must have confirmed: this side here, the friend's through its own
            // Confirmed call. A hello alone never stands in for the friend's confirmation.
            if (!friend.PairSafetyConfirmed)
            {
                return (null, "not confirmed here yet");
            }

            if (!friend.PairPeerConfirmed)
            {
                return (null, "the friend hasn't confirmed yet");
            }

            // A federation id another friend (or this server) already has would let this
            // friend's item tokens resolve to that friend's sharing rules.
            if (string.Equals(hello.FederationId, LocalFederationId(), StringComparison.OrdinalIgnoreCase)
                || config.RemoteServers.Any(s => s.Id != friendId && string.Equals(s.FederationId, hello.FederationId, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("[Federation] Paired friend {Name} claimed a federation id that is already in use; refused", friend.Name);
                return (null, "that federation id is already in use here");
            }

            string? issued = null;
            var updated = UpdateFriend(friendId, s =>
            {
                if (!string.IsNullOrEmpty(s.FederationId) && !string.Equals(s.FederationId, hello.FederationId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                s.FederationId = hello.FederationId;
                s.ApiKey = hello.Token;
                s.FederationPluginVersion = CleanVersion(hello.PluginVersion);
                if (string.IsNullOrEmpty(s.IssuedApiKey))
                {
                    s.IssuedApiKey = FederationTokenAuth.GenerateToken();
                }

                issued = s.IssuedApiKey;
            });
            if (updated == null || issued == null || !string.Equals(updated.FederationId, hello.FederationId, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "the friend's federation id changed");
            }

            _logger.LogInformation("[Federation] Paired friend {Name} is ready", updated.Name);
            return (new PairingHello { FederationId = LocalFederationId(), Token = issued, PluginVersion = PluginVersion() }, null);
        }

        // ------------------------------------------------------------------ links

        private async ValueTask<Stream> OpenLinkStreamAsync(IPAddress address, CancellationToken cancellationToken)
        {
            var id = FederationHttp.PairingIdOf(address);
            var runtime = id == null ? null : _friends.Values.FirstOrDefault(r => r.PairingId == id);
            if (runtime?.Link is not { IsClosed: false } link)
            {
                throw new HttpRequestException("The paired friend's link isn't up right now.");
            }

            try
            {
                var stream = await QuicTransportHost.OpenStream(link.Quic, cancellationToken).ConfigureAwait(false);
                return new DirectLinkConnection.CountingStream(stream, link);
            }
            catch (IOException ex)
            {
                link.MarkClosed(ex.Message);
                throw new HttpRequestException("The paired friend's link closed: " + ex.Message, ex);
            }
        }

        private async Task SuperviseAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Reconcile();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[Federation] Pairing check failed");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Starts a link keeper for each paired friend, and stops the ones no longer wanted.</summary>
        internal void Reconcile()
        {
            PruneOffers();
            var config = Config;
            var wanted = IsEnabled(config) && QuicTransportHost.Available
                ? config!.RemoteServers.Where(s => s.Paired && s.Enabled).ToDictionary(s => s.Id, StringComparer.Ordinal)
                : new Dictionary<string, RemoteServer>(StringComparer.Ordinal);
            foreach (var (id, runtime) in _friends.ToList())
            {
                if (!wanted.ContainsKey(id) && _friends.TryRemove(id, out _))
                {
                    _ = runtime.StopAsync();

                    // Unfriended (not just switched off): its pairing keys go too.
                    if (config?.RemoteServers.Any(s => s.Id == id && s.Paired) != true)
                    {
                        DeleteCertificate(id);
                    }
                }
            }

            foreach (var server in wanted.Values)
            {
                // A pairing that never connected goes away once its codes are long
                // expired; one that connected stays until the admin removes it.
                var neverConnected = server.PairLastConnectedUtc == null;
                var stale = server.PairedAtUtc is { } made && Now().UtcDateTime - made > NeverConnectedLimit;
                if (neverConnected && stale)
                {
                    if (_friends.TryRemove(server.Id, out var idle))
                    {
                        _ = idle.StopAsync();
                    }

                    RemoveNeverConnected(server.Id);
                    continue;
                }

                if (_friends.ContainsKey(server.Id))
                {
                    continue;
                }

                var runtime = CreateRuntime(server, null);
                if (runtime != null && _friends.TryAdd(server.Id, runtime))
                {
                    runtime.Start(_shutdown.Token);
                }
            }
        }

        private Runtime? CreateRuntime(RemoteServer server, PairPort? port)
        {
            if (!ulong.TryParse(server.PairingId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var pairingId)
                || string.IsNullOrEmpty(server.PairKey))
            {
                return null;
            }

            X509Certificate2 certificate;
            try
            {
                certificate = LoadCertificate(server.Id);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("[Federation] Paired friend {Name}: its pairing certificate can't be read ({Error}); pair again with new codes", server.Name, ex.Message);
                port?.Dispose();
                return null;
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(server.PairKey);
            }
            catch (FormatException)
            {
                certificate.Dispose();
                port?.Dispose();
                return null;
            }

            return new Runtime(this, server.Id, pairingId, server.PairLocalPort, (byte)server.PairRole, key, certificate, server.PairPeerFingerprint, port);
        }

        private string AddPairedFriend(string claimedName, byte[] key, X509Certificate2 certificate, byte[] peerFingerprint, byte[] ownFingerprint, string safety, List<IPEndPoint> endpoints, PairPort port)
        {
            var config = Config ?? throw new IOException("The plugin isn't loaded.");
            var friend = new RemoteServer
            {
                Kind = ServerKind.Jellyfin,
                Name = "Paired friend",
                Enabled = true,
                Paired = true,
                PairingId = FederationHttp.FormatPairingId(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8))),
                PairPeerFingerprint = Convert.ToHexString(peerFingerprint).ToLowerInvariant(),
                PairKey = Convert.ToBase64String(key),
                PairRole = ownFingerprint.AsSpan().SequenceCompareTo(peerFingerprint) < 0 ? 0 : 1,
                PairLastEndpoints = endpoints.Select(e => e.ToString()).ToList(),
                PairSafetyNumber = safety,
                PairedAtUtc = Now().UtcDateTime,
                PairLocalPort = port.Port,
                PairClaimedName = claimedName,

                // Nothing is shared until the admin chooses (and the safety number is confirmed).
                ShareAllLibraries = false,
                SharedLibraryFolderIds = new List<string>(),
                AllowDownloads = false,
                AllowRemoteTranscode = false,
                DirectLinkMode = "Off",
                StreamingMode = StreamingMode.Proxy,
            };
            friend.Url = FederationHttp.PairUrl(ulong.Parse(friend.PairingId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
            SaveCertificate(friend.Id, certificate);
            lock (_configGate)
            {
                config.RemoteServers.Add(friend);
                Plugin.Instance!.SaveConfiguration();
            }

            certificate.Dispose();
            var runtime = CreateRuntime(friend, port) ?? throw new IOException("The pairing couldn't start.");
            if (_friends.TryAdd(friend.Id, runtime))
            {
                runtime.Start(_shutdown.Token);
            }

            return friend.Id;
        }

        private void RemoveNeverConnected(string friendId)
        {
            lock (_configGate)
            {
                var config = Config;
                var removed = config?.RemoteServers.RemoveAll(s => s.Id == friendId && s.Paired) ?? 0;
                if (removed > 0)
                {
                    Plugin.Instance!.SaveConfiguration();
                    _logger.LogInformation("[Federation] Removed a pairing that never connected");
                }
            }

            DeleteCertificate(friendId);
        }

        private RemoteServer? UpdateFriend(string friendId, Action<RemoteServer> change)
            => UpdateFriend(friendId, s =>
            {
                change(s);
                return true;
            });

        // Saves only when the change says it changed something.
        private RemoteServer? UpdateFriend(string friendId, Func<RemoteServer, bool> change)
        {
            lock (_configGate)
            {
                var friend = Config?.RemoteServers.FirstOrDefault(s => s.Id == friendId && s.Paired);
                if (friend == null)
                {
                    return null;
                }

                if (change(friend))
                {
                    Plugin.Instance!.SaveConfiguration();
                }

                return friend;
            }
        }

        private async Task SendConfirmedAndHelloAsync(string friendId, bool sendConfirmed, CancellationToken cancellationToken)
        {
            var friend = Config?.RemoteServers.FirstOrDefault(s => s.Id == friendId && s.Paired);
            if (friend == null || !friend.PairSafetyConfirmed || !_friends.TryGetValue(friendId, out var runtime) || runtime.Link is not { IsClosed: false })
            {
                return;
            }

            try
            {
                // A friend's answers here are a few hundred bytes; anything bigger is refused.
                using var client = new HttpClient(FederationHttp.Handler(allowAutoRedirect: false)) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 16 * 1024 };
                if (sendConfirmed)
                {
                    using var confirmed = await client.PostAsync(friend.Url + "/Plugins/Federation/Pairing/Peer/Confirmed", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
                    _ = confirmed.StatusCode;
                }

                // The lower-certificate side introduces itself; the other answers.
                if (!friend.PairPeerConfirmed || friend.PairRole != 0 || !string.IsNullOrEmpty(friend.ApiKey))
                {
                    return;
                }

                string issued = string.Empty;
                UpdateFriend(friendId, s =>
                {
                    if (string.IsNullOrEmpty(s.IssuedApiKey))
                    {
                        s.IssuedApiKey = FederationTokenAuth.GenerateToken();
                    }

                    issued = s.IssuedApiKey;
                });
                var hello = new PairingHello { FederationId = LocalFederationId(), Token = issued, PluginVersion = PluginVersion() };
                using var response = await client.PostAsync(friend.Url + "/Plugins/Federation/Pairing/Peer/Hello", new StringContent(JsonSerializer.Serialize(hello), Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Federation] Paired friend {Name} isn't ready yet (HTTP {Status})", friend.Name, (int)response.StatusCode);
                    return;
                }

                var answer = JsonSerializer.Deserialize<PairingHello>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (answer == null || !IsToken(answer.Token) || !IsFederationId(answer.FederationId))
                {
                    return;
                }

                var config = Config;
                if (config == null || string.Equals(answer.FederationId, hello.FederationId, StringComparison.OrdinalIgnoreCase)
                    || config.RemoteServers.Any(s => s.Id != friendId && string.Equals(s.FederationId, answer.FederationId, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("[Federation] Paired friend {Name} answered with a federation id that is already in use; refused", friend.Name);
                    return;
                }

                UpdateFriend(friendId, s =>
                {
                    s.FederationId = answer.FederationId;
                    s.ApiKey = answer.Token;
                    s.FederationPluginVersion = CleanVersion(answer.PluginVersion);
                });
                _logger.LogInformation("[Federation] Paired friend {Name} is ready", friend.Name);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                _logger.LogDebug(ex, "[Federation] Couldn't tell paired friend {Name} yet", friend.Name);
            }
        }

        private void OnLinkUp(Runtime runtime, DirectLinkConnection link)
        {
            var path = link.RemoteEndPoint;
            UpdateFriend(runtime.FriendId, s =>
            {
                s.PairLastConnectedUtc = Now().UtcDateTime;
                var list = new List<string> { path.ToString() };
                list.AddRange(s.PairLastEndpoints.Where(e => e != path.ToString()).Take(PairingCode.MaxEndpoints - 1));
                s.PairLastEndpoints = list;
            });
            var friendId = runtime.FriendId;
            _ = _forwarder.RunAsync(link, () =>
            {
                var friend = Config?.RemoteServers.FirstOrDefault(s => s.Id == friendId && s.Paired && s.Enabled);
                return friend == null ? null : (friend.Id, friend.IssuedApiKey, friend.PairSafetyConfirmed);
            });
            _ = SendConfirmedAndHelloAsync(friendId, sendConfirmed: true, _shutdown.Token);
        }

        private bool AllowedByJellyfin(IPAddress address)
            => _network == null || _network.ShouldAllowServerAccess(address) == RemoteAccessPolicyResult.Allow;

        // ------------------------------------------------------------------ helpers

        private async Task<(List<IPEndPoint> Endpoints, IPEndPoint? Stun)> DiscoverEndpointsAsync(PairPort port, CancellationToken cancellationToken)
        {
            var servers = await StunClient.ResolveAsync(Config?.DirectLinkStunServers ?? Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
            var (mapped, _) = await StunClient.DiscoverAsync(port.Socket, servers, cancellationToken).ConfigureAwait(false);
            var endpoints = new List<IPEndPoint>();
            if (mapped != null)
            {
                endpoints.Add(mapped);
            }

            foreach (var (address, _) in OwnSubnets())
            {
                if (endpoints.Count >= PairingCode.MaxEndpoints)
                {
                    break;
                }

                var bytes = address.GetAddressBytes();
                var isPrivate = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
                if (isPrivate && (mapped == null || !mapped.Address.Equals(address)))
                {
                    endpoints.Add(new IPEndPoint(address, port.Port));
                }
            }

            return (endpoints.Distinct().Take(PairingCode.MaxEndpoints).ToList(), servers.FirstOrDefault());
        }

        private static List<(IPAddress Address, int PrefixLength)> OwnSubnets()
        {
            var result = new List<(IPAddress, int)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                {
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                        {
                            result.Add((unicast.Address, unicast.PrefixLength));
                        }
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // No interface list; only public endpoints will be accepted.
            }

            return result;
        }

        private HashSet<int> TakenPorts()
        {
            var taken = new HashSet<int>(_offers.Values.Select(o => o.Port));
            foreach (var server in Config?.RemoteServers ?? new List<RemoteServer>())
            {
                if (server.Paired && server.PairLocalPort > 0)
                {
                    taken.Add(server.PairLocalPort);
                }
            }

            return taken;
        }

        private void PruneOffers()
        {
            foreach (var (id, offer) in _offers.ToList())
            {
                if (Now() > offer.Offer.Expires && _offers.TryRemove(id, out _))
                {
                    offer.Dispose();
                }
            }
        }

        private Uri LocalBase()
        {
            var text = _library?.GetInternalPlaybackBaseUrl() ?? "http://127.0.0.1:8096";
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : new Uri("http://127.0.0.1:8096");
        }

        private string ServerName()
        {
            var name = _host?.FriendlyName;
            return string.IsNullOrWhiteSpace(name) ? "Jellyfin" : name;
        }

        private string LocalFederationId()
        {
            lock (_configGate)
            {
                // Same identity the friend service uses (made once, then kept).
                var config = Plugin.Instance!.Configuration;
                if (string.IsNullOrEmpty(config.LocalFederationId))
                {
                    config.LocalFederationId = Guid.NewGuid().ToString();
                    Plugin.Instance.SaveConfiguration();
                }

                return config.LocalFederationId;
            }
        }

        // A friend's version string, only as a short plain version (it is shown and stored).
        private static string CleanVersion(string? version)
            => version is { Length: > 0 and <= 32 } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? version : string.Empty;

        // A name a code claims, made safe for one log line.
        private static string Printable(string? name)
            => new string((name ?? string.Empty).Where(c => !char.IsControl(c)).Take(64).ToArray());

        private static string PluginVersion()
            => typeof(Plugin).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;

        private static bool IsToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);

        private static bool IsFederationId(string? id) => id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        private static X509Certificate2 CreateCertificate()
        {
            using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=jellyfin-federation-pairing", signing, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") },
                false));

            // Pinned by hash and never checked for expiry; a long life means it never needs replacing.
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(50));
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }

        private string CertificatePath(string friendId)
        {
            if (!Guid.TryParse(friendId, out var id))
            {
                throw new IOException("Bad friend id.");
            }

            var folder = Path.Combine(DataFolder ?? Plugin.Instance?.DataFolderPath ?? throw new IOException("The plugin isn't loaded."), CertificateFolder);
            return Path.Combine(folder, id.ToString("N") + ".pfx");
        }

        private void SaveCertificate(string friendId, X509Certificate2 certificate)
        {
            var path = CertificatePath(friendId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, Array.Empty<byte>());
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            try
            {
                File.WriteAllBytes(temp, certificate.Export(X509ContentType.Pkcs12));
                File.Move(temp, path, overwrite: false);
            }
            finally
            {
                // Only this call's own temporary file; gone already after the move.
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        private X509Certificate2 LoadCertificate(string friendId)
        {
            var loaded = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath(friendId), null, X509KeyStorageFlags.Exportable);
            if (!loaded.HasPrivateKey)
            {
                loaded.Dispose();
                throw new CryptographicException("The pairing certificate has no key.");
            }

            return loaded;
        }

        internal void DeleteCertificate(string friendId)
        {
            try
            {
                File.Delete(CertificatePath(friendId));
            }
            catch (IOException)
            {
                // Gone already.
            }
        }

        // ------------------------------------------------------------------ types

        private sealed class PendingOffer : IDisposable
        {
            private readonly IPEndPoint? _stun;
            private readonly CancellationTokenSource _stop = new();
            private PairPort? _port;
            private X509Certificate2? _certificate;

            public PendingOffer(PairingCode offer, byte[] bytes, ECDiffieHellman ephemeral, X509Certificate2 certificate, PairPort port, IPEndPoint? stun)
            {
                Offer = offer;
                Bytes = bytes;
                Ephemeral = ephemeral;
                _certificate = certificate;
                _port = port;
                Port = port.Port;
                _stun = stun;
            }

            public PairingCode Offer { get; }

            public byte[] Bytes { get; }

            public ECDiffieHellman Ephemeral { get; }

            public int Port { get; }

            public void StartKeepAlive(CancellationToken shutdown)
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _stop.Token);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(20), stop.Token).ConfigureAwait(false);
                            if (_stun != null && Volatile.Read(ref _port) is { } port)
                            {
                                port.KeepAlive(_stun);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Used, cancelled or expired.
                    }
                    finally
                    {
                        stop.Dispose();
                    }
                });
            }

            public PairPort TakePort() => Interlocked.Exchange(ref _port, null) ?? throw new IOException("The code's port is gone.");

            public X509Certificate2 TakeCertificate() => Interlocked.Exchange(ref _certificate, null) ?? throw new IOException("The code's certificate is gone.");

            public void Dispose()
            {
                try
                {
                    _stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already.
                }

                Interlocked.Exchange(ref _port, null)?.Dispose();
                Interlocked.Exchange(ref _certificate, null)?.Dispose();
                Ephemeral.Dispose();
                Offer.Secret.AsSpan().Clear();
            }
        }

        /// <summary>Keeps one paired friend's link up.</summary>
        private sealed class Runtime
        {
            private readonly PairingService _owner;
            private readonly byte[] _key;
            private readonly X509Certificate2 _certificate;
            private readonly string _peerFingerprint;
            private readonly CancellationTokenSource _stop = new();
            private PairPort? _port;
            private Task? _loop;

            public Runtime(PairingService owner, string friendId, ulong pairingId, int localPort, byte role, byte[] key, X509Certificate2 certificate, string peerFingerprint, PairPort? port)
            {
                _owner = owner;
                FriendId = friendId;
                PairingId = pairingId;
                LocalPort = localPort;
                Role = role;
                _key = key;
                _certificate = certificate;
                _peerFingerprint = peerFingerprint;
                _port = port;
            }

            public string FriendId { get; }

            public ulong PairingId { get; }

            public int LocalPort { get; }

            public byte Role { get; }

            public DirectLinkConnection? Link { get; private set; }

            public string State { get; private set; } = "connecting";

            public string Detail { get; private set; } = "Looking for the friend…";

            public DateTime? LastConnectedUtc { get; private set; }

            public void Start(CancellationToken shutdown)
            {
                var token = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _stop.Token).Token;
                _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
            }

            public async Task StopAsync()
            {
                try
                {
                    _stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (_loop != null)
                {
                    await _loop.ConfigureAwait(false);
                }

                if (Link != null)
                {
                    await Link.DisposeAsync().ConfigureAwait(false);
                }

                Interlocked.Exchange(ref _port, null)?.Dispose();
                _certificate.Dispose();
                _stop.Dispose();
            }

            private async Task RunAsync(CancellationToken cancellationToken)
            {
                var transcript = Transcript();
                var proofKey = PairingCrypto.SubKey(_key, "proof");
                var failures = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var port = Interlocked.Exchange(ref _port, null) ?? PairPort.Bind(LocalPort, Role);
                        IPEndPoint? path;
                        using (port)
                        {
                            port.SetKey(_key, Role);
                            State = LastConnectedUtc == null ? "connecting" : "reconnecting";
                            Detail = LastConnectedUtc == null ? "Looking for the friend…" : "The link dropped; looking for the friend again…";
                            var started = DateTime.UtcNow;
                            var everConnected = LastConnectedUtc != null || Config?.RemoteServers.FirstOrDefault(s => s.Id == FriendId)?.PairLastConnectedUtc != null;
                            path = await port.PunchAsync(Targets, round => Schedule(round, DateTime.UtcNow - started, everConnected), cancellationToken).ConfigureAwait(false);
                        }

                        if (path == null)
                        {
                            continue;
                        }

                        // The path is where an authenticated packet came from, which the
                        // friend can choose: the same rules as any endpoint it lists.
                        if (!PairingEndpoints.IsAcceptable(path, OwnSubnets(), AllowPrivateForTests))
                        {
                            State = "refused";
                            Detail = "The friend answered from an address that isn't allowed.";
                            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (!_owner.AllowedByJellyfin(path.Address))
                        {
                            State = "refused";
                            Detail = "Jellyfin's network settings don't allow the friend's address " + DirectLink.DirectLinkProtocol.Redact(path.Address) + ".";
                            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        object quic;
                        if (Role == 0)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                            quic = await QuicTransportHost.ConnectPaired(LocalPort, path, _certificate, _peerFingerprint, proofKey, transcript, 32, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            quic = await QuicTransportHost.AcceptPaired(LocalPort, path, _certificate, _peerFingerprint, proofKey, transcript, 32, ListenWindow, cancellationToken).ConfigureAwait(false);
                        }

                        var link = new DirectLinkConnection(quic, FriendId, false, null);
                        Link = link;
                        LastConnectedUtc = DateTime.UtcNow;
                        failures = 0;
                        State = "connected";
                        Detail = "Connected via " + DirectLink.DirectLinkProtocol.Redact(path);
                        _owner._logger.LogInformation("[Federation] Paired link to {Friend} is up via {Path}", FriendId, DirectLink.DirectLinkProtocol.Redact(path));
                        _owner.OnLinkUp(this, link);
                        await link.Closed.WaitAsync(cancellationToken).ConfigureAwait(false);
                        _owner._logger.LogInformation("[Federation] Paired link to {Friend} dropped: {Reason}", FriendId, DirectLink.DirectLinkProtocol.RedactText(link.CloseReason));
                        Link = null;
                        await link.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
                    {
                        failures++;
                        State = "reconnecting";
                        var detail = "The link didn't come up: " + ex.Message;
                        if (detail != Detail)
                        {
                            _owner._logger.LogInformation("[Federation] Paired link to {Friend}: {Detail}", FriendId, DirectLink.DirectLinkProtocol.RedactText(detail));
                        }

                        Detail = detail;
                        _owner._logger.LogDebug(ex, "[Federation] Paired link to {Friend} didn't come up", FriendId);
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * failures)), cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }

            private IReadOnlyList<IPEndPoint> Targets()
            {
                var friend = Config?.RemoteServers.FirstOrDefault(s => s.Id == FriendId);
                return (friend?.PairLastEndpoints ?? new List<string>())
                    .Select(e => IPEndPoint.TryParse(e, out var ep) ? ep : null)
                    .Where(e => e != null && PairingEndpoints.IsAcceptable(e, OwnSubnets(), AllowPrivateForTests))
                    .Cast<IPEndPoint>()
                    .ToList();
            }

            /// <summary>
            /// Probe at once, then every 250 ms for 8 s, every 15 s for 10 minutes, then every
            /// 2 minutes. A pairing that never connected only knows addresses from a code, so it
            /// stops probing them after 10 minutes (it still answers the friend's probes).
            /// </summary>
            internal static TimeSpan? Schedule(int round, TimeSpan elapsed, bool everConnected = true)
                => round == 0 ? TimeSpan.Zero
                    : elapsed < TimeSpan.FromSeconds(8) ? TimeSpan.FromMilliseconds(250)
                    : elapsed < TimeSpan.FromMinutes(10) ? TimeSpan.FromSeconds(15)
                    : everConnected ? TimeSpan.FromMinutes(2) : null;

            private byte[] Transcript()
            {
                // Client certificate first, then the server's, as both sides see them.
                var own = SHA256.HashData(_certificate.GetRawCertData());
                var peer = Convert.FromHexString(_peerFingerprint);
                return Role == 0 ? PairingCrypto.Concat(own, peer) : PairingCrypto.Concat(peer, own);
            }
        }
    }

    /// <summary>The introduction two paired servers swap once both admins confirmed.</summary>
    public sealed class PairingHello
    {
        /// <summary>Gets or sets the sender's federation id.</summary>
        public string FederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the token the sender made for the receiver to call it with.</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>Gets or sets the sender's plugin version.</summary>
        public string? PluginVersion { get; set; }
    }

    /// <summary>Pairing status for the admin page.</summary>
    public sealed class PairingStatus
    {
        /// <summary>Gets or sets a value indicating whether pairing is switched on.</summary>
        public bool Enabled { get; set; }

        /// <summary>Gets or sets a value indicating whether this server can pair (QUIC works).</summary>
        public bool Available { get; set; }

        /// <summary>Gets or sets why not, or the QUIC library version.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Gets or sets the first pairing port.</summary>
        public int PortFirst { get; set; }

        /// <summary>Gets or sets the last pairing port.</summary>
        public int PortLast { get; set; }

        /// <summary>Gets or sets the unused codes this server made.</summary>
        public List<PendingCodeStatus> PendingCodes { get; set; } = new();

        /// <summary>Gets or sets the paired friends.</summary>
        public List<PairedFriendStatus> Friends { get; set; } = new();
    }

    /// <summary>One unused code.</summary>
    public sealed class PendingCodeStatus
    {
        /// <summary>Gets or sets the id to cancel it with.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets when it expires.</summary>
        public DateTimeOffset Expires { get; set; }

        /// <summary>Gets or sets its port.</summary>
        public int Port { get; set; }
    }

    /// <summary>One paired friend.</summary>
    public sealed class PairedFriendStatus
    {
        /// <summary>Gets or sets the friend id.</summary>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>Gets or sets the name the admin gave it.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the name its code gave (untrusted).</summary>
        public string CallsItself { get; set; } = string.Empty;

        /// <summary>Gets or sets the link state.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>Gets or sets a readable detail.</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>Gets or sets the safety number to compare.</summary>
        public string SafetyNumber { get; set; } = string.Empty;

        /// <summary>Gets or sets a value indicating whether this admin confirmed it.</summary>
        public bool Confirmed { get; set; }

        /// <summary>Gets or sets a value indicating whether the friend's admin confirmed it.</summary>
        public bool FriendConfirmed { get; set; }

        /// <summary>Gets or sets a value indicating whether the friendship is set up (tokens swapped).</summary>
        public bool Ready { get; set; }

        /// <summary>Gets or sets this server's port for the pairing.</summary>
        public int Port { get; set; }

        /// <summary>Gets or sets since when the link is up.</summary>
        public DateTime? ConnectedSinceUtc { get; set; }

        /// <summary>Gets or sets when the link was last up.</summary>
        public DateTime? LastConnectedUtc { get; set; }
    }
}
