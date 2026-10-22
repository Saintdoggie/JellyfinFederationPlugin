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
        /// <summary>Most packets handled per second; the rest are dropped unread.</summary>
        public const int MaxPacketsPerSecond = 50;

        private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(1200);

        private readonly Socket _socket;
        private readonly byte _ownRole;
        private readonly Func<DateTimeOffset> _now;
        private readonly ReplayWindow _replay = new();
        private byte[]? _punchKey;
        private byte[]? _pairTag;

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

        /// <summary>Sets the pair key once it is known (the offerer learns it from the answer).</summary>
        public void SetKey(byte[] pairKey)
        {
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
                        await SendAsync(PairPunchPacket.Build(PairPunchPacket.Probe, _ownRole, _pairTag, _punchKey, _now()), target, cancellationToken).ConfigureAwait(false);
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
            var budget = MaxPacketsPerSecond;
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

                    if (window.ElapsedMilliseconds >= 1000)
                    {
                        window.Restart();
                        budget = MaxPacketsPerSecond;
                    }

                    if (--budget < 0 || received.RemoteEndPoint is not IPEndPoint from)
                    {
                        continue;
                    }

                    var packet = buffer.AsSpan(0, received.ReceivedBytes);
                    var key = _punchKey;
                    var tag = _pairTag;
                    if (key == null || tag == null || PairPunchPacket.PeekPairTag(packet) is not { } theirs || !theirs.AsSpan().SequenceEqual(tag))
                    {
                        // Not ours, or the key isn't known yet: never answered.
                        continue;
                    }

                    var type = PairPunchPacket.Verify(packet, _ownRole, key, _now(), _replay);
                    if (type == PairPunchPacket.Probe)
                    {
                        await SendAsync(PairPunchPacket.Build(PairPunchPacket.Ack, _ownRole, tag, key, _now()), from, cancellationToken).ConfigureAwait(false);
                    }
                    else if (type == PairPunchPacket.Ack)
                    {
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
