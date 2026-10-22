using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// The punch packets: <c>"JFDL"</c>, version, type (1 probe, 2 ack), sender
    /// role (0 initiator, 1 responder), the 16-byte session id, an 8-byte random
    /// nonce, and a 16-byte HMAC-SHA256 tag over all of that, keyed with the
    /// session's punch key. Only the two servers of this session can make one;
    /// anything else is dropped without an answer.
    /// </summary>
    public static class PunchPacket
    {
        /// <summary>Packet length.</summary>
        public const int Length = 47;

        /// <summary>A probe.</summary>
        public const byte Probe = 1;

        /// <summary>An answer to a probe.</summary>
        public const byte Ack = 2;

        private const int TagOffset = 31;

        /// <summary>Builds a packet.</summary>
        public static byte[] Build(byte type, bool fromInitiator, byte[] sessionId, byte[] punchKey)
        {
            var packet = new byte[Length];
            packet[0] = (byte)'J';
            packet[1] = (byte)'F';
            packet[2] = (byte)'D';
            packet[3] = (byte)'L';
            packet[4] = DirectLinkProtocol.Version;
            packet[5] = type;
            packet[6] = fromInitiator ? (byte)0 : (byte)1;
            sessionId.AsSpan(0, 16).CopyTo(packet.AsSpan(7));
            RandomNumberGenerator.Fill(packet.AsSpan(23, 8));
            HMACSHA256.HashData(punchKey, packet.AsSpan(0, TagOffset)).AsSpan(0, 16).CopyTo(packet.AsSpan(TagOffset));
            return packet;
        }

        /// <summary>
        /// The packet's type when it's a genuine packet of this session sent by
        /// the other role, otherwise 0.
        /// </summary>
        public static byte Verify(ReadOnlySpan<byte> packet, bool fromInitiator, byte[] sessionId, byte[] punchKey)
        {
            if (packet.Length != Length
                || packet[0] != 'J' || packet[1] != 'F' || packet[2] != 'D' || packet[3] != 'L'
                || packet[4] != DirectLinkProtocol.Version
                || (packet[5] != Probe && packet[5] != Ack)
                || packet[6] != (fromInitiator ? 0 : 1)
                || !packet.Slice(7, 16).SequenceEqual(sessionId.AsSpan(0, 16)))
            {
                return 0;
            }

            Span<byte> expected = stackalloc byte[32];
            HMACSHA256.HashData(punchKey, packet.Slice(0, TagOffset), expected);
            return CryptographicOperations.FixedTimeEquals(expected.Slice(0, 16), packet.Slice(TagOffset, 16)) ? packet[5] : (byte)0;
        }
    }

    /// <summary>The outcome of punching.</summary>
    public sealed record PunchResult(IPEndPoint? Path, bool Confirmed, string? Failure);

    /// <summary>
    /// Opens the path through both routers: each side sends probes to all of the
    /// other's candidates at once, so each router sees outgoing traffic and lets
    /// the other side's packets in. A probe that arrives is answered with an ack;
    /// an ack proves the path works both ways.
    /// </summary>
    public sealed class Puncher
    {
        private readonly Socket _socket;
        private readonly bool _isInitiator;
        private readonly byte[] _sessionId;
        private readonly byte[] _punchKey;
        private readonly List<IPEndPoint> _targets;
        private readonly HashSet<IPAddress> _allowedSources;

        /// <summary>
        /// Initializes a new instance of the <see cref="Puncher"/> class.
        /// </summary>
        /// <param name="socket">The bound UDP socket the link will use.</param>
        /// <param name="isInitiator">This side's role.</param>
        /// <param name="sessionIdHex">The session id.</param>
        /// <param name="sessionKey">The session key (see <see cref="DirectLinkProtocol.SessionKey"/>).</param>
        /// <param name="candidates">The other side's accepted candidates.</param>
        /// <param name="peerSymmetric">True when the other side reported a symmetric NAT (its ports are guessed around the reported one).</param>
        public Puncher(Socket socket, bool isInitiator, string sessionIdHex, byte[] sessionKey, IReadOnlyList<IPEndPoint> candidates, bool peerSymmetric)
        {
            _socket = socket;
            _isInitiator = isInitiator;
            _sessionId = Convert.FromHexString(sessionIdHex);
            _punchKey = DirectLinkProtocol.SubKey(sessionKey, "punch");
            _targets = Targets(candidates, peerSymmetric);
            _allowedSources = candidates.Select(c => c.Address).ToHashSet();
        }

        /// <summary>Most endpoints probed in one session.</summary>
        internal const int MaxTargets = 16;

        /// <summary>Gets or sets how long to try.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>Gets or sets the probe interval.</summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(50);

        /// <summary>Gets or sets how long to keep answering after the path is found (the other side may still need an ack).</summary>
        public TimeSpan Linger { get; set; } = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// Gets or sets, for the responder, how long after the first probe to go
        /// ahead without an ack (the initiator then already has its ack).
        /// </summary>
        public TimeSpan ProbeOnlyGrace { get; set; } = TimeSpan.FromMilliseconds(1500);

        /// <summary>The endpoints to aim at: each candidate, plus nearby ports for a symmetric NAT.</summary>
        internal static List<IPEndPoint> Targets(IReadOnlyList<IPEndPoint> candidates, bool peerSymmetric)
        {
            var targets = new List<IPEndPoint>();
            foreach (var candidate in candidates)
            {
                targets.Add(candidate);
                if (peerSymmetric)
                {
                    for (var delta = 1; delta <= 2; delta++)
                    {
                        foreach (var port in new[] { candidate.Port + delta, candidate.Port - delta })
                        {
                            if (port is >= 1024 and <= 65535)
                            {
                                targets.Add(new IPEndPoint(candidate.Address, port));
                            }
                        }
                    }
                }
            }

            // Bounded: a friend can't make this server spray many addresses.
            return targets.Distinct().Take(MaxTargets).ToList();
        }

        /// <summary>Punches until the path is found or <see cref="Timeout"/> passes.</summary>
        public async Task<PunchResult> RunAsync(CancellationToken cancellationToken)
        {
            if (_targets.Count == 0)
            {
                return new PunchResult(null, false, "the friend sent no address this server may use");
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(Timeout);
            IPEndPoint? ackFrom = null;
            IPEndPoint? probeFrom = null;
            var firstProbe = Stopwatch.StartNew();
            var gotProbe = false;
            var done = new TaskCompletionSource<PunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var sender = Task.Run(
                async () =>
                {
                    while (!stop.IsCancellationRequested && !done.Task.IsCompleted)
                    {
                        foreach (var target in _targets)
                        {
                            try
                            {
                                await _socket.SendToAsync(PunchPacket.Build(PunchPacket.Probe, _isInitiator, _sessionId, _punchKey), SocketFlags.None, target, stop.Token).ConfigureAwait(false);
                            }
                            catch (SocketException)
                            {
                                // Unreachable for now; the next round tries again.
                            }
                        }

                        await Task.Delay(Interval, stop.Token).ConfigureAwait(false);
                    }
                },
                stop.Token);

            var buffer = new byte[1500];
            try
            {
                while (true)
                {
                    SocketReceiveFromResult received;
                    try
                    {
                        received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token).ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
                    {
                        // ICMP errors from targets that don't exist; keep listening.
                        continue;
                    }
                    catch (SocketException ex)
                    {
                        return new PunchResult(null, false, "the UDP socket failed (" + ex.SocketErrorCode + ")");
                    }

                    if (received.RemoteEndPoint is not IPEndPoint from || !_allowedSources.Contains(from.Address))
                    {
                        continue;
                    }

                    var type = PunchPacket.Verify(buffer.AsSpan(0, received.ReceivedBytes), !_isInitiator, _sessionId, _punchKey);
                    if (type == PunchPacket.Probe)
                    {
                        try
                        {
                            await _socket.SendToAsync(PunchPacket.Build(PunchPacket.Ack, _isInitiator, _sessionId, _punchKey), SocketFlags.None, from, stop.Token).ConfigureAwait(false);
                        }
                        catch (SocketException)
                        {
                            // Try again on the next probe.
                        }

                        if (!gotProbe)
                        {
                            gotProbe = true;
                            probeFrom = from;
                            firstProbe.Restart();
                        }
                    }
                    else if (type == PunchPacket.Ack)
                    {
                        ackFrom ??= from;
                    }
                    else
                    {
                        continue;
                    }

                    if (ackFrom != null)
                    {
                        await LingerAsync(stop.Token).ConfigureAwait(false);
                        return new PunchResult(ackFrom, true, null);
                    }

                    if (!_isInitiator && gotProbe && firstProbe.Elapsed >= ProbeOnlyGrace)
                    {
                        return new PunchResult(probeFrom, false, null);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_isInitiator && probeFrom != null)
                {
                    return new PunchResult(probeFrom, false, null);
                }

                return new PunchResult(null, false, gotProbe ? "packets only got through one way" : "no packets got through either router");
            }
            finally
            {
                done.TrySetResult(new PunchResult(null, false, null));
                stop.Cancel();
                try
                {
                    await sender.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when punching stops.
                }
            }
        }

        // Keeps acking probes briefly so the other side also gets its ack.
        private async Task LingerAsync(CancellationToken cancellationToken)
        {
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linger.CancelAfter(Linger);
            var buffer = new byte[1500];
            try
            {
                while (true)
                {
                    var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), linger.Token).ConfigureAwait(false);
                    if (received.RemoteEndPoint is IPEndPoint from && _allowedSources.Contains(from.Address)
                        && PunchPacket.Verify(buffer.AsSpan(0, received.ReceivedBytes), !_isInitiator, _sessionId, _punchKey) == PunchPacket.Probe)
                    {
                        await _socket.SendToAsync(PunchPacket.Build(PunchPacket.Ack, _isInitiator, _sessionId, _punchKey), SocketFlags.None, from, linger.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Linger over.
            }
            catch (SocketException)
            {
                // Nothing more to answer.
            }
        }
    }
}
