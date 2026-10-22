using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>
    /// One pairing's UDP port while its link is down (docs/PAIRING.md, "The pairing
    /// ports"). It sends probes to the friend's endpoints and answers only valid punch
    /// packets for this pairing, with an ack of the same size. A path is found when an
    /// ack comes back. Nothing else on the port is ever answered.
    /// </summary>
    public sealed class PairPort : IDisposable
    {
        /// <summary>
        /// Most packets carrying this pairing's tag handled per second from one address.
        /// Packets without the tag are dropped at a glance and never use the budget, so a
        /// stranger's junk can't crowd out the friend's probes.
        /// </summary>
        public const int MaxPacketsPerSecond = 50;

        private const int MaxSourcesPerSecond = 64;
        private const int MaxSentNonces = 512;

        private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(1200);

        private readonly Socket _socket;
        private byte _ownRole;
        private readonly Func<DateTimeOffset> _now;
        private readonly ReplayWindow _replay = new();
        private byte[]? _punchKey;
        private byte[]? _pairTag;
        private readonly HashSet<UInt128> _expectedAcks = new();
        private readonly HashSet<IPAddress> _probed = new();
        private readonly Queue<UInt128> _expectedOrder = new();

        private PairPort(Socket socket, byte ownRole, Func<DateTimeOffset>? now)
        {
            _socket = socket;
            _ownRole = ownRole;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        /// <summary>Gets the local port.</summary>
        public int Port { get; }

        /// <summary>Gets the socket (for STUN before the punch starts).</summary>
        public Socket Socket => _socket;

        /// <summary>Binds <paramref name="port"/> on all IPv4 addresses.</summary>
        /// <returns>The port.</returns>
        public static PairPort Bind(int port, byte ownRole, Func<DateTimeOffset>? now = null)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Windows reports an ICMP "unreachable" as a receive error, which would
                    // end the loop; ignore it like Linux does.
                    const int SioUdpConnReset = -1744830452;
                    socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
                }

                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return new PairPort(socket, ownRole, now);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>Picks a free port in the range, trying each once in random order.</summary>
        /// <returns>The bound port, or null when every port is taken.</returns>
        public static PairPort? BindAny(int first, int last, byte ownRole, ISet<int> taken)
        {
            var ports = Enumerable.Range(first, Math.Max(0, last - first + 1)).Where(p => !taken.Contains(p)).OrderBy(_ => Random.Shared.Next()).ToList();
            foreach (var port in ports)
            {
                try
                {
                    return Bind(port, ownRole);
                }
                catch (SocketException)
                {
                    // In use by something else; try the next.
                }
            }

            return null;
        }

        /// <summary>
        /// Sets the pair key and this side's role once they are known (the offerer
        /// learns both from the answer; a port bound for a code doesn't know its role).
        /// </summary>
        public void SetKey(byte[] pairKey, byte ownRole)
        {
            if (ownRole > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(ownRole));
            }

            _ownRole = ownRole;
            _punchKey = PairingCrypto.SubKey(pairKey, "punch");
            _pairTag = PairPunchPacket.PairTag(pairKey);
        }

        /// <summary>Sends one STUN request to keep the router's mapping for this port alive.</summary>
        public void KeepAlive(IPEndPoint stunServer)
        {
            try
            {
                _socket.SendTo(DirectLink.StunClient.BuildRequest(out _), stunServer);
            }
            catch (SocketException)
            {
                // No route right now; the next one may get through.
            }
        }

        /// <summary>
        /// Probes <paramref name="targets"/> and answers the friend's probes until an ack
        /// comes back, then keeps answering briefly so the friend gets its ack too.
        /// <paramref name="schedule"/> gives the wait before each round of probes (by
        /// round number); null means stop probing (answers continue).
        /// </summary>
        /// <returns>The working path, or null when <paramref name="cancellationToken"/> ends first.</returns>
        public async Task<IPEndPoint?> PunchAsync(Func<IReadOnlyList<IPEndPoint>> targets, Func<int, TimeSpan?> schedule, CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IPEndPoint? path = null;
            var acked = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var probing = Task.Run(() => ProbeLoopAsync(targets, schedule, acked.Task, stop.Token), CancellationToken.None);
            var receiving = Task.Run(() => ReceiveLoopAsync(acked, stop.Token), CancellationToken.None);
            try
            {
                path = await acked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(Linger, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                path = null;
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAll(probing, receiving).ConfigureAwait(false);
            }

            return path;
        }

        private async Task ProbeLoopAsync(Func<IReadOnlyList<IPEndPoint>> targets, Func<int, TimeSpan?> schedule, Task found, CancellationToken cancellationToken)
        {
            try
            {
                for (var round = 0; !found.IsCompleted; round++)
                {
                    var wait = schedule(round);
                    if (wait == null)
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    }

                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait.Value, cancellationToken).ConfigureAwait(false);
                    }

                    if (_punchKey == null || _pairTag == null)
                    {
                        continue;
                    }

                    foreach (var target in targets())
                    {
                        lock (_probed)
                        {
                            if (_probed.Count < 64)
                            {
                                _probed.Add(target.Address);
                            }
                        }

                        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                        Expect(AckNonce(nonce));
                        await SendAsync(PairPunchPacket.Build(PairPunchPacket.Probe, _ownRole, _pairTag, _punchKey, _now(), nonce), target, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Done.
            }
        }

        private async Task ReceiveLoopAsync(TaskCompletionSource<IPEndPoint> acked, CancellationToken cancellationToken)
        {
            var buffer = new byte[2048];
            var budgets = new Dictionary<IPAddress, int>();
            var window = Stopwatch.StartNew();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    SocketReceiveFromResult received;
                    try
                    {
                        received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // ICMP errors from earlier sends; keep listening.
                        continue;
                    }

                    if (received.RemoteEndPoint is not IPEndPoint from)
                    {
                        continue;
                    }

                    var packet = buffer.AsSpan(0, received.ReceivedBytes);
                    var key = _punchKey;
                    var tag = _pairTag;
                    if (key == null || tag == null || PairPunchPacket.PeekPairTag(packet) is not { } theirs || !theirs.AsSpan().SequenceEqual(tag))
                    {
                        // Not ours, or the key isn't known yet: never answered, never counted.
                        continue;
                    }

                    // Only someone who has seen this pairing's packets knows the tag; even
                    // they get a budget per address, so the friend's own probes still count.
                    if (window.ElapsedMilliseconds >= 1000)
                    {
                        window.Restart();
                        budgets.Clear();
                    }

                    // Per address, not per port (one host can use many ports), and the
                    // addresses this port probes always get their own budget, so filling
                    // the sources can't lock the friend out.
                    budgets.TryGetValue(from.Address, out var used);
                    bool probedThere;
                    lock (_probed)
                    {
                        probedThere = _probed.Contains(from.Address);
                    }

                    if (used >= MaxPacketsPerSecond || (used == 0 && budgets.Count >= MaxSourcesPerSecond && !probedThere))
                    {
                        continue;
                    }

                    budgets[from.Address] = used + 1;

                    var type = PairPunchPacket.Verify(packet, _ownRole, key, _now(), _replay);
                    if (type == PairPunchPacket.Probe)
                    {
                        // The ack carries a nonce made from the probe's, so it answers only that probe.
                        var answer = AckNonce(PairPunchPacket.Nonce(packet));
                        await SendAsync(PairPunchPacket.Build(PairPunchPacket.Ack, _ownRole, tag, key, _now(), answer), from, cancellationToken).ConfigureAwait(false);
                    }
                    else if (type == PairPunchPacket.Ack && WasExpected(PairPunchPacket.Nonce(packet)))
                    {
                        // An old ack replayed from elsewhere answers no probe sent from this port.
                        acked.TrySetResult(from);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Done.
            }
            catch (ObjectDisposedException)
            {
                // Closed.
            }
        }

        /// <summary>The nonce an ack to a probe with <paramref name="probeNonce"/> carries.</summary>
        /// <returns>16 bytes.</returns>
        internal static byte[] AckNonce(ReadOnlySpan<byte> probeNonce)
        {
            Span<byte> input = stackalloc byte[19];
            "ack"u8.CopyTo(input);
            probeNonce.Slice(0, 16).CopyTo(input.Slice(3));
            return System.Security.Cryptography.SHA256.HashData(input).AsSpan(0, 16).ToArray();
        }

        private void Expect(byte[] ackNonce)
        {
            var key = System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(ackNonce);
            lock (_expectedAcks)
            {
                if (_expectedAcks.Add(key))
                {
                    _expectedOrder.Enqueue(key);
                }

                while (_expectedOrder.Count > MaxSentNonces)
                {
                    _expectedAcks.Remove(_expectedOrder.Dequeue());
                }
            }
        }

        private bool WasExpected(byte[] ackNonce)
        {
            var key = System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(ackNonce);
            lock (_expectedAcks)
            {
                return _expectedAcks.Contains(key);
            }
        }

        private async Task SendAsync(byte[] packet, IPEndPoint target, CancellationToken cancellationToken)
        {
            try
            {
                await _socket.SendToAsync(packet, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Unreachable right now.
            }
        }

        /// <inheritdoc />
        public void Dispose() => _socket.Dispose();
    }
}
