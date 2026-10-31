using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>Where a friend's direct link stands.</summary>
    public enum DirectLinkState
    {
        /// <summary>Switched off here (globally or for this friend).</summary>
        Off,

        /// <summary>Not possible: this server has no QUIC, or the friend doesn't support it.</summary>
        Unsupported,

        /// <summary>Being set up.</summary>
        Connecting,

        /// <summary>Up and carrying requests.</summary>
        Connected,

        /// <summary>The last attempt failed; retrying later. Traffic uses the friend's address meanwhile.</summary>
        Failed
    }

    /// <summary>One friend's direct link status (admin view).</summary>
    public sealed class DirectLinkFriendStatus
    {
        /// <summary>Gets or sets the friend's configured server id.</summary>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>Gets or sets the friend's name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the state (off, unsupported, connecting, connected, failed).</summary>
        public string State { get; set; } = "off";

        /// <summary>Gets or sets a readable explanation of the state.</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>Gets or sets the friend's UDP endpoint when connected.</summary>
        public string RemoteEndpoint { get; set; } = string.Empty;

        /// <summary>Gets or sets this server's router kind (cone, symmetric, unknown).</summary>
        public string NatLocal { get; set; } = "unknown";

        /// <summary>Gets or sets the friend's router kind.</summary>
        public string NatRemote { get; set; } = "unknown";

        /// <summary>Gets or sets the measured speed of the link, in Mbps (0 = not measured).</summary>
        public double Mbps { get; set; }

        /// <summary>Gets or sets when the link came up.</summary>
        public DateTime? ConnectedSinceUtc { get; set; }

        /// <summary>Gets or sets the last failure.</summary>
        public string? LastError { get; set; }

        /// <summary>Gets or sets when the next attempt is due.</summary>
        public DateTime? NextRetryUtc { get; set; }

        /// <summary>Gets or sets bytes received over the link.</summary>
        public long BytesIn { get; set; }

        /// <summary>Gets or sets bytes sent over the link.</summary>
        public long BytesOut { get; set; }

        /// <summary>Gets or sets requests in flight.</summary>
        public int ActiveStreams { get; set; }

        /// <summary>Gets or sets a value indicating whether the friend has a link open to this server (it fetches from here).</summary>
        public bool InboundConnected { get; set; }

        /// <summary>Gets or sets bytes this server sent the friend over its inbound link.</summary>
        public long InboundBytesOut { get; set; }

        /// <summary>Gets or sets the local UDP port of the friend's inbound link (0 = none).</summary>
        public int InboundLocalPort { get; set; }
    }

    /// <summary>The whole direct link status (admin view).</summary>
    public sealed class DirectLinkStatus
    {
        /// <summary>Gets or sets a value indicating whether QUIC works on this server.</summary>
        public bool Available { get; set; }

        /// <summary>Gets or sets why not, when it doesn't.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Gets or sets a value indicating whether direct links are switched on.</summary>
        public bool Enabled { get; set; }

        /// <summary>Gets or sets this server's public UDP endpoint from the last STUN answer.</summary>
        public string PublicEndpoint { get; set; } = string.Empty;

        /// <summary>Gets or sets this server's router kind from the last STUN answers.</summary>
        public string Nat { get; set; } = "unknown";

        /// <summary>Gets or sets the local UDP port of an inbound link, when one is up (0 = none; diagnostics).</summary>
        public int LocalPort { get; set; }

        /// <summary>Gets or sets this server's key fingerprint (what friends pin).</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>Gets or sets how many segments players got from read-ahead (since start).</summary>
        public long ReadAheadServed { get; set; }

        /// <summary>Gets or sets how many segments players asked the friend for themselves (since start).</summary>
        public long ReadAheadMissed { get; set; }

        /// <summary>Gets or sets the per-friend status.</summary>
        public List<DirectLinkFriendStatus> Friends { get; set; } = new();
    }

    /// <summary>
    /// Runs direct links: for each friend that supports them, opens one (this
    /// server as initiator, fetching the friend's media over it), keeps it up and
    /// retries with back-off; and answers friends' offers (this server as
    /// responder, serving its media to them over it). Everything falls back to
    /// the friend's normal address whenever a link isn't up.
    /// </summary>
    public sealed class DirectLinkManager : IHostedService, IAsyncDisposable
    {
        /// <summary>The back-off between attempts after failures.</summary>
        internal static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)
        };

        private const int MaxInboundLinks = 32;
        private const int MaxConcurrentResponderPunches = 4;
        private const int MaxConcurrentOutboundAttempts = 4;
        private static readonly TimeSpan StableLink = TimeSpan.FromMinutes(2);
        private const int MaxOffersPerWindow = 10;
        private static readonly TimeSpan OfferWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CapabilityRecheck = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(30);

        private readonly ILogger<DirectLinkManager> _logger;
        private readonly IRemoteServerClientFactory _clients;
        private readonly FederationLibraryManager? _library;
        private readonly MediaBrowser.Common.Net.INetworkManager? _network;
        private readonly SemaphoreSlim _responderPunches = new(MaxConcurrentResponderPunches);
        private readonly SemaphoreSlim _outboundAttempts = new(MaxConcurrentOutboundAttempts);
        private readonly ConcurrentDictionary<string, Outbound> _outbound = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DirectLinkConnection> _inbound = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _offers = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _identityGate = new();
        private readonly object _inboundGate = new();
        private DirectLinkIdentity? _identity;
        private Task? _loop;
        private int _stopped;
        private string _publicEndpoint = string.Empty;
        private NatKind _lastNat = NatKind.Unknown;

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectLinkManager"/> class.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="clients">Friend clients (signaling goes over the friend's normal address).</param>
        /// <param name="library">Gives this server's own loopback address for forwarding.</param>
        /// <param name="network">Jellyfin's network rules (remote access and IP filters apply to friends' link addresses).</param>
        public DirectLinkManager(ILogger<DirectLinkManager> logger, IRemoteServerClientFactory clients, FederationLibraryManager? library = null, MediaBrowser.Common.Net.INetworkManager? network = null)
        {
            _network = network;
            _logger = logger;
            _clients = clients;
            _library = library;
            Forwarder = new DirectLinkForwarder(LocalBase, logger);
            Current = this;
        }

        /// <summary>
        /// Gets the running manager, for code built outside dependency injection
        /// (friend clients). Null before the plugin starts.
        /// </summary>
        public static DirectLinkManager? Current { get; private set; }

        /// <summary>Gets the responder's request forwarder.</summary>
        internal DirectLinkForwarder Forwarder { get; }

        /// <summary>Gets or sets the clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>
        /// The protocol version to advertise in <c>Peer/SystemInfo</c>: 1 when this
        /// server accepts direct links, else 0.
        /// </summary>
        public static int AdvertisedVersion()
            => Plugin.Instance?.Configuration?.DirectLinkOptIn == true && QuicTransportHost.Available ? DirectLinkProtocol.Version : 0;

        /// <summary>
        /// The link client for friend <paramref name="serverId"/> when its direct
        /// link is up. Requests sent with it go to the friend over the link (only
        /// the play, direct stream and speed-test routes are served there).
        /// </summary>
        public bool TryGetClient(string serverId, out HttpClient client)
        {
            if (_outbound.TryGetValue(serverId, out var state)
                && state.Link is { IsClosed: false, Http: { } http })
            {
                client = http;
                return true;
            }

            client = null!;
            return false;
        }

        /// <summary>Whether friend <paramref name="serverId"/>'s direct link is up.</summary>
        public bool IsConnected(string serverId) => TryGetClient(serverId, out _);

        /// <summary>Forgets back-off for a friend so the next pass tries now (settings changed, admin asked).</summary>
        public void Reconsider(string serverId)
        {
            if (_outbound.TryGetValue(serverId, out var state))
            {
                lock (state)
                {
                    state.NextAttemptUtc = DateTime.MinValue;
                    state.Failures = 0;
                    state.CapabilityCheckedUtc = DateTime.MinValue;
                }
            }

            CancellationToken token;
            try
            {
                token = _shutdown.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => PassAsync(token));
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (QuicTransportHost.Available)
            {
                _logger.LogInformation("[Federation] Direct links are available on this server (QUIC ready)");
            }
            else
            {
                _logger.LogInformation("[Federation] Direct links aren't available on this server: {Reason}", QuicTransportHost.Reason);
            }

            _loop = Task.Run(() => LoopAsync(_shutdown.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // The host stops it, then disposes it, and it's registered twice (as
            // itself and as a hosted service), so this runs more than once.
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

            if (_loop != null)
            {
                try
                {
                    await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down regardless.
                }
            }

            foreach (var state in _outbound.Values)
            {
                if (state.Link != null)
                {
                    await state.Link.DisposeAsync().ConfigureAwait(false);
                }
            }

            foreach (var link in _inbound.Values)
            {
                await link.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            // _shutdown and _responderPunches are left undisposed on purpose: late
            // callbacks (Reconsider, a responder finishing) may still use them, and
            // neither holds anything that needs releasing.
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>The admin status.</summary>
        public DirectLinkStatus GetStatus()
        {
            var config = Plugin.Instance?.Configuration;
            var status = new DirectLinkStatus
            {
                Available = QuicTransportHost.Available,
                Reason = QuicTransportHost.Reason,
                Enabled = config?.DirectLinkOptIn == true,
                PublicEndpoint = _publicEndpoint,
                Nat = DirectLinkProtocol.NatName(_lastNat),
                Fingerprint = QuicTransportHost.Available ? Identity().Fingerprint : string.Empty,
                ReadAheadServed = SegmentReadAhead.Shared.Served,
                ReadAheadMissed = SegmentReadAhead.Shared.Missed
            };

            foreach (var server in (config?.RemoteServers ?? new List<RemoteServer>()).Where(s => s.Kind == ServerKind.Jellyfin))
            {
                var entry = new DirectLinkFriendStatus { ServerId = server.Id, Name = server.Name };
                if (_outbound.TryGetValue(server.Id, out var state))
                {
                    lock (state)
                    {
                        entry.State = state.State.ToString().ToLowerInvariant();
                        entry.Detail = DirectLinkProtocol.RedactText(state.Detail);
                        entry.NatLocal = DirectLinkProtocol.NatName(state.NatLocal);
                        entry.NatRemote = DirectLinkProtocol.NatName(state.NatRemote);
                        entry.LastError = DirectLinkProtocol.RedactText(state.LastError);
                        entry.NextRetryUtc = state.State == DirectLinkState.Failed ? state.NextAttemptUtc : null;
                        if (state.Link is { IsClosed: false } link)
                        {
                            entry.RemoteEndpoint = DirectLinkProtocol.Redact(link.RemoteEndPoint);
                            entry.Mbps = Math.Round(link.Mbps, 1);
                            entry.ConnectedSinceUtc = link.ConnectedUtc;
                            entry.BytesIn = link.BytesIn;
                            entry.BytesOut = link.BytesOut;
                            entry.ActiveStreams = link.ActiveStreams;
                        }
                    }
                }
                else
                {
                    var (state0, detail) = Eligibility(server, config);
                    entry.State = state0.ToString().ToLowerInvariant();
                    entry.Detail = DirectLinkProtocol.RedactText(detail);
                }

                if (_inbound.TryGetValue(server.Id, out var inbound) && !inbound.IsClosed)
                {
                    entry.InboundConnected = true;
                    entry.InboundBytesOut = inbound.BytesOut;
                    entry.InboundLocalPort = inbound.LocalEndPoint.Port;
                    if (status.LocalPort == 0)
                    {
                        status.LocalPort = entry.InboundLocalPort;
                    }
                }

                status.Friends.Add(entry);
            }

            return status;
        }

        /// <summary>
        /// Responder: answers friend <paramref name="caller"/>'s offer. Returns the
        /// HTTP status and, on 200, the answer; on refusal, a message.
        /// </summary>
        public async Task<(int Status, DirectLinkAnswer? Answer, string? Message)> HandleOfferAsync(RemoteServer caller, DirectLinkOffer? offer, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.DirectLinkOptIn != true || string.Equals(caller.DirectLinkMode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return (403, null, "Direct links are switched off on this server.");
            }

            if (!QuicTransportHost.Available)
            {
                return (503, null, "Direct links aren't available on this server: " + QuicTransportHost.Reason);
            }

            if (offer == null
                || offer.Version != DirectLinkProtocol.Version
                || !DirectLinkProtocol.IsSessionId(offer.SessionId)
                || !DirectLinkIdentity.IsFingerprint(offer.Fingerprint)
                || DirectLinkProtocol.ParseSecret(offer.Secret) is not { } initiatorSecret
                || offer.Candidates == null
                || offer.Candidates.Count > DirectLinkProtocol.MaxCandidates)
            {
                return (400, null, "Malformed offer.");
            }

            if (!TakeOfferSlot(caller.Id))
            {
                return (429, null, "Too many direct link attempts; try again later.");
            }

            if (!_inbound.ContainsKey(caller.Id) && _inbound.Values.Count(l => !l.IsClosed) >= MaxInboundLinks)
            {
                return (503, null, "This server has as many direct links as it takes.");
            }

            var allowPrivate = AllowPrivateFor(caller);
            var allowTailscale = AllowTailscaleFor(caller);
            var candidates = await AcceptPeerCandidatesAsync(offer.Candidates, caller, allowPrivate, allowTailscale, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return (400, null, "The offer had no usable address.");
            }

            var peerNat = DirectLinkProtocol.ParseNat(offer.NatType);
            if (!await _responderPunches.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return (503, null, "This server is already setting up several direct links; try again shortly.");
            }

            Socket? socket = null;
            var handedOff = false;
            try
            {
                socket = NewSocket();
                var (publicEndpoint, nat) = await DiscoverAsync(socket, cancellationToken).ConfigureAwait(false);
                if (nat == NatKind.Symmetric && peerNat == NatKind.Symmetric)
                {
                    socket.Dispose();
                    return (409, null, "Both servers are behind strict routers (symmetric NAT); a direct link can't get through.");
                }

                var own = LocalCandidates(socket, publicEndpoint, allowPrivate, allowTailscale);
                if (own.Count == 0)
                {
                    socket.Dispose();
                    return (503, null, "This server couldn't find its public address (STUN didn't answer; is outgoing UDP blocked?).");
                }

                var identity = Identity();
                var responderSecret = DirectLinkProtocol.NewSecret();
                var sessionKey = DirectLinkProtocol.SessionKey(initiatorSecret, responderSecret, offer.SessionId);
                var answer = new DirectLinkAnswer
                {
                    Fingerprint = identity.Fingerprint,
                    Secret = Convert.ToBase64String(responderSecret),
                    Candidates = own.Select(c => DirectLinkProtocol.ToCandidate(c.Endpoint, c.Kind)).ToList(),
                    NatType = DirectLinkProtocol.NatName(nat)
                };

                var sessionSocket = socket;
                socket = null;
                handedOff = true;
                var released = 0;
                void ReleaseSlot()
                {
                    if (Interlocked.Exchange(ref released, 1) == 0)
                    {
                        _responderPunches.Release();
                    }
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RespondAsync(caller, offer, sessionSocket, candidates, peerNat, sessionKey, identity, ReleaseSlot, _shutdown.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        ReleaseSlot();
                    }
                });
                return (200, answer, null);
            }
            catch (SocketException ex)
            {
                return (503, null, "This server's network refused UDP: " + ex.Message);
            }
            finally
            {
                socket?.Dispose();
                if (!handedOff)
                {
                    _responderPunches.Release();
                }
            }
        }

        /// <summary>One pass over the friends: starts links that are due.</summary>
        internal async Task PassAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            var attempts = new List<Task>();
            foreach (var server in config.RemoteServers.Where(s => s.Kind == ServerKind.Jellyfin).ToList())
            {
                var state = _outbound.GetOrAdd(server.Id, _ => new Outbound());
                var (eligible, detail) = Eligibility(server, config);
                if (eligible != DirectLinkState.Connecting)
                {
                    await DropAsync(state, eligible, detail).ConfigureAwait(false);
                    continue;
                }

                lock (state)
                {
                    if (state.Busy || state.Link is { IsClosed: false } || UtcNow() < state.NextAttemptUtc)
                    {
                        continue;
                    }

                    state.Busy = true;
                }

                attempts.Add(AttemptAsync(server, state, cancellationToken));
            }

            await DropIneligibleInboundAsync(config).ConfigureAwait(false);

            foreach (var gone in _outbound.Keys.Where(id => !config.RemoteServers.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                if (_outbound.TryRemove(gone, out var removed) && removed.Link != null)
                {
                    await removed.Link.DisposeAsync().ConfigureAwait(false);
                }
            }

            await Task.WhenAll(attempts).ConfigureAwait(false);
        }

        /// <summary>
        /// Closes friends' links to this server that settings no longer allow:
        /// direct links switched off, the friend switched off, disabled or
        /// removed, or Jellyfin's remote-access/IP rules now refusing its address.
        /// </summary>
        internal async Task DropIneligibleInboundAsync(PluginConfiguration config)
        {
            foreach (var (friendId, link) in _inbound.ToList())
            {
                var friend = config.RemoteServers.FirstOrDefault(s => string.Equals(s.Id, friendId, StringComparison.OrdinalIgnoreCase));
                var reason = InboundRefusal(config, friendId, link.RemoteEndPoint);
                if (reason == null)
                {
                    continue;
                }

                if (_inbound.TryRemove(new KeyValuePair<string, DirectLinkConnection>(friendId, link)))
                {
                    _logger.LogInformation("[Federation] Closed the direct link from {Friend}: {Reason}", friend?.Name ?? friendId, DirectLinkProtocol.RedactText(reason));
                    link.MarkClosed(reason);
                    await link.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private string? InboundRefusal(PluginConfiguration? config, string friendId, IPEndPoint remote)
        {
            var friend = config?.RemoteServers.FirstOrDefault(s => string.Equals(s.Id, friendId, StringComparison.OrdinalIgnoreCase));
            return config?.DirectLinkOptIn != true ? "direct links were switched off here"
                : friend == null ? "the friend was removed"
                : !friend.Enabled ? "the friend was disabled"
                : string.Equals(friend.DirectLinkMode, "Off", StringComparison.OrdinalIgnoreCase) ? "direct links were switched off for this friend"
                : _network != null && _network.ShouldAllowServerAccess(remote.Address) != MediaBrowser.Common.Net.RemoteAccessPolicyResult.Allow ? "Jellyfin's network settings no longer allow its address"
                : null;
        }

        /// <summary>The measured speed of friend <paramref name="serverId"/>'s link (0 when down or not measured).</summary>
        public double LinkMbps(string serverId)
            => _outbound.TryGetValue(serverId, out var state) && state.Link is { IsClosed: false } link ? link.Mbps : 0;

        /// <summary>Whether a link to this friend should be attempted, and why not.</summary>
        internal static (DirectLinkState State, string Detail) Eligibility(RemoteServer server, PluginConfiguration? config)
        {
            if (config?.DirectLinkOptIn != true)
            {
                return (DirectLinkState.Off, "Direct links are switched off in settings.");
            }

            if (!server.Enabled || string.Equals(server.DirectLinkMode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return (DirectLinkState.Off, "Switched off for this friend.");
            }

            if (!QuicTransportHost.Available)
            {
                return (DirectLinkState.Unsupported, "Not available on this server: " + QuicTransportHost.Reason + ".");
            }

            if (string.IsNullOrEmpty(server.ApiKey))
            {
                return (DirectLinkState.Unsupported, "Not connected to this friend yet.");
            }

            if (!DirectLinkProtocol.TestMode && !DirectLinkProtocol.IsSignalingSafe(RemoteServerClientFactory.EffectiveUrl(server)))
            {
                return (DirectLinkState.Unsupported, "Needs an https (or Tailscale) address for this friend, so the link's keys can't be swapped in transit.");
            }

            return (DirectLinkState.Connecting, string.Empty);
        }

        /// <summary>The wait before the next attempt after <paramref name="failures"/> failures in a row.</summary>
        internal static TimeSpan BackoffAfter(int failures)
            => Backoff[Math.Clamp(failures - 1, 0, Backoff.Length - 1)];

        private static Socket NewSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            return socket;
        }

        private static bool AllowPrivateFor(RemoteServer server)
        {
            if (DirectLinkProtocol.TestMode)
            {
                return true;
            }

            // A friend configured by a LAN address is on this LAN: its local
            // addresses are fair to aim at.
            return Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
                && IPAddress.TryParse(uri.Host, out var ip)
                && ip.AddressFamily == AddressFamily.InterNetwork
                && ip.GetAddressBytes() is var b
                && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }

        private static bool AllowTailscaleFor(RemoteServer server)
        {
            // Tailscale addresses are only aimed at when this friend is itself
            // reached over Tailscale (or in the smoke test).
            if (DirectLinkProtocol.TestMode)
            {
                return true;
            }

            return Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
                && (uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
                    || (IPAddress.TryParse(uri.Host, out var ip) && DirectLinkProtocol.IsTailscale(ip)));
        }

        private static async Task<List<IPEndPoint>> AcceptPeerCandidatesAsync(
            IEnumerable<DirectLinkCandidate>? offered,
            RemoteServer server,
            bool allowPrivate,
            bool allowTailscale,
            CancellationToken cancellationToken)
        {
            var candidates = DirectLinkProtocol.AcceptCandidates(offered, allowPrivate, allowTailscale);
            if (DirectLinkProtocol.TestMode || candidates.Count == 0 || !Uri.TryCreate(server.Url, UriKind.Absolute, out var uri))
            {
                return candidates;
            }

            var resolved = new HashSet<IPAddress>();
            try
            {
                foreach (var address in await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false))
                {
                    resolved.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
                }
            }
            catch (SocketException)
            {
                return new List<IPEndPoint>();
            }

            var ownSubnets = new List<(IPAddress Address, int PrefixLength)>();
            var hasTailscale = false;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                {
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    {
                        ownSubnets.Add((unicast.Address, unicast.PrefixLength));
                        hasTailscale |= DirectLinkProtocol.IsTailscale(unicast.Address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }

            return candidates.Where(endpoint =>
            {
                var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
                if (resolved.Contains(address))
                {
                    return true;
                }

                var bytes = address.GetAddressBytes();
                var isPrivate = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168);
                return (allowPrivate && isPrivate && Pairing.PairingEndpoints.IsAcceptable(endpoint, ownSubnets))
                    || (allowTailscale && hasTailscale && DirectLinkProtocol.IsTailscale(address));
            }).ToList();
        }

        private List<(IPEndPoint Endpoint, string Kind)> LocalCandidates(Socket socket, IPEndPoint? publicEndpoint, bool allowPrivate, bool allowTailscale)
        {
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            var result = new List<(IPEndPoint, string)>();
            if (publicEndpoint != null)
            {
                result.Add((publicEndpoint, "srflx"));
            }

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                {
                    foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
                    {
                        var candidate = new DirectLinkCandidate { Kind = "host", Address = address.ToString(), Port = port };
                        if ((DirectLinkProtocol.IsTailscale(address) ? allowTailscale : allowPrivate) && DirectLinkProtocol.AcceptCandidate(candidate, allowPrivate, allowTailscale) is { } endpoint)
                        {
                            result.Add((endpoint, "host"));
                        }
                    }
                }
            }
            catch (NetworkInformationException ex)
            {
                _logger.LogDebug(ex, "[Federation] Direct link: couldn't list network interfaces");
            }

            return result.DistinctBy(r => r.Item1).Take(DirectLinkProtocol.MaxCandidates).ToList();
        }

        private async Task<(IPEndPoint? Public, NatKind Nat)> DiscoverAsync(Socket socket, CancellationToken cancellationToken)
        {
            var servers = await StunClient.ResolveAsync(Plugin.Instance?.Configuration?.DirectLinkStunServers ?? Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
            var (publicEndpoint, nat) = await StunClient.DiscoverAsync(socket, servers, cancellationToken).ConfigureAwait(false);
            if (publicEndpoint != null)
            {
                var candidate = new DirectLinkCandidate { Kind = "srflx", Address = publicEndpoint.Address.ToString(), Port = publicEndpoint.Port };
                publicEndpoint = DirectLinkProtocol.AcceptCandidate(candidate, DirectLinkProtocol.TestMode, DirectLinkProtocol.TestMode);
            }

            if (publicEndpoint != null)
            {
                // This server's own address, shown only to its admin.
                _publicEndpoint = DirectLinkProtocol.Describe(publicEndpoint);
                _lastNat = nat;
            }

            return (publicEndpoint, nat);
        }

        private DirectLinkIdentity Identity()
        {
            lock (_identityGate)
            {
                return _identity ??= DirectLinkIdentity.LoadOrCreate(
                    Plugin.Instance?.DataFolderPath,
                    Plugin.Instance?.Configuration?.LocalFederationId ?? "jellyfin-federation",
                    _logger);
            }
        }

        private Uri LocalBase()
        {
            var text = _library?.GetInternalPlaybackBaseUrl() ?? "http://127.0.0.1:8096";
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : new Uri("http://127.0.0.1:8096");
        }

        private bool TakeOfferSlot(string friendId)
        {
            var queue = _offers.GetOrAdd(friendId, _ => new Queue<DateTime>());
            lock (queue)
            {
                var now = UtcNow();
                while (queue.Count > 0 && now - queue.Peek() > OfferWindow)
                {
                    queue.Dequeue();
                }

                if (queue.Count >= MaxOffersPerWindow)
                {
                    return false;
                }

                queue.Enqueue(now);
                return true;
            }
        }

        private async Task LoopAsync(CancellationToken cancellationToken)
        {
            // Let start-up (sync, provisioning) settle first.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(DirectLinkProtocol.TestMode ? 5 : 45), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PassAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Direct link pass failed");
                }

                try
                {
                    await Task.Delay(LoopInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task DropAsync(Outbound state, DirectLinkState newState, string detail)
        {
            DirectLinkConnection? old;
            lock (state)
            {
                old = state.Link;
                state.Link = null;
                state.State = newState;
                state.Detail = detail;
            }

            if (old != null)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task AttemptAsync(RemoteServer server, Outbound state, CancellationToken cancellationToken)
        {
            var acquired = false;
            try
            {
                // Avoid a synchronized STUN/socket/punch/speed-test burst when many
                // friends retry together. Per-friend backoff remains independent.
                await _outboundAttempts.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
                var failure = await ConnectAsync(server, state, cancellationToken).ConfigureAwait(false);
                if (failure != null)
                {
                    Fail(server, state, failure.Value.Detail, failure.Value.Unsupported, failure.Value.RetryAfter);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                Fail(server, state, "Unexpected error: " + ex.Message, false, null);
                _logger.LogDebug(ex, "[Federation] Direct link to {Friend} failed", server.Name);
            }
            finally
            {
                if (acquired)
                {
                    _outboundAttempts.Release();
                }

                lock (state)
                {
                    state.Busy = false;
                }
            }
        }

        private void Fail(RemoteServer server, Outbound state, string detail, bool unsupported, TimeSpan? retryAfter)
        {
            bool changed;
            lock (state)
            {
                state.Failures++;
                var wait = retryAfter ?? BackoffAfter(state.Failures);
                state.NextAttemptUtc = UtcNow() + wait;
                changed = state.State != (unsupported ? DirectLinkState.Unsupported : DirectLinkState.Failed) || state.LastError != detail;
                state.State = unsupported ? DirectLinkState.Unsupported : DirectLinkState.Failed;
                state.LastError = detail;
                state.Detail = unsupported ? detail : detail + " Using the friend's address meanwhile.";
            }

            if (changed)
            {
                _logger.LogInformation("[Federation] Direct link to {Friend}: {Detail}", server.Name, DirectLinkProtocol.RedactText(detail));
            }
        }

        private async Task<(string Detail, bool Unsupported, TimeSpan? RetryAfter)?> ConnectAsync(RemoteServer server, Outbound state, CancellationToken cancellationToken)
        {
            var client = _clients.GetClient(server);
            if (UtcNow() - state.CapabilityCheckedUtc > CapabilityRecheck || state.PeerVersion == 0)
            {
                var (info, error) = await client.GetSystemInfoDetailedAsync(cancellationToken).ConfigureAwait(false);
                state.CapabilityCheckedUtc = UtcNow();
                if (info == null)
                {
                    // Usually the friend restarting: try again soon at first, then back off.
                    return ("Couldn't reach the friend" + (string.IsNullOrEmpty(error) ? "." : ": " + error), false, state.Failures < 2 ? Backoff[0] : null);
                }

                state.PeerVersion = info.DirectLinkVersion;
                if (info.DirectLinkVersion < DirectLinkProtocol.Version)
                {
                    var hasNewPlugin = Version.TryParse(info.FederationPluginVersion, out var v) && v >= new Version(0, 0, 195);
                    return (hasNewPlugin ? "The friend has direct links switched off or unavailable." : "The friend needs Federation 0.0.195 or newer.", true, CapabilityRecheck);
                }
            }

            lock (state)
            {
                state.State = DirectLinkState.Connecting;
                state.Detail = "Connecting…";
            }

            var sw = Stopwatch.StartNew();
            var allowPrivate = AllowPrivateFor(server);
            var allowTailscale = AllowTailscaleFor(server);
            using var socket = NewSocket();
            var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            var (publicEndpoint, nat) = await DiscoverAsync(socket, cancellationToken).ConfigureAwait(false);
            state.NatLocal = nat;
            var own = LocalCandidates(socket, publicEndpoint, allowPrivate, allowTailscale);
            if (own.Count == 0)
            {
                return ("STUN servers didn't answer, so this server doesn't know its public address (is outgoing UDP blocked?).", false, null);
            }

            var identity = Identity();
            var sessionId = DirectLinkProtocol.NewSessionId();
            var secret = DirectLinkProtocol.NewSecret();
            var offer = new DirectLinkOffer
            {
                SessionId = sessionId,
                Fingerprint = identity.Fingerprint,
                Secret = Convert.ToBase64String(secret),
                Candidates = own.Select(c => DirectLinkProtocol.ToCandidate(c.Endpoint, c.Kind)).ToList(),
                NatType = DirectLinkProtocol.NatName(nat)
            };

            var (status, answer) = await client.PostPeerForJsonAsync<DirectLinkAnswer>("DirectLink/Offer", offer, cancellationToken).ConfigureAwait(false);
            if (status == 403)
            {
                return ("The friend has direct links switched off for this server.", true, CapabilityRecheck);
            }

            if (status == 409)
            {
                state.NatRemote = NatKind.Symmetric;
                return ("Both servers are behind strict routers (symmetric NAT), so a direct link can't get through.", false, TimeSpan.FromHours(6));
            }

            if (status == 429)
            {
                return ("The friend is limiting direct link attempts.", false, TimeSpan.FromMinutes(10));
            }

            if (status != 200 || answer == null)
            {
                return (status == 0 ? "Couldn't reach the friend to set up the link." : "The friend couldn't set up the link (HTTP " + status + ").", false, null);
            }

            if (answer.Version != DirectLinkProtocol.Version
                || !DirectLinkIdentity.IsFingerprint(answer.Fingerprint)
                || DirectLinkProtocol.ParseSecret(answer.Secret) is not { } responderSecret)
            {
                return ("The friend's answer was malformed.", false, null);
            }

            var peerCandidates = await AcceptPeerCandidatesAsync(answer.Candidates, server, allowPrivate, allowTailscale, cancellationToken).ConfigureAwait(false);
            state.NatRemote = DirectLinkProtocol.ParseNat(answer.NatType);
            if (peerCandidates.Count == 0)
            {
                return ("The friend sent no address this server may use.", false, null);
            }

            var sessionKey = DirectLinkProtocol.SessionKey(secret, responderSecret, sessionId);
            var puncher = new Puncher(socket, true, sessionId, sessionKey, peerCandidates, state.NatRemote == NatKind.Symmetric);
            var punch = await puncher.RunAsync(cancellationToken).ConfigureAwait(false);
            if (punch.Path == null)
            {
                var strict = nat == NatKind.Symmetric || state.NatRemote == NatKind.Symmetric ? " (one side's router is strict)" : string.Empty;
                _logger.LogInformation(
                    "[Federation] Direct link to {Friend}: punching from {Local} reached none of {Targets}",
                    server.Name,
                    string.Join(", ", own.Select(c => c.Kind + " " + DirectLinkProtocol.Redact(c.Endpoint))),
                    string.Join(", ", peerCandidates.Select(DirectLinkProtocol.Redact)));
                return ("Couldn't get through the routers: " + punch.Failure + strict + ".", false, null);
            }

            // The QUIC stack opens its own socket on the same port; the routers'
            // mappings are keyed on that port, so the punched path carries over.
            socket.Close();
            object quic;
            try
            {
                quic = await DirectLinkHandshake.ConnectAsync(localPort, punch.Path, identity.Certificate, answer.Fingerprint, sessionKey, sessionId, identity.Fingerprint, cancellationToken).ConfigureAwait(false);
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                return ("The link didn't authenticate (" + ex.Message + ").", false, null);
            }
            catch (System.IO.IOException ex)
            {
                return ("The encrypted connection didn't come up: " + ex.Message + ".", false, null);
            }

            var serverId = server.Id;
            var link = new DirectLinkConnection(
                quic,
                serverId,
                true,
                () => Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId)?.ApiKey);
            link.StartCloseWatch();
            lock (state)
            {
                if (!ReferenceEquals(_outbound.GetValueOrDefault(server.Id), state)
                    || Eligibility(Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id) ?? server, Plugin.Instance?.Configuration).State != DirectLinkState.Connecting)
                {
                    // Removed or switched off while connecting.
                    state.Link = null;
                    _ = link.DisposeAsync().AsTask();
                    return null;
                }

                state.Link = link;
                state.State = DirectLinkState.Connected;
                state.Detail = "Connected";
                state.LastError = null;
            }

            _logger.LogInformation(
                "[Federation] Direct link to {Friend} is up via {Endpoint} in {Ms} ms (routers: here {NatLocal}, friend {NatRemote})",
                server.Name,
                DirectLinkProtocol.Redact(punch.Path),
                sw.ElapsedMilliseconds,
                DirectLinkProtocol.NatName(nat),
                DirectLinkProtocol.NatName(state.NatRemote));

            _ = link.Closed.ContinueWith(
                _ =>
                {
                    bool wasCurrent;
                    lock (state)
                    {
                        wasCurrent = ReferenceEquals(state.Link, link);
                        if (wasCurrent)
                        {
                            state.Link = null;
                            state.State = DirectLinkState.Failed;
                            state.LastError = "The link dropped: " + link.CloseReason + ".";
                            state.Detail = state.LastError + " Using the friend's address meanwhile.";
                            // A link that stays up resets the back-off; one that keeps
                            // dropping backs off like a failure.
                            state.Failures = UtcNow() - link.ConnectedUtc >= StableLink ? 1 : state.Failures + 1;
                            state.NextAttemptUtc = UtcNow() + BackoffAfter(state.Failures);
                        }
                    }

                    if (wasCurrent)
                    {
                        _logger.LogInformation("[Federation] Direct link to {Friend} dropped ({Reason}); trying again later", server.Name, DirectLinkProtocol.RedactText(link.CloseReason));
                        _ = link.DisposeAsync().AsTask();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Authentication, not an 8 MiB speed sample, makes the link usable.
            // A slow sample must not hold playback on the old tunnel for 20 seconds.
            _ = MeasureAndUpdateAsync(server, state, link, _shutdown.Token);
            return null;
        }

        private async Task MeasureAndUpdateAsync(RemoteServer server, Outbound state, DirectLinkConnection link, CancellationToken cancellationToken)
        {
            var measured = await MeasureAsync(link, cancellationToken).ConfigureAwait(false);
            if (measured <= 0)
            {
                return;
            }

            lock (state)
            {
                // A reconnect may have replaced this link while the old sample was
                // running. Never let that late result describe the replacement.
                if (!ReferenceEquals(state.Link, link) || link.IsClosed)
                {
                    return;
                }

                link.Mbps = measured;
            }

            _logger.LogInformation("[Federation] Direct link to {Friend} measured {Mbps:F0} Mbps", server.Name, measured);
        }

        private async Task<double> MeasureAsync(DirectLinkConnection link, CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var sw = Stopwatch.StartNew();
                using var response = await link.Http!.GetAsync("/Plugins/Federation/Peer/BitrateTest?size=8000000", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return 0;
                }

                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                }

                return total * 8 / 1e6 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.IO.IOException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "[Federation] Direct link speed test failed");
                return 0;
            }
        }

        private async Task RespondAsync(RemoteServer caller, DirectLinkOffer offer, Socket socket, List<IPEndPoint> candidates, NatKind peerNat, byte[] sessionKey, DirectLinkIdentity identity, Action setupDone, CancellationToken cancellationToken)
        {
            var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            try
            {
                PunchResult punch;
                using (socket)
                {
                    var puncher = new Puncher(socket, false, offer.SessionId, sessionKey, candidates, peerNat == NatKind.Symmetric);
                    punch = await puncher.RunAsync(cancellationToken).ConfigureAwait(false);
                }

                if (punch.Path == null)
                {
                    _logger.LogInformation("[Federation] Direct link from {Friend}: couldn't get through the routers ({Reason})", caller.Name, DirectLinkProtocol.RedactText(punch.Failure));
                    return;
                }

                // Requests over the link reach Jellyfin on loopback, where its
                // remote-access switch and IP allow/block lists don't apply; apply
                // them here to the friend's real address instead.
                if (_network != null && _network.ShouldAllowServerAccess(punch.Path.Address) != MediaBrowser.Common.Net.RemoteAccessPolicyResult.Allow)
                {
                    _logger.LogInformation("[Federation] Direct link from {Friend} refused: Jellyfin's network settings don't allow {Address}", caller.Name, DirectLinkProtocol.Redact(punch.Path.Address));
                    return;
                }

                var quic = await DirectLinkHandshake.AcceptAsync(
                    localPort,
                    punch.Path,
                    identity.Certificate,
                    offer.Fingerprint,
                    sessionKey,
                    offer.SessionId,
                    identity.Fingerprint,
                    Forwarder.MaxConcurrentStreams,
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);

                // Setting up is over: the slot is for links being set up, not for
                // links in use (those count against MaxInboundLinks).
                setupDone();
                DirectLinkConnection link;
                try
                {
                    link = new DirectLinkConnection(quic, caller.Id, false, null);
                }
                catch
                {
                    await QuicTransportHost.Close(quic).ConfigureAwait(false);
                    throw;
                }

                // Settings may have changed during STUN, punching and the TLS
                // handshake. Recheck authorization and capacity immediately before
                // the link can serve, and serialize check-plus-insert across peers.
                DirectLinkConnection? previous = null;
                string? refusal = null;
                lock (_inboundGate)
                {
                    refusal = InboundRefusal(Plugin.Instance?.Configuration, caller.Id, punch.Path);
                    if (refusal == null && !_inbound.ContainsKey(caller.Id)
                        && _inbound.Values.Count(l => !l.IsClosed) >= MaxInboundLinks)
                    {
                        refusal = "this server has as many direct links as it takes";
                    }

                    if (refusal == null)
                    {
                        // A reconnect atomically replaces this friend's old link.
                        _inbound.AddOrUpdate(caller.Id, link, (_, old) =>
                        {
                            previous = old;
                            return link;
                        });
                    }
                }

                if (refusal != null)
                {
                    _logger.LogInformation("[Federation] Direct link from {Friend} closed before serving: {Reason}", caller.Name, DirectLinkProtocol.RedactText(refusal));
                    link.MarkClosed(refusal);
                    await link.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                if (previous != null && !ReferenceEquals(previous, link))
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                _logger.LogInformation("[Federation] Direct link from {Friend} is up via {Endpoint}", caller.Name, DirectLinkProtocol.Redact(punch.Path));

                // A link only carries the dialler's requests, so this home's viewers
                // still need our own link to the friend. The friend just proved it
                // runs a new enough plugin and that the routers let us through:
                // dial back now instead of waiting out the capability recheck.
                if (!IsConnected(caller.Id))
                {
                    Reconsider(caller.Id);
                }

                await Forwarder.RunAsync(link, caller.Name).ConfigureAwait(false);
                _inbound.TryRemove(new KeyValuePair<string, DirectLinkConnection>(caller.Id, link));
                _logger.LogInformation("[Federation] Direct link from {Friend} ended ({Reason})", caller.Name, DirectLinkProtocol.RedactText(link.CloseReason));
                await link.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or SocketException)
            {
                _logger.LogInformation("[Federation] Direct link from {Friend} didn't come up: {Reason}", caller.Name, DirectLinkProtocol.RedactText(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Direct link from {Friend} failed", caller.Name);
            }
        }

        private sealed class Outbound
        {
            public DirectLinkState State { get; set; } = DirectLinkState.Connecting;

            public string Detail { get; set; } = "Waiting for the first attempt.";

            public DirectLinkConnection? Link { get; set; }

            public bool Busy { get; set; }

            public int Failures { get; set; }

            public DateTime NextAttemptUtc { get; set; } = DateTime.MinValue;

            public DateTime CapabilityCheckedUtc { get; set; } = DateTime.MinValue;

            public int PeerVersion { get; set; }

            public NatKind NatLocal { get; set; }

            public NatKind NatRemote { get; set; }

            public string? LastError { get; set; }
        }
    }
}
