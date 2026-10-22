using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.QuicTransport
{
    /// <summary>
    /// The direct link's QUIC transport, called by the plugin through delegates
    /// (only BCL types cross over: connections are opaque objects, streams are
    /// <see cref="Stream"/>s). Failures surface as <see cref="IOException"/> with a
    /// readable message, or <see cref="AuthenticationException"/> when the other
    /// side isn't the pinned friend.
    /// </summary>
    public static class QuicTransport
    {
        private const byte ProofMarker = 0x01;
        private const string Alpn = "jf-fed-dl/1";

        private static readonly List<SslApplicationProtocol> Protocols = new() { new SslApplicationProtocol(Alpn) };
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

        // Paired links: home routers often forget a quiet UDP mapping after 30 s.
        private const byte PairedMarker = 0x02;
        private static readonly List<SslApplicationProtocol> PairedProtocols = new() { new SslApplicationProtocol("jf-fed-pair/1") };
        private static readonly TimeSpan PairedIdleTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan PairedKeepAlive = TimeSpan.FromSeconds(10);

        /// <summary>Whether QUIC works in this load context.</summary>
        // How much each side lets the other have in flight. The library's defaults
        // keep one download to roughly 270 KB in flight, which over a 20 ms round trip
        // caps it near 110 Mbps however fast both lines are. The connection window
        // bounds the memory all streams together can hold.
        private static long _connectionWindow = 32L * 1024 * 1024;
        private static long _streamWindow = 8L * 1024 * 1024;

        public static bool IsSupported() => QuicConnection.IsSupported && QuicListener.IsSupported;

        /// <summary>Sets the receive windows for new connections (bytes); 0 keeps the library defaults.</summary>
        public static void ConfigureWindows(long connection, long stream)
        {
            Interlocked.Exchange(ref _connectionWindow, connection);
            Interlocked.Exchange(ref _streamWindow, stream);
        }

        private static QuicReceiveWindowSizes Windows()
        {
            var sizes = new QuicReceiveWindowSizes();
            var connection = Interlocked.Read(ref _connectionWindow);
            var stream = Interlocked.Read(ref _streamWindow);
            if (connection > 0)
            {
                sizes.Connection = (int)Math.Min(int.MaxValue, connection);
            }

            if (stream > 0)
            {
                var value = (int)Math.Min(int.MaxValue, stream);
                sizes.LocallyInitiatedBidirectionalStream = value;
                sizes.RemotelyInitiatedBidirectionalStream = value;
            }

            return sizes;
        }

        /// <summary>The runtime's reason QUIC doesn't work (or the library version when it does).</summary>
        public static string Diagnostics()
        {
            try
            {
                var api = typeof(QuicConnection).Assembly.GetType("System.Net.Quic.MsQuicApi");
                const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var reason = api?.GetProperty("NotSupportedReason", Flags)?.GetValue(null) as string;
                var version = api?.GetProperty("MsQuicLibraryVersion", Flags)?.GetValue(null) as string;
                return string.IsNullOrEmpty(reason) ? version ?? "ok" : reason;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        /// <summary>
        /// Initiator: connects from <paramref name="localPort"/> to the punched
        /// path, pinning the friend's certificate, then sends this side's proof and
        /// checks the friend's.
        /// </summary>
        public static async Task<object> ConnectAsync(
            int localPort,
            IPEndPoint remote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] ownProof,
            byte[] expectedProof,
            CancellationToken cancellationToken)
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = remote,
                LocalEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                IdleTimeout = IdleTimeout,
                KeepAliveInterval = KeepAlive,
                HandshakeTimeout = TimeSpan.FromSeconds(10),

                // One allowed so the initiator can wait on AcceptInboundStreamAsync to
                // learn when the connection ends (with 0 it throws at once). A friend
                // that actually opens one is closed as misbehaving.
                MaxInboundBidirectionalStreams = 1,
                MaxInboundUnidirectionalStreams = 0,
                InitialReceiveWindowSizes = Windows(),
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = Protocols,
                    TargetHost = "jellyfin-federation",
                    ClientCertificates = new X509CertificateCollection { ownCertificate },
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint)
                }
            };

            QuicConnection connection;
            try
            {
                connection = await QuicConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token).ConfigureAwait(false);
                await control.WriteAsync(ProofMessage(ownProof), timeout.Token).ConfigureAwait(false);
                var answer = await ReadExactlyAsync(control, 33, timeout.Token).ConfigureAwait(false);
                if (!ProofMatches(answer, expectedProof))
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                control.CompleteWrites();
                return connection;
            }
            catch (Exception ex)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw Translate(ex);
            }
        }

        /// <summary>
        /// Responder: listens on <paramref name="localPort"/> for one connection
        /// from <paramref name="expectedRemote"/>'s address with the pinned client
        /// certificate, checks its proof and answers with this side's. The listener
        /// is gone afterwards; the connection lives on.
        /// </summary>
        public static async Task<object> AcceptAsync(
            int localPort,
            IPAddress expectedRemote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] ownProof,
            byte[] expectedProof,
            int maxStreams,
            TimeSpan wait,
            CancellationToken cancellationToken)
        {
            var serverOptions = new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                IdleTimeout = IdleTimeout,
                KeepAliveInterval = KeepAlive,
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                MaxInboundBidirectionalStreams = maxStreams,
                MaxInboundUnidirectionalStreams = 0,
                InitialReceiveWindowSizes = Windows(),
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = Protocols,
                    ServerCertificate = ownCertificate,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint)
                }
            };

            QuicListener listener;
            try
            {
                listener = await QuicListener.ListenAsync(
                    new QuicListenerOptions
                    {
                        ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                        ApplicationProtocols = Protocols,
                        ListenBacklog = 4,
                        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }

            QuicConnection? connection = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(wait);
                while (connection == null)
                {
                    QuicConnection candidate;
                    try
                    {
                        candidate = await listener.AcceptConnectionAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when ((ex is QuicException || ex is AuthenticationException) && !timeout.IsCancellationRequested)
                    {
                        // A handshake that failed (wrong certificate, garbage): keep waiting for the friend.
                        continue;
                    }

                    if (!candidate.RemoteEndPoint.Address.Equals(expectedRemote))
                    {
                        await candidate.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    connection = candidate;
                }

                await using var control = await connection.AcceptInboundStreamAsync(timeout.Token).ConfigureAwait(false);
                var proof = await ReadExactlyAsync(control, 33, timeout.Token).ConfigureAwait(false);
                if (!ProofMatches(proof, expectedProof))
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                await control.WriteAsync(ProofMessage(ownProof), timeout.Token).ConfigureAwait(false);
                control.CompleteWrites();
                return connection;
            }
            catch (Exception ex)
            {
                if (connection != null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                throw Translate(ex);
            }
            finally
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Opens a request stream on a connection.</summary>
        /// <summary>
        /// Paired friends (docs/PAIRING.md): connects as the client of a pairing link.
        /// Both sides may open request streams. Before returning, both sides prove they
        /// hold the pair key with fresh nonces on a control stream:
        /// <c>C→S 0x02|nC</c>, <c>S→C 0x02|nS|HMAC(k,"S"|t|nC|nS)</c>,
        /// <c>C→S HMAC(k,"C"|t|nC|nS)</c>, where <c>t</c> binds both certificates.
        /// </summary>
        /// <returns>The connection (opaque to the caller).</returns>
        public static async Task<object> ConnectPairedAsync(
            int localPort,
            IPEndPoint remote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] proofKey,
            byte[] transcript,
            int maxInboundStreams,
            CancellationToken cancellationToken)
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = remote,
                LocalEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                IdleTimeout = PairedIdleTimeout,
                KeepAliveInterval = PairedKeepAlive,
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                MaxInboundBidirectionalStreams = maxInboundStreams,
                MaxInboundUnidirectionalStreams = 0,
                InitialReceiveWindowSizes = Windows(),
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = PairedProtocols,
                    TargetHost = "jellyfin-federation-pairing",
                    ClientCertificates = new X509CertificateCollection { ownCertificate },
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint)
                }
            };

            QuicConnection connection;
            try
            {
                connection = await QuicConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token).ConfigureAwait(false);
                var clientNonce = RandomNumberGenerator.GetBytes(16);
                await control.WriteAsync(Prepend(PairedMarker, clientNonce), timeout.Token).ConfigureAwait(false);
                var answer = await ReadExactlyAsync(control, 1 + 16 + 32, timeout.Token).ConfigureAwait(false);
                if (answer == null || answer[0] != PairedMarker)
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                var serverNonce = answer.AsSpan(1, 16).ToArray();
                if (!CryptographicOperations.FixedTimeEquals(answer.AsSpan(17, 32), PairedProof(proofKey, (byte)'S', transcript, clientNonce, serverNonce)))
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                await control.WriteAsync(PairedProof(proofKey, (byte)'C', transcript, clientNonce, serverNonce), timeout.Token).ConfigureAwait(false);
                control.CompleteWrites();

                // Wait for the friend to accept the proof (it closes its side), so a
                // refused link never looks connected here.
                var end = new byte[1];
                if (await control.ReadAsync(end, timeout.Token).ConfigureAwait(false) != 0)
                {
                    throw new AuthenticationException("unexpected data on the link's control stream");
                }

                return connection;
            }
            catch (Exception ex)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw Translate(ex);
            }
        }

        /// <summary>
        /// Paired friends: listens on <paramref name="localPort"/> for at most
        /// <paramref name="wait"/> for one connection from <paramref name="expectedRemote"/>
        /// with the pinned certificate and a valid proof (see <see cref="ConnectPairedAsync"/>).
        /// The listener is closed afterwards in every case.
        /// </summary>
        /// <returns>The connection (opaque to the caller).</returns>
        public static async Task<object> AcceptPairedAsync(
            int localPort,
            IPEndPoint expectedRemote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] proofKey,
            byte[] transcript,
            int maxInboundStreams,
            TimeSpan wait,
            CancellationToken cancellationToken)
        {
            var serverOptions = new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                IdleTimeout = PairedIdleTimeout,
                KeepAliveInterval = PairedKeepAlive,
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                MaxInboundBidirectionalStreams = maxInboundStreams,
                MaxInboundUnidirectionalStreams = 0,
                InitialReceiveWindowSizes = Windows(),
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = PairedProtocols,
                    ServerCertificate = ownCertificate,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint)
                }
            };

            QuicListener listener;
            try
            {
                listener = await QuicListener.ListenAsync(
                    new QuicListenerOptions
                    {
                        ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                        ApplicationProtocols = PairedProtocols,
                        ListenBacklog = 2,
                        ConnectionOptionsCallback = (candidate, _, _) =>
                        {
                            // Only the friend's address gets a handshake at all; anyone
                            // else is refused before any TLS work.
                            var from = candidate.RemoteEndPoint;
                            var address = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
                            if (!address.Equals(expectedRemote.Address) || from.Port != expectedRemote.Port)
                            {
                                throw new AuthenticationException("not the paired friend's address");
                            }

                            return ValueTask.FromResult(serverOptions);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }

            QuicConnection? connection = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(wait);
                while (connection == null)
                {
                    QuicConnection candidate;
                    try
                    {
                        candidate = await listener.AcceptConnectionAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when ((ex is QuicException || ex is AuthenticationException) && !timeout.IsCancellationRequested)
                    {
                        continue;
                    }

                    var from = candidate.RemoteEndPoint;
                    if (!(from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address).Equals(expectedRemote.Address) || from.Port != expectedRemote.Port)
                    {
                        await candidate.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    connection = candidate;
                }

                using var proofTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                proofTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var control = await connection.AcceptInboundStreamAsync(proofTimeout.Token).ConfigureAwait(false);
                var hello = await ReadExactlyAsync(control, 17, proofTimeout.Token).ConfigureAwait(false);
                if (hello == null || hello[0] != PairedMarker)
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                var clientNonce = hello.AsSpan(1, 16).ToArray();
                var serverNonce = RandomNumberGenerator.GetBytes(16);
                var answer = new byte[1 + 16 + 32];
                answer[0] = PairedMarker;
                serverNonce.CopyTo(answer, 1);
                PairedProof(proofKey, (byte)'S', transcript, clientNonce, serverNonce).CopyTo(answer, 17);
                await control.WriteAsync(answer, proofTimeout.Token).ConfigureAwait(false);
                var proof = await ReadExactlyAsync(control, 32, proofTimeout.Token).ConfigureAwait(false);
                if (proof == null || !CryptographicOperations.FixedTimeEquals(proof, PairedProof(proofKey, (byte)'C', transcript, clientNonce, serverNonce)))
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                control.CompleteWrites();
                return connection;
            }
            catch (Exception ex)
            {
                if (connection != null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                throw Translate(ex);
            }
            finally
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }

        public static async Task<Stream> OpenStreamAsync(object connection, CancellationToken cancellationToken)
        {
            try
            {
                return await ((QuicConnection)connection).OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }
        }

        /// <summary>
        /// Waits for the next stream the other side opens (unidirectional ones
        /// are refused). Throws <see cref="IOException"/> once the connection ends.
        /// </summary>
        public static async Task<Stream> AcceptStreamAsync(object connection, CancellationToken cancellationToken)
        {
            var quic = (QuicConnection)connection;
            while (true)
            {
                QuicStream stream;
                try
                {
                    stream = await quic.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (QuicException ex)
                {
                    throw new IOException(Describe(ex), ex);
                }

                if (stream.Type == QuicStreamType.Bidirectional)
                {
                    return stream;
                }

                stream.Abort(QuicAbortDirection.Both, 1);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Ends this side's writes on a stream (the other side reads end-of-stream).</summary>
        public static void CompleteWrites(Stream stream)
        {
            if (stream is QuicStream quic)
            {
                quic.CompleteWrites();
            }
        }

        /// <summary>Aborts a stream both ways.</summary>
        public static void Abort(Stream stream)
        {
            if (stream is QuicStream quic)
            {
                quic.Abort(QuicAbortDirection.Both, 1);
            }
        }

        /// <summary>The other side's UDP endpoint.</summary>
        public static IPEndPoint RemoteEndPoint(object connection) => ((QuicConnection)connection).RemoteEndPoint;

        /// <summary>This side's UDP endpoint.</summary>
        public static IPEndPoint LocalEndPoint(object connection) => ((QuicConnection)connection).LocalEndPoint;

        /// <summary>Closes the connection gracefully, then releases it.</summary>
        public static async Task CloseAsync(object connection)
        {
            var quic = (QuicConnection)connection;
            try
            {
                await quic.CloseAsync(0).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Closing a dead connection may throw; it's gone either way.
            }

            await quic.DisposeAsync().ConfigureAwait(false);
        }

        private static bool Matches(X509Certificate? certificate, string expected)
        {
            if (certificate == null || expected.Length != 64)
            {
                return false;
            }

            byte[] wanted;
            try
            {
                wanted = Convert.FromHexString(expected);
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), wanted);
        }

        private static byte[] PairedProof(byte[] key, byte side, byte[] transcript, byte[] clientNonce, byte[] serverNonce)
        {
            var message = new byte[1 + transcript.Length + 32];
            message[0] = side;
            transcript.CopyTo(message, 1);
            clientNonce.CopyTo(message, 1 + transcript.Length);
            serverNonce.CopyTo(message, 1 + transcript.Length + 16);
            return HMACSHA256.HashData(key, message);
        }

        private static byte[] Prepend(byte first, byte[] rest)
        {
            var message = new byte[1 + rest.Length];
            message[0] = first;
            rest.CopyTo(message, 1);
            return message;
        }

        private static byte[] ProofMessage(byte[] proof)
        {
            var message = new byte[33];
            message[0] = ProofMarker;
            proof.AsSpan(0, 32).CopyTo(message.AsSpan(1));
            return message;
        }

        private static bool ProofMatches(byte[]? message, byte[] expected)
            => message != null && message.Length == 33 && message[0] == ProofMarker && CryptographicOperations.FixedTimeEquals(message.AsSpan(1), expected);

        private static Exception Translate(Exception ex) => ex switch
        {
            QuicException quic => new IOException(Describe(quic), quic),
            _ => ex
        };

        private static string Describe(QuicException ex) => ex.QuicError switch
        {
            QuicError.ConnectionIdle => "the link went quiet (no answer for 60 s)",
            QuicError.ConnectionTimeout => "the friend didn't answer",
            QuicError.ConnectionAborted => "the friend closed the link",
            QuicError.ConnectionRefused => "the friend refused the link",
            QuicError.OperationAborted => "closed",
            _ => "QUIC: " + ex.QuicError
        };

        private static async Task<byte[]?> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                filled += read;
            }

            return buffer;
        }
    }
}
