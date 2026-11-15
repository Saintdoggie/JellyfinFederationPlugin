using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// Keeps a punched UDP socket open and carries the QUIC listener's packets
    /// through it.
    /// <para>
    /// Why: Windows Firewall lets a friend's packets in only on the very socket that
    /// sent to the friend first. The side that accepts a link punches with one
    /// socket and would then open a new one for the QUIC listener on the same port;
    /// the firewall treats the friend's first QUIC packet to that new socket as
    /// unsolicited and drops it, unless the admin added a firewall rule. With the
    /// bridge the punched socket never closes: the listener sits on loopback (which
    /// the firewall does not filter) and this class copies packets between the two.
    /// </para>
    /// Only the punched friend's exact endpoint is let in, and only the listener's
    /// exact loopback endpoint is let out.
    /// </summary>
    public sealed class UdpBridge : IDisposable
    {
        /// <summary>Env var that turns the bridge on off Windows, for the NAT smoke test (test builds only).</summary>
        public const string TestBridgeEnv = "FEDERATION_DIRECTLINK_TEST_BRIDGE";

        private const int SocketBuffer = 4 * 1024 * 1024;
        private readonly Socket _outer;
        private readonly Socket _inner;
        private readonly SocketAddress _peer;
        private readonly SocketAddress _listener;
        private long _dropped;
        private int _started;
        private int _disposed;

        private UdpBridge(Socket outer, Socket inner, IPEndPoint peer, int listenerPort)
        {
            _outer = outer;
            _inner = inner;
            Peer = peer;
            Local = (IPEndPoint)outer.LocalEndPoint!;
            ListenerPort = listenerPort;
            ListenerSees = (IPEndPoint)inner.LocalEndPoint!;
            _peer = peer.Serialize();
            _listener = new IPEndPoint(IPAddress.Loopback, listenerPort).Serialize();
        }

        /// <summary>Gets a value indicating whether an accepting side must keep its punched socket.</summary>
        public static bool Needed
            => OperatingSystem.IsWindows()
                || (DirectLinkProtocol.TestMode && Environment.GetEnvironmentVariable(TestBridgeEnv) == "1");

        /// <summary>Gets the friend's endpoint, the only one let in.</summary>
        public IPEndPoint Peer { get; }

        /// <summary>Gets the punched socket's own endpoint.</summary>
        public IPEndPoint Local { get; }

        /// <summary>Gets the loopback port the QUIC listener must bind.</summary>
        public int ListenerPort { get; }

        /// <summary>Gets the loopback endpoint the QUIC listener sees the friend's packets come from.</summary>
        public IPEndPoint ListenerSees { get; }

        /// <summary>Gets how many packets were refused for coming from somewhere else.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>
        /// Takes over <paramref name="punched"/> (the caller must not use or close it
        /// afterwards; <see cref="Dispose"/> closes it).
        /// </summary>
        /// <returns>The bridge, not yet copying.</returns>
        public static UdpBridge Create(Socket punched, IPEndPoint peer)
        {
            ArgumentNullException.ThrowIfNull(punched);
            ArgumentNullException.ThrowIfNull(peer);
            if (peer.AddressFamily != AddressFamily.InterNetwork || punched.AddressFamily != AddressFamily.InterNetwork)
            {
                punched.Dispose();
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            }

            Socket? inner = null;
            try
            {
                inner = NewLoopbackSocket();
                int listenerPort;
                using (var probe = NewLoopbackSocket())
                {
                    // A port that is free right now; the listener binds it a moment later.
                    listenerPort = ((IPEndPoint)probe.LocalEndPoint!).Port;
                }

                Tune(punched);
                Tune(inner);
                try
                {
                    // Like MsQuic's own sockets: an oversized packet is lost, never split.
                    punched.DontFragment = true;
                }
                catch (Exception ex) when (ex is SocketException or NotSupportedException)
                {
                    // Not fatal: the path then fragments instead.
                }

                return new UdpBridge(punched, inner, peer, listenerPort);
            }
            catch
            {
                inner?.Dispose();
                punched.Dispose();
                throw;
            }
        }

        /// <summary>Starts copying. Call once the listener is being bound.</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            Run("federation-link-in", () => Pump(_outer, _inner, _peer, _listener));
            Run("federation-link-out", () => Pump(_inner, _outer, _listener, _peer));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _outer.Dispose();
            _inner.Dispose();
        }

        /// <summary>Whether two IPv4 socket addresses name the same address and port.</summary>
        internal static bool SameEndpoint(SocketAddress a, SocketAddress b)
            => a.Family == b.Family && a.Buffer.Span.Slice(2, 6).SequenceEqual(b.Buffer.Span.Slice(2, 6));

        private static Socket NewLoopbackSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static void Tune(Socket socket)
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows reports an ICMP "unreachable" as a receive error; ignore it like Linux does.
                const int SioUdpConnReset = -1744830452;
                socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
            }

            try
            {
                socket.ReceiveBufferSize = SocketBuffer;
                socket.SendBufferSize = SocketBuffer;
            }
            catch (SocketException)
            {
                // The system's own sizes then.
            }

            socket.Blocking = true;
        }

        private static void Run(string name, ThreadStart body)
            => new Thread(body) { IsBackground = true, Name = name }.Start();

        private static bool Passing(SocketError error)
            => error is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable or SocketError.MessageSize or SocketError.NoBufferSpaceAvailable
                or SocketError.WouldBlock or SocketError.TimedOut;

        private void Pump(Socket from, Socket to, SocketAddress onlyFrom, SocketAddress sendTo)
        {
            var buffer = GC.AllocateUninitializedArray<byte>(65535);
            var source = new SocketAddress(AddressFamily.InterNetwork);
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    var length = from.ReceiveFrom(buffer, SocketFlags.None, source);
                    if (!SameEndpoint(source, onlyFrom))
                    {
                        Interlocked.Increment(ref _dropped);
                        continue;
                    }

                    to.SendTo(buffer.AsSpan(0, length), SocketFlags.None, sendTo);
                }
                catch (SocketException ex) when (Passing(ex.SocketErrorCode) && Volatile.Read(ref _disposed) == 0)
                {
                    // One packet lost; QUIC resends.
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // Closed, or the socket is beyond use: QUIC notices the silence and the link ends.
                    return;
                }
            }
        }
    }
}
